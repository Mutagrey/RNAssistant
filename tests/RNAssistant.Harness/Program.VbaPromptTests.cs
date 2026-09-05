using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Llm;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Storage;
using RNAssistant.Office;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.Vba;
using RNAssistant.Office.WebView;
using RNAssistant.Desktop;
using RNAssistant.OfficeHosts;
using RNAssistant.OfficeHosts.Vba;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void VbaReaderValidatesTypedSnapshots()
        {
            var adapter = new FakeOfficeAdapter();
            var source = "Option Explicit\r\nPublic Sub ReadMe()\r\nEnd Sub";
            adapter.SetVbaModule("ReaderModule", source, "ClassModule");
            var reader = new VbaReader(adapter.VbaHostBackend);

            VbaModuleState module;
            ToolRunResult error;
            AssertTrue(reader.TryReadModule("ReaderModule", 1000000, out module, out error),
                "reader accepts a complete typed module snapshot");
            AssertEqual(source, module.Code, "reader preserves exact VBA source bytes");
            AssertEqual("ClassModule", module.ComponentType, "reader carries component type");
            AssertEqual(VbaTextCanonicalizer.LiveCodeLineCount(source), module.LineCount,
                "reader carries live line metadata");

            adapter.QueueVbaModuleSnapshot(new RNAssistant.Office.Domains.Vba.VbaModuleSnapshot
            {
                Name = "ReaderModule",
                ComponentType = "ClassModule",
                Code = null
            });
            AssertTrue(!reader.TryReadModule("ReaderModule", 1000000, out module, out error),
                "reader rejects a malformed typed field");
            AssertEqual("vba_read_invalid", error.ErrorCode, "malformed module has stable error code");

            foreach (var malformed in new[]
            {
                new RNAssistant.Office.Domains.Vba.VbaModuleSnapshot { Name = "OtherModule", Code = "x", ComponentType = "ClassModule" },
                new RNAssistant.Office.Domains.Vba.VbaModuleSnapshot { Name = "ReaderModule", Code = "x", ComponentType = "ClassModule", CodeSha256 = new string('0', 64) },
                new RNAssistant.Office.Domains.Vba.VbaModuleSnapshot { Name = "ReaderModule", Code = "x", ComponentType = "ClassModule", Truncated = true }
            })
            {
                adapter.QueueVbaModuleSnapshot(malformed);
                AssertTrue(!reader.TryReadModule("ReaderModule", 1000000, out module, out error),
                    "reader rejects target, hash and truncation inconsistencies");
                AssertEqual("vba_read_invalid", error.ErrorCode, "inconsistent module has stable error code");
            }

            adapter.QueueVbaModuleSnapshot(new RNAssistant.Office.Domains.Vba.VbaModuleSnapshot
            {
                Name = "ReaderModule",
                Code = "x\n...[truncated]",
                ComponentType = "ClassModule",
                Truncated = true
            });
            AssertTrue(!reader.TryReadModule("ReaderModule", 1000000, out module, out error),
                "mutation snapshot rejects truncated source");

            IReadOnlyList<VbaModuleState> project;
            foreach (var malformed in new[]
            {
                new RNAssistant.Office.Domains.Vba.VbaProjectSnapshot { Modules = null },
                new RNAssistant.Office.Domains.Vba.VbaProjectSnapshot { Modules = new RNAssistant.Office.Domains.Vba.VbaProjectComponentSnapshot[] { null } },
                new RNAssistant.Office.Domains.Vba.VbaProjectSnapshot { Modules = new[] { new RNAssistant.Office.Domains.Vba.VbaProjectComponentSnapshot() } },
                new RNAssistant.Office.Domains.Vba.VbaProjectSnapshot { Modules = new[]
                {
                    new RNAssistant.Office.Domains.Vba.VbaProjectComponentSnapshot { Name = "Module1", ComponentType = "StdModule" },
                    new RNAssistant.Office.Domains.Vba.VbaProjectComponentSnapshot { Name = "module1", ComponentType = "ClassModule" }
                } }
            })
            {
                adapter.QueueVbaProjectSnapshot(malformed);
                AssertTrue(!reader.TryReadProject(out project, out error),
                    "reader rejects malformed project snapshots");
                AssertEqual("vba_read_invalid", error.ErrorCode, "malformed project has stable error code");
            }

            adapter.QueueVbaProjectSnapshot(new RNAssistant.Office.Domains.Vba.VbaProjectSnapshot
            {
                Modules = new RNAssistant.Office.Domains.Vba.VbaProjectComponentSnapshot[0]
            });
            AssertTrue(reader.TryReadProject(out project, out error) && project.Count == 0,
                "reader distinguishes a valid empty project");

            var requestedName = "Sales report";
            var normalizedName = VbaReader.NormalizeModuleName(requestedName);
            adapter.SetVbaModule(normalizedName, "Sub Main()\nEnd Sub", "StdModule");
            var readsBefore = adapter.VbaBackendCalls.Count;
            ToolRunResult resource;
            AssertTrue(reader.TryReadResourceModule(requestedName, 1000, out module, out resource),
                "resource read falls back to the deterministic normalized name");
            AssertEqual(2, adapter.VbaBackendCalls.Count - readsBefore, "normalization fallback performs exactly two reads");
            AssertEqual(normalizedName, module.Name, "resource observation binds the resolved module name");

            adapter.QueueVbaModuleSnapshot(new RNAssistant.Office.Domains.Vba.VbaModuleSnapshot
            {
                Name = "ReaderModule",
                Code = null
            });
            AssertTrue(!reader.TryReadResourceModule("ReaderModule", 1000, out module, out resource) &&
                resource.ErrorCode == "vba_read_invalid",
                "resource read does not expose malformed successful backend data");
            AssertTrue(module == null, "malformed resource data creates no observation");
        }

        private static void VbaPublicToolsUseNativeRuntime()
        {
            WithTempExecutor(delegate(
                OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var publicTools = executor.GetControllerTools()
                    .Where(tool => tool.Id.StartsWith(
                        "common.vba_", StringComparison.Ordinal) ||
                        tool.Id == "common.office_run_macro")
                    .ToArray();
                AssertEqual(6, publicTools.Length,
                    "all public VBA and macro tools are present");
                foreach (var tool in publicTools)
                {
                    AssertTrue(NativeToolRuntimeAdapter.Owns(tool.Id),
                        tool.Id + " is owned by native ToolRuntime");
                    var binding = DirectToolBindingCatalog.Resolve(tool.Id);
                    AssertTrue(binding != null && binding.HandlerId.StartsWith(
                            "vba.public.", StringComparison.Ordinal) &&
                            binding.HandlerId.IndexOf(".intent.",
                                StringComparison.Ordinal) >= 0,
                        tool.Id + " has an exact non-legacy handler binding");
                    AssertTrue(tool.Policy != null &&
                            tool.Policy.RequiresConfirmation,
                        tool.Id + " carries source-owned confirmation policy");
                    AssertEqual(tool.Id == "common.office_run_macro"
                            ? ToolEffect.External : ToolEffect.Write,
                        tool.Policy.Effect,
                        tool.Id + " carries its exact effect kind");
                }

                var session = NewSession(adapter);
                var command = Command(
                    "common.office_run_macro",
                    "macroName", "Module1.Main",
                    "arguments", new JArray("value", 2, true));
                var pending = PrepareVbaNative(
                    executor, session, command);
                AssertEqual(ToolEffect.External,
                    pending.Record.Context.Policy.Policy.Effect,
                    "captured macro runtime policy preserves the external effect");
                AssertEqual(ToolExecutionOutcome.AwaitingConfirmation,
                    pending.Record.Outcome,
                    "macro preparation reaches native confirmation");
                var completed = ConfirmVbaNative(pending);
                AssertEqual(ToolExecutionOutcome.Unknown, completed.Outcome,
                    "arbitrary macro result never certifies its external effect");
                AssertTrue(completed.MayHaveDispatched,
                    "macro marks the exact backend dispatch boundary");
                AssertEqual(ToolEffectEvidence.Unknown,
                    completed.Evidence.Effect,
                    "macro carries unknown effect evidence");
                AssertEqual(1,
                    adapter.CountVbaCalls(FakeVbaOperation.RunMacro),
                    "macro backend dispatches exactly once");

                var manualWrite = executor.ExecuteManual(
                    Command("common.vba_write_module",
                        "moduleName", "ManualNative",
                        "code", "Sub ManualNativeRun()\nEnd Sub",
                        "mode", "createOnly"),
                    executor.GetControllerTools().ToList(),
                    new AppSettings { AutoConfirmToolActions = false },
                    false,
                    true,
                    session);
                AssertTrue(manualWrite.Success,
                    "authorized manual VBA execution prepares and consumes its native guard");
                AssertContains(adapter.GetVbaModuleCode("ManualNative"),
                    "ManualNativeRun",
                    "manual VBA execution reaches the typed mutation backend");
            });
        }

        private static void VbaSemanticIntentContractsAreStrict()
        {
            WithTempExecutor(delegate(
                OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tools = executor.GetControllerTools()
                    .Where(tool => VbaToolCatalog.Owns(tool.Id))
                    .ToArray();
                var write = JObject.Parse(tools.Single(tool =>
                    tool.Id == VbaToolCatalog.WriteModule).ArgumentSchemaJson);
                AssertTrue(write["anyOf"] == null &&
                    write.SelectToken("properties.newModuleName") == null &&
                    write.SelectToken("properties.mode.enum[3]") == null,
                    "write schema contains only whole-source intent");

                var rename = JObject.Parse(tools.Single(tool =>
                    tool.Id == VbaToolCatalog.RenameModule).ArgumentSchemaJson);
                AssertEqual(2, ((JObject)rename["properties"]).Properties().Count(),
                    "rename schema contains only two semantic names");
                AssertTrue(rename.SelectToken("properties.mode") == null,
                    "rename schema has no constant discriminator");

                var patch = JObject.Parse(tools.Single(tool =>
                    tool.Id == VbaToolCatalog.ApplyPatch).ArgumentSchemaJson);
                var hunk = (JObject)patch.SelectToken("properties.patch.items");
                AssertEqual(4, ((JObject)hunk["properties"]).Properties().Count(),
                    "VBA patch hunk contains replacement plus optional exact context");
                AssertTrue(hunk.SelectToken("properties.op") == null,
                    "fixed replace operation belongs to runtime");
                var editingSkill = BuiltInSkillProvider.GetSkills(adapter)
                    .Single(skill => skill.Id == "common.vba_code_editing")
                    .BodyMarkdown;
                AssertContains(editingSkill, "contextBefore",
                    "VBA editing skill teaches exact patch disambiguation");

                var restore = JObject.Parse(tools.Single(tool =>
                    tool.Id == VbaToolCatalog.RestoreBackup).ArgumentSchemaJson);
                AssertTrue(restore.SelectToken("properties.target") != null &&
                    restore.SelectToken("properties.moduleName") != null &&
                    restore.SelectToken("properties.backupId") == null,
                    "restore accepts readable target or latest-for-module intent");
                var promptRestore = ToolSchemaSupport.ForPrompt(restore);
                var promptRestoreVariants = promptRestore["anyOf"] as JArray;
                AssertTrue(promptRestore["properties"] == null &&
                    promptRestoreVariants != null &&
                    promptRestoreVariants.OfType<JObject>().All(variant =>
                        ((JObject)variant["properties"]).Properties().Count() == 1 &&
                        ((JArray)variant["required"]).Count == 1),
                    "model prompt exposes two complete restore alternatives without an optional envelope");
                var backupTime = new DateTime(
                    2026, 9, 3, 10, 0, 0, DateTimeKind.Utc);
                var firstTarget = VbaResourceProvider.BackupSemanticTarget(
                    new VbaModuleBackup
                    {
                        ModuleName = "Module1",
                        CreatedUtc = backupTime.AddTicks(1)
                    });
                var secondTarget = VbaResourceProvider.BackupSemanticTarget(
                    new VbaModuleBackup
                    {
                        ModuleName = "Module1",
                        CreatedUtc = backupTime.AddTicks(2)
                    });
                AssertTrue(!string.Equals(firstTarget, secondTarget,
                        StringComparison.OrdinalIgnoreCase),
                    "readable backup targets preserve sub-second identity");

                string error;
                AssertTrue(ModelToolResultProjection.ValidateAcceptedCall(
                        new ToolCall("patch_current", VbaToolCatalog.ApplyPatch,
                            "{\"moduleName\":\"Module1\",\"patch\":[{\"find\":\"old\",\"text\":\"new\"}]}"),
                        out error),
                    "current patch history is replayable");
                AssertTrue(ModelToolResultProjection.ValidateAcceptedCall(
                        new ToolCall("patch_context", VbaToolCatalog.ApplyPatch,
                            "{\"moduleName\":\"Module1\",\"patch\":[{\"find\":\"old\",\"text\":\"new\",\"contextBefore\":\"Sub Main()\\n\",\"contextAfter\":\"\\nEnd Sub\"}]}"),
                        out error),
                    "context-qualified patch history is replayable");
                AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(
                        new ToolCall("patch_old", VbaToolCatalog.ApplyPatch,
                            "{\"moduleName\":\"Module1\",\"patch\":[{\"op\":\"replace\",\"find\":\"old\",\"text\":\"new\"}]}"),
                        out error),
                    "old constant-op patch history requires reset");
                AssertTrue(ModelToolResultProjection.ValidateAcceptedCall(
                        new ToolCall("rename_current", VbaToolCatalog.RenameModule,
                            "{\"moduleName\":\"Old\",\"newModuleName\":\"New\"}"),
                        out error),
                    "separate rename history is replayable");
                AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(
                        new ToolCall("rename_old", VbaToolCatalog.WriteModule,
                            "{\"moduleName\":\"Old\",\"newModuleName\":\"New\",\"mode\":\"rename\"}"),
                        out error),
                    "old write/rename branch requires reset");
                AssertTrue(ModelToolResultProjection.ValidateAcceptedCall(
                        new ToolCall("restore_current", VbaToolCatalog.RestoreBackup,
                            "{\"target\":\"VBA backup: Module1 backup 2026-09-03 10:00:00.0000000Z\"}"),
                        out error),
                    "readable restore target history is replayable");
                AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(
                        new ToolCall("restore_old", VbaToolCatalog.RestoreBackup,
                            "{\"backupId\":\"backup-secret\"}"),
                        out error),
                    "raw backup-id restore history requires reset");
                AssertTrue(!ModelToolResultProjection.ValidateAcceptedCall(
                        new ToolCall("restore_mixed", VbaToolCatalog.RestoreBackup,
                            "{\"target\":\"VBA backup: Module1 backup\",\"moduleName\":\"Module1\"}"),
                        out error),
                    "restore requires exactly one semantic selector");

                var invocation = new ToolInvocation
                {
                    ToolCallId = "vba_projection",
                    ToolId = VbaToolCatalog.RestoreBackup
                };
                var opaqueHash = new string('a', 64);
                var projected = ModelToolResultProjection.Project(
                    AgentJsonProtocol.CreateToolResultMessage(
                        invocation,
                        RNAssistant.Core.Tools.Contracts.ToolResult.Ok(
                            "Restored backup-secret with hash-secret and " +
                                opaqueHash + " from rna://unlisted/path.",
                            "{\"moduleName\":\"Module1\",\"target\":\"VBA backup: Module1 backup\",\"backupId\":\"backup-secret\",\"mutationId\":\"mutation-secret\",\"codeSha256\":\"hash-secret\",\"resourceUri\":\"rna://secret\",\"journaled\":true,\"operations\":[{\"op\":\"replace\",\"changed\":true}],\"restore\":{\"requestId\":\"request-secret\",\"changed\":true}}"),
                        ToolResultRoles.Tool));
                AssertContains(projected.Content, "VBA backup: Module1 backup",
                    "VBA result keeps semantic target evidence");
                AssertContains(projected.Content, "moduleName",
                    "VBA result keeps semantic component evidence");
                AssertTrue(projected.Content.IndexOf("backupId",
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    projected.Content.IndexOf("mutationId",
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    projected.Content.IndexOf("Sha256",
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    projected.Content.IndexOf("requestId",
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    projected.Content.IndexOf("journaled",
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    projected.Content.IndexOf("\"op\"",
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    projected.Content.IndexOf("backup-secret",
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    projected.Content.IndexOf("hash-secret",
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    projected.Content.IndexOf(opaqueHash,
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    projected.Content.IndexOf("rna://",
                        StringComparison.OrdinalIgnoreCase) < 0,
                    "VBA model result hides runtime identity in data and message");
            });
        }

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
                        ["find"] = "\"old\"",
                        ["text"] = "\"new\""
                    }));

                var pending = PrepareVbaNative(executor, session, command);
                var blocked = ToolRunResultFactory.Create(pending.Record);
                AssertTrue(!blocked.Success, "vba replace blocked");
                AssertEqual("awaiting_confirmation", blocked.Status, "vba replace waits for confirmation");
                AssertTrue(string.IsNullOrWhiteSpace(command.RuntimeGuardJson),
                    "public VBA no longer writes compatibility command guards");
                AssertTrue(!string.IsNullOrWhiteSpace(pending.Record.PreparedStateJson),
                    "runtime persists typed preparation before confirmation");
                AssertContains(blocked.DataJson, "operations", "confirmation includes the validated patch preview");
                AssertEqual(2, adapter.TotalBackendCallCount, "confirmation preflight reads and validates without a public read call");
                AssertEqual(0, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule), "confirmation preflight does not write VBA");
                AssertContains(adapter.VbaModuleCode, "\"old\"", "blocked mutation leaves code unchanged");

                var completed = ConfirmVbaNative(pending);
                var result = ToolRunResultFactory.Create(completed);

                AssertTrue(result.Success, "replace result");
                AssertEqual(ToolExecutionOutcome.Ok, completed.Outcome,
                    "verified VBA mutation is a native success");
                AssertEqual(ToolEffectEvidence.VerifiedChange,
                    completed.Evidence.Effect,
                    "read-back certifies the native mutation effect");
                AssertContains(adapter.VbaModuleCode, "\"new\"", "updated module");
                AssertTrue(adapter.VbaModuleCode.IndexOf("\"old\"", StringComparison.Ordinal) < 0, "old text removed");
                var backups = backupStore.List("Excel", "doc");
                AssertEqual(1, backups.Count, "backup count");
                AssertEqual("Module1", backups[0].ModuleName, "backup module");
                AssertTrue(backups[0].Code == null, "backup list is metadata-only");
                AssertContains(backupStore.Find("Excel", "doc", backups[0].BackupId, null).Code, "\"old\"", "backup code");
            });
        }

        private static void VbaMutationServiceOwnsApplyPatch()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"before\"\nEnd Sub";
                var journal = new VbaJournalStore(paths);
                var reader = new VbaReader(adapter.VbaHostBackend);
                var service = new VbaMutationService(
                    new VbaMutationHostDocumentContext(adapter.VbaHostBackend),
                    new VbaMutationJournalStoreAdapter(journal),
                    new VbaMutationHostReader(reader),
                    new VbaMutationHostBackend(adapter.VbaHostBackend));
                var session = NewSession(adapter);
                var command = Command("common.vba_apply_patch", "moduleName", "Module1");
                var correlation = new VbaMutationCorrelation
                {
                    SessionId = session.Id,
                    StepId = command.RuntimeStepId,
                    ToolCallId = command.ToolCallId
                };

                var guard = service.PrepareApplyPatchGuard(new VbaApplyPatchGuardRequest
                {
                    RequestedModuleName = "Module1",
                    Correlation = correlation
                });
                AssertTrue(guard.Success, "service prepares its own typed patch guard");
                var result = service.ApplyPatch(
                    new VbaApplyPatchRequest
                    {
                        RequestedModuleName = guard.ResolvedModuleName,
                        Operations = new List<VbaPatchOperationRequest>
                        {
                            new VbaPatchOperationRequest
                            {
                                Operation = "replace",
                                Find = "\"before\"",
                                Text = "\"after\""
                            }
                        },
                        Guard = guard.Guard,
                        Correlation = correlation
                    },
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Ok, result.Status, "service applies and verifies patch");
                AssertContains(adapter.VbaModuleCode, "\"after\"", "service dispatches the intended source");
                AssertTrue(result.Data["journalStatus"] == null,
                    "typed outcome does not expose internal journal status");
                AssertTrue(!string.IsNullOrWhiteSpace((string)result.Data["mutationId"]),
                    "typed outcome keeps mutation correlation evidence");
                var record = journal.ListMutations(adapter.HostName, adapter.DocumentKey).Single();
                AssertEqual(VbaMutationStatuses.Committed, record.Terminal.Status,
                    "service owns terminal module assessment");
            });
        }

        private static void VbaWholeModuleWriteServiceOwnsWorkflow()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter();
                var store = new VbaJournalStore(paths);
                var reader = new VbaReader(adapter.VbaHostBackend);
                var service = new VbaMutationService(
                    new VbaMutationHostDocumentContext(adapter.VbaHostBackend),
                    new VbaMutationJournalStoreAdapter(store),
                    new VbaMutationHostReader(reader),
                    new VbaMutationHostBackend(adapter.VbaHostBackend));
                var correlation = new VbaMutationCorrelation { SessionId = "whole-write-service" };
                const string requestedName = "123 phase 6E target!";

                var preparation = service.PrepareWholeModuleWriteGuard(
                    new VbaWholeModuleWriteGuardRequest
                    {
                        RequestedModuleName = requestedName,
                        Correlation = correlation
                    });
                AssertTrue(preparation.Success, "typed service prepares a missing whole-write target");
                AssertTrue(!string.Equals(requestedName, preparation.ResolvedModuleName, StringComparison.Ordinal),
                    "typed service owns deterministic create-name normalization");

                var created = service.WriteWholeModule(
                    new VbaWholeModuleWriteRequest
                    {
                        ModuleName = preparation.ResolvedModuleName,
                        Code = "Option Explicit\nPublic Value As Long",
                        ComponentType = "ClassModule",
                        Mode = VbaWholeModuleWriteMode.CreateOnly,
                        Guard = preparation.Guard,
                        Correlation = correlation
                    },
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Ok, created.Status,
                    "typed whole-write service creates and verifies the module");
                AssertContains(adapter.GetVbaModuleCode(preparation.ResolvedModuleName), "Public Value",
                    "typed backend receives complete source");
                AssertEqual("ClassModule", (string)created.Data["componentType"],
                    "creation preserves the requested component type");
                AssertEqual(1,
                    adapter.CountVbaCalls(FakeVbaOperation.CreateModule),
                    "domain workflow dispatches one create action");
                AssertEqual(VbaMutationStatuses.Committed,
                    store.ListMutations(adapter.HostName, adapter.DocumentKey).Single().Terminal.Status,
                    "domain workflow owns the terminal journal result");

                correlation.ExpectedContentSha256 = VbaTextCanonicalizer.LiveCodeSha256(
                    adapter.GetVbaModuleCode(preparation.ResolvedModuleName));

                var existingPreparation = service.PrepareWholeModuleWriteGuard(
                    new VbaWholeModuleWriteGuardRequest
                    {
                        RequestedModuleName = requestedName,
                        Correlation = correlation
                    });
                AssertTrue(existingPreparation.Success,
                    "typed service prepares the existing normalized target");
                var createDispatches =
                    adapter.CountVbaCalls(FakeVbaOperation.CreateModule);
                var rejected = service.WriteWholeModule(
                    new VbaWholeModuleWriteRequest
                    {
                        ModuleName = existingPreparation.ResolvedModuleName,
                        Code = "Option Explicit\nPublic Value As String",
                        ComponentType = "StdModule",
                        Mode = VbaWholeModuleWriteMode.CreateOnly,
                        Guard = existingPreparation.Guard,
                        Correlation = correlation
                    },
                    CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Error, rejected.Status,
                    "createOnly rejects an existing target inside the domain service");
                AssertEqual("vba_module_exists", rejected.ErrorCode,
                    "createOnly keeps its stable error code");
                AssertEqual(1, store.ListMutations(adapter.HostName, adapter.DocumentKey).Count,
                    "existence rejection does not create a journal preparation");
                AssertEqual(createDispatches,
                    adapter.CountVbaCalls(FakeVbaOperation.CreateModule),
                    "existence rejection does not dispatch create");

                var missingPreparation = service.PrepareWholeModuleWriteGuard(
                    new VbaWholeModuleWriteGuardRequest
                    {
                        RequestedModuleName = "MissingUpdateTarget",
                        Correlation = correlation
                    });
                AssertTrue(missingPreparation.Success,
                    "typed service prepares a missing update-only target");
                var missingUpdate = service.WriteWholeModule(
                    new VbaWholeModuleWriteRequest
                    {
                        ModuleName = missingPreparation.ResolvedModuleName,
                        Code = "Sub Missing()\nEnd Sub",
                        ComponentType = "StdModule",
                        Mode = VbaWholeModuleWriteMode.UpdateOnly,
                        Guard = missingPreparation.Guard,
                        Correlation = correlation
                    },
                    CancellationToken.None);
                AssertEqual("vba_module_not_found", missingUpdate.ErrorCode,
                    "updateOnly rejects a missing target before persistence or dispatch");
                AssertEqual(1, store.ListMutations(adapter.HostName, adapter.DocumentKey).Count,
                    "missing update rejection creates no journal preparation");
                AssertEqual(createDispatches,
                    adapter.CountVbaCalls(FakeVbaOperation.CreateModule),
                    "missing update rejection does not dispatch create");

                const string raceName = "TypeRaceTarget";
                const string raceCode = "Option Explicit\nPublic SameSource As Long";
                var racePreparation = service.PrepareWholeModuleWriteGuard(
                    new VbaWholeModuleWriteGuardRequest
                    {
                        RequestedModuleName = raceName,
                        Correlation = correlation
                    });
                AssertTrue(racePreparation.Success, "type-race target is initially missing");
                adapter.BeforeVbaBackendCall = call =>
                {
                    if (call.Operation == FakeVbaOperation.CreateModule)
                    {
                        adapter.SetVbaModule(raceName, raceCode, "StdModule");
                    }
                };
                var raced = service.WriteWholeModule(
                    new VbaWholeModuleWriteRequest
                    {
                        ModuleName = raceName,
                        Code = raceCode,
                        ComponentType = "ClassModule",
                        Mode = VbaWholeModuleWriteMode.CreateOnly,
                        Guard = racePreparation.Guard,
                        Correlation = correlation
                    },
                    CancellationToken.None);
                adapter.BeforeVbaBackendCall = null;
                AssertEqual(VbaMutationOutcomeStatus.Unknown, raced.Status,
                    "same source with a different raced component type is not false committed");
                AssertEqual(false, raced.Retryable, "type-diverged create is not retried");
                AssertEqual(VbaMutationStatuses.Unknown,
                    store.ListMutations(adapter.HostName, adapter.DocumentKey)
                        .Single(item => item.Prepared.ModuleName == raceName)
                        .Terminal.Status,
                    "type-diverged create is durably unknown");
            });
        }

        private static void VbaDeleteModuleServiceOwnsWorkflow()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter();
                const string moduleName = "DeleteTarget";
                const string source = "Option Explicit\nPublic Value As Long";
                adapter.SetVbaModule(moduleName, source, "ClassModule");
                var store = new VbaJournalStore(paths);
                var reader = new VbaReader(adapter.VbaHostBackend);
                var service = new VbaMutationService(
                    new VbaMutationHostDocumentContext(adapter.VbaHostBackend),
                    new VbaMutationJournalStoreAdapter(store),
                    new VbaMutationHostReader(reader),
                    new VbaMutationHostBackend(adapter.VbaHostBackend));
                var correlation = new VbaMutationCorrelation
                {
                    SessionId = "delete-service",
                    RunId = "delete-run",
                    TurnId = "delete-turn",
                    StepId = "delete-step",
                    ToolCallId = "delete-call"
                };
                var unguarded = service.DeleteModule(
                    new VbaDeleteModuleRequest
                    {
                        ModuleName = moduleName,
                        Correlation = correlation
                    },
                    CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Error, unguarded.Status,
                    "typed delete service refuses an unprepared mutation");
                AssertEqual("vba_internal_snapshot_missing", unguarded.ErrorCode,
                    "missing delete guard fails with the stable snapshot code");
                AssertEqual(0, store.ListMutations(adapter.HostName, adapter.DocumentKey).Count,
                    "unguarded delete creates no journal preparation");
                AssertEqual(0,
                    adapter.CountVbaCalls(FakeVbaOperation.DeleteModule),
                    "unguarded delete does not dispatch the backend");

                var preparation = service.PrepareDeleteModuleGuard(
                    new VbaDeleteModuleGuardRequest
                    {
                        RequestedModuleName = moduleName,
                        Correlation = correlation
                    });
                AssertTrue(preparation.Success, "typed service prepares the delete guard");

                var dryRun = service.DeleteModule(
                    new VbaDeleteModuleRequest
                    {
                        ModuleName = preparation.ResolvedModuleName,
                        DryRun = true,
                        Guard = preparation.Guard,
                        Correlation = correlation
                    },
                    CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Ok, dryRun.Status,
                    "typed delete service owns dry-run validation");
                AssertEqual(0, store.ListMutations(adapter.HostName, adapter.DocumentKey).Count,
                    "delete dry-run does not prepare a journal record");
                AssertEqual(0,
                    adapter.CountVbaCalls(FakeVbaOperation.DeleteModule),
                    "delete dry-run does not dispatch the backend");

                var deleted = service.DeleteModule(
                    new VbaDeleteModuleRequest
                    {
                        ModuleName = preparation.ResolvedModuleName,
                        Guard = preparation.Guard,
                        Correlation = correlation
                    },
                    CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Ok, deleted.Status,
                    "typed delete service dispatches and verifies deletion");
                var deleteRequest =
                    (RNAssistant.Office.Domains.Vba.VbaDeleteModuleRequest)adapter
                    .SingleVbaCall(FakeVbaOperation.DeleteModule).Request;
                AssertEqual(VbaTextCanonicalizer.LiveCodeSha256(source),
                    deleteRequest.ExpectedCodeSha256,
                    "typed backend receives the exact live-state compare-and-swap hash");
                var finalRead = new VbaMutationHostReader(reader)
                    .ReadModule(moduleName, 1000000);
                AssertTrue(!finalRead.Success && finalRead.IsNotFound,
                    "typed delete workflow verifies the module is absent");
                var record = store.ListMutations(adapter.HostName, adapter.DocumentKey).Single();
                AssertEqual(VbaMutationStatuses.Committed, record.Terminal.Status,
                    "typed delete workflow owns the terminal journal result");
                AssertEqual("delete-call", record.Prepared.ToolCallId,
                    "typed delete journal keeps accepted-call correlation");

                const string protectedName = "ThisWorkbook";
                adapter.SetVbaModule(protectedName, "Private Sub Workbook_Open()\nEnd Sub", "DocumentModule");
                var protectedPreparation = service.PrepareDeleteModuleGuard(
                    new VbaDeleteModuleGuardRequest
                    {
                        RequestedModuleName = protectedName,
                        Correlation = correlation
                    });
                AssertTrue(protectedPreparation.Success,
                    "protected component state can be prepared for a fail-closed preview");
                var dispatches =
                    adapter.CountVbaCalls(FakeVbaOperation.DeleteModule);
                var protectedResult = service.DeleteModule(
                    new VbaDeleteModuleRequest
                    {
                        ModuleName = protectedName,
                        Guard = protectedPreparation.Guard,
                        Correlation = correlation
                    },
                    CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Error, protectedResult.Status,
                    "typed delete service blocks document modules");
                AssertEqual("vba_component_type_read_only", protectedResult.ErrorCode,
                    "protected component refusal keeps its stable code");
                AssertEqual(dispatches,
                    adapter.CountVbaCalls(FakeVbaOperation.DeleteModule),
                    "protected component refusal does not dispatch delete");
                AssertEqual(1, store.ListMutations(adapter.HostName, adapter.DocumentKey).Count,
                    "protected component refusal creates no journal preparation");
            });
        }

        private static void VbaRestoreServiceOwnsWorkflow()
        {
            WithTempPaths(paths =>
            {
                const string moduleName = "Module1";
                const string currentCode = "Sub Current()\nEnd Sub";
                const string selectedCode = "Sub Selected()\nEnd Sub";
                var adapter = new FakeOfficeAdapter { VbaModuleCode = currentCode };
                var store = new VbaJournalStore(paths);
                var selected = store.Save(
                    adapter.HostName,
                    adapter.DocumentKey,
                    adapter.DocumentTitle,
                    moduleName,
                    "StdModule",
                    selectedCode);
                var reader = new VbaReader(adapter.VbaHostBackend);
                var service = new VbaMutationService(
                    new VbaMutationHostDocumentContext(adapter.VbaHostBackend),
                    new VbaMutationJournalStoreAdapter(store),
                    new VbaMutationHostReader(reader),
                    new VbaMutationHostBackend(adapter.VbaHostBackend));
                var correlation = new VbaMutationCorrelation
                {
                    SessionId = "restore-service",
                    RunId = "restore-run",
                    TurnId = "restore-turn",
                    StepId = "restore-step",
                    ToolCallId = "restore-call"
                };

                var unguarded = service.RestoreBackup(
                    new VbaRestoreRequest
                    {
                        BackupId = selected.BackupId,
                        ModuleName = moduleName,
                        Correlation = correlation
                    },
                    CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Error, unguarded.Status,
                    "typed restore service refuses an unprepared mutation");
                AssertEqual("vba_internal_snapshot_missing", unguarded.ErrorCode,
                    "missing restore guard fails with the stable snapshot code");
                AssertEqual(0, store.ListMutations(adapter.HostName, adapter.DocumentKey).Count,
                    "unguarded restore creates no journal preparation");
                AssertEqual(0, adapter.CountVbaWholeModuleWriteCalls(),
                    "unguarded restore does not dispatch a backend mutation");

                var preparation = service.PrepareRestoreGuard(
                    new VbaRestoreGuardRequest
                    {
                        BackupId = selected.BackupId,
                        ModuleName = moduleName,
                        Correlation = correlation
                    });
                AssertTrue(preparation.Success, "typed service prepares the restore guard");
                AssertEqual(selected.BackupId, preparation.BackupId,
                    "restore preparation pins the exact backup id");
                AssertEqual(moduleName, preparation.ModuleName,
                    "restore preparation pins the backup module name");

                var newer = store.Save(
                    adapter.HostName,
                    adapter.DocumentKey,
                    adapter.DocumentTitle,
                    moduleName,
                    "StdModule",
                    "Sub Newer()\nEnd Sub");
                var mutationDispatches =
                    adapter.CountVbaWholeModuleWriteCalls();
                var substituted = service.RestoreBackup(
                    new VbaRestoreRequest
                    {
                        BackupId = newer.BackupId,
                        ModuleName = moduleName,
                        Guard = preparation.Guard,
                        Correlation = correlation
                    },
                    CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Error, substituted.Status,
                    "typed restore service rejects a substituted backup after preparation");
                AssertEqual("vba_restore_backup_changed", substituted.ErrorCode,
                    "backup substitution has a distinct stable error code");
                AssertEqual(0, store.ListMutations(adapter.HostName, adapter.DocumentKey).Count,
                    "backup substitution creates no journal preparation");
                AssertEqual(mutationDispatches,
                    adapter.CountVbaWholeModuleWriteCalls(),
                    "backup substitution does not dispatch a backend mutation");

                var preparedBackupHash = preparation.Guard.BackupLiveCodeSha256;
                preparation.Guard.BackupLiveCodeSha256 = "tampered-backup-hash";
                var alteredBackup = service.RestoreBackup(
                    new VbaRestoreRequest
                    {
                        BackupId = preparation.BackupId,
                        ModuleName = preparation.ModuleName,
                        Guard = preparation.Guard,
                        Correlation = correlation
                    },
                    CancellationToken.None);
                AssertEqual("vba_restore_backup_changed", alteredBackup.ErrorCode,
                    "restore guard binds the selected backup live source as well as its id");
                AssertEqual(0, store.ListMutations(adapter.HostName, adapter.DocumentKey).Count,
                    "altered backup evidence creates no journal preparation");
                preparation.Guard.BackupLiveCodeSha256 = preparedBackupHash;

                adapter.VbaModuleCode = "Sub ChangedAfterConfirmation()\nEnd Sub";
                var staleTarget = service.RestoreBackup(
                    new VbaRestoreRequest
                    {
                        BackupId = preparation.BackupId,
                        ModuleName = preparation.ModuleName,
                        Guard = preparation.Guard,
                        Correlation = correlation
                    },
                    CancellationToken.None);
                AssertEqual("stale_vba_module", staleTarget.ErrorCode,
                    "restore guard rejects target changes after preparation");
                AssertEqual(0, store.ListMutations(adapter.HostName, adapter.DocumentKey).Count,
                    "stale restore target creates no journal preparation");
                adapter.VbaModuleCode = currentCode;

                var dryRun = service.RestoreBackup(
                    new VbaRestoreRequest
                    {
                        BackupId = preparation.BackupId,
                        ModuleName = preparation.ModuleName,
                        DryRun = true,
                        Guard = preparation.Guard,
                        Correlation = correlation
                    },
                    CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Ok, dryRun.Status,
                    "typed restore service owns dry-run validation");
                AssertEqual(selected.BackupId, (string)dryRun.Data["backupId"],
                    "restore dry-run identifies the exact selected backup");
                AssertEqual(0, store.ListMutations(adapter.HostName, adapter.DocumentKey).Count,
                    "restore dry-run creates no journal preparation");
                AssertEqual(mutationDispatches,
                    adapter.CountVbaWholeModuleWriteCalls(),
                    "restore dry-run does not dispatch a backend mutation");

                var restored = service.RestoreBackup(
                    new VbaRestoreRequest
                    {
                        BackupId = preparation.BackupId,
                        ModuleName = preparation.ModuleName,
                        Guard = preparation.Guard,
                        Correlation = correlation
                    },
                    CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Ok, restored.Status,
                    "typed restore service dispatches and verifies restore");
                AssertEqual(selectedCode, adapter.VbaModuleCode,
                    "typed restore backend receives the selected backup source");
                var replace =
                    (RNAssistant.Office.Domains.Vba.VbaReplaceModuleRequest)adapter
                    .SingleVbaCall(FakeVbaOperation.ReplaceModule).Request;
                AssertEqual(VbaTextCanonicalizer.LiveCodeSha256(currentCode),
                    replace.ExpectedCodeSha256,
                    "typed restore backend receives the exact current-state compare-and-swap hash");
                var record = store.ListMutations(adapter.HostName, adapter.DocumentKey).Single();
                AssertEqual(VbaMutationStatuses.Committed, record.Terminal.Status,
                    "typed restore workflow owns the terminal journal result");
                AssertEqual("restore-call", record.Prepared.ToolCallId,
                    "typed restore journal keeps accepted-call correlation");

                var incompatible = store.Save(
                    adapter.HostName,
                    adapter.DocumentKey,
                    adapter.DocumentTitle,
                    moduleName,
                    "ClassModule",
                    "Option Explicit\nPublic Value As String");
                mutationDispatches =
                    adapter.CountVbaWholeModuleWriteCalls();
                var incompatiblePreparation = service.PrepareRestoreGuard(
                    new VbaRestoreGuardRequest
                    {
                        BackupId = incompatible.BackupId,
                        ModuleName = moduleName,
                        Correlation = correlation
                    });
                AssertTrue(!incompatiblePreparation.Success,
                    "restore preparation blocks an incompatible existing component type");
                AssertEqual("vba_restore_component_type_mismatch",
                    incompatiblePreparation.Error.ErrorCode,
                    "component-type mismatch keeps its stable error code");
                AssertEqual(1, store.ListMutations(adapter.HostName, adapter.DocumentKey).Count,
                    "component-type refusal creates no journal preparation");
                AssertEqual(mutationDispatches,
                    adapter.CountVbaWholeModuleWriteCalls(),
                    "component-type refusal does not dispatch a backend mutation");
            });
        }

        private static void VbaMutationPrepareFailureBlocksDispatch()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter
                {
                    VbaModuleCode = "Sub Main()\nDebug.Print \"before\"\nEnd Sub"
                };
                var store = new VbaJournalStore(paths);
                var journal = new FaultingVbaMutationJournal(store) { FailPrepare = true };
                var backend = new ScriptedVbaMutationBackend(request =>
                {
                    adapter.VbaModuleCode = request.Code;
                    return VbaMutationActionResult.Succeeded("written");
                });
                var service = CreateTypedMutationService(adapter, journal, backend);

                var outcome = service.ApplyPatch(
                    PrepareTypedPatch(service, "prepare-failure", "\"before\"", "\"after\""),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Error, outcome.Status,
                    "prepare persistence failure is a definite error");
                AssertEqual("vba_journal_prepare_failed", outcome.ErrorCode,
                    "prepare failure keeps a stable code");
                AssertEqual(0, backend.DispatchCount, "prepare failure blocks backend dispatch");
                AssertEqual(0, store.ListMutations(adapter.HostName, adapter.DocumentKey).Count,
                    "prepare failure creates no durable mutation");
            });
        }

        private static void VbaMutationTerminalFailureIsUnknown()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter
                {
                    VbaModuleCode = "Sub Main()\nDebug.Print \"before\"\nEnd Sub"
                };
                var store = new VbaJournalStore(paths);
                var journal = new FaultingVbaMutationJournal(store) { FailComplete = true };
                var backend = new ScriptedVbaMutationBackend(request =>
                {
                    adapter.VbaModuleCode = request.Code;
                    return VbaMutationActionResult.Succeeded("written");
                });
                var service = CreateTypedMutationService(adapter, journal, backend);

                var outcome = service.ApplyPatch(
                    PrepareTypedPatch(service, "terminal-failure", "\"before\"", "\"after\""),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Unknown, outcome.Status,
                    "terminal persistence failure cannot claim a durable outcome");
                AssertEqual(false, outcome.Retryable,
                    "unknown terminal persistence failure is never automatically retryable");
                AssertEqual(false, (bool)outcome.Data["terminalRecorded"],
                    "public evidence exposes only terminal durability, not its internal status");
                AssertTrue(outcome.Data["journalStatus"] == null,
                    "terminal failure does not leak internal journal status");
                AssertEqual(1, backend.DispatchCount, "mutation is dispatched only once");
                AssertTrue(store.ListMutations(adapter.HostName, adapter.DocumentKey).Single().Terminal == null,
                    "failed terminal append leaves the prepared record open for reconciliation");
            });
        }

        private static void VbaMutationRollbackProseIsNotEvidence()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter
                {
                    VbaModuleCode = "Sub Main()\nDebug.Print \"before\"\nEnd Sub"
                };
                var store = new VbaJournalStore(paths);
                var backend = new ScriptedVbaMutationBackend(request =>
                    VbaMutationActionResult.Error(
                        "Write failed; original code was restored.",
                        null,
                        "scripted_write_failed",
                        false));
                var service = CreateTypedMutationService(
                    adapter,
                    new VbaMutationJournalStoreAdapter(store),
                    backend);

                var outcome = service.ApplyPatch(
                    PrepareTypedPatch(service, "rollback-prose", "\"before\"", "\"after\""),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Error, outcome.Status,
                    "backend failure with unchanged state is a definite error");
                AssertEqual(VbaMutationStatuses.NotApplied,
                    store.ListMutations(adapter.HostName, adapter.DocumentKey).Single().Terminal.Status,
                    "journal uses inspected live state rather than message text");

                var explicitBackend = new ScriptedVbaMutationBackend(request =>
                    VbaMutationActionResult.Error(
                        "Structured rollback result.",
                        null,
                        "scripted_write_failed",
                        false,
                        VbaMutationDisposition.RolledBack));
                var explicitService = CreateTypedMutationService(
                    adapter,
                    new VbaMutationJournalStoreAdapter(store),
                    explicitBackend);
                var explicitOutcome = explicitService.ApplyPatch(
                    PrepareTypedPatch(explicitService, "rollback-structured", "\"before\"", "\"after\""),
                    CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Error, explicitOutcome.Status,
                    "structured rollback remains a definite error outcome");
                AssertEqual(VbaMutationStatuses.RolledBack,
                    store.ListMutations(adapter.HostName, adapter.DocumentKey).Last().Terminal.Status,
                    "only explicit disposition plus verified before state records rollback");
            });
        }

        private static void VbaMutationBackendThrowBeforeEffect()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter
                {
                    VbaModuleCode = "Sub Main()\nDebug.Print \"before\"\nEnd Sub"
                };
                var store = new VbaJournalStore(paths);
                var backend = new ScriptedVbaMutationBackend(request =>
                {
                    throw new InvalidOperationException("scripted backend throw");
                });
                var service = CreateTypedMutationService(
                    adapter,
                    new VbaMutationJournalStoreAdapter(store),
                    backend);

                var outcome = service.ApplyPatch(
                    PrepareTypedPatch(service, "throw-before", "\"before\"", "\"after\""),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Error, outcome.Status,
                    "throw before mutation is a definite error after inspection");
                AssertEqual(VbaMutationStatuses.NotApplied,
                    store.ListMutations(adapter.HostName, adapter.DocumentKey).Single().Terminal.Status,
                    "unchanged live state is durably not applied");
                AssertEqual(1, backend.DispatchCount, "throwing backend is not retried");
            });
        }

        private static void VbaMutationCommittedAfterBackendThrow()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter
                {
                    VbaModuleCode = "Sub Main()\nDebug.Print \"before\"\nEnd Sub"
                };
                var store = new VbaJournalStore(paths);
                var backend = new ScriptedVbaMutationBackend(request =>
                {
                    adapter.VbaModuleCode = request.Code;
                    throw new InvalidOperationException("throw after write");
                });
                var service = CreateTypedMutationService(
                    adapter,
                    new VbaMutationJournalStoreAdapter(store),
                    backend);

                var outcome = service.ApplyPatch(
                    PrepareTypedPatch(service, "throw-after", "\"before\"", "\"after\""),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Ok, outcome.Status,
                    "verified intended state wins over a backend error report");
                AssertEqual(VbaMutationStatuses.Committed,
                    store.ListMutations(adapter.HostName, adapter.DocumentKey).Single().Terminal.Status,
                    "intended live state is durably committed");
                AssertEqual(true, (bool)outcome.Data["backendReportedError"],
                    "result retains that the backend reported an error");
                AssertEqual(1, backend.DispatchCount, "post-effect throw is not retried");
            });
        }

        private static void VbaMutationReadBackDivergenceIsUnknown()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter
                {
                    VbaModuleCode = "Sub Main()\nDebug.Print \"before\"\nEnd Sub"
                };
                var store = new VbaJournalStore(paths);
                var backend = new ScriptedVbaMutationBackend(request =>
                {
                    adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"diverged\"\nEnd Sub";
                    return VbaMutationActionResult.Succeeded(
                        "backend reported success",
                        new JObject
                        {
                            ["journalStatus"] = "forged",
                            ["packageJournalStatus"] = "forged",
                            ["terminalRecorded"] = true,
                            ["backendReportedError"] = false,
                            ["compileValidation"] = "forged"
                        });
                });
                var service = CreateTypedMutationService(
                    adapter,
                    new VbaMutationJournalStoreAdapter(store),
                    backend);

                var outcome = service.ApplyPatch(
                    PrepareTypedPatch(service, "divergence", "\"before\"", "\"after\""),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Unknown, outcome.Status,
                    "diverged read-back is unknown");
                AssertEqual(false, outcome.Retryable, "unknown divergence cannot be retried automatically");
                AssertTrue(outcome.Data["journalStatus"] == null &&
                    outcome.Data["packageJournalStatus"] == null &&
                    outcome.Data["terminalRecorded"] == null &&
                    outcome.Data["backendReportedError"] == null &&
                    outcome.Data["compileValidation"] == null,
                    "backend data cannot forge reserved journal evidence");
                AssertEqual(VbaMutationStatuses.Unknown,
                    store.ListMutations(adapter.HostName, adapter.DocumentKey).Single().Terminal.Status,
                    "durable assessment records unknown");
                AssertEqual(1, backend.DispatchCount, "diverged write is dispatched once");
            });
        }

        private static void VbaMutationUnavailableReadBackIsUnknown()
        {
            WithTempPaths(paths =>
            {
                var adapter = new FakeOfficeAdapter
                {
                    VbaModuleCode = "Sub Main()\nDebug.Print \"before\"\nEnd Sub"
                };
                var store = new VbaJournalStore(paths);
                var backend = new ScriptedVbaMutationBackend(request =>
                {
                    adapter.VbaModuleCode = request.Code;
                    adapter.QueueVbaFailure(FakeVbaOperation.ReadModule,
                        "VBA access denied.", "vba_access_error", false);
                    adapter.QueueVbaFailure(FakeVbaOperation.ReadModule,
                        "VBA access denied.", "vba_access_error", false);
                    return VbaMutationActionResult.Succeeded("backend reported success");
                });
                var service = CreateTypedMutationService(
                    adapter,
                    new VbaMutationJournalStoreAdapter(store),
                    backend);

                var outcome = service.ApplyPatch(
                    PrepareTypedPatch(service, "unavailable-readback", "\"before\"", "\"after\""),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Unknown, outcome.Status,
                    "unavailable read-back cannot claim success or failure");
                AssertEqual(false, outcome.Retryable,
                    "unreadable final state cannot be retried automatically");
                AssertEqual(VbaMutationStatuses.Unknown,
                    store.ListMutations(adapter.HostName, adapter.DocumentKey).Single().Terminal.Status,
                    "unreadable live state is durably unknown");
                AssertEqual(1, backend.DispatchCount, "unreadable write is dispatched once");
            });
        }

        private static void VbaMutationCancellationBoundaries()
        {
            WithTempPaths(paths =>
            {
                const string beforeCode = "Sub Main()\nDebug.Print \"before\"\nEnd Sub";
                const string intendedCode = "Sub Main()\nDebug.Print \"after\"\nEnd Sub";
                var adapter = new FakeOfficeAdapter { VbaModuleCode = beforeCode };
                var store = new VbaJournalStore(paths);
                var backend = new ScriptedVbaMutationBackend(request =>
                    VbaMutationActionResult.Succeeded("unused"));
                var service = CreateTypedMutationService(
                    adapter,
                    new VbaMutationJournalStoreAdapter(store),
                    backend);
                var before = new VbaModuleState
                {
                    Name = "Module1",
                    Code = beforeCode,
                    ComponentType = "StdModule"
                };
                var preDispatch = service.PrepareJournaledMutation(new VbaModuleMutationRequest
                {
                    Operation = "write",
                    ModuleName = "Module1",
                    Before = before,
                    IntendedAfterExists = true,
                    IntendedAfterCode = intendedCode,
                    IntendedComponentType = "StdModule",
                    Correlation = new VbaMutationCorrelation { SessionId = "cancel-before" }
                });
                AssertTrue(preDispatch.Success, "pre-dispatch cancellation has a prepared record");
                var source = new CancellationTokenSource();
                source.Cancel();
                var dispatched = 0;
                try
                {
                    service.ExecuteJournaledMutation(
                        preDispatch.Preparation,
                        () =>
                        {
                            dispatched += 1;
                            return VbaMutationActionResult.Succeeded("unexpected");
                        },
                        source.Token);
                    throw new InvalidOperationException("pre-dispatch cancellation was ignored");
                }
                catch (OperationCanceledException)
                {
                }
                AssertEqual(0, dispatched, "pre-dispatch cancellation blocks the effect");
                AssertEqual(VbaMutationStatuses.NotApplied,
                    store.ListMutations(adapter.HostName, adapter.DocumentKey)
                        .Single(item => item.Prepared.MutationId == preDispatch.Preparation.MutationId)
                        .Terminal.Status,
                    "pre-dispatch cancellation records not applied");

                var postDispatch = service.PrepareJournaledMutation(new VbaModuleMutationRequest
                {
                    Operation = "write",
                    ModuleName = "Module1",
                    Before = before,
                    IntendedAfterExists = true,
                    IntendedAfterCode = intendedCode,
                    IntendedComponentType = "StdModule",
                    Correlation = new VbaMutationCorrelation { SessionId = "cancel-after" }
                });
                var outcome = service.ExecuteJournaledMutation(
                    postDispatch.Preparation,
                    () =>
                    {
                        adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"diverged\"\nEnd Sub";
                        throw new OperationCanceledException("cancelled after dispatch");
                    },
                    CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Unknown, outcome.Status,
                    "post-dispatch cancellation with diverged state is unknown");
                AssertEqual(false, outcome.Retryable,
                    "post-dispatch cancellation cannot trigger an automatic retry");
                AssertEqual(VbaMutationStatuses.Unknown,
                    store.ListMutations(adapter.HostName, adapter.DocumentKey)
                        .Single(item => item.Prepared.MutationId == postDispatch.Preparation.MutationId)
                        .Terminal.Status,
                    "post-dispatch cancellation records unknown");
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
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();

                var command = Command(
                    "common.vba_apply_patch",
                    "moduleName", "Module1",
                    "patch", new JArray(new JObject
                    {
                        ["find"] = "\"old\"",
                        ["text"] = "\"new\""
                    }));
                var pending = PrepareVbaNative(executor, session, command);
                var waiting = ToolRunResultFactory.Create(pending.Record);
                AssertEqual("awaiting_confirmation", waiting.Status, "mutation waits for confirmation");

                var persisted = JsonConvert.DeserializeObject<ToolExecutionRecord>(
                    JsonConvert.SerializeObject(pending.Record));
                AssertTrue(!string.IsNullOrWhiteSpace(persisted.PreparedStateJson),
                    "typed prepared state survives persistence");
                var otherExecutor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                using (var accessDeadline = new CancellationTokenSource(2000))
                {
                    var available = otherExecutor.RunVbaMacro("Module1.Main", NewSession(adapter), accessDeadline.Token);
                    AssertEqual("unknown", available.Status,
                        "another executor dispatches while confirmation waits without inferring effect");
                    AssertEqual("Module1.Main", adapter.RanMacros.Last(),
                        "another executor acquires document access while confirmation waits");
                }
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"changed elsewhere\"\nEnd Sub";
                pending.Record = persisted;
                var stale = ToolRunResultFactory.Create(
                    ConfirmVbaNative(pending));

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
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var command = Command(
                    "common.vba_write_module",
                    "moduleName", "CreatedDuringConfirmation",
                    "componentType", "StdModule",
                    "code", "Sub Requested()\nEnd Sub",
                    "mode", "createOnly");
                var pending = PrepareVbaNative(executor, session, command);
                var waiting = ToolRunResultFactory.Create(pending.Record);
                AssertEqual("awaiting_confirmation", waiting.Status, "create waits for confirmation");

                adapter.SetVbaModule("CreatedDuringConfirmation", "Sub External()\nEnd Sub", "StdModule");
                var stale = ToolRunResultFactory.Create(
                    ConfirmVbaNative(pending));

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
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };
                var session = NewSession(adapter);

                var updated = executor.ExecuteManual(
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
                var created = executor.ExecuteManual(
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

                AssertTrue(ReadVbaSource(executor, session, actualName).Complete,
                    "complete source refreshes the normalized module before another write");

                var repeated = executor.ExecuteManual(
                    Command("common.vba_write_module", "moduleName", requestedName, "componentType", "StdModule", "code", "Option Explicit\nPublic Value As String"),
                    tools,
                    settings,
                    false,
                    false,
                    session);
                AssertTrue(repeated.Success, "same invalid name deterministically updates the normalized module");
                AssertEqual(false, (bool)JObject.Parse(repeated.DataJson ?? "{}")["created"], "repeated write is an update, not a duplicate create");
                AssertContains(adapter.GetVbaModuleCode(actualName), "As String", "normalized module is updated in place");
                var listed = ListVbaComponents(executor, session);
                AssertEqual(1, listed.Items.Count(item => string.Equals(
                        item.Title,
                        actualName,
                        StringComparison.OrdinalIgnoreCase)),
                    "normalization remains idempotent");

                adapter.SetVbaModule("SafeName", "Sub KeepMe()\nEnd Sub", "StdModule");
                var collisionSafe = executor.ExecuteManual(
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
                AssertContains(ReadVbaSource(executor, session, "ObservedModule").Text, "Original",
                    "whole-source resource read records the edit observation");
                adapter.SetVbaModule("ObservedModule", "Sub ExternalChange()\nEnd Sub", "StdModule");
                var observedWrite = Command(
                    "common.vba_write_module",
                    "moduleName", "ObservedModule",
                    "code", "Sub IntendedFromOldSource()\nEnd Sub");
                var stale = executor.ExecuteManual(observedWrite, tools, settings, false, false, session);
                AssertEqual("stale_vba_module", stale.ErrorCode, "runtime uses a prior read snapshot without a model hash argument");
                AssertContains(stale.DataJson, "reconcileBeforeOverwrite", "stale whole write explains reconciliation");
                AssertContains(adapter.GetVbaModuleCode("ObservedModule"), "ExternalChange", "stale whole write preserves external code");
                AssertContains(ReadVbaSource(executor, session, "ObservedModule").Text,
                    "ExternalChange",
                    "whole-source overwrite requires a fresh current read after stale detection");
                AssertTrue(executor.ExecuteManual(observedWrite, tools, settings, false, false, session).Success,
                    "intentional overwrite succeeds only after the current source is visible");
                AssertContains(adapter.GetVbaModuleCode("ObservedModule"), "IntendedFromOldSource", "intentional retry writes complete source");
            });
        }

        private static void VbaRenameIntentIsStrictAndAtomic()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                const string source = "Option Explicit\nPublic Sub Run()\nEnd Sub";
                var adapter = new FakeOfficeAdapter();
                adapter.SetVbaModule("OldModule", source, "ClassModule");
                var store = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, store, new SkillStore(paths));
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var writeSchema = JObject.Parse(tools.Single(item =>
                    item.Id == "common.vba_write_module").ArgumentSchemaJson);
                AssertTrue(writeSchema["anyOf"] == null &&
                    writeSchema.SelectToken("properties.newModuleName") == null,
                    "whole-source write no longer carries a rename branch");
                var renameSchema = JObject.Parse(tools.Single(item =>
                    item.Id == "common.vba_rename_module").ArgumentSchemaJson);
                AssertEqual(2, ((JObject)renameSchema["properties"]).Properties().Count(),
                    "rename exposes only source and destination names");
                AssertEqual(2, ((JArray)renameSchema["required"]).Count,
                    "both semantic rename names are required");
                AssertTrue(renameSchema.SelectToken("properties.mode") == null &&
                    renameSchema.SelectToken("properties.code") == null &&
                    renameSchema.SelectToken("properties.componentType") == null,
                    "rename cannot mix whole-source write arguments");

                var renamed = executor.ExecuteManual(
                    Command(
                        "common.vba_rename_module",
                        "moduleName", "OldModule",
                        "newModuleName", "RenamedModule"),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    NewSession(adapter));

                AssertTrue(renamed.Success, "public rename intent succeeds");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("OldModule"), "old identity is absent");
                AssertEqual(source, adapter.GetVbaModuleCode("RenamedModule"), "rename preserves exact source");
                var data = JObject.Parse(renamed.DataJson ?? "{}");
                AssertEqual("OldModule", (string)data["previousModuleName"], "result returns previous name");
                AssertEqual("RenamedModule", (string)data["moduleName"], "result returns actual new name");
                AssertEqual("rename", (string)data["mode"], "result identifies rename branch");
                AssertTrue(data["journalStatus"] == null,
                    "rename result does not expose internal journal status");
                AssertTrue(!string.IsNullOrWhiteSpace((string)data["mutationId"]),
                    "rename result keeps mutation correlation evidence");
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

                var invalid = executor.ExecuteManual(
                    Command(
                        "common.vba_rename_module",
                        "moduleName", "RenamedModule",
                        "newModuleName", "AnotherModule",
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
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var command = Command(
                    "common.vba_rename_module",
                    "moduleName", "RenameSource",
                    "newModuleName", "RenameTarget");
                var pending = PrepareVbaNative(executor, session, command);
                var waiting = ToolRunResultFactory.Create(pending.Record);
                AssertEqual("awaiting_confirmation", waiting.Status, "rename waits for confirmation");
                AssertContains(waiting.Message, "RenameSource", "confirmation identifies source");
                AssertContains(waiting.Message, "RenameTarget", "confirmation identifies destination");

                adapter.SetVbaModule("RenameTarget", "Sub External()\nEnd Sub", "StdModule");
                var stale = ToolRunResultFactory.Create(
                    ConfirmVbaNative(pending));
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
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };
                var result = executor.ExecuteManual(
                    Command("common.vba_delete_module", "moduleName", "Module1"),
                    tools,
                    settings,
                    false,
                    false,
                    session);

                AssertTrue(result.Success, "delete succeeds without a public read");
                AssertTrue(adapter.CountVbaCalls(FakeVbaOperation.ReadModule) > 0,
                    "runtime reads the module internally");
                AssertTrue(adapter.CountVbaCalls(FakeVbaOperation.DeleteModule) > 0,
                    "runtime performs the delete after validation");
                AssertEqual(1, backupStore.List("Excel", "doc").Count, "delete keeps one rollback backup");

                adapter.SetVbaModule("Module2", "Sub BeforeRead()\nEnd Sub", "StdModule");
                AssertContains(ReadVbaSource(executor, session, "Module2").Text, "BeforeRead",
                    "optional resource read records the delete observation");
                adapter.SetVbaModule("Module2", "Sub ChangedAfterRead()\nEnd Sub", "StdModule");
                var deleteObserved = Command("common.vba_delete_module", "moduleName", "Module2");
                var stale = executor.ExecuteManual(deleteObserved, tools, settings, false, false, session);
                AssertEqual("stale_vba_module", stale.ErrorCode, "runtime uses an optional prior delete snapshot without a hash argument");
                ReadVbaSource(executor, session, "Module2");
                AssertContains(adapter.GetVbaModuleCode("Module2"), "ChangedAfterRead", "stale delete keeps the changed module");
                AssertTrue(executor.ExecuteManual(deleteObserved, tools, settings, false, false, session).Success,
                    "same-tool retry deletes after the stale warning");
                AssertEqual(2, backupStore.List("Excel", "doc").Count, "retried delete keeps the current source backup");

                adapter.SetVbaModule("Module3", "Sub SeenInFirstChat()\nEnd Sub", "StdModule");
                AssertContains(ReadVbaSource(executor, session, "Module3").Text, "SeenInFirstChat",
                    "first chat records its optional resource observation");
                adapter.SetVbaModule("Module3", "Sub ChangedForSecondChat()\nEnd Sub", "StdModule");
                var secondSession = NewSession(adapter);
                var secondChatDelete = executor.ExecuteManual(
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
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var session = NewSession(adapter);
                var command = Command("common.vba_delete_module", "moduleName", "Module1");
                var firstPending = PrepareVbaNative(executor, session, command);
                AssertEqual("awaiting_confirmation",
                    ToolRunResultFactory.Create(firstPending.Record).Status,
                    "delete waits with a bound guard");

                adapter.RuntimeDocumentKeyValue = "runtime-other-document";
                var sameDocument = ToolRunResultFactory.Create(
                    ConfirmVbaNative(firstPending));
                AssertTrue(sameDocument.Success, "stable document key tolerates a changed runtime identity");

                adapter.VbaModuleCode = "Sub Main()\nEnd Sub";
                var changedCommand = Command("common.vba_delete_module", "moduleName", "Module1");
                var secondPending = PrepareVbaNative(
                    executor, session, changedCommand);
                AssertEqual("awaiting_confirmation",
                    ToolRunResultFactory.Create(secondPending.Record).Status,
                    "second delete waits with a bound guard");
                adapter.DocumentKeyValue = "other-document";
                adapter.RuntimeDocumentKeyValue = "runtime-different-document";
                session.DocumentKey = adapter.DocumentKeyValue;
                var blocked = ToolRunResultFactory.Create(
                    ConfirmVbaNative(secondPending));

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
                var session = NewSession(adapter);
                var command = new ToolInvocation { ToolId = executor.VbaToolId("vba_apply_patch") };
                command.Arguments["moduleName"] = "Module2";
                command.Arguments["patch"] = new JArray
                {
                    new JObject
                    {
                        ["find"] = "\"old\"",
                        ["text"] = "\"new\""
                    },
                    new JObject
                    {
                        ["find"] = "End Sub",
                        ["text"] = "End Sub\nPublic Sub Added()\nEnd Sub"
                    }
                };

                var result = executor.ExecuteManual(command, new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(adapter.HostName)), new AppSettings { AutoConfirmToolActions = true }, false, false, session);

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

                AssertTrue(ReadVbaSource(executor, session, "Module2").Complete,
                    "complete source refreshes the module before the next patch");

                var mixedNoOp = executor.ExecuteManual(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module2",
                        "patch", new JArray(
                            new JObject { ["find"] = "\"new\"", ["text"] = "\"new\"" },
                            new JObject { ["find"] = "Sub Run()", ["text"] = "Sub RunFixed()" })),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertTrue(mixedNoOp.Success, "already-satisfied hunk does not abort later exact replacements");
                AssertContains(adapter.GetVbaModuleCode("Module2"), "Sub RunFixed()", "later hunk still changes source");

                AssertTrue(ReadVbaSource(executor, session, "Module2").Complete,
                    "complete source refreshes the module before a no-op patch");

                var writesBeforeNoOp = adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule);
                var backupsBeforeNoOp = backupStore.List("Excel", "doc").Count;
                var allNoOp = executor.ExecuteManual(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module2",
                        "patch", new JArray(new JObject
                        {
                            ["find"] = "Sub RunFixed()",
                            ["text"] = "Sub RunFixed()"
                        })),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertTrue(allNoOp.Success, "all-no-op patch completes successfully");
                AssertContains(allNoOp.DataJson, "\"changed\":false", "all-no-op patch reports its outcome");
                AssertEqual(writesBeforeNoOp, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule),
                    "all-no-op patch performs no backend write");
                AssertEqual(backupsBeforeNoOp, backupStore.List("Excel", "doc").Count,
                    "all-no-op patch creates no mutation backup");

                var malformed = new ToolInvocation { ToolId = executor.VbaToolId("vba_apply_patch") };
                malformed.Arguments["moduleName"] = "Module2";
                malformed.Arguments["patch"] = "[{\"op\":\"replace\"}}trailing";
                var malformedResult = executor.ExecuteManual(malformed, new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(adapter.HostName)), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(!malformedResult.Success, "malformed patch rejected");
                AssertContains(malformedResult.Message,
                    "$.patch has the wrong JSON type",
                    "native schema gate rejects a stringified patch");

                var emptyAnchor = Command(
                    "common.vba_apply_patch",
                    "moduleName", "Module2",
                    "patch", new JArray(new JObject { ["find"] = string.Empty, ["text"] = "Debug.Print 1" }));
                var emptyAnchorResult = executor.ExecuteManual(emptyAnchor, new List<ToolCatalogEntry>(OfficeToolCatalog.ForHost(adapter.HostName)), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(!emptyAnchorResult.Success, "empty exact block rejected");
                AssertContains(emptyAnchorResult.Message, "shorter than minLength", "empty exact block schema diagnostic");
            });
        }

        private static void VbaExactPatchPreservesCompleteLines()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "Option Explicit\r\nPublic Sub Run()\r\nDebug.Print 1\r\nEnd Sub";
                var result = executor.ExecuteManual(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(
                            new JObject
                            {
                                ["find"] = "Debug.Print 1",
                                ["text"] = "Dim value As Long\nDebug.Print 1"
                            },
                            new JObject
                            {
                                ["find"] = "Debug.Print 1",
                                ["text"] = "Debug.Print 1\nvalue = 2"
                            })),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);

                AssertTrue(result.Success, "ordered exact hunks patch successfully: " + result.Message);
                AssertEqual(
                    "Option Explicit\r\nPublic Sub Run()\r\nDim value As Long\r\nDebug.Print 1\r\nvalue = 2\r\nEnd Sub",
                    adapter.VbaModuleCode,
                    "exact hunks preserve CRLF and untouched source");
            });

            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "A\r\nB\r\n";
                var appended = executor.ExecuteManual(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["find"] = "B",
                            ["text"] = "B\nC"
                        })),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
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
                var appended = executor.ExecuteManual(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["find"] = "A",
                            ["text"] = "A\nB"
                        })),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
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
                adapter.QueueVbaModuleSnapshot(new RNAssistant.Office.Domains.Vba.VbaModuleSnapshot
                {
                    Name = "Module1",
                    Code = null
                });
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths));
                var command = Command("common.vba_write_module", "moduleName", "Module1", "code", "Sub Changed()\nEnd Sub", "mode", "updateOnly");

                var result = executor.ExecuteManual(command, OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "write blocked");
                AssertEqual("vba_read_invalid", result.ErrorCode, "invalid live state blocks write");
                AssertEqual("Sub Original()\nEnd Sub", adapter.VbaModuleCode, "module unchanged");
                AssertEqual(1, adapter.VbaBackendCalls.Count,
                    "only backup read executed");

                adapter.VbaBackendCalls.Clear();
                var create = Command("common.vba_write_module", "moduleName", "NewModule", "code", "Sub NewMacro()\nEnd Sub", "mode", "createOnly");
                var created = executor.ExecuteManual(create, OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(created.Success, "missing module can be created");
                AssertContains(adapter.GetVbaModuleCode("NewModule"), "NewMacro", "new module code");

                var missingPatch = Command(
                    "common.vba_apply_patch",
                    "moduleName", "MissingModule",
                    "patch", new Newtonsoft.Json.Linq.JArray(new Newtonsoft.Json.Linq.JObject
                    {
                        ["find"] = "Option Explicit",
                        ["text"] = "Option Explicit\nSub Added()\nEnd Sub"
                    }));
                var missingPatchResult = executor.ExecuteManual(missingPatch, OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);
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

                var result = executor.ExecuteManual(command, OfficeToolCatalog.ForHost(adapter.HostName).ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "line-number patch mode rejected by schema");
                AssertEqual("invalid_arguments", result.ErrorCode, "removed addressing mode fails before patch execution");
                AssertEqual("Sub Main()\nEnd Sub", adapter.VbaModuleCode, "removed addressing mode leaves module unchanged");
            });
        }

        private static void VbaWriteRejectsDuplicateProcedures()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Original()\nEnd Sub";
                var backupStore = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var tools = OfficeToolCatalog.ForHost(adapter.HostName)
                    .Concat(executor.GetControllerTools()).ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };
                var session = NewSession(adapter);
                var duplicateCode =
                    "Option Explicit\n" +
                    "Public Sub RenderReport()\n" +
                    "End Sub\n\n" +
                    "Private Sub RenderReport()\n" +
                    "End Sub";

                var rejected = executor.ExecuteManual(
                    Command(
                        "common.vba_write_module",
                        "moduleName", "Module1",
                        "code", duplicateCode,
                        "mode", "updateOnly"),
                    tools,
                    settings,
                    false,
                    false,
                    session);

                AssertTrue(!rejected.Success, "duplicate whole-source write rejected");
                AssertEqual("vba_code_invalid", rejected.ErrorCode,
                    "duplicate write uses stable source validation code");
                AssertContains(rejected.Message, "duplicate procedure/property declaration",
                    "duplicate diagnostic tells the model to reconcile source");
                AssertEqual("Sub Original()\nEnd Sub", adapter.VbaModuleCode,
                    "duplicate write leaves module unchanged");
                AssertEqual(0, backupStore.List(adapter.HostName, adapter.DocumentKey).Count,
                    "duplicate write creates no backup");
                AssertEqual(0, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule),
                    "duplicate write does not dispatch replacement");

                var propertyCode =
                    "Option Explicit\n" +
                    "Private mValue As Long\n\n" +
                    "Public Property Get Value() As Long\n" +
                    "    Value = mValue\n" +
                    "End Property\n\n" +
                    "Public Property Let Value(ByVal input As Long)\n" +
                    "    mValue = input\n" +
                    "End Property";
                var accepted = executor.ExecuteManual(
                    Command(
                        "common.vba_write_module",
                        "moduleName", "Module1",
                        "code", propertyCode,
                        "mode", "updateOnly"),
                    tools,
                    settings,
                    false,
                    false,
                    session);
                AssertTrue(accepted.Success,
                    "matching Get/Let property accessors are not false duplicates");
                AssertContains(adapter.VbaModuleCode, "Property Let Value",
                    "valid property source is written");
            });
        }

        private static void VbaPatchRejectsStaleExactSource()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "A\nB\nC";
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var session = NewSession(adapter);
                AssertEqual("A\nB\nC", ReadVbaSource(executor, session, "Module1").Text,
                    "initial source resource snapshot read");

                var first = executor.ExecuteManual(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
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
                        ["find"] = "A\nB",
                        ["text"] = "A\nY"
                    }));
                var rejected = executor.ExecuteManual(
                    staleSource,
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertEqual("vba_snapshot_refresh_required", rejected.ErrorCode,
                    "a second mutation cannot use the model's stale pre-write source");
                AssertEqual("A\nX\nB\nC", adapter.VbaModuleCode, "stale exact patch leaves current module intact");
                AssertEqual(1, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule),
                    "stale exact hunk never reaches the backend writer");
            });
        }

        private static void VbaPatchRejectsDuplicateProcedures()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                const string original =
                    "Option Explicit\n" +
                    "Public Sub RenderReport()\n" +
                    "    Debug.Print \"current\"\n" +
                    "End Sub";
                adapter.VbaModuleCode = original;
                var backupStore = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var tools = OfficeToolCatalog.ForHost(adapter.HostName)
                    .Concat(executor.GetControllerTools()).ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };
                var session = NewSession(adapter);
                var find =
                    "Public Sub RenderReport()\n" +
                    "    Debug.Print \"current\"\n" +
                    "End Sub";
                var text = find + "\n\n" +
                    "Private Sub RenderReport()\n" +
                    "    Debug.Print \"duplicate\"\n" +
                    "End Sub";

                var rejected = executor.ExecuteManual(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["find"] = find,
                            ["text"] = text
                        })),
                    tools,
                    settings,
                    false,
                    false,
                    session);

                AssertTrue(!rejected.Success, "duplicate-producing patch rejected");
                AssertEqual("vba_code_invalid", rejected.ErrorCode,
                    "duplicate patch uses stable source validation code");
                AssertContains(rejected.Message, "duplicate procedure/property declaration",
                    "duplicate patch reports source-level duplicate");
                AssertEqual(original, adapter.VbaModuleCode,
                    "duplicate patch leaves module unchanged");
                AssertEqual(0, backupStore.List(adapter.HostName, adapter.DocumentKey).Count,
                    "duplicate patch creates no backup");
                AssertEqual(0, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule),
                    "duplicate patch does not dispatch replacement");
            });
        }

        private static void VbaPurePatchTextContract()
        {
            foreach (var newline in new[] { "\n", "\r\n", "\r" })
            {
                var source = "' Контекст" + newline + "Debug.Print \"C:\\temp\\n\"" + newline + "End Sub";
                var result = VbaPatchEngine.Replace(source, "Debug.Print \"C:\\temp\\n\"\nEnd Sub",
                    "Debug.Print \"C:\\temp\\r\\n\"\nEnd Sub");
                AssertEqual(VbaPatchStatus.Changed, result.Status, "plain text engine changes exact match");
                AssertEqual(source.Replace("C:\\temp\\n", "C:\\temp\\r\\n"), result.Text,
                    "literal backslashes and surrounding source preserved for each newline style");
                AssertEqual(VbaPatchStatus.Unchanged,
                    VbaPatchEngine.Replace(source, source, source).Status, "no-op is distinct from change");
                var overlappingLines = "A" + newline + "A" + newline + "A";
                var overlap = VbaPatchEngine.Replace(overlappingLines, "A\nA", "B");
                AssertEqual(VbaPatchStatus.Ambiguous, overlap.Status, "overlap rejected after newline matching");
                AssertEqual(2, overlap.MatchCount, "both overlapping line blocks counted");
                AssertEqual(overlappingLines, overlap.Text, "overlapping line patch preserves original source");
            }
            foreach (var sample in new[]
            {
                new { Source = "A\nA", Find = "A", Count = 2 },
                new { Source = "aaaa", Find = "aaa", Count = 2 },
                new { Source = "ababa", Find = "aba", Count = 2 },
                new { Source = "aaaaa", Find = "aaa", Count = 3 }
            })
            {
                var ambiguous = VbaPatchEngine.Replace(sample.Source, sample.Find, "B");
                AssertEqual(VbaPatchStatus.Ambiguous, ambiguous.Status, "all starting offsets count for uniqueness");
                AssertEqual(sample.Count, ambiguous.MatchCount, "full match count retained for tool guidance");
                AssertEqual(sample.Source, ambiguous.Text, "ambiguity preserves original text");
                AssertEqual(VbaPatchStatus.Ambiguous,
                    VbaPatchEngine.Replace(sample.Source, sample.Find, sample.Find).Status,
                    "unchanged replacement still requires one unambiguous match");
            }
            AssertEqual("B", VbaPatchEngine.Replace("aaa", "aaa", "B").Text, "full source remains a unique match");
            AssertEqual(VbaPatchStatus.NotFound, VbaPatchEngine.Replace("A", "B", "C").Status, "stale source rejected");
            AssertEqual(VbaPatchStatus.EmptyFind, VbaPatchEngine.Replace("A", null, "B").Status, "empty find rejected");
            AssertEqual(string.Empty, VbaPatchEngine.Replace("A", "A", null).Text, "null replacement is deletion");
        }

        private static void VbaLiveHashPreservesLineStructure()
        {
            AssertEqual(
                VbaTextCanonicalizer.LiveCodeSha256("Option Explicit\r\nSub Main()\r\nEnd Sub"),
                VbaTextCanonicalizer.LiveCodeSha256("Option Explicit\nSub Main()\nEnd Sub\n"),
                "line ending transport is normalized");
            AssertTrue(
                !string.Equals(VbaTextCanonicalizer.LiveCodeSha256("\nOption Explicit"), VbaTextCanonicalizer.LiveCodeSha256("Option Explicit"), StringComparison.Ordinal),
                "leading blank line changes live hash");
            AssertTrue(
                !string.Equals(VbaTextCanonicalizer.LiveCodeSha256("Option Explicit\n\n"), VbaTextCanonicalizer.LiveCodeSha256("Option Explicit\n"), StringComparison.Ordinal),
                "trailing blank line changes live hash");
            AssertTrue(
                !string.Equals(VbaTextCanonicalizer.LiveCodeSha256("' RNAssistantSession: id=x\nOption Explicit"), VbaTextCanonicalizer.LiveCodeSha256("Option Explicit"), StringComparison.Ordinal),
                "runtime marker changes live hash");
            AssertEqual(
                VbaTextCanonicalizer.VbeComparableCodeSha256("Sub Main()\n    Debug.Print \"Value\"\nEnd Sub"),
                VbaTextCanonicalizer.VbeComparableCodeSha256("sub Main ( )\r\nDebug.Print \"Value\"\r\nend sub\r\n\r\n"),
                "VBE-only formatting is comparable");
            AssertTrue(
                !string.Equals(
                    VbaTextCanonicalizer.VbeComparableCodeSha256("Debug.Print \"Value\""),
                    VbaTextCanonicalizer.VbeComparableCodeSha256("Debug.Print \"Changed\""),
                    StringComparison.Ordinal),
                "string literal changes remain significant");
            AssertTrue(
                !string.Equals(
                    VbaTextCanonicalizer.VbeComparableCodeSha256("End Sub"),
                    VbaTextCanonicalizer.VbeComparableCodeSha256("EndSub"),
                    StringComparison.Ordinal),
                "token boundaries remain significant");
            var literal = "Debug.Print \"C:\\temp\\n \"\"Value\"\"\" ' Сохранить Регистр\\r\\n";
            AssertEqual(literal, VbaTextCanonicalizer.NormalizeLiveCode(literal),
                "live normalization does not decode literal escapes");
            AssertEqual(literal, VbaTextCanonicalizer.NormalizePackageCode(literal),
                "package normalization preserves strings and comments");
            AssertTrue(VbaTextCanonicalizer.VbeComparableCodeSha256(literal) !=
                VbaTextCanonicalizer.VbeComparableCodeSha256(literal.Replace("Value", "value")),
                "quoted literal case remains significant");
            AssertTrue(VbaTextCanonicalizer.VbeComparableCodeSha256(literal) !=
                VbaTextCanonicalizer.VbeComparableCodeSha256(literal.Replace("Регистр", "регистр")),
                "apostrophe comment text remains significant");
            AssertTrue(TextPatternEngine.Sha256("Option Explicit\r\n") !=
                VbaTextCanonicalizer.LiveCodeSha256("Option Explicit\r\n"),
                "raw transport bytes and normalized live identity are not interchangeable");
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
                        ["find"] = "Sub Main()\nDebug.Print \"old\"",
                        ["text"] = "Sub Main()\nDebug.Print \"new\""
                    }
                };
                var command = Command(
                    "common.vba_apply_patch",
                    "moduleName", "Module1",
                    "patch", patch);

                var result = executor.ExecuteManual(
                    command,
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);

                AssertTrue(result.Success, "VBE-normalized patch result");
                AssertContains(adapter.VbaModuleCode, "\"new\"", "patch was applied");
                var data = JObject.Parse(result.DataJson);
                AssertEqual(true, (bool)data["vbeNormalized"], "VBE normalization reported");
                AssertEqual(VbaTextCanonicalizer.LiveCodeSha256(adapter.VbaModuleCode), (string)data["codeSha256"], "actual read-back hash returned");
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
                VbaTextCanonicalizer.VbeComparableCodeSha256("Sub Changed()\nEnd Sub"),
                VbaTextCanonicalizer.VbeComparableCodeSha256(component.CodeModule.Code),
                "read-back code is VBE-equivalent");
        }

        private static void VbaProjectRenamePreservesComponentIdentity()
        {
            const string source = "Option Explicit\nPublic Sub Main()\nEnd Sub";
            var document = new FakeVbaDocumentObject();
            var component = document.VBProject.VBComponents.Seed("OldModule", source, 2);
            var designer = component.Designer;
            var hash = VbaTextCanonicalizer.LiveCodeSha256(source);

            var result = VbaProjectSupport.RenameModule(
                document,
                "OldModule",
                "NewModule",
                hash,
                "ClassModule");

            AssertTrue(result.Success, "COM rename succeeds");
            var renamed = document.VBProject.VBComponents.Cast<FakeVbaComponent>().Single();
            AssertTrue(object.ReferenceEquals(component, renamed), "rename preserves the VBComponent object");
            AssertTrue(object.ReferenceEquals(designer, renamed.Designer), "rename preserves component metadata/designer identity");
            AssertEqual("NewModule", renamed.Name, "COM rename changes only the component name");
            AssertEqual(2, renamed.Type, "COM rename preserves component type");
            AssertEqual(source, renamed.CodeModule.Code, "COM rename preserves source");
            try
            {
                VbaProjectSupport.ReadModule(document, "OldModule", 1000);
                throw new InvalidOperationException(
                    "old VBA component name remained readable after rename");
            }
            catch (RNAssistant.Office.Domains.Vba.VbaBackendException ex)
            {
                AssertEqual("vba_module_not_found", ex.ErrorCode,
                    "old name is absent after rename");
            }

            var typeRace = VbaProjectSupport.RenameModule(
                document,
                "NewModule",
                "WrongTypeTarget",
                hash,
                "StdModule");
            AssertEqual("stale_vba_module", typeRace.ErrorCode,
                "COM rename compare-and-swap rejects source type drift");
            AssertEqual("NewModule", renamed.Name,
                "source type mismatch leaves component identity unchanged");

            document.VBProject.VBComponents.Seed("Collision", "Sub Existing()\nEnd Sub");
            var collision = VbaProjectSupport.RenameModule(document, "NewModule", "Collision", hash);
            AssertEqual("vba_module_exists", collision.ErrorCode, "COM rename rejects destination collision");
            AssertEqual("NewModule", renamed.Name, "collision leaves source identity unchanged");

            var stale = VbaProjectSupport.RenameModule(
                document,
                "NewModule",
                "AnotherName",
                VbaTextCanonicalizer.LiveCodeSha256("Sub Stale()\nEnd Sub"));
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
            document.Name = "Bound O'Brien.xlsm";
            AssertEqual("'Bound O''Brien.xlsm'!Module1.Run",
                VbaProjectSupport.QualifyDocumentMacroName(
                    document,
                    "'Other.xlsm'!Module1.Run"),
                "macro dispatch is forced to the exact bound document");
            var component = document.VBProject.VBComponents.Seed("Module1", "Sub ExternalChange()\nEnd Sub");
            var staleHash = VbaTextCanonicalizer.LiveCodeSha256("Sub EarlierSnapshot()\nEnd Sub");

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
            AssertEqual("MSForm", read.ComponentType,
                "COM UserForm type is listed canonically");

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
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };
                var session = NewSession(adapter);
                var publicCreate = executor.ExecuteManual(
                    Command("common.vba_write_module", "moduleName", "UserForm2", "componentType", "MSForm", "code", "Option Explicit\n", "mode", "createOnly"),
                    tools,
                    settings,
                    false,
                    false,
                    session);
                AssertTrue(publicCreate.Success, "public UserForm create succeeds");

                AssertTrue(ReadVbaSource(executor, session, "UserForm2").Complete,
                    "complete UserForm code read refreshes the model snapshot");

                var publicEdit = executor.ExecuteManual(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "UserForm2",
                        "patch", new JArray(new JObject
                        {
                            ["find"] = "Option Explicit",
                            ["text"] = "Option Explicit\nPrivate Sub UserForm_Activate()\nEnd Sub"
                        })),
                    tools,
                    settings,
                    false,
                    false,
                    session);
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
            AssertContains(editing.BodyMarkdown, "RUNTIME_CONTEXT.document.vba_project_target",
                "project-wide VBA inspection starts directly from the bound project target");
            AssertContains(editing.BodyMarkdown, "filtered search, not project inventory",
                "VBA skill does not mistake one query match for the full project");
            AssertContains(editing.BodyMarkdown, "first `VBA project` target",
                "VBA discovery fallback selects the aggregate project target");
            AssertContains(editing.BodyMarkdown, "one successful result contains the complete representation",
                "VBA skill relies on whole model-facing resource reads");
            AssertContains(editing.BodyMarkdown, "one ordered patch call",
                "VBA skill batches same-snapshot hunks into one atomic mutation");
            AssertContains(editing.BodyMarkdown, "before a second source mutation",
                "VBA skill requires a fresh complete model-visible snapshot between writes");
            AssertContains(editing.BodyMarkdown, "never backslash escaping",
                "VBA skill distinguishes JSON transport escaping from VBA string syntax");
            AssertContains(editing.BodyMarkdown, "never creates a missing module", "patch remains existing-only");
            AssertContains(editing.BodyMarkdown, "repeat the exact anchor block", "insertions use explicit exact replacement text");
            AssertContains(editing.BodyMarkdown, "common.vba_rename_module", "skill explains the strict rename intent");
            AssertTrue(editing.BodyMarkdown.IndexOf("mode=rename", StringComparison.Ordinal) < 0,
                "skill no longer teaches the retired write branch");
            AssertContains(editing.BodyMarkdown, "Never imitate rename with write plus delete", "skill forbids unsafe rename emulation");
            AssertContains(editing.BodyMarkdown, "does not rewrite explicit references", "skill warns about qualified VBA references");
            AssertContains(editing.BodyMarkdown, "Option Explicit", "skill includes baseline VBA code quality");
            AssertContains(editing.BodyMarkdown, "complete Option block", "skill preserves all leading Option directives");
            AssertContains(editing.BodyMarkdown, "duplicate procedure/property declarations",
                "skill requires final-source duplicate self-checks before VBA write tools");
            AssertContains(editing.BodyMarkdown, "PtrSafe", "skill covers Office x64 declarations");
            AssertContains(editing.BodyMarkdown, "VBE-equivalent source read-back", "skill describes normalized verification precisely");
            AssertContains(editing.BodyMarkdown, "does not prove VBA compilation or runtime behavior", "read-back is not overstated as functional validation");
            AssertContains(editing.BodyMarkdown, "common.vba_userform_authoring", "general VBA editing points to the focused UserForm profile");
        }

        private static void VbaResourcesReadBoundedSource()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = string.Join("\n", Enumerable.Range(1, 80)
                    .Select(index => "line" + index).ToArray());
                var session = NewSession(adapter);
                var component = VbaComponent(executor, session, "Module1");
                AssertTrue(component.Reference.Uri.StartsWith("rna://vba/", StringComparison.Ordinal),
                    "VBA component uses the canonical provider URI");
                AssertTrue(component.Reference.Uri.IndexOf("Module1", StringComparison.OrdinalIgnoreCase) < 0,
                    "VBA component URI does not expose its module name");

                var first = ReadResource(
                    executor.ResourceGateway,
                    session,
                    component.Reference.Uri,
                    ResourceRepresentations.Source,
                    null,
                    128).Result;
                AssertEqual(adapter.VbaModuleCode.Substring(0, 128), first.Text,
                    "VBA resource returns the exact first bounded chunk");
                AssertEqual(128, first.ReturnedCharacters, "VBA resource read obeys maxChars");
                AssertTrue(first.Truncated && !string.IsNullOrWhiteSpace(first.NextCursor),
                    "VBA resource read exposes a continuation cursor");
                AssertEqual(VbaTextCanonicalizer.LiveCodeSha256(adapter.VbaModuleCode), first.ContentSha256,
                    "VBA resource read carries the full live source hash");

                var second = ReadResource(
                    executor.ResourceGateway,
                    session,
                    component.Reference.Uri,
                    ResourceRepresentations.Source,
                    first.NextCursor,
                    128,
                    first.Resource.Reference.Revision).Result;
                AssertEqual(128, second.Offset, "VBA continuation starts at the exact prior cursor");
                AssertEqual(adapter.VbaModuleCode.Substring(128, 128), second.Text,
                    "VBA continuation returns the next exact source chunk");

                var removed = executor.ExecuteManual(
                    Command("excel.vba_read_lines", "moduleName", "Module1", "startLine", 3, "lineCount", 1),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings(),
                    false,
                    false);
                AssertEqual("unknown_tool", removed.ErrorCode, "removed range-read id is rejected");

                var removedFacade = executor.ExecuteManual(
                    Command("common.vba_read_module", "moduleName", "Module1"),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings(),
                    false,
                    false,
                    session);
                AssertEqual("unknown_tool", removedFacade.ErrorCode,
                    "removed public VBA read facade is rejected without an alias");

                adapter.VbaModuleCode = string.Join("\n", Enumerable.Range(1, 250).Select(index => "line" + index).ToArray());
                var whole = ReadVbaSource(executor, session, "Module1", 32000);
                AssertTrue(whole.Complete, "bounded resource read reports complete source when it fits");
                AssertContains(whole.Text, "line250",
                    "resource source read returns the complete module when it fits the bound");
            });
        }

        private static void VbaMutationRequiresCompleteModelVisibleRefresh()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode =
                    "Option Explicit\nSub Main()\nDebug.Print \"before\"\nEnd Sub\n" +
                    string.Join("\n", Enumerable.Range(1, 80)
                        .Select(index => "' padding " + index).ToArray());
                var session = NewSession(adapter);
                var tools = OfficeToolCatalog.ForHost(adapter.HostName)
                    .Concat(executor.GetControllerTools()).ToList();
                var component = VbaComponent(executor, session, "Module1");
                var initial = ReadResource(
                    executor.ResourceGateway,
                    session,
                    component.Reference.Uri,
                    ResourceRepresentations.Source,
                    null,
                    32000).Result;
                AssertTrue(initial.Complete, "initial complete source is model-visible");

                var first = executor.ExecuteManual(
                    Command("common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["find"] = "\"before\"",
                            ["text"] = "\"first\""
                        })),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertTrue(first.Success, "first mutation succeeds and verifies read-back: " + first.Message);

                var blocked = executor.ExecuteManual(
                    Command("common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["find"] = "\"first\"",
                            ["text"] = "\"second\""
                        })),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertEqual("vba_snapshot_refresh_required", blocked.ErrorCode,
                    "internal write verification does not refresh the model snapshot");

                var partial = ReadResource(
                    executor.ResourceGateway,
                    session,
                    component.Reference.Uri,
                    ResourceRepresentations.Source,
                    null,
                    128).Result;
                AssertTrue(!partial.Complete, "first bounded chunk is incomplete");
                var stillBlocked = executor.ExecuteManual(
                    Command("common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["find"] = "\"first\"",
                            ["text"] = "\"second\""
                        })),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertEqual("vba_snapshot_refresh_required", stillBlocked.ErrorCode,
                    "a partial source read cannot authorize another mutation");

                var refreshed = ReadResource(
                    executor.ResourceGateway,
                    session,
                    component.Reference.Uri,
                    ResourceRepresentations.Source,
                    null,
                    32000).Result;
                AssertTrue(refreshed.Complete, "complete current source refreshes model authority");
                var second = executor.ExecuteManual(
                    Command("common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["find"] = "\"first\"",
                            ["text"] = "\"second\""
                        })),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertTrue(second.Success, "mutation succeeds after complete current source read");
                AssertContains(adapter.VbaModuleCode, "\"second\"",
                    "second mutation applies to the refreshed source");
            });
        }

        private static void VbaPatchRejectsAmbiguousExactSource()
        {
            foreach (var sample in new[]
            {
                new { Source = "Sub One()\nEnd Sub\nSub Two()\nEnd Sub", Find = "End Sub", AutoConfirm = false, ChangeFirst = false },
                new { Source = "Sub One()\n' aaaa\nEnd Sub", Find = "aaa", AutoConfirm = false, ChangeFirst = false },
                new { Source = "Sub One()\n' aaaa\nEnd Sub", Find = "aaa", AutoConfirm = true, ChangeFirst = true }
            })
            {
                WithTempPaths(delegate(AppDataPaths paths)
                {
                    var adapter = new FakeOfficeAdapter { VbaModuleCode = sample.Source };
                    var store = new VbaJournalStore(paths);
                    var executor = new OfficeToolExecutor(adapter, store, new SkillStore(paths));
                    var operations = new JArray();
                    if (sample.ChangeFirst)
                    {
                        operations.Add(new JObject
                        {
                            ["find"] = "Sub One()",
                            ["text"] = "Sub Changed()"
                        });
                    }
                    operations.Add(new JObject
                    {
                        ["find"] = sample.Find,
                        ["text"] = "Debug.Print 1\nEnd Sub"
                    });
                    var result = executor.ExecuteManual(
                        Command("common.vba_apply_patch", "moduleName", "Module1", "patch", operations),
                        OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                        new AppSettings { AutoConfirmToolActions = sample.AutoConfirm },
                        false,
                        false);
                    AssertTrue(!result.Success, "ambiguous exact block rejected");
                    AssertEqual("vba_patch_ambiguous", result.ErrorCode, "ambiguous exact block error");
                    AssertTrue(!string.Equals("awaiting_confirmation", result.Status, StringComparison.OrdinalIgnoreCase), "ambiguous patch fails before confirmation");
                    AssertContains(result.Message, "contextBefore or contextAfter", "ambiguous exact block recovery guidance");
                    AssertEqual(2, (int)JObject.Parse(result.DataJson)["matchCount"], "tool reports overlapping matches");
                    AssertEqual(sample.Source, adapter.VbaModuleCode, "no earlier operation is partially written");
                    AssertEqual(0, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule),
                        "ambiguous patch never dispatches a write");
                    AssertEqual(0, store.List("Excel", "doc").Count, "ambiguous patch creates no backup");
                    AssertEqual(0, store.ListMutations("Excel", "doc").Count, "ambiguous patch creates no mutation journal entry");
                });
            }
        }

        private static void VbaPatchDisambiguatesWithExactContext()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor,
                FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode =
                    "Sub One()\nDebug.Print \"same\"\nEnd Sub\n" +
                    "Sub Two()\nDebug.Print \"same\"\nEnd Sub";
                var result = executor.ExecuteManual(
                    Command(
                        VbaToolCatalog.ApplyPatch,
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["find"] = "Debug.Print \"same\"",
                            ["text"] = "Debug.Print \"changed\"",
                            ["contextBefore"] = "Sub Two()\n",
                            ["contextAfter"] = "\nEnd Sub"
                        })),
                    OfficeToolCatalog.ForHost(adapter.HostName)
                        .Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);

                AssertTrue(result.Success,
                    "exact context disambiguates repeated source");
                AssertEqual(
                    "Sub One()\nDebug.Print \"same\"\nEnd Sub\n" +
                    "Sub Two()\nDebug.Print \"changed\"\nEnd Sub",
                    adapter.VbaModuleCode,
                    "context is verified but only find is replaced");
                AssertEqual(1,
                    adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule),
                    "context-qualified patch remains one whole-module dispatch");
            });

            var stale = VbaPatchEngine.Replace(
                "A\nX\nA", "A", "B", "missing\n", null);
            AssertEqual(VbaPatchStatus.NotFound, stale.Status,
                "stale exact context is rejected");
            AssertEqual("A\nX\nA", stale.Text,
                "stale context never mutates source");
        }

        private static void VbaExactPatchPreservesBoundaryNewlines()
        {
            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "A\nB\nC";
                var result = executor.ExecuteManual(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["find"] = "B",
                            ["text"] = "X\n"
                        })),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
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
                var result = executor.ExecuteManual(
                    Command(
                        "common.vba_apply_patch",
                        "moduleName", "Module1",
                        "patch", new JArray(new JObject
                        {
                            ["find"] = "B\n",
                            ["text"] = "X"
                        })),
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);
                AssertTrue(result.Success, "exact source may include a blank-line boundary");
                AssertEqual("A\nX\nC", adapter.VbaModuleCode, "only the exact matched bytes are replaced");
            });

            WithTempExecutor(delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                adapter.VbaModuleCode = "P\r\nA\r\nB\r\nS";
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var parsed = ParseV4(
                    "{\"message\":\"patch\",\"final\":false,\"tool_calls\":[{\"name\":\"common.vba_apply_patch\",\"arguments\":{\"moduleName\":\"Module1\",\"patch\":[{\"find\":\"A\\nB\",\"text\":\"\\nA\\n\\nB\\n\"}]}}]}",
                    tools.ToArray());
                AssertTrue(parsed.Success, "raw model JSON with escaped newlines parses");
                var invocation = new ToolInvocation
                {
                    ToolCallId = "fixture_vba",
                    ToolId = parsed.Response.ToolCalls[0].Name
                };
                ToolArgumentNormalizer.AddProperties(parsed.Response.ToolCalls[0].Arguments, invocation.Arguments);
                var result = executor.ExecuteManual(
                    invocation,
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

            foreach (var invalid in new[]
            {
                new { Code = "Sub Main()\nDebug.Print \\\"wrong\\\"\nEnd Sub", Reason = "C/JSON-style" },
                new { Code = "Sub Main()\nDebug.Print \"unclosed\nEnd Sub", Reason = "not closed" },
                new { Code = "Attribute VB_Name = \"Module1\"\nSub Main()\nEnd Sub", Reason = "export-file metadata" },
                new { Code = "Sub Main()\nIf value == 1 Then Debug.Print value\nEnd Sub", Reason = "non-VBA token" },
                new { Code = "#If VBA7 Then\nSub Main()\nEnd Sub", Reason = "#If block" }
            })
            {
                var invalidSyntax = VbaProjectSupport.ReplaceModule(
                    document, "Module1", invalid.Code, false);
                AssertTrue(!invalidSyntax.Success, "unsafe VBA syntax is rejected");
                AssertEqual("vba_code_invalid", invalidSyntax.ErrorCode,
                    "unsafe VBA syntax uses the validation error code");
                AssertContains(invalidSyntax.Message, invalid.Reason,
                    "unsafe VBA syntax explains the failure");
            }

            var validQuotes = VbaProjectSupport.ReplaceModule(
                document,
                "Module1",
                "Sub Main()\nDebug.Print \"C:\\\\\" & \"\"\"quoted\"\"\"\nEnd Sub",
                false);
            AssertTrue(validQuotes.Success,
                "VBA path backslashes and doubled string quotes remain valid");

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
                var tool = new ToolCatalogEntry
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
                    RiskLevel = 3,
                    PackageVersion = "1.0.0",
                    EntryPoint = "Main",
                    ArgumentOrder = new List<string> { "value" },
                    Components = new List<ToolPackageComponentDefinition>
                    {
                        new ToolPackageComponentDefinition
                        {
                            Name = "RNA_CustomVba",
                            Type = "StdModule",
                            FileName = "RNA_CustomVba.bas",
                            Code = code
                        }
                    }
                };
                tool.Policy = VbaPackageToolHandler.PolicyFor(tool);
                tool.Binding = VbaPackageToolHandler.BindingFor(tool);
                adapter.QueueVbaActionResult(FakeVbaOperation.RunMacro,
                    RNAssistant.Office.Domains.Vba.VbaBackendActionResult.Error(
                        "macro failed", null, "macro_failed", true));
                var command = Command(tool.Id, "value", "test");
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(new[] { tool }).ToList();

                var result = executor.ExecuteManual(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "custom macro result");
                AssertEqual("unknown", result.Status,
                    "post-dispatch custom macro failure does not infer effect");
                AssertEqual(false, result.Retryable,
                    "post-dispatch custom macro failure is not automatically retryable");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_CustomVba"), "temporary module cleaned after failure");
                AssertContains(result.DataJson, "sessionInstalled", "session lifecycle recorded");
            });
        }

        private static void VbaFailedModuleWriteRestoresCode()
        {
            var document = new FakeVbaDocumentObject();
            var component = document.VBProject.VBComponents.Seed("Module1", "Sub Original()\nEnd Sub");
            component.CodeModule.FailNextAdd = true;

            var failedReplacement = VbaProjectSupport.ReplaceModule(
                document, "Module1", "Sub Changed()\nEnd Sub", false);
            AssertTrue(!failedReplacement.Success, "failed VBA replacement is rejected");
            AssertEqual("vba_module_replace_failed", failedReplacement.ErrorCode,
                "replacement has a specific error code");
            AssertContains(failedReplacement.Message, "original code was restored",
                "atomic replacement diagnostic");
            AssertEqual("original-source-restored",
                (string)failedReplacement.Data["rollbackDisposition"],
                "verified rollback is structured evidence");
            AssertTrue(failedReplacement.Data["failure"] != null,
                "replacement failure retains exception diagnostics");

            AssertEqual(
                VbaTextCanonicalizer.NormalizeLiveCode("Sub Original()\nEnd Sub"),
                VbaTextCanonicalizer.NormalizeLiveCode(component.CodeModule.Code),
                "original code restored");

            var newDocument = new FakeVbaDocumentObject();
            newDocument.VBProject.VBComponents.FailNextAddedModuleWrite = true;
            var failedCreate = VbaProjectSupport.ReplaceModule(
                newDocument, "NewModule", "Sub Main()\nEnd Sub", true);
            AssertTrue(!failedCreate.Success, "failed new VBA module is rejected");
            AssertContains(failedCreate.Message, "incomplete module was removed",
                "new module cleanup diagnostic");
            AssertEqual("created-module-removed",
                (string)failedCreate.Data["rollbackDisposition"],
                "created module cleanup is structured evidence");
            AssertEqual(0, newDocument.VBProject.VBComponents.Count, "incomplete module removed");
        }

        private static void VbaReadBackRejectsWriteDrift()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Main()\nDebug.Print \"old\"\nEnd Sub";
                adapter.QueueVbaActionResult(
                    FakeVbaOperation.ReplaceModule,
                    RNAssistant.Office.Domains.Vba.VbaBackendActionResult.Ok(
                        "scripted success without write"));
                var backupStore = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, backupStore, new SkillStore(paths));
                var command = Command(
                    executor.VbaToolId("vba_apply_patch"),
                    "moduleName", "Module1",
                    "patch", new JArray(new JObject
                    {
                        ["find"] = "\"old\"",
                        ["text"] = "\"new\""
                    }));

                var result = executor.ExecuteManual(command, OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
                    new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(!result.Success, "write drift is not reported as success");
                AssertEqual("error", result.Status,
                    "verified unchanged state is a definite not-applied error");
                AssertEqual("vba_patch_verify_mismatch", result.ErrorCode, "write drift error code");
                AssertContains(result.DataJson, "expectedCodeSha256", "expected hash returned");
                AssertContains(result.DataJson, "actualCodeSha256", "actual hash returned");
                AssertEqual(1, backupStore.List("Excel", "doc").Count, "rollback backup retained");
                AssertEqual(VbaMutationStatuses.NotApplied,
                    backupStore.ListMutations("Excel", "doc").Single().Terminal.Status,
                    "read-back drift matching before state is durably not applied");
            });
        }

        private static void VbaReadBackRejectsDeleteDrift()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = new FakeOfficeAdapter();
                adapter.VbaModuleCode = "Sub Main()\nEnd Sub";
                adapter.QueueVbaActionResult(
                    FakeVbaOperation.DeleteModule,
                    RNAssistant.Office.Domains.Vba.VbaBackendActionResult.Ok(
                        "scripted success without delete"));
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths));
                var command = Command(
                    executor.VbaToolId("vba_delete_module"),
                    "moduleName", "Module1");

                var result = executor.ExecuteManual(command, OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
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
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var session = NewSession(adapter);
                var backupTarget = executor.ResourceGateway.Find(
                    session, null, "backups").Items.Single().Target;
                AssertEqual(backupTarget,
                    executor.VbaBackupSemanticTarget(backup.BackupId),
                    "internal UI adapter resolves the same readable backup target");
                var command = Command(executor.VbaToolId("vba_restore_backup"),
                    "target", backupTarget);

                var listedBackup = executor.ResourceGateway.List(
                    session,
                    VbaResourceProvider.ProviderName,
                    VbaResourceProvider.BackupKind,
                    null,
                    20).Items.Single();
                AssertEqual(backup.BackupId, listedBackup.Metadata["backupId"],
                    "backup resource metadata exposes restore id");
                AssertTrue(!listedBackup.Metadata.Values.Any(value =>
                    value != null && value.IndexOf("Restored", StringComparison.Ordinal) >= 0),
                    "backup listing does not duplicate source code into model context");
                AssertContains(ReadResource(
                    executor.ResourceGateway,
                    session,
                    listedBackup.Reference.Uri,
                    ResourceRepresentations.Source,
                    null,
                    32000).Result.Text, "Restored", "backup source is read only on demand");

                var missingSelector = executor.ExecuteManual(
                    Command(executor.VbaToolId("vba_restore_backup")),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertEqual("invalid_arguments", missingSelector.ErrorCode, "restore requires an explicit backup or module selector");

                var result = executor.ExecuteManual(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false, session);

                AssertTrue(result.Success, "restore result");
                AssertTrue(result.Message.IndexOf(backup.BackupId,
                        StringComparison.OrdinalIgnoreCase) < 0,
                    "restore message does not expose raw backup identity");
                AssertContains(adapter.VbaModuleCode, "Restored", "restored module code");
                AssertEqual(2, backupStore.List("Excel", "doc").Count, "restore preserves current version as backup");

                backupStore.Save("Excel", "doc", "Harness.xlsx", "RestoredClass", "ClassModule", "Option Explicit\nPublic Value As String");
                var classTarget = executor.ResourceGateway.Find(
                    session, "RestoredClass", "backups").Items.Single().Target;
                var classRestore = executor.ExecuteManual(
                    Command(executor.VbaToolId("vba_restore_backup"), "target", classTarget),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false,
                    session);
                AssertTrue(classRestore.Success, "missing class module restore result");
                var restoredClass = VbaComponent(executor, session, "RestoredClass");
                AssertEqual("ClassModule", restoredClass.Metadata["componentType"],
                    "restore preserves class module type");
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
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList();
                var command = Command("common.vba_restore_backup", "moduleName", "Module1");
                var pending = PrepareVbaNative(executor, session, command);
                var waiting = ToolRunResultFactory.Create(pending.Record);
                AssertEqual("awaiting_confirmation", waiting.Status, "restore waits for confirmation");
                AssertTrue(!command.Arguments.ContainsKey("backupId"),
                    "accepted arguments remain unchanged during preparation");
                AssertContains(waiting.DataJson, selected.BackupId, "restore confirmation identifies the pinned backup");
                AssertTrue(waiting.DataJson.IndexOf("Sub Selected", StringComparison.Ordinal) < 0,
                    "restore confirmation preview does not duplicate backup source");
                var origin = JsonConvert.DeserializeObject<VbaNativePreparedState>(pending.Record.PreparedStateJson).RestoredFrom;
                AssertTrue(origin.IsExact && origin.Uri.EndsWith("/backup/" + selected.BackupId, StringComparison.Ordinal),
                    "standalone backup has its own exact retained restore origin before confirmation");

                backupStore.Save("Excel", "doc", "Harness.xlsx", "Module1", "StdModule", "Sub Newer()\nEnd Sub");
                var restored = ToolRunResultFactory.Create(
                    ConfirmVbaNative(pending));

                AssertTrue(restored.Success, "pinned restore succeeds");
                AssertContains(adapter.VbaModuleCode, "Selected", "confirmation restores the originally selected backup");
                AssertTrue(adapter.VbaModuleCode.IndexOf("Newer", StringComparison.Ordinal) < 0, "newer backup does not replace confirmed target");
                var scope = executor.ResourceAuthority.Scope(session, true);
                var head = executor.ResourceAuthority.Store.GetHead(scope, VbaResourceProvider.ComponentIdentity(session.DocumentAuthorityId, "Module1"));
                var revisions = (IResourceRevisionStore)executor.ResourceAuthority.Store;
                AssertEqual(origin.Revision, revisions.GetRevision(scope, head.Revision).RestoredFrom.Revision, "terminal publication keeps the pinned backup origin");
                var retained = executor.ResourceGateway.Read(session, new ResourceReadRequest { Reference = origin,
                    Representation = ResourceRepresentations.Source, MaxChars = 32000 }).Result;
                AssertContains(retained.Text, "Sub Selected", "exact restore origin remains readable through the gateway");
            });
        }

        private static void VbaRestorePreservesExactSourceLineage()
        {
            WithTempPaths(paths =>
            {
                const string original = "Sub Original()\nEnd Sub";
                var adapter = new FakeOfficeAdapter { VbaModuleCode = original };
                var journal = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));
                var session = NewSession(adapter);
                var first = ReadVbaSource(executor, session, "Module1").Resource.Reference;
                adapter.VbaModuleCode = "Sub Intermediate()\nEnd Sub";
                ReadVbaSource(executor, session, "Module1");
                adapter.VbaModuleCode = original;
                var selected = ReadVbaSource(executor, session, "Module1").Resource.Reference;
                AssertTrue(first.Revision != selected.Revision, "equal bytes after observed drift have distinct lineage");
                var write = executor.ExecuteManual(Command(VbaToolCatalog.WriteModule, "moduleName", "Module1", "code", "Sub Current()\nEnd Sub"),
                    executor.GetControllerTools().ToList(), new AppSettings { AutoConfirmToolActions = true }, false, false, session);
                AssertTrue(write.Success, "guarded source mutation succeeded: " + write.Message);
                var backup = journal.List(adapter.HostName, adapter.DocumentKey).First();
                AssertEqual(selected.Revision, backup.SourceResource.Revision, "prepared journal pins the exact pre-image, not the first matching hash");
                var scope = executor.ResourceAuthority.Scope(session, true);
                var beforeRestore = executor.ResourceAuthority.Store.GetHead(scope, selected.Identity).Revision;
                var pending = PrepareVbaNative(executor, session, Command(VbaToolCatalog.RestoreBackup, "moduleName", "Module1"));
                var pinned = JsonConvert.DeserializeObject<VbaNativePreparedState>(pending.Record.PreparedStateJson).RestoredFrom;
                AssertEqual(selected.Revision, pinned.Revision, "confirmation pins the recorded module origin");
                var restored = ConfirmVbaNative(pending);
                AssertEqual(ToolExecutionOutcome.Ok, restored.Outcome, "restore succeeded");
                var head = executor.ResourceAuthority.Store.GetHead(scope, selected.Identity).Revision;
                var revisions = (IResourceRevisionStore)executor.ResourceAuthority.Store;
                var metadata = revisions.GetRevision(scope, head);
                AssertEqual(beforeRestore.Revision, metadata.Parent.Revision, "restore parent is the immediately preceding live head");
                AssertEqual(selected.Revision, metadata.RestoredFrom.Revision, "restore retains the selected exact origin despite an older equal-content revision");
                AssertTrue(head.Revision != selected.Revision && head.Revision != first.Revision, "restore publishes a new revision instead of reviving history");
                AssertEqual(ResourceEffectOutcome.Restored, executor.ResourceAuthority.Store.Capture(scope).Commits.Last().Effect.Outcome,
                    "shared authority owns the restored effect");
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

                var result = executor.ExecuteManual(
                    command,
                    OfficeToolCatalog.ForHost(adapter.HostName).Concat(executor.GetControllerTools()).ToList(),
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
                var session = NewSession(adapter);

                var list = ListVbaComponents(executor, session);

                AssertTrue(list.Items.Count > 0, "safe VBA resource access continues after reconciliation");
                AssertEqual(VbaMutationStatuses.Committed,
                    store.ListMutations("Excel", "doc").Single(item => item.Prepared.MutationId == applied.MutationId).Terminal.Status,
                    "live intended state reconciles as committed");
                AssertEqual(0, adapter.CountVbaCalls(FakeVbaOperation.ReplaceModule),
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
                ListVbaComponents(executor, session);
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
                adapter.QueueVbaFailure(FakeVbaOperation.ReadModule,
                    "VBA access denied.", "vba_access_error", false);
                ListVbaComponents(executor, session);
                AssertEqual(VbaMutationStatuses.Unknown,
                    store.ListMutations("Excel", "doc").Single(item => item.Prepared.MutationId == unknown.MutationId).Terminal.Status,
                    "unreadable live state reconciles as unknown");
            });
        }

        private static void VbaQueuedGuardReadsWaitForMutation()
        {
            WithTempPaths(paths =>
            {
                const string before = "Sub Main()\nDebug.Print \"before\"\nEnd Sub";
                const string after = "Sub Main()\nDebug.Print \"after\"\nEnd Sub";
                var adapter = new FakeOfficeAdapter { VbaModuleCode = before };
                var journal = new VbaJournalStore(paths);
                var first = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));
                var second = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(first.GetControllerTools()).ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };
                var firstSession = NewSession(adapter);
                var secondSession = NewSession(adapter);
                AssertEqual(before, ReadVbaSource(second, secondSession, "Module1").Text,
                    "second chat observes the source before another mutation");
                var queuedCommand = Command("common.vba_apply_patch", "moduleName", "Module1", "patch",
                    new JArray(new JObject { ["find"] = "\"before\"", ["text"] = "\"queued\"" }));
                AssertTrue(string.IsNullOrWhiteSpace(queuedCommand.RuntimeGuardJson), "queued call has not prepared a guard");

                using (var enteredWrite = new ManualResetEventSlim(false))
                using (var releaseWrite = new ManualResetEventSlim(false))
                using (var queuedStarted = new ManualResetEventSlim(false))
                using (var prematureRead = new ManualResetEventSlim(false))
                {
                    var mutationPaused = 0;
                    var writeCalls = 0;
                    adapter.BeforeVbaBackendCall = call =>
                    {
                        if (call.Operation == FakeVbaOperation.ReplaceModule)
                            Interlocked.Increment(ref writeCalls);
                        if (Volatile.Read(ref mutationPaused) != 0 &&
                            (call.Operation == FakeVbaOperation.ReadModule ||
                             call.Operation == FakeVbaOperation.ReadProject))
                            prematureRead.Set();
                    };
                    adapter.VbaWriteTransform = code =>
                    {
                        Volatile.Write(ref mutationPaused, 1);
                        enteredWrite.Set();
                        if (!releaseWrite.Wait(5000)) throw new InvalidOperationException("test write was not released");
                        Volatile.Write(ref mutationPaused, 0);
                        return code;
                    };
                    var writeTask = Task.Run(() => first.ExecuteManual(
                        Command("common.vba_write_module", "moduleName", "Module1", "code", after),
                        tools, settings, false, false, firstSession));
                    Task<ToolRunResult> queuedTask = null;
                    try
                    {
                        AssertTrue(enteredWrite.Wait(5000), "first write owns the live mutation window");
                        queuedTask = Task.Run(() =>
                        {
                            queuedStarted.Set();
                            return second.ExecuteManual(queuedCommand, tools, settings, false, false, secondSession);
                        });
                        AssertTrue(queuedStarted.Wait(5000), "unprepared mutation starts on the second executor");
                        AssertTrue(!prematureRead.Wait(150), "queued preparation cannot read inside another mutation");
                        AssertTrue(!queuedTask.IsCompleted, "unprepared mutation waits for the document gate");
                    }
                    finally
                    {
                        releaseWrite.Set();
                        AssertTrue(writeTask.Wait(5000), "first mutation completes after release");
                        if (queuedTask != null)
                            AssertTrue(queuedTask.Wait(5000), "queued mutation completes after release");
                    }

                    AssertTrue(writeTask.GetAwaiter().GetResult().Success, "first mutation succeeds");
                    AssertEqual("vba_snapshot_refresh_required", queuedTask.GetAwaiter().GetResult().ErrorCode,
                        "queued preparation checks the changed source after acquiring the gate");
                    AssertEqual(1, writeCalls, "stale queued mutation never dispatches a second write");
                    AssertEqual(after, adapter.VbaModuleCode, "first mutation source is preserved");
                    AssertEqual(1, journal.ListMutations(adapter.HostName, adapter.DocumentKey).Count,
                        "stale queued mutation creates no second prepared journal entry");
                }
            });
        }

        private static void VbaReconciliationWaitsForActiveMutation(string consumer)
        {
            WithTempPaths(paths =>
            {
                const string before = "Sub Main()\nDebug.Print \"before\"\nEnd Sub";
                const string after = "Sub Main()\nDebug.Print \"after\"\nEnd Sub";
                var adapter = new FakeOfficeAdapter { VbaModuleCode = before };
                var journal = new VbaJournalStore(paths);
                var first = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));
                var second = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));
                var tools = OfficeToolCatalog.ForHost(adapter.HostName).Concat(first.GetControllerTools()).ToList();
                var settings = new AppSettings { AutoConfirmToolActions = true };
                var session = NewSession(adapter);
                var readSession = OfficeToolExecutor.CreateIsolatedManualSession(session);
                using (var enteredWrite = new ManualResetEventSlim(false))
                using (var releaseWrite = new ManualResetEventSlim(false))
                using (var readStarted = new ManualResetEventSlim(false))
                using (var prematureAccess = new ManualResetEventSlim(false))
                {
                    var mutationPaused = 0;
                    adapter.BeforeVbaBackendCall = call =>
                    {
                        if (Volatile.Read(ref mutationPaused) != 0) prematureAccess.Set();
                    };
                    adapter.VbaWriteTransform = code =>
                    {
                        Volatile.Write(ref mutationPaused, 1);
                        enteredWrite.Set();
                        if (!releaseWrite.Wait(5000)) throw new InvalidOperationException("test write was not released");
                        Volatile.Write(ref mutationPaused, 0);
                        return code;
                    };
                    var snapshotReads = adapter.DocumentSnapshotReadCount;
                    var observedCode = string.Empty;
                    var writeTask = Task.Run(() => first.ExecuteManual(
                        Command("common.vba_write_module", "moduleName", "Module1", "code", after),
                        tools, settings, false, false, session));
                    Task<string> readTask = null;
                    try
                    {
                        AssertTrue(enteredWrite.Wait(5000), "first mutation reached the active effect window");
                        readTask = Task.Run(() =>
                        {
                            readStarted.Set();
                            var payload = ReadDuringVbaMutation(consumer, second, readSession, tools, settings);
                            observedCode = adapter.VbaModuleCode;
                            return payload;
                        });
                        AssertTrue(readStarted.Wait(5000), consumer + " starts document access");
                        AssertTrue(!readTask.Wait(150), consumer + " waits for the active mutation");
                        AssertTrue(!prematureAccess.IsSet, consumer + " does not call the backend inside the mutation");
                        AssertEqual(snapshotReads, adapter.DocumentSnapshotReadCount,
                            consumer + " does not capture an intermediate document snapshot");
                        AssertTrue(journal.ListMutations(adapter.HostName, adapter.DocumentKey).Single().Terminal == null,
                            "reconciliation cannot close the mutation while its document gate is held");
                    }
                    finally
                    {
                        releaseWrite.Set();
                        AssertTrue(writeTask.Wait(5000), "mutation completes after release");
                        if (readTask != null)
                            AssertTrue(readTask.Wait(5000), consumer + " completes after mutation release");
                    }

                    AssertTrue(writeTask.GetAwaiter().GetResult().Success, "mutation succeeds");
                    AssertTrue(!string.IsNullOrWhiteSpace(readTask.GetAwaiter().GetResult()), consumer + " returns a payload");
                    AssertEqual(after, observedCode, consumer + " observes only the completed mutation");
                    AssertEqual(VbaMutationStatuses.Committed,
                        journal.ListMutations(adapter.HostName, adapter.DocumentKey).Single().Terminal.Status,
                        "journal terminal agrees with the verified committed effect");
                }
            });
        }

        private static string ReadDuringVbaMutation(
            string consumer,
            OfficeToolExecutor executor,
            ChatSession session,
            IReadOnlyList<ToolCatalogEntry> tools,
            AppSettings settings)
        {
            if (consumer == "vba resource") return ReadVbaSource(executor, session, "Module1").Text;
            if (consumer == "document resource")
            {
                var document = executor.ResourceGateway.List(session, LiveDocumentResourceProvider.ProviderName,
                    LiveDocumentResourceProvider.DocumentKind, null, 20).Items.Single();
                return ReadResource(executor.ResourceGateway, session, document.Reference.Uri,
                    ResourceRepresentations.Text, null, 128).Result.Text;
            }

            ToolRunResult result;
            if (consumer == "editor module")
            {
                executor.BindResourceAuthority(session);
                using (var data = new ResourceDataPlaneService(executor.ResourceGateway))
                    return new VbaEditorResourceService(executor.ResourceGateway, data)
                        .Open(session, "Module1", CancellationToken.None).Resource.Revision;
            }
            else if (consumer == "editor project")
                result = executor.ReadVbaProjectForEditor(session);
            else if (consumer == "manual read")
                result = executor.ExecuteManual(Command("excel.read_range", "sheet", "Data", "address", "A1:B2"),
                    tools, settings, false, true, session);
            else
                throw new InvalidOperationException("Unknown concurrent read consumer: " + consumer);
            AssertTrue(result.Success, consumer + " succeeds after the document mutation; " +
                result.ErrorCode + ": " + result.Message);
            return result.DataJson ?? result.Message;
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

        private static VbaMutationService CreateTypedMutationService(
            FakeOfficeAdapter adapter,
            IVbaMutationJournal journal,
            IVbaMutationBackend backend)
        {
            return new VbaMutationService(
                new VbaMutationHostDocumentContext(adapter.VbaHostBackend),
                journal,
                new VbaMutationHostReader(new VbaReader(
                    adapter.VbaHostBackend)),
                backend);
        }

        private static VbaApplyPatchRequest PrepareTypedPatch(
            VbaMutationService service,
            string sessionId,
            string find,
            string text)
        {
            var correlation = new VbaMutationCorrelation { SessionId = sessionId };
            var preparation = service.PrepareApplyPatchGuard(new VbaApplyPatchGuardRequest
            {
                RequestedModuleName = "Module1",
                Correlation = correlation
            });
            AssertTrue(preparation.Success, "typed patch guard preparation succeeds");
            return new VbaApplyPatchRequest
            {
                RequestedModuleName = preparation.ResolvedModuleName,
                Operations = new List<VbaPatchOperationRequest>
                {
                    new VbaPatchOperationRequest
                    {
                        Operation = "replace",
                        Find = find,
                        Text = text
                    }
                },
                Guard = preparation.Guard,
                Correlation = correlation
            };
        }

        private sealed class ScriptedVbaMutationBackend : IVbaMutationBackend
        {
            private readonly Func<VbaModuleWriteRequest, VbaMutationActionResult> _replace;
            private readonly Func<VbaRenameBackendRequest, VbaMutationActionResult> _rename;

            public ScriptedVbaMutationBackend(
                Func<VbaModuleWriteRequest, VbaMutationActionResult> replace)
                : this(replace, null)
            {
            }

            public ScriptedVbaMutationBackend(
                Func<VbaModuleWriteRequest, VbaMutationActionResult> replace,
                Func<VbaRenameBackendRequest, VbaMutationActionResult> rename)
            {
                _replace = replace;
                _rename = rename;
            }

            public int DispatchCount { get; private set; }

            public VbaMutationActionResult ReplaceModule(VbaModuleWriteRequest request)
            {
                DispatchCount += 1;
                return _replace == null ? null : _replace(request);
            }

            public VbaMutationActionResult CreateModule(VbaModuleCreateRequest request)
            {
                DispatchCount += 1;
                return VbaMutationActionResult.Error(
                    "Scripted create backend is not configured.",
                    null,
                    "scripted_create_not_configured",
                    false);
            }

            public VbaMutationActionResult RenameModule(VbaRenameBackendRequest request)
            {
                DispatchCount += 1;
                return _rename == null
                    ? VbaMutationActionResult.Error(
                        "Scripted rename backend is not configured.",
                        null,
                        "scripted_rename_not_configured",
                        false)
                    : _rename(request);
            }

            public VbaMutationActionResult DeleteModule(VbaModuleDeleteRequest request)
            {
                DispatchCount += 1;
                return VbaMutationActionResult.Error(
                    "Scripted delete backend is not configured.",
                    null,
                    "scripted_delete_not_configured",
                    false);
            }

            public VbaMutationActionResult RestoreModule(VbaRestoreBackendRequest request)
            {
                DispatchCount += 1;
                return VbaMutationActionResult.Error(
                    "Scripted restore backend is not configured.",
                    null,
                    "scripted_restore_not_configured",
                    false);
            }
        }

        private sealed class FaultingVbaMutationJournal : IVbaMutationJournal
        {
            private readonly VbaJournalStore _store;

            public FaultingVbaMutationJournal(VbaJournalStore store)
            {
                _store = store;
            }

            public bool FailPrepare { get; set; }
            public bool FailComplete { get; set; }

            public VbaBackupReadResult FindBackup(
                string host,
                string documentKey,
                string backupId,
                string moduleName)
            {
                try
                {
                    var backup = _store.Find(
                        host,
                        documentKey,
                        backupId,
                        moduleName);
                    return backup == null
                        ? VbaBackupReadResult.NotFound()
                        : VbaBackupReadResult.Found(new VbaBackupSnapshot(
                            backup.BackupId,
                            backup.ModuleName,
                            backup.ComponentType,
                            backup.CodeSha256,
                            backup.CodeByteLength,
                            backup.Code,
                            backup.CreatedUtc));
                }
                catch (VbaJournalException ex)
                {
                    return VbaBackupReadResult.Failure(
                        ex.Message,
                        "vba_backup_unavailable",
                        false);
                }
            }

            public VbaMutationPreparation PrepareMutation(
                VbaMutationPreparation preparation,
                string beforeCode,
                string intendedAfterCode)
            {
                if (FailPrepare) throw new IOException("scripted prepare persistence failure");
                return _store.PrepareMutation(preparation, beforeCode, intendedAfterCode);
            }

            public void CompleteMutation(
                string host,
                string documentKey,
                string mutationId,
                string status,
                bool? actualExists,
                string actualCodeSha256,
                string actualComparableCodeSha256,
                string errorCode,
                string message)
            {
                if (FailComplete) throw new IOException("scripted terminal persistence failure");
                _store.CompleteMutation(
                    host,
                    documentKey,
                    mutationId,
                    status,
                    actualExists,
                    actualCodeSha256,
                    actualComparableCodeSha256,
                    errorCode,
                    message);
            }
        }

        private static VbaPendingExecution PrepareVbaNative(
            OfficeToolExecutor executor,
            ChatSession session,
            ToolInvocation command)
        {
            var runtime = executor.CreateNativeRuntime(
                session,
                executor.GetControllerTools(),
                new AppSettings { AutoConfirmToolActions = false },
                "agent",
                false,
                (execution, preparation) => "pending-vba");
            if (string.IsNullOrWhiteSpace(command.ToolCallId))
                command.ToolCallId = "call_" + Guid.NewGuid().ToString("N");
            var call = new ToolCall(
                command.ToolCallId,
                command.ToolId,
                JsonConvert.SerializeObject(command.Arguments, Formatting.None));
            var policy = runtime.Describe(call);
            if (policy == null)
                throw new InvalidOperationException(
                    "Native VBA policy was not captured: " + command.ToolId);
            var runId = session == null || session.LastRun == null ||
                string.IsNullOrWhiteSpace(session.LastRun.RunId)
                    ? "run-vba-native" : session.LastRun.RunId;
            var turnId = session == null || session.LastRun == null ||
                string.IsNullOrWhiteSpace(session.LastRun.TurnId)
                    ? "turn-vba-native" : session.LastRun.TurnId;
            var executionContext = new ToolExecutionContext(
                call, policy, runId, turnId, runId + ":1",
                DateTime.UtcNow, false, 5);
            var record = runtime.ExecuteAsync(
                executionContext, CancellationToken.None).GetAwaiter().GetResult();
            return new VbaPendingExecution
            {
                Runtime = runtime,
                Record = record
            };
        }

        private static ToolExecutionRecord ConfirmVbaNative(
            VbaPendingExecution pending)
        {
            var record = pending == null ? null : pending.Record;
            if (record == null ||
                record.Outcome != ToolExecutionOutcome.AwaitingConfirmation)
                throw new InvalidOperationException(
                    "A native pending VBA record is required.");
            var context = new ToolExecutionContext(
                record.Context.Call,
                record.Context.Policy,
                record.Context.RunId,
                record.Context.TurnId,
                record.Context.StepId,
                DateTime.UtcNow,
                true,
                5,
                record.PreparedStateJson);
            return pending.Runtime.ExecuteAsync(
                context, CancellationToken.None).GetAwaiter().GetResult();
        }

        private sealed class VbaPendingExecution
        {
            internal NativeToolRuntimeAdapter Runtime { get; set; }
            internal ToolExecutionRecord Record { get; set; }
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
