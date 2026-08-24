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
        private static void VbaReplaceTextBacksUpModule()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"old\"\nEnd Sub";
                var backupStore = new VbaBackupStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var session = NewSession(adapter);
                var command = new ToolCommand { ToolId = executor.VbaToolId("vba_replace_text") };
                command.Arguments["moduleName"] = "Module1";
                command.Arguments["find"] = "\"old\"";
                command.Arguments["replace"] = "\"new\"";

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
                AssertContains(backups[0].Code, "\"old\"", "backup code");
            });
        }

        private static void VbaConfirmedMutationRejectsStaleSnapshot()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"old\"\nEnd Sub";
                var backupStore = new VbaBackupStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var session = NewSession(adapter);
                var tools = adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList();

                var command = Command("common.vba_replace_text", "moduleName", "Module1", "find", "\"old\"", "replace", "\"new\"");
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
                    "common.vba_create_module",
                    "moduleName", "CreatedDuringConfirmation",
                    "componentType", "StdModule",
                    "code", "Sub Requested()\nEnd Sub");
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
                var backupStore = new VbaBackupStore(paths);
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
                var listed = executor.Execute(Command("common.vba_list_modules"), tools, new AppSettings(), false, false);
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

        private static void VbaDeleteNeedsNoPublicRead()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Main()\nEnd Sub";
                var backupStore = new VbaBackupStore(paths);
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

        private static void VbaGuardRejectsRuntimeDocumentSwitch()
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
                var blocked = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);

                AssertEqual("vba_snapshot_context_changed", blocked.ErrorCode, "runtime document switch invalidates the guard");
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
                var backupStore = new VbaBackupStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var command = new ToolCommand { ToolId = executor.VbaToolId("vba_apply_patch") };
                command.Arguments["moduleName"] = "Module2";
                command.Arguments["expectedCodeSha256"] = VbaToolManifestParser.LiveCodeSha256(adapter.GetVbaModuleCode("Module2"));
                command.Arguments["patch"] = new JArray
                {
                    new JObject
                    {
                        ["op"] = "replace",
                        ["find"] = "\"old\"",
                        ["text"] = "\"new\"",
                        ["pattern"] = null,
                        ["replace"] = null,
                        ["startLine"] = null,
                        ["deleteCount"] = null
                    },
                    new JObject
                    {
                        ["op"] = "insertAfter",
                        ["find"] = "End Sub",
                        ["text"] = "Public Sub Added()\nEnd Sub"
                    }
                };

                var result = executor.Execute(command, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "patch result");
                AssertContains(adapter.GetVbaModuleCode("Module2"), "\"new\"", "module2 updated");
                AssertContains(adapter.GetVbaModuleCode("Module2"), "End Sub\nPublic Sub Added()", "insertAfter adds a safe line boundary");
                AssertTrue(adapter.GetVbaModuleCode("Module2").IndexOf("End SubPublic", StringComparison.Ordinal) < 0, "insertAfter does not concatenate procedures");
                AssertContains(adapter.GetVbaModuleCode("Module1"), "\"untouched\"", "module1 untouched");
                var backups = backupStore.List("Excel", "doc");
                AssertEqual(1, backups.Count, "backup count");
                AssertEqual("Module2", backups[0].ModuleName, "backup module");
                AssertContains(backups[0].Code, "\"old\"", "backup code");

                var malformed = new ToolCommand { ToolId = executor.VbaToolId("vba_apply_patch") };
                malformed.Arguments["moduleName"] = "Module2";
                malformed.Arguments["expectedCodeSha256"] = VbaToolManifestParser.LiveCodeSha256(adapter.GetVbaModuleCode("Module2"));
                malformed.Arguments["patch"] = "[{\"op\":\"replace\"}}trailing";
                var malformedResult = executor.Execute(malformed, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(!malformedResult.Success, "malformed patch rejected");
                AssertContains(malformedResult.Message, "$.patch must be a native JSON array", "malformed patch diagnostic");

                var emptyAnchor = Command(
                    "excel.vba_apply_patch",
                    "moduleName", "Module2",
                    "expectedCodeSha256", VbaToolManifestParser.LiveCodeSha256(adapter.GetVbaModuleCode("Module2")),
                    "patch", new JArray(new JObject { ["op"] = "insertBefore", ["find"] = string.Empty, ["text"] = "Debug.Print 1" }));
                var emptyAnchorResult = executor.Execute(emptyAnchor, new List<ToolDefinition>(adapter.GetBuiltInTools()), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(!emptyAnchorResult.Success, "empty insertion anchor rejected");
                AssertContains(emptyAnchorResult.Message, "shorter than minLength", "empty insertion anchor schema diagnostic");
            });
        }

        private static void VbaBackupFailureBlocksReplacement()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Original()\nEnd Sub";
                adapter.QueueResult("excel.vba_read_module", ToolResult.Ok("malformed read", "{}"));
                var executor = new OfficeToolExecutor(adapter, new VbaBackupStore(paths), new SkillStore(paths));
                var command = Command("excel.vba_replace_module", "moduleName", "Module1", "code", "Sub Changed()\nEnd Sub", "createIfMissing", false);

                var result = executor.Execute(command, adapter.GetBuiltInTools().ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "replacement blocked");
                AssertEqual("vba_backup_failed", result.ErrorCode, "backup failure code");
                AssertEqual(false, result.Retryable, "backup failure retryable");
                AssertEqual("Sub Original()\nEnd Sub", adapter.VbaModuleCode, "module unchanged");
                AssertEqual(1, adapter.Executed.Count, "only backup read executed");

                adapter.Executed.Clear();
                var create = Command("excel.vba_replace_module", "moduleName", "NewModule", "code", "Sub NewMacro()\nEnd Sub", "createIfMissing", true);
                var created = executor.Execute(create, adapter.GetBuiltInTools().ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(created.Success, "missing module can be created");
                AssertContains(adapter.GetVbaModuleCode("NewModule"), "NewMacro", "new module code");
            });
        }

        private static void VbaPatchRejectsLineOverrun()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "Sub Main()\nEnd Sub";
                var command = Command(
                    executor.VbaToolId("vba_apply_patch"),
                    "moduleName", "Module1",
                    "expectedCodeSha256", VbaToolManifestParser.LiveCodeSha256(adapter.VbaModuleCode),
                    "patch", "[{\"op\":\"replaceLines\",\"startLine\":2,\"deleteCount\":5,\"text\":\"End Sub\"}]");

                var result = executor.Execute(command, adapter.GetBuiltInTools().ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "line overrun rejected");
                AssertContains(result.Message, "past the end", "line overrun message");
                AssertEqual("Sub Main()\nEnd Sub", adapter.VbaModuleCode, "line overrun leaves module unchanged");
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
                var executor = new OfficeToolExecutor(adapter, new VbaBackupStore(paths), new SkillStore(paths));
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
                    "excel.vba_apply_patch",
                    "moduleName", "Module1",
                    "expectedCodeSha256", VbaToolManifestParser.LiveCodeSha256(adapter.VbaModuleCode),
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
                    Command("excel.vba_create_module", "moduleName", "UserForm2", "componentType", "MSForm", "code", "Option Explicit\n"),
                    tools,
                    settings,
                    false,
                    false);
                AssertTrue(publicCreate.Success, "public UserForm create succeeds");

                var code = adapter.GetVbaModuleCode("UserForm2");
                var publicEdit = executor.Execute(
                    Command(
                        "excel.vba_replace_text",
                        "moduleName", "UserForm2",
                        "expectedCodeSha256", VbaToolManifestParser.LiveCodeSha256(code),
                        "find", "Option Explicit",
                        "replace", "Option Explicit\nPrivate Sub UserForm_Activate()\nEnd Sub"),
                    tools,
                    settings,
                    false,
                    false);
                AssertTrue(publicEdit.Success, "public UserForm code edit succeeds");
                AssertContains(adapter.GetVbaModuleCode("UserForm2"), "UserForm_Activate", "public UserForm code changed");
            });
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

                var legacy = executor.Execute(
                    Command("excel.vba_read_lines", "moduleName", "Module1", "startLine", 3, "lineCount", 1),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings(),
                    false,
                    false);
                AssertEqual("three", (string)JObject.Parse(legacy.DataJson)["code"], "legacy range-read id remains compatible");

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

        private static void VbaPatchRejectsAmbiguousAnchors()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "Sub One()\nEnd Sub\nSub Two()\nEnd Sub";
                var result = executor.Execute(
                    Command(
                        "excel.vba_apply_patch",
                        "moduleName", "Module1",
                        "expectedCodeSha256", VbaToolManifestParser.LiveCodeSha256(adapter.VbaModuleCode),
                        "patch", "[{\"op\":\"insertBefore\",\"find\":\"End Sub\",\"text\":\"Debug.Print 1\\n\"}]"),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = false },
                    false,
                    false);
                AssertTrue(!result.Success, "ambiguous anchor rejected");
                AssertEqual("vba_patch_ambiguous", result.ErrorCode, "ambiguous anchor error");
                AssertTrue(!string.Equals("waiting_confirmation", result.Status, StringComparison.OrdinalIgnoreCase), "ambiguous patch fails before confirmation");
                AssertContains(result.Message, "replaceLines", "ambiguous anchor recovery guidance");
                AssertContains(result.Message, "do not bypass", "ambiguous anchor bypass guidance");
                AssertTrue(adapter.VbaModuleCode.IndexOf("Debug.Print", StringComparison.Ordinal) < 0, "module unchanged");
            });
        }

        private static void VbaLinePatchDoesNotInsertTrailingBlankLine()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "A\nB\nC";
                var result = executor.Execute(
                    Command(
                        "excel.vba_apply_patch",
                        "moduleName", "Module1",
                        "expectedCodeSha256", VbaToolManifestParser.LiveCodeSha256(adapter.VbaModuleCode),
                        "patch", "[{\"op\":\"replaceLines\",\"startLine\":2,\"deleteCount\":1,\"text\":\"X\\n\"}]"),
                    adapter.GetBuiltInTools().Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);
                AssertTrue(result.Success, "line patch result");
                AssertEqual("A\nX\nC", adapter.VbaModuleCode, "single text terminator does not add a blank line");
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
                var backupStore = new VbaBackupStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var command = Command(
                    executor.VbaToolId("vba_replace_text"),
                    "moduleName", "Module1",
                    "expectedCodeSha256", VbaToolManifestParser.LiveCodeSha256(adapter.VbaModuleCode),
                    "find", "\"old\"",
                    "replace", "\"new\"");

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
                var executor = new OfficeToolExecutor(adapter, new VbaBackupStore(paths), new SkillStore(paths));
                var command = Command(
                    executor.VbaToolId("vba_delete_module"),
                    "moduleName", "Module1",
                    "expectedCodeSha256", VbaToolManifestParser.LiveCodeSha256(adapter.VbaModuleCode));

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
                var backupStore = new VbaBackupStore(paths);
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
                var modules = executor.Execute(Command(executor.VbaToolId("vba_list_modules")), tools, new AppSettings(), false, false);
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
                var backupStore = new VbaBackupStore(paths);
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

        private static void VbaBackupStoreSkipsBrokenFiles()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new VbaBackupStore(paths);
                var backup = store.Save("Excel", "doc", "Harness.xlsx", "Module1", "StdModule", "Sub Main()\nEnd Sub");
                var directory = Path.Combine(paths.VbaBackupDirectory, AppDataPaths.SafeFileName("Excel|doc"));
                File.WriteAllText(Path.Combine(directory, "broken.json"), "{ broken");

                var backups = store.List("Excel", "doc");

                AssertEqual(1, backups.Count, "backup count");
                AssertEqual(backup.BackupId, backups[0].BackupId, "backup id");
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
            AssertEqual(10015, sessionUsage["usedChars"].Value<int>(), "session used chars");
            AssertEqual(5012, sessionUsage["usedTokens"].Value<int>(), "session used tokens");
            AssertEqual(1, sessionUsage["messageCount"].Value<int>(), "session message count");
            AssertTrue(!sessionUsage["actual"].Value<bool>(), "session actual");
        }
    }
}
