using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void VbaApplyPatchBacksUpModule()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"old\"\nEnd Sub";
                var backupStore = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var session = NewSession(adapter);
                var command = Command(
                    "common.vba_apply_patch",
                    "moduleName", "Module1",
                    "patch", new JArray(new JObject
                    {
                        ["op"] = "replace",
                        ["find"] = "\"old\"",
                        ["text"] = "\"new\""
                    }));

                var blocked = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = false }, false, false, session);
                AssertTrue(!blocked.Success, "vba replace blocked");
                AssertEqual("waiting_confirmation", blocked.Status, "vba replace waits for confirmation");
                AssertTrue(!string.IsNullOrWhiteSpace(command.RuntimeGuardJson), "runtime reads and binds its own snapshot before confirmation");
                AssertContains(blocked.DataJson, "operations", "confirmation includes the validated patch preview");
                AssertEqual(2, adapter.Executed.Count, "confirmation preflight reads and validates without a public read call");
                AssertEqual(0, adapter.Executed.Count(item => item.ToolId.EndsWith(".vba_replace_module", StringComparison.OrdinalIgnoreCase)), "confirmation preflight does not write VBA");
                AssertContains(adapter.VbaModuleCode, "\"old\"", "blocked mutation leaves code unchanged");

                var result = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = true }, false, false, session);

                AssertTrue(result.Success, "replace result");
                AssertContains(adapter.VbaModuleCode, "\"new\"", "updated module");
                AssertTrue(adapter.VbaModuleCode.IndexOf("\"old\"", StringComparison.Ordinal) < 0, "old text removed");
                var backups = backupStore.List("Excel", "doc");
                AssertEqual(1, backups.Count, "backup count");
                AssertEqual("Module1", backups[0].ModuleName, "backup module");
                AssertTrue(backups[0].Code == null, "backup list is metadata-only");
                AssertContains(backupStore.Find("Excel", "doc", backups[0].BackupId, null).Code, "\"old\"", "backup code");
            });
        }

        private static void VbaConfirmedMutationRejectsStaleSnapshot()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"old\"\nEnd Sub";
                var backupStore = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var session = NewSession(adapter);
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();

                var command = Command(
                    "common.vba_apply_patch",
                    "moduleName", "Module1",
                    "patch", new JArray(new JObject
                    {
                        ["op"] = "replace",
                        ["find"] = "\"old\"",
                        ["text"] = "\"new\""
                    }));
                var waiting = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = false }, false, false, session);
                AssertEqual("waiting_confirmation", waiting.Status, "mutation waits for confirmation");

                var persistedCommand = JsonConvert.DeserializeObject<ToolCommand>(JsonConvert.SerializeObject(command));
                AssertTrue(!string.IsNullOrWhiteSpace(persistedCommand.RuntimeGuardJson), "runtime guard survives persistence");
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"changed elsewhere\"\nEnd Sub";
                var stale = executor.Execute(persistedCommand, tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);

                AssertEqual("stale_vba_module", stale.ErrorCode, "confirmed stale mutation rejected");
                AssertContains(adapter.VbaModuleCode, "changed elsewhere", "stale mutation does not overwrite external change");
                AssertEqual(0, backupStore.List("Excel", "doc").Count, "stale mutation does not create a needless backup");
            });
        }

        private static void VbaCreateRejectsConfirmationRace()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var session = NewSession(adapter);
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var command = Command(
                    "common.vba_write_module",
                    "moduleName", "CreatedDuringConfirmation",
                    "componentType", "StdModule",
                    "code", "Sub Requested()\nEnd Sub",
                    "mode", "createOnly");
                var waiting = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = false }, false, false, session);
                AssertEqual("waiting_confirmation", waiting.Status, "create waits for confirmation");

                adapter.SetVbaModule("CreatedDuringConfirmation", "Sub External()\nEnd Sub", "StdModule");
                var stale = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);

                AssertEqual("stale_vba_module", stale.ErrorCode, "create detects a module added during confirmation");
                AssertContains(adapter.GetVbaModuleCode("CreatedDuringConfirmation"), "External", "create race does not overwrite module");
            });
        }

        private static void VbaWriteUpsertsAndNormalizesName()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub OldCode()\nEnd Sub";
                var backupStore = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };
                var session = NewSession(adapter);

                var updated = executor.Execute(
                    Command("common.vba_write_module", "moduleName", "Module1", "code", "Sub UpdatedCode()\nEnd Sub"),
                    tools,
                    settings,
                    false,
                    false,
                    session);
                AssertTrue(updated.Success, "write updates an existing module without a create/read sequence");
                AssertContains(adapter.VbaModuleCode, "UpdatedCode", "existing module source replaced");
                AssertEqual(1, backupStore.List("Excel", "doc").Count, "existing module write creates a backup");

                var requestedName = "123 very bad-module name with spaces and punctuation !!! more than forty chars";
                var created = executor.Execute(
                    Command("common.vba_write_module", "moduleName", requestedName, "componentType", "ClassModule", "code", "Option Explicit\nPublic Value As Long"),
                    tools,
                    settings,
                    false,
                    false,
                    session);
                AssertTrue(created.Success, "write creates a missing normalized module");
                var createdData = JObject.Parse(created.DataJson ?? "{}");
                var actualName = (string)createdData["moduleName"];
                AssertEqual(true, (bool)createdData["nameNormalized"], "name normalization is reported");
                AssertTrue(!string.IsNullOrWhiteSpace(actualName) && actualName.Length <= 31 && char.IsLetter(actualName[0]),
                    "normalized VBA component name is valid and bounded");
                AssertContains(adapter.GetVbaModuleCode(actualName), "Public Value", "normalized module receives requested source");

                var repeated = executor.Execute(
                    Command("common.vba_write_module", "moduleName", requestedName, "componentType", "StdModule", "code", "Option Explicit\nPublic Value As String"),
                    tools,
                    settings,
                    false,
                    false,
                    session);
                AssertTrue(repeated.Success, "same invalid name deterministically updates the normalized module");
                AssertEqual(false, (bool)JObject.Parse(repeated.DataJson ?? "{}")["created"], "repeated write is an update, not a duplicate create");
                AssertContains(adapter.GetVbaModuleCode(actualName), "As String", "normalized module is updated in place");
                var listed = executor.Execute(Command("common.vba_read_module"), tools, new AppSettings(), false, false);
                AssertEqual(1, (JObject.Parse(listed.DataJson)["modules"] as JArray).OfType<JObject>()
                    .Count(item => string.Equals((string)item["name"], actualName, StringComparison.OrdinalIgnoreCase)),
                    "normalization remains idempotent");

                adapter.SetVbaModule("SafeName", "Sub KeepMe()\nEnd Sub", "StdModule");
                var collisionSafe = executor.Execute(
                    Command("common.vba_write_module", "moduleName", "SafeName!", "code", "Sub NewNormalized()\nEnd Sub"),
                    tools,
                    settings,
                    false,
                    false,
                    session);
                AssertTrue(collisionSafe.Success, "invalid name is normalized without colliding with its plain valid form");
                AssertTrue(!string.Equals("SafeName", (string)JObject.Parse(collisionSafe.DataJson)["moduleName"], StringComparison.OrdinalIgnoreCase),
                    "normalized name includes a deterministic collision-resistant suffix");
                AssertContains(adapter.GetVbaModuleCode("SafeName"), "KeepMe", "normalization does not overwrite a colliding valid module");

                adapter.SetVbaModule("ObservedModule", "Sub Original()\nEnd Sub", "StdModule");
                AssertTrue(executor.Execute(
                    Command("common.vba_read_module", "moduleName", "ObservedModule"),
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session).Success, "whole-source edit observation read");
                adapter.SetVbaModule("ObservedModule", "Sub ExternalChange()\nEnd Sub", "StdModule");
                var observedWrite = Command(
                    "common.vba_write_module",
                    "moduleName", "ObservedModule",
                    "code", "Sub IntendedFromOldSource()\nEnd Sub");
                var stale = executor.Execute(observedWrite, tools, settings, false, false, session);
                AssertEqual("stale_vba_module", stale.ErrorCode, "runtime uses a prior read snapshot without a model hash argument");
                AssertContains(stale.DataJson, "reconcileBeforeOverwrite", "stale whole write explains reconciliation");
                AssertContains(adapter.GetVbaModuleCode("ObservedModule"), "ExternalChange", "stale whole write preserves external code");
                AssertTrue(executor.Execute(observedWrite, tools, settings, false, false, session).Success,
                    "an intentional same-tool retry can explicitly overwrite after the stale warning");
                AssertContains(adapter.GetVbaModuleCode("ObservedModule"), "IntendedFromOldSource", "intentional retry writes complete source");
            });
        }

        private static void VbaWriteRenameIsStrictAndAtomic()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                const string source = "Option Explicit\nPublic Sub Run()\nEnd Sub";
                var adapter = new FakeOfficeAdapter();
                adapter.SetVbaModule("OldModule", source, "ClassModule");
                var store = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, store, new SkillStore(paths));
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var definition = tools.Single(item => item.Id == "common.vba_write_module");
                var schema = JObject.Parse(definition.ArgumentSchemaJson);
                var variants = schema["anyOf"] as JArray;
                AssertEqual(2, variants == null ? 0 : variants.Count, "write tool exposes exactly write and rename branches");
                var renameVariant = variants == null ? null : variants.OfType<JObject>().FirstOrDefault(item =>
                    string.Equals((string)item.SelectToken("properties.mode.enum[0]"), "rename", StringComparison.Ordinal));
                AssertTrue(renameVariant != null, "rename branch is explicit");
                AssertTrue(renameVariant["properties"]["code"] == null && renameVariant["properties"]["componentType"] == null,
                    "rename branch does not expose write-only arguments");

                var promptParameters = ToolSchemaSupport.ForPrompt(schema);
                AssertTrue(promptParameters["properties"] == null && promptParameters["anyOf"] is JArray,
                    "model prompt exposes two complete alternatives without a misleading optional envelope");
                var promptRenameVariant = ((JArray)promptParameters["anyOf"]).OfType<JObject>().Single(item =>
                    string.Equals((string)item.SelectToken("properties.mode.enum[0]"), "rename", StringComparison.Ordinal));
                AssertEqual(3, ((JObject)promptRenameVariant["properties"]).Properties().Count(),
                    "model rename branch exposes only moduleName, newModuleName, and mode");
                AssertEqual(3, ((JArray)promptRenameVariant["required"]).Count,
                    "all model rename arguments are required");
                AssertTrue(promptRenameVariant.SelectToken("properties.code") == null &&
                    promptRenameVariant.SelectToken("properties.componentType") == null,
                    "model rename branch cannot mix copy/write arguments");

                var renamed = executor.Execute(
                    Command(
                        "common.vba_write_module",
                        "moduleName", "OldModule",
                        "newModuleName", "RenamedModule",
                        "mode", "rename"),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    NewSession(adapter));

                AssertTrue(renamed.Success, "public write rename succeeds");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("OldModule"), "old identity is absent");
                AssertEqual(source, adapter.GetVbaModuleCode("RenamedModule"), "rename preserves exact source");
                var data = JObject.Parse(renamed.DataJson ?? "{}");
                AssertEqual("OldModule", (string)data["previousModuleName"], "result returns previous name");
                AssertEqual("RenamedModule", (string)data["moduleName"], "result returns actual new name");
                AssertEqual("rename", (string)data["mode"], "result identifies rename branch");
                AssertEqual(VbaMutationStatuses.Committed, (string)data["journalStatus"], "two-name journal commits");
                AssertEqual(0, store.List("Excel", "doc").Count, "identity-only rename does not expose a misleading source backup");
                var journal = store.ListPackageMutations("Excel", "doc").Single();
                AssertEqual("rename", journal.Prepared.Operation, "rename uses a two-identity prepared record");
                AssertEqual(2, journal.Prepared.Components.Count, "journal records old and new identities");
                AssertEqual(VbaMutationStatuses.Committed, journal.Terminal.Status, "rename journal terminal is committed");
                var row = store.QueryMutations("Excel", "doc", new VbaMutationQueryRequest()).Rows.Single();
                AssertEqual(VbaMutationKinds.Module, row.Kind, "rename projects as a module mutation");
                AssertEqual(1, row.ComponentCount, "rename remains one logical component");
                AssertTrue(row.ComponentNames.Contains("OldModule") && row.ComponentNames.Contains("RenamedModule"),
                    "diagnostics retain both names");

                var invalid = executor.Execute(
                    Command(
                        "common.vba_write_module",
                        "moduleName", "RenamedModule",
                        "newModuleName", "AnotherModule",
                        "mode", "rename",
                        "code", source),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    NewSession(adapter));
                AssertTrue(!invalid.Success, "rename with write-only code is rejected by schema");
                AssertContains(invalid.Message, "unsupported property code", "invalid rename reports the conflicting branch argument");
                AssertEqual(source, adapter.GetVbaModuleCode("RenamedModule"), "invalid branch leaves source identity unchanged");
            });
        }

        private static void VbaRenameRejectsConfirmationRace()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                const string source = "Sub Original()\nEnd Sub";
                adapter.SetVbaModule("RenameSource", source, "StdModule");
                var session = NewSession(adapter);
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var command = Command(
                    "common.vba_write_module",
                    "moduleName", "RenameSource",
                    "newModuleName", "RenameTarget",
                    "mode", "rename");
                var waiting = executor.Execute(
                    command,
                    tools,
                    new AppSettings { AutoConfirmToolActions = false },
                    false,
                    false,
                    session);
                AssertEqual("waiting_confirmation", waiting.Status, "rename waits for confirmation");
                AssertContains(waiting.Message, "RenameSource", "confirmation identifies source");
                AssertContains(waiting.Message, "RenameTarget", "confirmation identifies destination");

                adapter.SetVbaModule("RenameTarget", "Sub External()\nEnd Sub", "StdModule");
                var stale = executor.Execute(
                    command,
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertEqual("stale_vba_module", stale.ErrorCode, "destination created during confirmation blocks rename");
                AssertEqual(source, adapter.GetVbaModuleCode("RenameSource"), "source remains under its old name");
                AssertContains(adapter.GetVbaModuleCode("RenameTarget"), "External", "racing destination is preserved");
            });
        }

        private static void VbaDeleteNeedsNoPublicRead()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Main()\nEnd Sub";
                var backupStore = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };
                var session = NewSession(adapter);
                var result = executor.Execute(
                    Command("common.vba_delete_module", "moduleName", "Module1"),
                    tools,
                    settings,
                    false,
                    false,
                    session);

                AssertTrue(result.Success, "delete succeeds without a public read");
                AssertTrue(adapter.Executed.Any(item => item.ToolId.EndsWith(".vba_read_module", StringComparison.OrdinalIgnoreCase)),
                    "runtime reads the module internally");
                AssertTrue(adapter.Executed.Any(item => item.ToolId.EndsWith(".vba_delete_module_internal", StringComparison.OrdinalIgnoreCase)),
                    "runtime performs the delete after validation");
                AssertEqual(1, backupStore.List("Excel", "doc").Count, "delete keeps one rollback backup");

                adapter.SetVbaModule("Module2", "Sub BeforeRead()\nEnd Sub", "StdModule");
                AssertTrue(executor.Execute(
                    Command("common.vba_read_module", "moduleName", "Module2"),
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session).Success, "optional delete observation read");
                adapter.SetVbaModule("Module2", "Sub ChangedAfterRead()\nEnd Sub", "StdModule");
                var deleteObserved = Command("common.vba_delete_module", "moduleName", "Module2");
                var stale = executor.Execute(deleteObserved, tools, settings, false, false, session);
                AssertEqual("stale_vba_module", stale.ErrorCode, "runtime uses an optional prior delete snapshot without a hash argument");
                AssertContains(adapter.GetVbaModuleCode("Module2"), "ChangedAfterRead", "stale delete keeps the changed module");
                AssertTrue(executor.Execute(deleteObserved, tools, settings, false, false, session).Success,
                    "same-tool retry deletes after the stale warning");
                AssertEqual(2, backupStore.List("Excel", "doc").Count, "retried delete keeps the current source backup");

                adapter.SetVbaModule("Module3", "Sub SeenInFirstChat()\nEnd Sub", "StdModule");
                AssertTrue(executor.Execute(
                    Command("common.vba_read_module", "moduleName", "Module3"),
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session).Success, "first chat records its optional observation");
                adapter.SetVbaModule("Module3", "Sub ChangedForSecondChat()\nEnd Sub", "StdModule");
                var secondSession = NewSession(adapter);
                var secondChatDelete = executor.Execute(
                    Command("common.vba_delete_module", "moduleName", "Module3"),
                    tools,
                    settings,
                    false,
                    false,
                    secondSession);
                AssertTrue(secondChatDelete.Success, "an observation from another chat does not block an intentional mutation");
            });
        }

        private static void VbaGuardHandlesStableAndChangedDocumentIdentities()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "Sub Main()\nEnd Sub";
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var session = NewSession(adapter);
                var command = Command("common.vba_delete_module", "moduleName", "Module1");
                AssertEqual("waiting_confirmation", executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = false }, false, false, session).Status,
                    "delete waits with a bound guard");

                adapter.RuntimeDocumentKeyValue = "runtime-other-document";
                var sameDocument = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                AssertTrue(sameDocument.Success, "stable document key tolerates a changed runtime identity");

                adapter.VbaModuleCode = "Sub Main()\nEnd Sub";
                var changedCommand = Command("common.vba_delete_module", "moduleName", "Module1");
                AssertEqual("waiting_confirmation", executor.Execute(changedCommand, tools, new AppSettings { AutoConfirmToolActions = false }, false, false, session).Status,
                    "second delete waits with a bound guard");
                adapter.DocumentKeyValue = "other-document";
                adapter.RuntimeDocumentKeyValue = "runtime-different-document";
                session.DocumentKey = adapter.DocumentKeyValue;
                var blocked = executor.Execute(changedCommand, tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);

                AssertEqual("vba_snapshot_context_changed", blocked.ErrorCode, "different document invalidates the guard");
                AssertContains(adapter.VbaModuleCode, "Sub Main", "document switch does not delete module");
            });
        }

        private static void VbaApplyPatchTargetsNamedModule()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.SetVbaModule("Module1", "Sub Main()\nDebug.Print \"untouched\"\nEnd Sub", "StdModule");
                adapter.SetVbaModule("Module2", "Sub Run()\nDebug.Print \"old\"\nEnd Sub", "StdModule");
                var backupStore = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var command = new ToolCommand { ToolId = executor.VbaToolId("vba_apply_patch") };
                command.Arguments["moduleName"] = "Module2";
                command.Arguments["patch"] = new JArray
                {
                    new JObject
                    {
                        ["op"] = "replace",
                        ["find"] = "\"old\"",
                        ["text"] = "\"new\""
                    },
                    new JObject
                    {
                        ["op"] = "replace",
                        ["find"] = "End Sub",
                        ["text"] = "End Sub\nPublic Sub Added()\nEnd Sub"
                    }
                };

                var result = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "patch result");
                AssertContains(adapter.GetVbaModuleCode("Module2"), "\"new\"", "module2 updated");
                AssertContains(adapter.GetVbaModuleCode("Module2"), "End Sub\nPublic Sub Added()", "exact hunk adds the requested line boundary");
                AssertTrue(adapter.GetVbaModuleCode("Module2").IndexOf("End SubPublic", StringComparison.Ordinal) < 0, "exact hunk does not concatenate procedures");
                AssertContains(adapter.GetVbaModuleCode("Module1"), "\"untouched\"", "module1 untouched");
                var backups = backupStore.List("Excel", "doc");
                AssertEqual(1, backups.Count, "backup count");
                AssertEqual("Module2", backups[0].ModuleName, "backup module");
                AssertTrue(backups[0].Code == null, "backup list is metadata-only");
                AssertContains(backupStore.Find("Excel", "doc", backups[0].BackupId, null).Code, "\"old\"", "backup code");

                var malformed = new ToolCommand { ToolId = executor.VbaToolId("vba_apply_patch") };
                malformed.Arguments["moduleName"] = "Module2";
                malformed.Arguments["patch"] = "[{\"op\":\"replace\"}}trailing";
                var malformedResult = executor.Execute(malformed, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(!malformedResult.Success, "malformed patch rejected");
                AssertContains(malformedResult.Message, "$.patch must be a native JSON array", "malformed patch diagnostic");

                var emptyAnchor = Command(
                    "common.vba_apply_patch",
                    "moduleName", "Module2",
                    "patch", new JArray(new JObject { ["op"] = "replace", ["find"] = string.Empty, ["text"] = "Debug.Print 1" }));
                var emptyAnchorResult = executor.Execute(emptyAnchor, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(!emptyAnchorResult.Success, "empty exact block rejected");
                AssertContains(emptyAnchorResult.Message, "shorter than minLength", "empty exact block schema diagnostic");
            });
        }

        private static void VbaExactPatchPreservesCompleteLines()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "Option Explicit\r\nPublic Sub Run()\r\nDebug.Print 1\r\nEnd Sub";
                var result = executor.Execute(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(
                            new JObject
                            {
                                ["op"] = "replace",
                                ["find"] = "Debug.Print 1",
                                ["text"] = "Dim value As Long\nDebug.Print 1"
                            },
                            new JObject
                            {
                                ["op"] = "replace",
                                ["find"] = "Debug.Print 1",
                                ["text"] = "Debug.Print 1\nvalue = 2"
                            })),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);

                AssertTrue(result.Success, "ordered exact hunks patch successfully");
                AssertEqual(
                    "Option Explicit\r\nPublic Sub Run()\r\nDim value As Long\r\nDebug.Print 1\r\nvalue = 2\r\nEnd Sub",
                    adapter.VbaModuleCode,
                    "exact hunks preserve CRLF and untouched source");
            });

            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "A\r\nB\r\n";
                var appended = executor.Execute(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["op"] = "replace",
                            ["find"] = "B",
                            ["text"] = "B\nC"
                        })),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);

                AssertTrue(appended.Success, "exact replacement can append after a unique block");
                AssertEqual("A\r\nB\r\nC\r\n", adapter.VbaModuleCode,
                    "exact replacement normalizes LF to CRLF without trimming content");
            });

            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "A\r\n";
                var appended = executor.Execute(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["op"] = "replace",
                            ["find"] = "A",
                            ["text"] = "A\nB"
                        })),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);

                AssertTrue(appended.Success, "exact hunk may append to a terminated module");
                AssertEqual("A\r\nB\r\n", adapter.VbaModuleCode,
                    "unchanged suffix preserves the module's final transport terminator");
            });
        }

        private static void VbaInvalidStateBlocksWrite()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Original()\nEnd Sub";
                adapter.QueueResult("excel.vba_read_module", ToolResult.Ok("malformed read", "{}"));
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths));
                var command = Command("common.vba_write_module", "moduleName", "Module1", "code", "Sub Changed()\nEnd Sub", "mode", "updateOnly");

                var result = executor.Execute(command, adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "write blocked");
                AssertEqual("vba_read_invalid", result.ErrorCode, "invalid live state blocks write");
                AssertEqual("Sub Original()\nEnd Sub", adapter.VbaModuleCode, "module unchanged");
                AssertEqual(1, adapter.Executed.Count, "only backup read executed");

                adapter.Executed.Clear();
                var create = Command("common.vba_write_module", "moduleName", "NewModule", "code", "Sub NewMacro()\nEnd Sub", "mode", "createOnly");
                var created = executor.Execute(create, adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(created.Success, "missing module can be created");
                AssertContains(adapter.GetVbaModuleCode("NewModule"), "NewMacro", "new module code");

                var missingPatch = Command(
                    "common.vba_apply_patch",
                    "moduleName", "MissingModule",
                    "patch", new Newtonsoft.Json.Linq.JArray(new Newtonsoft.Json.Linq.JObject
                    {
                        ["op"] = "replace",
                        ["find"] = "Option Explicit",
                        ["text"] = "Option Explicit\nSub Added()\nEnd Sub"
                    }));
                var missingPatchResult = executor.Execute(missingPatch, adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertEqual("vba_module_not_found", missingPatchResult.ErrorCode, "patch cannot masquerade as module creation");
                AssertContains(missingPatchResult.Message, "common.vba_write_module", "missing patch points directly to the creation tool");
                AssertContains(missingPatchResult.DataJson, "creationTool", "missing patch returns machine-readable recovery guidance");
            });
        }

        private static void VbaPatchRejectsAddressingModes()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "Sub Main()\nEnd Sub";
                var command = Command(
                    executor.VbaToolId("vba_apply_patch"),
                    "moduleName", "Module1",
                    "patch", new JArray(new JObject
                    {
                        ["op"] = "replaceLines",
                        ["startLine"] = 2,
                        ["deleteCount"] = 5,
                        ["text"] = "End Sub"
                    }));

                var result = executor.Execute(command, adapter.GetBuiltInTools().ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "line-number patch mode rejected by schema");
                AssertEqual("invalid_arguments", result.ErrorCode, "removed addressing mode fails before patch execution");
                AssertEqual("Sub Main()\nEnd Sub", adapter.VbaModuleCode, "removed addressing mode leaves module unchanged");
            });
        }

        private static void VbaPatchRejectsStaleExactSource()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "A\nB\nC";
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var session = NewSession(adapter);
                AssertTrue(executor.Execute(
                    Command("common.vba_read_module", "moduleName", "Module1", "startLine", 1, "lineCount", 3),
                    tools,
                    new AppSettings(),
                    false,
                    false,
                    session).Success, "initial line snapshot read");

                var first = executor.Execute(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["op"] = "replace",
                            ["find"] = "B",
                            ["text"] = "X\nB"
                        })),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertTrue(first.Success, "first exact patch changes the surrounding source");
                AssertEqual("A\nX\nB\nC", adapter.VbaModuleCode, "first patch applied in memory then replaced whole module");

                var staleSource = Command(
                    "common.vba_apply_patch",
                    "moduleName", "Module1",
                    "patch", new JArray(new JObject
                    {
                        ["op"] = "replace",
                        ["find"] = "A\nB",
                        ["text"] = "A\nY"
                    }));
                var rejected = executor.Execute(
                    staleSource,
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertEqual("vba_patch_stale_source", rejected.ErrorCode,
                    "a stale exact hunk cannot target shifted current text");
                AssertEqual("A\nX\nB\nC", adapter.VbaModuleCode, "stale exact patch leaves current module intact");
                AssertEqual(1, adapter.Executed.Count(item => item.ToolId.EndsWith(".vba_replace_module", StringComparison.OrdinalIgnoreCase)),
                    "stale exact hunk never reaches the backend writer");
            });
        }

        private static void VbaLiveHashPreservesLineStructure()
        {
            AssertEqual(
                VbaToolManifestParser.LiveCodeSha256("Option Explicit\r\nSub Main()\r\nEnd Sub"),
                VbaToolManifestParser.LiveCodeSha256("Option Explicit\nSub Main()\nEnd Sub\n"),
                "line ending transport is normalized");
            AssertTrue(
                !string.Equals(VbaToolManifestParser.LiveCodeSha256("\nOption Explicit"), VbaToolManifestParser.LiveCodeSha256("Option Explicit"), StringComparison.Ordinal),
                "leading blank line changes live hash");
            AssertTrue(
                !string.Equals(VbaToolManifestParser.LiveCodeSha256("Option Explicit\n\n"), VbaToolManifestParser.LiveCodeSha256("Option Explicit\n"), StringComparison.Ordinal),
                "trailing blank line changes live hash");
            AssertTrue(
                !string.Equals(VbaToolManifestParser.LiveCodeSha256("' RNAssistantSession: id=x\nOption Explicit"), VbaToolManifestParser.LiveCodeSha256("Option Explicit"), StringComparison.Ordinal),
                "runtime marker changes live hash");
            AssertEqual(
                VbaToolManifestParser.VbeComparableCodeSha256("Sub Main()\n    Debug.Print \"Value\"\nEnd Sub"),
                VbaToolManifestParser.VbeComparableCodeSha256("sub Main ( )\r\nDebug.Print \"Value\"\r\nend sub\r\n\r\n"),
                "VBE-only formatting is comparable");
            AssertTrue(
                !string.Equals(
                    VbaToolManifestParser.VbeComparableCodeSha256("Debug.Print \"Value\""),
                    VbaToolManifestParser.VbeComparableCodeSha256("Debug.Print \"Changed\""),
                    StringComparison.Ordinal),
                "string literal changes remain significant");
            AssertTrue(
                !string.Equals(
                    VbaToolManifestParser.VbeComparableCodeSha256("End Sub"),
                    VbaToolManifestParser.VbeComparableCodeSha256("EndSub"),
                    StringComparison.Ordinal),
                "token boundaries remain significant");
        }

        private static void VbaReadBackAcceptsVbeNormalization()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Main()\r\nDebug.Print \"old\"\r\nEnd Sub";
                adapter.VbaReportedLineCountOffset = 1;
                adapter.VbaWriteTransform = code =>
                    code.Replace("Sub Main()", "sub Main ( )") + "\r\n\r\n";
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths));
                var patch = new JArray
                {
                    new JObject
                    {
                        ["op"] = "replace",
                        ["find"] = "Sub Main()\nDebug.Print \"old\"",
                        ["text"] = "Sub Main()\nDebug.Print \"new\""
                    }
                };
                var command = Command(
                    "common.vba_apply_patch",
                    "moduleName", "Module1",
                    "patch", patch);

                var result = executor.Execute(
                    command,
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);

                AssertTrue(result.Success, "VBE-normalized patch result");
                AssertContains(adapter.VbaModuleCode, "\"new\"", "patch was applied");
                var data = JObject.Parse(result.DataJson);
                AssertEqual(true, (bool)data["vbeNormalized"], "VBE normalization reported");
                AssertEqual(VbaToolManifestParser.LiveCodeSha256(adapter.VbaModuleCode), (string)data["codeSha256"], "actual read-back hash returned");
            });
        }

        private static void VbaProjectWriteAcceptsVbeNormalization()
        {
            var document = new FakeVbaDocumentObject();
            var component = document.VBProject.VBComponents.Seed("Module1", "Sub Original()\nEnd Sub");
            component.CodeModule.ReportedLineCountOffset = 1;
            component.CodeModule.WriteTransform = code =>
                code.Replace("Sub Changed()", "sub Changed ( )") + "\r\n\r\n";

            var result = VbaProjectSupport.ReplaceModule(document, "Module1", "Sub Changed()\nEnd Sub\n", false);

            AssertTrue(result.Success, "COM write accepts VBE normalization and phantom line count");
            AssertContains(component.CodeModule.Code, "Changed", "changed code remains in module");
            AssertEqual(
                VbaToolManifestParser.VbeComparableCodeSha256("Sub Changed()\nEnd Sub"),
                VbaToolManifestParser.VbeComparableCodeSha256(component.CodeModule.Code),
                "read-back code is VBE-equivalent");
        }

        private static void VbaProjectRenamePreservesComponentIdentity()
        {
            const string source = "Option Explicit\nPublic Sub Main()\nEnd Sub";
            var document = new FakeVbaDocumentObject();
            var component = document.VBProject.VBComponents.Seed("OldModule", source, 2);
            var designer = component.Designer;
            var hash = VbaToolManifestParser.LiveCodeSha256(source);

            var result = VbaProjectSupport.RenameModule(document, "OldModule", "NewModule", hash);

            AssertTrue(result.Success, "COM rename succeeds");
            var renamed = document.VBProject.VBComponents.Cast<FakeVbaComponent>().Single();
            AssertTrue(object.ReferenceEquals(component, renamed), "rename preserves the VBComponent object");
            AssertTrue(object.ReferenceEquals(designer, renamed.Designer), "rename preserves component metadata/designer identity");
            AssertEqual("NewModule", renamed.Name, "COM rename changes only the component name");
            AssertEqual(2, renamed.Type, "COM rename preserves component type");
            AssertEqual(source, renamed.CodeModule.Code, "COM rename preserves source");
            AssertEqual("vba_module_not_found", VbaProjectSupport.ReadModule(document, "OldModule", 1000).ErrorCode,
                "old name is absent after rename");

            document.VBProject.VBComponents.Seed("Collision", "Sub Existing()\nEnd Sub");
            var collision = VbaProjectSupport.RenameModule(document, "NewModule", "Collision", hash);
            AssertEqual("vba_module_exists", collision.ErrorCode, "COM rename rejects destination collision");
            AssertEqual("NewModule", renamed.Name, "collision leaves source identity unchanged");

            var stale = VbaProjectSupport.RenameModule(
                document,
                "NewModule",
                "AnotherName",
                VbaToolManifestParser.LiveCodeSha256("Sub Stale()\nEnd Sub"));
            AssertEqual("stale_vba_module", stale.ErrorCode, "COM rename compare-and-swap rejects source drift");
            AssertEqual("NewModule", renamed.Name, "stale rename leaves source identity unchanged");

            var documentModule = document.VBProject.VBComponents.Seed("ThisDocument", "Option Explicit", 100);
            var blocked = VbaProjectSupport.RenameModule(document, "ThisDocument", "RenamedDocument");
            AssertEqual("vba_component_type_read_only", blocked.ErrorCode, "document module rename remains blocked");
            AssertEqual("ThisDocument", documentModule.Name, "blocked document module name is unchanged");
        }

        private static void VbaBackendCompareAndSwapRejectsDrift()
        {
            var document = new FakeVbaDocumentObject();
            var component = document.VBProject.VBComponents.Seed("Module1", "Sub ExternalChange()\nEnd Sub");
            var staleHash = VbaToolManifestParser.LiveCodeSha256("Sub EarlierSnapshot()\nEnd Sub");

            var write = VbaProjectSupport.ReplaceModule(
                document,
                "Module1",
                "Sub Requested()\nEnd Sub",
                false,
                staleHash);
            AssertEqual("stale_vba_module", write.ErrorCode, "backend rejects a late write race");
            AssertContains(component.CodeModule.Code, "ExternalChange", "late write race preserves current code");

            var delete = VbaProjectSupport.DeleteModule(document, "Module1", staleHash);
            AssertEqual("stale_vba_module", delete.ErrorCode, "backend rejects a late delete race");
            AssertEqual(1, document.VBProject.VBComponents.Count, "late delete race preserves current component");
        }

        private static void VbaUserFormCreateAndCodeEdit()
        {
            var document = new FakeVbaDocumentObject();
            var created = VbaProjectSupport.CreateModule(document, "UserForm1", "MSForm", "Option Explicit\n");
            AssertTrue(created.Success, "COM UserForm create succeeds");
            var form = document.VBProject.VBComponents.Cast<FakeVbaComponent>().Single(component => component.Name == "UserForm1");
            AssertEqual(3, form.Type, "COM UserForm uses MSForm component type");
            var read = VbaProjectSupport.ReadModule(document, "UserForm1", 1000000);
            AssertEqual("MSForm", (string)JObject.Parse(read.DataJson)["type"], "COM UserForm type is listed canonically");

            var edited = VbaProjectSupport.ReplaceModule(
                document,
                "UserForm1",
                "Option Explicit\nPrivate Sub UserForm_Initialize()\nEnd Sub",
                false);
            AssertTrue(edited.Success, "COM UserForm code-behind edit succeeds");
            AssertContains(form.CodeModule.Code, "UserForm_Initialize", "COM UserForm code-behind changed");
            AssertEqual("vba_component_type_read_only", VbaProjectSupport.DeleteModule(document, "UserForm1").ErrorCode, "COM UserForm delete remains blocked");

            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };
                var publicCreate = executor.Execute(
                    Command("common.vba_write_module", "moduleName", "UserForm2", "componentType", "MSForm", "code", "Option Explicit\n", "mode", "createOnly"),
                    tools,
                    settings,
                    false,
                    false);
                AssertTrue(publicCreate.Success, "public UserForm create succeeds");

                var publicEdit = executor.Execute(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "UserForm2",
                        "patch", new JArray(new JObject
                        {
                            ["op"] = "replace",
                            ["find"] = "Option Explicit",
                            ["text"] = "Option Explicit\nPrivate Sub UserForm_Activate()\nEnd Sub"
                        })),
                    tools,
                    settings,
                    false,
                    false);
                AssertTrue(publicEdit.Success, "public UserForm code edit succeeds");
                AssertContains(adapter.GetVbaModuleCode("UserForm2"), "UserForm_Activate", "public UserForm code changed");
            });
        }

        private static void VbaCodeOnlyUserFormSkillIsExplicit()
        {
            var skills = BuiltInSkillProvider.GetSkills(FakeOfficeAdapter.ForHost("Excel"));
            var userForm = skills.Single(skill => string.Equals(
                skill.Id,
                "common.vba_userform_authoring",
                StringComparison.OrdinalIgnoreCase));
            AssertTrue(userForm.BuiltIn && userForm.Enabled, "code-only UserForm skill is available");
            AssertContains(userForm.Description, "entirely from source code", "catalog triggers for code-only authoring");
            AssertContains(userForm.BodyMarkdown, "Me.Controls.Add", "skill creates controls from source");
            AssertContains(userForm.BodyMarkdown, "Private WithEvents", "skill explains fixed control events");
            AssertContains(userForm.BodyMarkdown, "form-level Collection", "skill retains dynamic event sinks");
            AssertContains(userForm.BodyMarkdown, "unload an already loaded form", "skill rebuilds live instances after edits");
            AssertContains(userForm.BodyMarkdown, "Designer-time controls/properties and FRX assets are unsupported", "skill excludes designer state precisely");
            AssertContains(userForm.BodyMarkdown, ".form.vba", "skill documents code-only package storage");
            AssertContains(userForm.BodyMarkdown, "one journaled component transaction", "skill documents atomic package lifecycle");

            var editing = skills.Single(skill => string.Equals(
                skill.Id,
                "common.vba_code_editing",
                StringComparison.OrdinalIgnoreCase));
            AssertContains(editing.Description, "Use whenever a request changes VBA source", "catalog reliably triggers VBA editing guidance");
            AssertContains(editing.BodyMarkdown, "never creates a missing module", "patch remains existing-only");
            AssertContains(editing.BodyMarkdown, "repeat the exact anchor block", "insertions use explicit exact replacement text");
            AssertContains(editing.BodyMarkdown, "mode=rename", "skill explains the strict rename branch");
            AssertContains(editing.BodyMarkdown, "Never imitate rename with write plus delete", "skill forbids unsafe rename emulation");
            AssertContains(editing.BodyMarkdown, "does not rewrite explicit references", "skill warns about qualified VBA references");
            AssertContains(editing.BodyMarkdown, "Option Explicit", "skill includes baseline VBA code quality");
            AssertContains(editing.BodyMarkdown, "complete Option block", "skill preserves all leading Option directives");
            AssertContains(editing.BodyMarkdown, "PtrSafe", "skill covers Office x64 declarations");
            AssertContains(editing.BodyMarkdown, "VBE-equivalent source read-back", "skill describes normalized verification precisely");
            AssertContains(editing.BodyMarkdown, "does not prove VBA compilation or runtime behavior", "read-back is not overstated as functional validation");
            AssertContains(editing.BodyMarkdown, "common.vba_userform_authoring", "general VBA editing points to the focused UserForm profile");
        }

        private static void VbaReadLinesReturnsExactRange()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "one\ntwo\nthree\nfour";
                var result = executor.Execute(
                    Command("common.vba_read_module", "moduleName", "Module1", "startLine", 2, "lineCount", 2),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings(),
                    false,
                    false);
                var data = JObject.Parse(result.DataJson);
                AssertTrue(result.Success, "read lines result");
                AssertEqual("two\nthree", (string)data["code"], "exact line range");
                AssertEqual(2, (int)data["returnedLineCount"], "returned line count");
                AssertEqual(4, (int)data["totalLineCount"], "total line count");
                AssertEqual(VbaToolManifestParser.LiveCodeSha256(adapter.VbaModuleCode), (string)data["codeSha256"], "full module live hash");

                var firstTwo = executor.Execute(
                    Command("common.vba_read_module", "moduleName", "Module1", "lineCount", 2),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings(),
                    false,
                    false);
                AssertEqual("one\ntwo", (string)JObject.Parse(firstTwo.DataJson)["code"],
                    "lineCount alone selects a bounded range from line one");

                var removed = executor.Execute(
                    Command("excel.vba_read_lines", "moduleName", "Module1", "startLine", 3, "lineCount", 1),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings(),
                    false,
                    false);
                AssertEqual("unknown_tool", removed.ErrorCode, "removed range-read id is rejected");

                adapter.VbaModuleCode = string.Join("\n", Enumerable.Range(1, 250).Select(index => "line" + index).ToArray());
                var wholeCommand = Command("common.vba_read_module", "moduleName", "Module1");
                wholeCommand.Arguments["startLine"] = null;
                wholeCommand.Arguments["lineCount"] = null;
                wholeCommand.Arguments["maxChars"] = null;
                var whole = executor.Execute(
                    wholeCommand,
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings(),
                    false,
                    false);
                AssertTrue(whole.Success, "whole read accepts nullable strict-schema optionals");
                AssertContains((string)JObject.Parse(whole.DataJson)["code"], "line250", "default read mode returns the whole bounded module, not 200 lines");
            });
        }

        private static void VbaPatchRejectsAmbiguousExactSource()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "Sub One()\nEnd Sub\nSub Two()\nEnd Sub";
                var result = executor.Execute(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["op"] = "replace",
                            ["find"] = "End Sub",
                            ["text"] = "Debug.Print 1\nEnd Sub"
                        })),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = false },
                    false,
                    false);
                AssertTrue(!result.Success, "ambiguous exact block rejected");
                AssertEqual("vba_patch_ambiguous", result.ErrorCode, "ambiguous exact block error");
                AssertTrue(!string.Equals("waiting_confirmation", result.Status, StringComparison.OrdinalIgnoreCase), "ambiguous patch fails before confirmation");
                AssertContains(result.Message, "surrounding source", "ambiguous exact block recovery guidance");
                AssertTrue(adapter.VbaModuleCode.IndexOf("Debug.Print", StringComparison.Ordinal) < 0, "module unchanged");
            });
        }

        private static void VbaExactPatchPreservesBoundaryNewlines()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "A\nB\nC";
                var result = executor.Execute(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["op"] = "replace",
                            ["find"] = "B",
                            ["text"] = "X\n"
                        })),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);
                AssertTrue(result.Success, "exact newline patch result");
                AssertEqual("A\nX\n\nC", adapter.VbaModuleCode,
                    "runtime preserves the newline explicitly supplied inside replacement text");
            });

            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "A\nB\n\nC";
                var result = executor.Execute(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["op"] = "replace",
                            ["find"] = "B\n",
                            ["text"] = "X"
                        })),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);
                AssertTrue(result.Success, "exact source may include a blank-line boundary");
                AssertEqual("A\nX\nC", adapter.VbaModuleCode, "only the exact matched bytes are replaced");
            });

            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "P\r\nA\r\nB\r\nS";
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var parsed = new AgentResponseParser().Parse(
                    "{\"message\":\"patch\",\"tool_calls\":[{\"id\":\"call_vba\",\"name\":\"common.vba_apply_patch\",\"arguments\":{\"moduleName\":\"Module1\",\"patch\":[{\"op\":\"replace\",\"find\":\"A\\nB\",\"text\":\"\\nA\\n\\nB\\n\"}]}}]}",
                    tools);
                AssertTrue(parsed.Success, "raw model JSON with escaped newlines parses");
                var result = executor.Execute(
                    AgentJsonProtocol.ToCommand(parsed.Response.ToolCalls[0]),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);
                AssertTrue(result.Success, "model-originated exact patch executes");
                AssertEqual("P\r\n\r\nA\r\n\r\nB\r\n\r\nS", adapter.VbaModuleCode,
                    "JSON normalization preserves leading, internal, and trailing newlines and only converts them to CRLF");
            });
        }

        private static void VbaWriteRejectsHiddenControlCharacters()
        {
            var document = new FakeVbaDocumentObject();
            var component = document.VBProject.VBComponents.Seed("Module1", "Sub Main()\nEnd Sub");
            var result = VbaProjectSupport.ReplaceModule(document, "Module1", "\uFEFFSub Changed()\nEnd Sub", false);
            AssertTrue(!result.Success, "hidden BOM rejected");
            AssertEqual("vba_code_invalid", result.ErrorCode, "hidden BOM error code");
            AssertContains(component.CodeModule.Code, "Sub Main", "invalid write leaves code unchanged");

            var rawControl = VbaProjectSupport.ReplaceModule(document, "Module1", "Sub Changed()\nDebug.Print \"a\u000bb\"\nEnd Sub", false);
            AssertTrue(!rawControl.Success, "raw control character rejected");
            AssertContains(rawControl.Message, "U+000B", "control character code reported");
            AssertContains(rawControl.Message, "ChrW$(11)", "control character fix explained");

            var joinedProcedures = VbaProjectSupport.ReplaceModule(
                document,
                "Module1",
                "Public Function One() As Long\nOne = 1\nEnd FunctionPublic Function Two() As Long\nTwo = 2\nEnd Function",
                false);
            AssertTrue(!joinedProcedures.Success, "joined procedures rejected");
            AssertContains(joinedProcedures.Message, "join a block terminator", "joined procedure diagnostic");
            AssertContains(component.CodeModule.Code, "Sub Main", "joined procedure write leaves code unchanged");

            var commentText = VbaProjectSupport.ReplaceModule(
                document,
                "Module1",
                "Sub Main()\nRem End FunctionPublic Function is diagnostic text\nEnd Sub",
                false);
            AssertTrue(commentText.Success, "Rem comment does not trigger joined procedure guard");

            var cleared = VbaProjectSupport.ReplaceModule(document, "Module1", string.Empty, false);
            AssertTrue(cleared.Success, "existing module can be cleared");
            AssertEqual(string.Empty, component.CodeModule.Code, "module cleared");
        }

        private static void VbaCustomMacroFailureCleansSession()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var code =
                    "Option Explicit\n" +
                    "' <RNAssistantTool>\n" +
                    "' {\"protocolVersion\":1,\"id\":\"excel.custom_vba\",\"name\":\"Custom VBA\",\"description\":\"Test tool\",\"host\":\"Excel\",\"packageVersion\":\"1.0.0\",\"entryPoint\":\"Main\",\"components\":[\"RNA_CustomVba\"],\"argumentOrder\":[\"value\"],\"parameters\":{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"description\":\"Test value.\"}},\"required\":[\"value\"],\"additionalProperties\":false},\"mutatesDocument\":true,\"agentCanRun\":false,\"requiresConfirmation\":true}\n" +
                    "' </RNAssistantTool>\n" +
                    "Public Function Main(ByVal value As String) As String\n" +
                    "    Main = value\n" +
                    "End Function";
                var tool = new ToolDefinition
                {
                    Id = "excel.custom_vba",
                    Host = "Excel",
                    Name = "Custom VBA",
                    Executor = "vba",
                    Code = code,
                    ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"description\":\"Test value.\"}},\"required\":[\"value\"],\"additionalProperties\":false}",
                    Enabled = true,
                    BuiltIn = false,
                    MutatesDocument = true,
                    RequiresConfirmation = true,
                    RiskLevel = 3
                };
                adapter.QueueResult("excel.run_macro", ToolResult.Fail("macro failed", null, "macro_failed", true));
                var command = Command(tool.Id, "value", "test");
                var tools = adapter.GetBuiltInTools().Concat(new[] { tool }).ToList();

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "custom macro result");
                AssertEqual("failed", result.Status, "custom macro failure status");
                AssertEqual(true, result.Retryable, "custom macro retryable");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_CustomVba"), "temporary module cleaned after failure");
                AssertContains(result.DataJson, "sessionInstalled", "session lifecycle recorded");
            });
        }

        private static void VbaFailedModuleWriteRestoresCode()
        {
            var document = new FakeVbaDocumentObject();
            var component = document.VBProject.VBComponents.Seed("Module1", "Sub Original()\nEnd Sub");
            component.CodeModule.FailNextAdd = true;

            try
            {
                VbaProjectSupport.ReplaceModule(document, "Module1", "Sub Changed()\nEnd Sub", false);
                throw new InvalidOperationException("failed VBA replacement was accepted");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "original code was restored", "atomic replacement diagnostic");
            }

            AssertEqual(
                VbaToolManifestParser.NormalizeLiveCode("Sub Original()\nEnd Sub"),
                VbaToolManifestParser.NormalizeLiveCode(component.CodeModule.Code),
                "original code restored");

            var newDocument = new FakeVbaDocumentObject();
            newDocument.VBProject.VBComponents.FailNextAddedModuleWrite = true;
            try
            {
                VbaProjectSupport.ReplaceModule(newDocument, "NewModule", "Sub Main()\nEnd Sub", true);
                throw new InvalidOperationException("failed new VBA module was accepted");
            }
            catch (InvalidOperationException ex)
            {
                AssertContains(ex.Message, "incomplete module was removed", "new module cleanup diagnostic");
            }
            AssertEqual(0, newDocument.VBProject.VBComponents.Count, "incomplete module removed");
        }

        private static void VbaReadBackRejectsWriteDrift()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"old\"\nEnd Sub";
                adapter.QueueResult("excel.vba_replace_module", ToolResult.Ok("scripted success without write"));
                var backupStore = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var command = Command(
                    executor.VbaToolId("vba_apply_patch"),
                    "moduleName", "Module1",
                    "patch", new JArray(new JObject
                    {
                        ["op"] = "replace",
                        ["find"] = "\"old\"",
                        ["text"] = "\"new\""
                    }));

                var result = executor.Execute(command, adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "write drift is not reported as success");
                AssertEqual("partial_failure", result.Status, "write drift status");
                AssertEqual("vba_patch_verify_mismatch", result.ErrorCode, "write drift error code");
                AssertContains(result.DataJson, "expectedCodeSha256", "expected hash returned");
                AssertContains(result.DataJson, "actualCodeSha256", "actual hash returned");
                AssertEqual(1, backupStore.List("Excel", "doc").Count, "rollback backup retained");
            });
        }

        private static void VbaReadBackRejectsDeleteDrift()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Main()\nEnd Sub";
                adapter.QueueResult("excel.vba_delete_module_internal", ToolResult.Ok("scripted success without delete"));
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths));
                var command = Command(
                    executor.VbaToolId("vba_delete_module"),
                    "moduleName", "Module1");

                var result = executor.Execute(command, adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "delete drift is not reported as success");
                AssertEqual("vba_delete_verify_failed", result.ErrorCode, "delete drift error code");
                AssertContains(adapter.VbaModuleCode, "Sub Main", "module remains visible");
            });
        }

        private static void VbaRestoreAppliesBackup()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Current()\nEnd Sub";
                var backupStore = new VbaJournalStore(paths);
                var backup = backupStore.Save("Excel", "doc", "Harness.xlsx", "Module1", "StdModule", "Sub Restored()\nEnd Sub");
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var command = Command(executor.VbaToolId("vba_restore_backup"), "backupId", backup.BackupId, "moduleName", "Module1");
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();

                var listedBackups = executor.Execute(
                    Command(executor.VbaToolId("vba_list_backups")),
                    tools,
                    new AppSettings(),
                    false,
                    false);
                var listedData = JObject.Parse(listedBackups.DataJson ?? "{}");
                var listedBackup = ((JArray)listedData["backups"]).OfType<JObject>().Single();
                AssertEqual(backup.BackupId, (string)listedBackup["backupId"], "backup metadata exposes restore id");
                AssertTrue(listedBackup["code"] == null, "backup listing does not duplicate source code into model context");

                var missingSelector = executor.Execute(
                    Command(executor.VbaToolId("vba_restore_backup")),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);
                AssertEqual("invalid_arguments", missingSelector.ErrorCode, "restore requires an explicit backup or module selector");

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "restore result");
                AssertContains(adapter.VbaModuleCode, "Restored", "restored module code");
                AssertEqual(2, backupStore.List("Excel", "doc").Count, "restore preserves current version as backup");

                var classBackup = backupStore.Save("Excel", "doc", "Harness.xlsx", "RestoredClass", "ClassModule", "Option Explicit\nPublic Value As String");
                var classRestore = executor.Execute(
                    Command(executor.VbaToolId("vba_restore_backup"), "backupId", classBackup.BackupId),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);
                AssertTrue(classRestore.Success, "missing class module restore result");
                var modules = executor.Execute(Command(executor.VbaToolId("vba_read_module")), tools, new AppSettings(), false, false);
                var restoredClass = (JObject.Parse(modules.DataJson ?? "{}")["modules"] as JArray ?? new JArray())
                    .OfType<JObject>()
                    .First(item => string.Equals((string)item["name"], "RestoredClass", StringComparison.OrdinalIgnoreCase));
                AssertEqual("ClassModule", (string)restoredClass["type"], "restore preserves class module type");
            });
        }

        private static void VbaRestorePinsBackupBeforeConfirmation()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Current()\nEnd Sub";
                var backupStore = new VbaJournalStore(paths);
                var selected = backupStore.Save("Excel", "doc", "Harness.xlsx", "Module1", "StdModule", "Sub Selected()\nEnd Sub");
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var session = NewSession(adapter);
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();
                var command = Command("common.vba_restore_backup", "moduleName", "Module1");
                var waiting = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = false }, false, false, session);
                AssertEqual("waiting_confirmation", waiting.Status, "restore waits for confirmation");
                AssertEqual(selected.BackupId, Convert.ToString(command.Arguments["backupId"]), "latest backup is resolved to an exact id before confirmation");
                AssertContains(waiting.DataJson, selected.BackupId, "restore confirmation identifies the pinned backup");
                AssertTrue(waiting.DataJson.IndexOf("Sub Selected", StringComparison.Ordinal) < 0,
                    "restore confirmation preview does not duplicate backup source");

                backupStore.Save("Excel", "doc", "Harness.xlsx", "Module1", "StdModule", "Sub Newer()\nEnd Sub");
                var restored = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);

                AssertTrue(restored.Success, "pinned restore succeeds");
                AssertContains(adapter.VbaModuleCode, "Selected", "confirmation restores the originally selected backup");
                AssertTrue(adapter.VbaModuleCode.IndexOf("Newer", StringComparison.Ordinal) < 0, "newer backup does not replace confirmed target");
            });
        }

        private static void VbaJournalRecoversTailAndRejectsCorruption()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new VbaJournalStore(paths);
                var backup = store.Save("Excel", "doc", "Harness.xlsx", "Module1", "StdModule", "Sub Main()\nEnd Sub");
                var directory = Path.Combine(paths.VbaJournalDirectory, AppDataPaths.SafeFileName("Excel|doc"));
                var journal = Path.Combine(directory, "mutations.events.jsonl");
                File.AppendAllText(journal, "{\"SchemaVersion\":");
                var second = store.Save("Excel", "doc", "Harness.xlsx", "Module2", "StdModule", "Sub Two()\nEnd Sub");

                var backups = store.List("Excel", "doc");

                AssertEqual(2, backups.Count, "incomplete final record is removed before append");
                AssertTrue(backups.Any(item => item.BackupId == backup.BackupId), "first backup survives tail recovery");
                AssertTrue(backups.Any(item => item.BackupId == second.BackupId), "second backup is appended after recovery");
                AssertEqual(2, store.ReadEvents("Excel", "doc").Count, "journal sequence remains contiguous");

                var lines = File.ReadAllLines(journal);
                var unknown = JObject.Parse(lines[0]);
                unknown["UnhashedExtension"] = "must-not-be-ignored";
                var unknownLines = lines.ToArray();
                unknownLines[0] = unknown.ToString(Formatting.None);
                File.WriteAllLines(journal, unknownLines);
                try
                {
                    store.List("Excel", "doc");
                    throw new InvalidOperationException("VBA journal with an unknown field was accepted");
                }
                catch (VbaJournalException)
                {
                }

                var tampered = JObject.Parse(lines[0]);
                tampered["Data"]["ModuleName"] = "Tampered";
                lines[0] = tampered.ToString(Formatting.None);
                File.WriteAllLines(journal, lines);
                try
                {
                    store.List("Excel", "doc");
                    throw new InvalidOperationException("tampered VBA journal was accepted");
                }
                catch (VbaJournalException)
                {
                }
            });
        }

        private static void VbaJournalRecordsMutationAndCorrelation()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                const string before = "Sub Main()\nDebug.Print \"journal-before-marker\"\nEnd Sub";
                const string after = "Sub Main()\nDebug.Print \"journal-after-marker\"\nEnd Sub";
                var adapter = new FakeOfficeAdapter { VbaModuleCode = before };
                var store = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, store, new SkillStore(paths));
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { RunId = "run-vba", TurnId = "turn-vba" };
                var command = Command("common.vba_write_module", "moduleName", "Module1", "code", after);
                command.ToolCallId = "call-vba";
                command.RuntimeStepId = "step-vba";

                var result = executor.Execute(
                    command,
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);

                AssertTrue(result.Success, "journaled mutation succeeds");
                AssertContains(result.DataJson, "mutationId", "tool result exposes mutation correlation");
                var record = store.ListMutations("Excel", "doc").Single();
                AssertEqual("write", record.Prepared.Operation, "prepared operation");
                AssertEqual(session.Id, record.Prepared.SessionId, "prepared chat id");
                AssertEqual("run-vba", record.Prepared.RunId, "prepared run id");
                AssertEqual("turn-vba", record.Prepared.TurnId, "prepared turn id");
                AssertEqual("step-vba", record.Prepared.StepId, "prepared step id");
                AssertEqual("call-vba", record.Prepared.ToolCallId, "prepared tool call id");
                AssertTrue(record.Prepared.BeforeCodeReference != null, "prepared before CAS reference");
                AssertTrue(record.Prepared.IntendedAfterCodeReference != null, "prepared after CAS reference");
                AssertEqual(VbaMutationStatuses.Committed, record.Terminal.Status, "terminal mutation status");

                var metadata = store.List("Excel", "doc").Single();
                AssertTrue(metadata.Code == null, "backup projection does not hydrate source");
                AssertEqual(record.Prepared.BackupId, metadata.BackupId, "backup is derived from prepared record");
                AssertEqual(before, store.Find("Excel", "doc", metadata.BackupId, null).Code, "backup hydrates from CAS on demand");
                var journal = Path.Combine(paths.VbaJournalDirectory, AppDataPaths.SafeFileName("Excel|doc"), "mutations.events.jsonl");
                var journalText = File.ReadAllText(journal);
                AssertTrue(journalText.IndexOf("journal-before-marker", StringComparison.Ordinal) < 0, "before source is absent from journal");
                AssertTrue(journalText.IndexOf("journal-after-marker", StringComparison.Ordinal) < 0, "after source is absent from journal");
            });
        }

        private static void VbaMutationDiagnosticsPaginateAndHydrateDiffs()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new VbaJournalStore(paths);
                var module = store.PrepareMutation(new VbaMutationPreparation
                {
                    Operation = "write",
                    Host = "Excel",
                    DocumentKey = "doc",
                    DocumentTitle = "Harness.xlsx",
                    ModuleName = "Module1",
                    ComponentType = "StdModule",
                    BeforeExists = true,
                    IntendedAfterExists = true,
                    SessionId = "chat-module",
                    RunId = "run-module",
                    ToolCallId = "call-module"
                }, "Sub BeforeModule()\nEnd Sub", "Sub AfterModule()\nEnd Sub");
                store.CompleteMutation(
                    "Excel", "doc", module.MutationId, VbaMutationStatuses.Committed, true,
                    module.IntendedAfterCodeSha256, module.IntendedAfterComparableCodeSha256, null, "module committed");

                var package = store.PreparePackageMutation(new VbaPackageMutationPreparation
                {
                    Operation = "install",
                    PackageId = "diagnostics-package",
                    PackageVersion = "1.0.0",
                    RetainBackups = true,
                    Host = "Excel",
                    DocumentKey = "doc",
                    DocumentTitle = "Harness.xlsx",
                    SessionId = "chat-package",
                    RunId = "run-package",
                    ToolCallId = "call-package",
                    Components = new List<VbaPackageMutationComponent>
                    {
                        new VbaPackageMutationComponent
                        {
                            ModuleName = "PackageModule",
                            BeforeExists = true,
                            BeforeComponentType = "StdModule",
                            BeforeCode = "Sub PackageBefore()\nEnd Sub",
                            IntendedAfterExists = true,
                            IntendedAfterComponentType = "StdModule",
                            IntendedAfterCode = "Sub PackageAfter()\nEnd Sub"
                        }
                    }
                });
                store.CompletePackageMutation(
                    "Excel", "doc", package.MutationId, VbaMutationStatuses.Committed,
                    new[]
                    {
                        new VbaPackageMutationComponentAssessment
                        {
                            ModuleName = "PackageModule",
                            ActualExists = true,
                            ActualComponentType = "StdModule",
                            ActualCodeSha256 = package.Components[0].IntendedAfterCodeSha256,
                            MatchesIntendedAfter = true
                        }
                    }, null, "package committed");

                var firstPage = store.QueryMutations("Excel", "doc", new VbaMutationQueryRequest { PageSize = 1 });
                AssertEqual(2, firstPage.TotalRows, "query projects module and package records");
                AssertEqual(package.MutationId, firstPage.Rows.Single().MutationId, "query orders newest mutation first");
                AssertTrue(firstPage.HasMore && !string.IsNullOrWhiteSpace(firstPage.NextCursor), "query exposes snapshot cursor");

                store.PrepareMutation(new VbaMutationPreparation
                {
                    Operation = "delete",
                    Host = "Excel",
                    DocumentKey = "doc",
                    ModuleName = "LaterModule",
                    ComponentType = "StdModule",
                    BeforeExists = true,
                    IntendedAfterExists = false
                }, "Sub Later()\nEnd Sub", null);
                var secondPage = store.QueryMutations("Excel", "doc", new VbaMutationQueryRequest
                {
                    PageSize = 1,
                    Cursor = firstPage.NextCursor
                });
                AssertEqual(2, secondPage.TotalRows, "cursor keeps the original journal snapshot");
                AssertEqual(module.MutationId, secondPage.Rows.Single().MutationId, "older page is stable after append");

                var filtered = store.QueryMutations("Excel", "doc", new VbaMutationQueryRequest
                {
                    Kind = VbaMutationKinds.Package,
                    Search = "PackageModule",
                    RunId = "run-package"
                });
                AssertEqual(1, filtered.TotalMatches, "package metadata and correlation are searchable");
                AssertEqual(2, filtered.Rows[0].SourceEventSeqs.Count, "query row retains both source events");

                var detail = store.GetMutationDetail("Excel", "doc", package.MutationId);
                var component = detail.Components.Single();
                AssertContains(component.BeforeCode, "PackageBefore", "detail lazily hydrates before source");
                AssertContains(component.IntendedAfterCode, "PackageAfter", "detail lazily hydrates intended source");
                AssertTrue(component.CanRestore && !string.IsNullOrWhiteSpace(component.BackupId), "retained package before state is restorable");
                AssertEqual(true, component.MatchesIntendedAfter, "terminal component assessment is exposed");
            });
        }

        private static void VbaJournalReconcilesInterruptedMutations()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                const string before = "Sub BeforeState()\nEnd Sub";
                const string after = "Sub AfterState()\nEnd Sub";
                var adapter = new FakeOfficeAdapter { VbaModuleCode = after };
                var store = new VbaJournalStore(paths);
                var applied = store.PrepareMutation(new VbaMutationPreparation
                {
                    Operation = "write",
                    Host = "Excel",
                    DocumentKey = "doc",
                    DocumentTitle = "Harness.xlsx",
                    ModuleName = "Module1",
                    ComponentType = "StdModule",
                    BeforeExists = true,
                    IntendedAfterExists = true
                }, before, after);
                var executor = new OfficeToolExecutor(adapter, store, new SkillStore(paths));
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();

                var list = executor.Execute(Command("common.vba_list_backups"), tools, new AppSettings(), false, false);

                AssertTrue(list.Success, "safe VBA access continues after reconciliation");
                AssertEqual(VbaMutationStatuses.Committed,
                    store.ListMutations("Excel", "doc").Single(item => item.Prepared.MutationId == applied.MutationId).Terminal.Status,
                    "live intended state reconciles as committed");
                AssertEqual(0, adapter.Executed.Count(item => item.ToolId.EndsWith(".vba_replace_module", StringComparison.OrdinalIgnoreCase)),
                    "reconciliation never replays a write");

                var notApplied = store.PrepareMutation(new VbaMutationPreparation
                {
                    Operation = "write",
                    Host = "Excel",
                    DocumentKey = "doc",
                    DocumentTitle = "Harness.xlsx",
                    ModuleName = "Module1",
                    ComponentType = "StdModule",
                    BeforeExists = true,
                    IntendedAfterExists = true
                }, after, "Sub LaterState()\nEnd Sub");
                executor.Execute(Command("common.vba_list_backups"), tools, new AppSettings(), false, false);
                AssertEqual(VbaMutationStatuses.NotApplied,
                    store.ListMutations("Excel", "doc").Single(item => item.Prepared.MutationId == notApplied.MutationId).Terminal.Status,
                    "live before state reconciles as not applied");

                var unknown = store.PrepareMutation(new VbaMutationPreparation
                {
                    Operation = "write",
                    Host = "Excel",
                    DocumentKey = "doc",
                    DocumentTitle = "Harness.xlsx",
                    ModuleName = "Module1",
                    ComponentType = "StdModule",
                    BeforeExists = true,
                    IntendedAfterExists = true
                }, after, "Sub UnknownTarget()\nEnd Sub");
                adapter.QueueResult("excel.vba_read_module", ToolResult.Fail("VBA access denied.", null, "vba_access_error", false));
                executor.Execute(Command("common.vba_list_backups"), tools, new AppSettings(), false, false);
                AssertEqual(VbaMutationStatuses.Unknown,
                    store.ListMutations("Excel", "doc").Single(item => item.Prepared.MutationId == unknown.MutationId).Terminal.Status,
                    "unreadable live state reconciles as unknown");
            });
        }

        private static void VbaReconciliationWaitsForActiveMutation()
        {
            WithTempPaths(paths =>
            {
                const string before = "Sub Main()\nDebug.Print \"before\"\nEnd Sub";
                const string after = "Sub Main()\nDebug.Print \"after\"\nEnd Sub";
                var adapter = new FakeOfficeAdapter { VbaModuleCode = before };
                var enteredWrite = new ManualResetEventSlim(false);
                var releaseWrite = new ManualResetEventSlim(false);
                adapter.VbaWriteTransform = code =>
                {
                    enteredWrite.Set();
                    if (!releaseWrite.Wait(5000)) throw new InvalidOperationException("test write was not released");
                    return code;
                };
                var journal = new VbaJournalStore(paths);
                var first = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));
                var second = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));
                var tools = adapter.GetBuiltInTools().Concat(first.GetControllerTools()).ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };
                var session = NewSession(adapter);

                var writeTask = Task.Run(() => first.Execute(
                    Command("common.vba_write_module", "moduleName", "Module1", "code", after),
                    tools, settings, false, false, session));
                AssertTrue(enteredWrite.Wait(5000), "first mutation reached the active effect window");
                var readStarted = new ManualResetEventSlim(false);
                var readTask = Task.Run(() =>
                {
                    readStarted.Set();
                    return second.Execute(
                        Command("common.vba_read_module", "moduleName", "Module1"),
                        tools, settings, false, false, session);
                });
                AssertTrue(readStarted.Wait(5000), "second VBA access started");

                var prematureTerminal = false;
                var deadline = DateTime.UtcNow.AddMilliseconds(500);
                while (DateTime.UtcNow < deadline)
                {
                    var record = journal.ListMutations(adapter.HostName, adapter.DocumentKey).Single();
                    if (record.Terminal != null)
                    {
                        prematureTerminal = true;
                        break;
                    }
                    Thread.Sleep(10);
                }
                releaseWrite.Set();
                var write = writeTask.GetAwaiter().GetResult();
                var read = readTask.GetAwaiter().GetResult();
                var terminal = journal.ListMutations(adapter.HostName, adapter.DocumentKey).Single().Terminal;

                AssertTrue(write.Success && read.Success, "both VBA operations complete");
                AssertEqual(VbaMutationStatuses.Committed, terminal.Status,
                    "journal terminal agrees with the verified committed effect");
                AssertTrue(!prematureTerminal, "reconciliation does not close a mutation that owns the document lock");
            });
        }

        private static void VbaJournalUsesHistoryProtection()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                const string marker = "PRIVATE_VBA_SOURCE_6c91";
                var salt = Enumerable.Range(71, 32).Select(value => (byte)value).ToArray();
                var protector = new StorageProtector(
                    HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256,
                    "portable VBA history secret",
                    salt);
                var store = new VbaJournalStore(paths, () => protector);
                var backup = store.Save("Excel", "doc", "Harness.xlsx", "Module1", "StdModule", "Sub " + marker + "()\nEnd Sub");
                var journal = Path.Combine(paths.VbaJournalDirectory, AppDataPaths.SafeFileName("Excel|doc"), "mutations.events.jsonl");
                var rawJournal = File.ReadAllText(journal);

                AssertContains(rawJournal, "EncryptedData", "VBA journal event data is encrypted");
                AssertTrue(rawJournal.IndexOf(marker, StringComparison.Ordinal) < 0, "VBA source is absent from journal plaintext");
                AssertTrue(store.ReadEvents("Excel", "doc").All(item => item.HashAlgorithm == HistoryIntegrityModes.HmacSha256),
                    "VBA journal uses selected HMAC mode");
                AssertContains(store.Find("Excel", "doc", backup.BackupId, null).Code, marker, "protected VBA CAS hydrates");
                foreach (var blob in Directory.GetFiles(paths.ChatBlobDirectory, "*.blob", SearchOption.AllDirectories))
                {
                    AssertTrue(StorageProtector.IsProtectedPayload(File.ReadAllBytes(blob)), "VBA CAS blob is encrypted");
                }

                var wrong = new StorageProtector(
                    HistoryIntegrityModes.HmacSha256,
                    HistoryEncryptionModes.Aes256CbcHmacSha256,
                    "wrong VBA history secret",
                    salt);
                try
                {
                    new VbaJournalStore(paths, () => wrong).ReadEvents("Excel", "doc");
                    throw new InvalidOperationException("wrong VBA history key was accepted");
                }
                catch (VbaJournalException)
                {
                }
            });
        }

        private static void ContextUsageEstimatorCountsPromptAndSession()
        {
            var settings = new AppSettings { ContextWindowOverrideTokens = 8000 };
            var promptUsage = JObject.FromObject(ContextUsageEstimator.FromPrompt(new[]
            {
                new ChatMessage { Role = "system", Content = "abc" },
                new ChatMessage { Role = "user", Content = "defg" }
            }, settings, 12));
            AssertEqual(7, promptUsage["usedChars"].Value<int>(), "prompt used chars");
            AssertEqual(12, promptUsage["usedTokens"].Value<int>(), "prompt used tokens");
            AssertEqual(4928, promptUsage["limitTokens"].Value<int>(), "prompt input token budget");
            AssertEqual(2, promptUsage["messageCount"].Value<int>(), "prompt message count");
            AssertTrue(promptUsage["actual"].Value<bool>(), "prompt actual");

            var estimatedJson = JObject.FromObject(ContextUsageEstimator.FromPrompt(
                new[] { new ChatMessage { Role = "user", Content = "hello" } },
                settings,
                null,
                new LlmRequestOptions
                {
                    ResponseFormat = LlmResponseFormats.JsonObject
                }));
            AssertTrue(estimatedJson["usedTokens"].Value<int>() > 0, "json response mode counts toward estimated request usage");

            var session = new ChatSession();
            session.Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = "hello",
                Attachments = new List<ChatAttachment>
                {
                    new ChatAttachment { Kind = "image", ExtractedCharCount = 10000 }
                }
            });
            session.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = "internal activity",
                Activity = new ChatActivity { Kind = "tool" }
            });
            session.Context.Notes.Add(new ContextNote { Text = "selection!" });
            var sessionUsage = JObject.FromObject(ContextUsageEstimator.FromSession(session, settings));
            AssertTrue(sessionUsage["usedChars"].Value<int>() < 500,
                "session usage counts historical attachment references, not extracted bodies");
            AssertTrue(sessionUsage["usedTokens"].Value<int>() < 200,
                "session usage does not reserve historical image tokens");
            AssertEqual(1, sessionUsage["messageCount"].Value<int>(), "session message count");
            AssertTrue(!sessionUsage["actual"].Value<bool>(), "session actual");
        }
    }
}
