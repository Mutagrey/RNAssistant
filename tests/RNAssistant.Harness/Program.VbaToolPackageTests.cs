using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;
using RNAssistant.Office.Vba;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void VbaToolManifestValidatesTypedEntryPoint()
        {
            var tool = BuildVbaPackageToolForTest();
            var parsed = new VbaToolManifestParser().Parse("RNA_Echo", tool.Code);

            AssertTrue(parsed.Success, "typed VBA manifest accepted");
            AssertEqual("excel.echo_vba", parsed.Tool.Id, "manifest id");
            AssertEqual("Echo", parsed.Tool.EntryPoint, "entry point");
            AssertEqual(4, parsed.Tool.ArgumentOrder.Count, "argument order");
            AssertEqual(2, parsed.Tool.Components.Count, "declared components");
            AssertTrue(VbaToolManifestParser.ValidIdentifier(new string('A', 40)), "entry-point identifier limit remains 40");
            AssertTrue(!VbaToolManifestParser.ValidComponentName(new string('A', 32)), "VBE component names stop at 31 characters");
            AssertEqual("invalid_component_name",
                new VbaToolManifestParser().Parse(new string('A', 32), tool.Code).ErrorCode,
                "overlong component rejected before COM");

            var invalid = tool.Code.Replace("As String\n    Echo =", "As Variant\n    Echo =");
            var invalidResult = new VbaToolManifestParser().Parse("RNA_Echo", invalid);
            AssertEqual("entry_signature", invalidResult.ErrorCode, "String return is mandatory");

            var duplicateSafety = tool.Code.Replace(
                "\"requiresConfirmation\":true}",
                "\"requiresConfirmation\":true,\"requiresConfirmation\":false}");
            AssertEqual("manifest_invalid_json",
                new VbaToolManifestParser().Parse("RNA_Echo", duplicateSafety).ErrorCode,
                "duplicate manifest safety fields are rejected");
        }

        private static void VbaToolStoreRoundTripsPackageSources()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var store = new ToolStore(paths);
                var tool = BuildVbaPackageToolForTest();
                tool.Components[1].FileName = "wrong-extension.bas";
                var saved = store.SaveOne(tool);

                AssertTrue(saved != null, "VBA package saved");
                AssertEqual(2, saved.Components.Count, "component metadata roundtrip");
                AssertContains(saved.Code, "<RNAssistantTool>", "entry source roundtrip");
                AssertTrue(File.Exists(Path.Combine(saved.StoragePath, "src", "RNA_Echo.bas")), "entry .bas stored");
                AssertTrue(File.Exists(Path.Combine(saved.StoragePath, "src", "RNA_EchoService.cls")), "class .cls stored");
                AssertTrue(!File.Exists(Path.Combine(saved.StoragePath, "src", "wrong-extension.bas")), "component filename derived from name and type");
                AssertTrue(!File.Exists(Path.Combine(saved.StoragePath, "pipeline.json")), "VBA package does not persist an unrelated pipeline sidecar");

                var metadataPath = Path.Combine(saved.StoragePath, "tool.json");
                var metadata = JObject.Parse(File.ReadAllText(metadataPath));
                ((JArray)metadata["Components"]).RemoveAt(1);
                File.WriteAllText(metadataPath, metadata.ToString());
                AssertTrue(!store.Load().Any(candidate => string.Equals(candidate.Id, tool.Id, StringComparison.OrdinalIgnoreCase)),
                    "VBA package with manifest/component drift is excluded from the catalog");
                store.SaveOne(tool);

                var supportingSource = Path.Combine(saved.StoragePath, "src", "RNA_EchoService.cls");
                File.Delete(supportingSource);
                AssertTrue(!store.Load().Any(candidate => string.Equals(candidate.Id, tool.Id, StringComparison.OrdinalIgnoreCase)),
                    "VBA package with a missing declared source is excluded from the catalog");
                store.SaveOne(tool);
                var unexpectedSource = Path.Combine(saved.StoragePath, "src", "Unexpected.bas");
                File.WriteAllText(unexpectedSource, "Option Explicit");
                AssertTrue(!store.Load().Any(candidate => string.Equals(candidate.Id, tool.Id, StringComparison.OrdinalIgnoreCase)),
                    "VBA package with an undeclared source is excluded from the catalog");
                store.SaveOne(tool);
                var legacyForm = Path.Combine(saved.StoragePath, "src", "Legacy.frm");
                File.WriteAllText(legacyForm, "VERSION 5.00");
                AssertTrue(!store.Load().Any(candidate => string.Equals(candidate.Id, tool.Id, StringComparison.OrdinalIgnoreCase)),
                    "VBA package with a forbidden exported form is excluded from the catalog");
                store.SaveOne(tool);
                AssertTrue(!File.Exists(legacyForm), "saving a code-only package removes the forbidden legacy form sidecar");

                tool.Components.RemoveAt(1);
                tool.Code = tool.Code.Replace(",\"RNA_EchoService\"", string.Empty);
                store.SaveOne(tool);
                AssertTrue(!File.Exists(Path.Combine(saved.StoragePath, "src", "RNA_EchoService.cls")), "removed source file cleaned");
            });
        }

        private static void VbaToolPackageRejectsDuplicateSources()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tool = BuildVbaPackageToolForTest();
                tool.Components.Add(new VbaToolComponent
                {
                    Name = "RNA_EchoService",
                    Type = "ClassModule",
                    Code = "Option Explicit"
                });
                var validation = executor.ValidateToolDefinition(tool);
                AssertEqual("vba_component_duplicate", validation.ErrorCode, "duplicate component rejected before save");
            });
        }

        private static void VbaToolPackageReservesInternalCommandIds()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tool = BuildVbaPackageToolForTest();
                tool.Id = "excel.vba_install_package_internal";
                tool.Code = tool.Code.Replace("excel.echo_vba", tool.Id);

                var validation = executor.ValidateToolDefinition(tool);

                AssertEqual("reserved_tool_id", validation.ErrorCode, "internal VBA command id is reserved");
            });
        }

        private static void VbaToolSessionExecutionUsesTypedArgumentsAndCleansUp()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tool = BuildVbaPackageToolForTest();
                var command = Command(tool.Id, "text", "hello", "count", 2, "ratio", 1.5);
                var tools = adapter.GetBuiltInTools().Concat(new[] { tool }).ToList();

                var result = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);

                AssertTrue(result.Success, "session VBA tool succeeds");
                AssertEqual("fake-vba-result", result.Message, "String output wrapped by runtime");
                var run = adapter.Executed.Last(item => string.Equals(item.ToolId, "excel.run_macro", StringComparison.OrdinalIgnoreCase));
                AssertEqual("[\"hello\",2,1.5,true]", Convert.ToString(run.Arguments["argumentsJson"]), "typed positional arguments and default");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_Echo"), "entry module cleaned");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_EchoService"), "class module cleaned");

                adapter.QueueResult("excel.vba_read_module", ToolResult.Fail("VBA project is unavailable.", null, "vba_access_error", true));
                var blocked = executor.Execute(command, tools, new AppSettings { AutoConfirmToolActions = true }, false, false);
                AssertTrue(!blocked.Success, "unreadable VBA package state blocks execution");
                AssertEqual("vba_package_probe_failed", blocked.ErrorCode, "unreadable VBA package error code");
                AssertEqual(1, adapter.RanMacros.Count, "probe failure does not run macro");
            });
        }

        private static void VbaToolPersistentInstallRequiresMacroDocumentAndTracksOwnership()
        {
            WithTempExecutor(FakeOfficeAdapter.ForHost("Excel"), delegate(OfficeToolExecutor executor, FakeOfficeAdapter adapter)
            {
                var tool = BuildVbaPackageToolForTest();
                var blocked = executor.InstallVbaTool(tool, false);
                AssertEqual("vba_macro_enabled_document_required", blocked.ErrorCode, "non-macro document blocked");

                adapter.SetDocumentTitle("Harness.xlsm");
                var installed = executor.InstallVbaTool(tool, false);
                AssertTrue(installed.Success, "macro-enabled install succeeds");
                AssertContains(adapter.GetVbaModuleCode("RNA_Echo"), "RNAssistantPackage:", "ownership marker installed");
                AssertEqual("installed", executor.GetVbaInstallationStatus(tool), "installation status");

                var removed = executor.RemoveVbaTool(tool);
                AssertTrue(removed.Success, "owned package uninstalled");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_Echo"), "owned entry removed");

                adapter.SetVbaModule("RNA_Echo", tool.Components[0].Code, "StdModule");
                adapter.SetVbaModule("RNA_EchoService", tool.Components[1].Code, "ClassModule");
                var notOwned = executor.RemoveVbaTool(tool);
                AssertEqual("vba_component_not_owned", notOwned.ErrorCode, "unmarked local source preserved");
                AssertContains(adapter.GetVbaModuleCode("RNA_Echo"), "<RNAssistantTool>", "unmarked source remains");
            });
        }

        private static void VbaPackageJournalRecordsAtomicTransactions()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetDocumentTitle("Harness.xlsm");
                var journal = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));
                var tool = BuildVbaPackageToolForTest();
                var session = NewSession(adapter);
                session.LastRun = new ChatRunRecord { RunId = "run-package", TurnId = "turn-package" };

                var installed = executor.InstallVbaTool(tool, false, session);

                AssertTrue(installed.Success, "journaled package install succeeds");
                var installData = JObject.Parse(installed.DataJson);
                AssertTrue((bool)installData["packageJournaled"], "package install result exposes journal boundary");
                AssertTrue(installData["packageJournalStatus"] == null,
                    "package result does not expose internal journal status");
                var records = journal.ListPackageMutations("Excel", "doc");
                AssertEqual(1, records.Count, "one transaction records the complete install");
                AssertEqual(2, records[0].Prepared.Components.Count, "install transaction contains every component");
                AssertEqual("run-package", records[0].Prepared.RunId, "package transaction keeps run correlation");
                AssertEqual("turn-package", records[0].Prepared.TurnId, "package transaction keeps turn correlation");
                AssertEqual("committed", records[0].Terminal.Status, "install terminal is committed");
                AssertTrue(records[0].Terminal.Components.All(component => component.MatchesIntendedAfter), "every installed component is verified");

                var removed = executor.RemoveVbaTool(tool, session);

                AssertTrue(removed.Success, "journaled package removal succeeds");
                records = journal.ListPackageMutations("Excel", "doc");
                AssertEqual(2, records.Count, "install and removal are separate atomic transactions");
                AssertEqual("package_remove", records[1].Prepared.Operation, "second transaction is removal");
                AssertTrue(records[1].Prepared.RetainBackups, "persistent package removal retains rollback sources");
                AssertTrue(records[1].Prepared.Components.All(component => component.BeforeCodeReference != null), "removal snapshots every component in CAS");
                AssertTrue(records[1].Terminal.Components.All(component => component.MatchesIntendedAfter), "every removed component is verified absent");
                AssertEqual(2, journal.List("Excel", "doc").Count, "package removal exposes one backup per prior component");
                var casHealth = CasService(paths, new ChatStore(paths), journal, () => StorageProtector.None).Audit();
                AssertTrue(casHealth.ReachabilityComplete, "CAS scanner validates package transaction records");
                AssertEqual(0, casHealth.OrphanBlobCount, "package before/intended sources remain reachable from the journal");

                var journalPath = Directory.GetFiles(paths.VbaJournalDirectory, "mutations.events.jsonl", SearchOption.AllDirectories).Single();
                var journalText = File.ReadAllText(journalPath);
                AssertTrue(!journalText.Contains("Public Function Echo"), "package source bodies are referenced through CAS, not embedded in JSONL");
                AssertEqual(2, journal.ReadEvents("Excel", "doc").Count(item =>
                    string.Equals(item.Type, VbaJournalEventTypes.PackageMutationPrepared, StringComparison.Ordinal)),
                    "one preparation event per package operation");
            });
        }

        private static void VbaPackageJournalReconcilesInterruptedTransaction()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var journal = new VbaJournalStore(paths);
                var tool = BuildVbaPackageToolForTest();
                var packageHash = TextPatternEngine.Sha256(string.Join(
                    "\n",
                    tool.Components.OrderBy(component => component.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(component => component.Name + ":" +
                            VbaTextCanonicalizer.PackageCodeSha256(component.Code))
                        .ToArray()));
                var ownershipMarker = "RNAssistantPackage: id=" + tool.Id + "; version=" +
                    tool.PackageVersion + "; hash=" + packageHash + ";";
                var prepared = journal.PreparePackageMutation(new VbaPackageMutationPreparation
                {
                    Operation = "package_install",
                    PackageId = tool.Id,
                    PackageVersion = tool.PackageVersion,
                    OwnershipMarker = ownershipMarker,
                    Host = "Excel",
                    DocumentKey = "doc",
                    RuntimeDocumentKey = adapter.RuntimeDocumentKey,
                    DocumentTitle = adapter.DocumentTitle,
                    Components = tool.Components.Select(component => new VbaPackageMutationComponent
                    {
                        ModuleName = component.Name,
                        BeforeExists = false,
                        IntendedAfterExists = true,
                        IntendedAfterComponentType = component.Type,
                        IntendedAfterCode = component.Code
                    }).ToList()
                });
                adapter.SetVbaModule(
                    tool.Components[0].Name,
                    "' " + ownershipMarker + "\n" + tool.Components[0].Code,
                    tool.Components[0].Type);
                var executor = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));

                var read = ReadVbaSource(executor, NewSession(adapter), tool.Components[0].Name);

                AssertContains(read.Text, "RNAssistantPackage",
                    "safe VBA resource access continues after package reconciliation");
                var record = journal.ListPackageMutations("Excel", "doc").Single(item =>
                    string.Equals(item.Prepared.MutationId, prepared.MutationId, StringComparison.OrdinalIgnoreCase));
                AssertEqual("unknown", record.Terminal.Status, "mixed interrupted package state is never auto-replayed");
                AssertTrue(record.Terminal.Components.Single(item => item.ModuleName == tool.Components[0].Name).MatchesIntendedAfter,
                    "written component is recognized as intended");
                AssertTrue(record.Terminal.Components.Single(item => item.ModuleName == tool.Components[1].Name).MatchesBefore,
                    "missing component is recognized as before state");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode(tool.Components[1].Name), "reconciliation does not create the missing component");
            });
        }

        private static void VbaPackageTerminalLossBlocksOrphanRun()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new VbaJournalStore(paths);
                var tool = BuildVbaPackageToolForTest();
                var source = VbaPackageToolAdapter.ToSource(tool);
                var failing = new FaultingPackageJournal(new VbaPackageJournalStoreAdapter(store))
                {
                    FailNextComplete = true
                };
                var first = CreatePackageService(adapter, failing);

                var unknown = first.Execute(PackageExecution(source), CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Unknown, unknown.Status, "lost terminal is unknown");
                AssertEqual("vba_package_journal_terminal_failed", unknown.ErrorCode, "lost terminal code");
                AssertEqual(0, adapter.RanMacros.Count, "macro is not run without durable install terminal");
                AssertContains(adapter.GetVbaModuleCode("RNA_Echo"), "RNAssistantSession:", "session component remains observable");
                var installRecord = store.ListPackageMutations("Excel", "doc").Single();
                AssertTrue(installRecord.Terminal == null, "failed terminal is not invented");
                AssertTrue(!string.IsNullOrWhiteSpace(installRecord.Prepared.LifecycleId), "session lifecycle is durable");
                AssertContains(installRecord.Prepared.OwnershipMarker, "lifecycle=", "marker carries lifecycle correlation");
                var toolStore = new ToolStore(paths);
                toolStore.SaveOne(tool);
                var catalogExecutor = new OfficeToolExecutor(adapter, store, new SkillStore(paths), toolStore);
                var catalogStatus = new ToolCatalogService(adapter, catalogExecutor, toolStore)
                    .GetVisibleTools()
                    .Single(item => string.Equals(item.Id, tool.Id, StringComparison.OrdinalIgnoreCase))
                    .InstallationStatus;
                AssertEqual("session_cleanup_required", catalogStatus, "catalog exposes orphan recovery state");

                var recovered = CreatePackageService(adapter, new VbaPackageJournalStoreAdapter(store));
                AssertTrue(recovered.ReconcilePendingMutations() == null, "restart reconciliation records observed state");
                var blocked = recovered.Execute(PackageExecution(source), CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Error, blocked.Status, "orphan run is blocked after restart");
                AssertEqual("vba_session_cleanup_required", blocked.ErrorCode, "orphan requires explicit cleanup");
                AssertEqual(0, adapter.RanMacros.Count, "orphan is never executed");
                adapter.SetDocumentTitle("Harness.xlsm");
                var overwriteBlocked = recovered.InstallPersistent(new VbaPackageInstallRequest
                {
                    Source = source
                }, CancellationToken.None);
                AssertEqual("vba_session_cleanup_required", overwriteBlocked.ErrorCode,
                    "persistent install cannot overwrite an incomplete session lifecycle");
                AssertEqual(1, store.ListPackageMutations("Excel", "doc").Count,
                    "blocked overwrite creates no new mutation");

                var removed = recovered.RemoveOwned(new VbaPackageRemoveRequest
                {
                    Source = source,
                    Correlation = new VbaMutationCorrelation { SessionId = "recovery-session" }
                }, CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Ok, removed.Status, "explicit recovery cleanup succeeds");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_Echo"), "orphan entry removed");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_EchoService"), "orphan dependency removed");
                var records = store.ListPackageMutations("Excel", "doc");
                AssertEqual(2, records.Count, "install and explicit cleanup remain separate journal actions");
                AssertEqual(records[0].Prepared.LifecycleId, records[1].Prepared.LifecycleId,
                    "install and cleanup share one lifecycle correlation");
                var lifecycleRows = store.QueryMutations("Excel", "doc", new VbaMutationQueryRequest
                {
                    Search = records[0].Prepared.LifecycleId
                }).Rows;
                AssertEqual(2, lifecycleRows.Count, "diagnostics can search the complete session lifecycle");
                AssertTrue(lifecycleRows.All(row => row.SessionOnly == true), "diagnostics identifies session mutations");
                var detail = store.GetMutationDetail("Excel", "doc", records[0].Prepared.MutationId);
                AssertEqual(records[0].Prepared.LifecycleId, detail.LifecycleId, "detail exposes lifecycle correlation");
                AssertContains(detail.OwnershipMarker, "RNAssistantSession:", "detail exposes exact ownership evidence");
                var rowDto = RNAssistant.Office.Contracts.VbaMutationRowDto.From(lifecycleRows[0]);
                var detailDto = RNAssistant.Office.Contracts.VbaMutationDetailResponse.From(detail);
                AssertEqual(detail.LifecycleId, rowDto.LifecycleId, "bridge row preserves lifecycle correlation");
                AssertEqual(detail.OwnershipMarker, detailDto.OwnershipMarker, "bridge detail preserves ownership evidence");
            });
        }

        private static void VbaPackageMarkerDriftBlocksRun()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var tool = BuildVbaPackageToolForTest();
                var marker = "' RNAssistantSession: id=" + tool.Id + "; version=" + tool.PackageVersion +
                    "; hash=" + new string('a', 64) + "; lifecycle=wrong;\n";
                foreach (var component in tool.Components)
                {
                    adapter.SetVbaModule(component.Name, marker + component.Code, component.Type);
                }
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths));
                var tools = adapter.GetBuiltInTools().Concat(new[] { tool }).ToList();

                var result = executor.Execute(
                    Command(tool.Id, "text", "hello", "count", 2, "ratio", 1.5),
                    tools,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);

                AssertEqual("vba_session_cleanup_required", result.ErrorCode, "marker drift blocks execution");
                AssertEqual("recovery_required", executor.GetVbaInstallationStatus(tool), "marker drift has explicit status");
                AssertEqual(0, adapter.RanMacros.Count, "drifted marker is never run");
                AssertEqual("vba_package_drift", executor.RemoveVbaTool(tool).ErrorCode,
                    "ambiguous marker cannot be removed automatically");
                AssertContains(adapter.GetVbaModuleCode("RNA_Echo"), "lifecycle=wrong", "ambiguous code is preserved");
            });
        }

        private static void VbaPackageJournalBlocksStrippedOrphanMarker()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new VbaJournalStore(paths);
                var source = VbaPackageToolAdapter.ToSource(BuildVbaPackageToolForTest());
                var failing = new FaultingPackageJournal(new VbaPackageJournalStoreAdapter(store))
                {
                    FailNextComplete = true
                };
                var first = CreatePackageService(adapter, failing);
                AssertEqual(VbaMutationOutcomeStatus.Unknown,
                    first.Execute(PackageExecution(source), CancellationToken.None).Status,
                    "fixture leaves session install without terminal");
                var recovered = CreatePackageService(adapter, new VbaPackageJournalStoreAdapter(store));
                AssertTrue(recovered.ReconcilePendingMutations() == null, "orphan install is reconciled as committed");
                foreach (var component in source.Components)
                {
                    var live = adapter.GetVbaModuleCode(component.Name);
                    adapter.SetVbaModule(
                        component.Name,
                        string.Join("\n", live.Replace("\r\n", "\n").Split('\n').Skip(1).ToArray()),
                        component.Type);
                }

                var blocked = recovered.Execute(PackageExecution(source), CancellationToken.None);

                AssertEqual("vba_session_cleanup_required", blocked.ErrorCode,
                    "durable incomplete lifecycle wins over stripped live marker");
                AssertEqual(0, adapter.RanMacros.Count, "stripped orphan source is never run");
                AssertEqual("vba_package_drift",
                    recovered.RemoveOwned(new VbaPackageRemoveRequest { Source = source }, CancellationToken.None).ErrorCode,
                    "stripped ownership cannot authorize deletion");
                AssertContains(adapter.GetVbaModuleCode("RNA_Echo"), "<RNAssistantTool>", "unowned code is preserved");
            });
        }

        private static void VbaPackageSessionInstallRejectsProbeRace()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new VbaJournalStore(paths);
                var tool = BuildVbaPackageToolForTest();
                var source = VbaPackageToolAdapter.ToSource(tool);
                var readCount = 0;
                adapter.BeforeExecuteTool = command =>
                {
                    if (command == null ||
                        !command.ToolId.EndsWith(".vba_read_module", StringComparison.OrdinalIgnoreCase)) return;
                    readCount += 1;
                    if (readCount != 3) return;
                    foreach (var component in tool.Components)
                    {
                        adapter.SetVbaModule(component.Name, component.Code, component.Type);
                    }
                };
                var service = CreatePackageService(adapter, new VbaPackageJournalStoreAdapter(store));

                var outcome = service.Execute(PackageExecution(source), CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Error, outcome.Status, "probe race is definite error");
                AssertEqual("vba_package_state_changed", outcome.ErrorCode, "probe race error code");
                AssertEqual(0, adapter.RanMacros.Count, "probe race never runs macro");
                AssertTrue(!adapter.Executed.Any(item => item.ToolId.EndsWith(".vba_install_package_internal", StringComparison.OrdinalIgnoreCase)),
                    "probe race blocks install dispatch");
                AssertEqual(0, store.ListPackageMutations("Excel", "doc").Count,
                    "probe race blocks journal preparation before overwrite intent is accepted");
                AssertEqual(tool.Components[0].Code, adapter.GetVbaModuleCode(tool.Components[0].Name),
                    "racing document-local source is preserved");
            });
        }

        private static void VbaPackageRechecksBeforeMacro()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new VbaJournalStore(paths);
                var tool = BuildVbaPackageToolForTest();
                foreach (var component in tool.Components)
                {
                    adapter.SetVbaModule(component.Name, component.Code, component.Type);
                }
                var drifted = tool.Components[0].Code.Replace("Echo =", "Echo = \"changed\" &");
                var readCount = 0;
                adapter.BeforeExecuteTool = command =>
                {
                    if (command == null ||
                        !command.ToolId.EndsWith(".vba_read_module", StringComparison.OrdinalIgnoreCase)) return;
                    readCount += 1;
                    if (readCount == 3)
                    {
                        adapter.SetVbaModule(tool.Components[0].Name, drifted, tool.Components[0].Type);
                    }
                };
                var service = CreatePackageService(adapter, new VbaPackageJournalStoreAdapter(store));

                var outcome = service.Execute(
                    PackageExecution(VbaPackageToolAdapter.ToSource(tool)),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Error, outcome.Status, "pre-run drift is definite error");
                AssertEqual("vba_package_drift", outcome.ErrorCode, "pre-run drift error code");
                AssertEqual(0, adapter.RanMacros.Count, "pre-run drift blocks macro dispatch");
                AssertEqual(0, store.ListPackageMutations("Excel", "doc").Count,
                    "document-local execution creates no package mutation record");
                AssertEqual(drifted, adapter.GetVbaModuleCode(tool.Components[0].Name),
                    "pre-run drift is preserved for review");
            });
        }

        private static void VbaPackageCatalogRejectsExtraComponent()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var tool = BuildVbaPackageToolForTest();
                var live = VbaPackageToolAdapter.ToSource(tool).Components.ToList();
                live.Add(new VbaPackageSourceComponent
                {
                    Name = "RNA_Unexpected",
                    Type = "StdModule",
                    Code = "Option Explicit"
                });
                var service = CreatePackageService(
                    adapter,
                    new VbaPackageJournalStoreAdapter(new VbaJournalStore(paths)));

                var status = service.ClassifyDocumentSnapshot(
                    VbaPackageToolAdapter.ToSource(tool),
                    live);

                AssertEqual("modified_local", status,
                    "an undeclared document package component is catalog drift");
            });
        }

        private static void VbaPackagePrepareFailureBlocksDispatch()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetDocumentTitle("Harness.xlsm");
                var journal = new FaultingPackageJournal(
                    new VbaPackageJournalStoreAdapter(new VbaJournalStore(paths)))
                {
                    FailNextPrepare = true
                };
                var service = CreatePackageService(adapter, journal);

                var outcome = service.InstallPersistent(new VbaPackageInstallRequest
                {
                    Source = VbaPackageToolAdapter.ToSource(BuildVbaPackageToolForTest())
                }, CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Error, outcome.Status, "prepare failure is definite error");
                AssertEqual("vba_package_journal_prepare_failed", outcome.ErrorCode, "prepare failure code");
                AssertTrue(!adapter.Executed.Any(item => item.ToolId.EndsWith("vba_install_package_internal")),
                    "prepare failure blocks backend dispatch");
            });
        }

        private static void VbaPackageBackendThrowIsAssessed()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new VbaJournalStore(paths);
                var service = CreatePackageService(adapter, new VbaPackageJournalStoreAdapter(store));
                adapter.ThrowOnToolId = "excel.vba_install_package_internal";

                var outcome = service.Execute(
                    PackageExecution(VbaPackageToolAdapter.ToSource(BuildVbaPackageToolForTest())),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Error, outcome.Status, "throw before mutation is definite error");
                AssertEqual(0, adapter.RanMacros.Count, "failed install never runs macro");
                AssertEqual(VbaMutationStatuses.NotApplied, store.ListPackageMutations("Excel", "doc").Single().Terminal.Status,
                    "before state is durably assessed");
            });
        }

        private static void VbaPackageMutateThenThrowCommits()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new VbaJournalStore(paths);
                var tool = BuildVbaPackageToolForTest();
                var injected = false;
                adapter.BeforeExecuteTool = command =>
                {
                    if (injected || command == null ||
                        !string.Equals(command.ToolId, "excel.vba_install_package_internal", StringComparison.OrdinalIgnoreCase)) return;
                    injected = true;
                    var marker = Convert.ToString(command.Arguments["marker"]);
                    foreach (var component in JArray.Parse(Convert.ToString(command.Arguments["componentsJson"])).OfType<JObject>())
                    {
                        adapter.SetVbaModule(
                            (string)component["name"],
                            "' " + marker + "\n" + (string)component["code"],
                            (string)component["type"]);
                    }
                    adapter.ThrowOnToolId = command.ToolId;
                };
                var service = CreatePackageService(adapter, new VbaPackageJournalStoreAdapter(store));

                var outcome = service.Execute(
                    PackageExecution(VbaPackageToolAdapter.ToSource(tool)),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Ok, outcome.Status, "verified intended install wins backend throw");
                AssertEqual(1, adapter.RanMacros.Count, "verified package runs once");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_Echo"), "verified package still cleans");
                AssertEqual(2, store.ListPackageMutations("Excel", "doc").Count, "install and cleanup are journalled");
            });
        }

        private static void VbaPackageMarkerOnlyDivergenceIsUnknown()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetDocumentTitle("Harness.xlsm");
                var store = new VbaJournalStore(paths);
                var tool = BuildVbaPackageToolForTest();
                foreach (var component in tool.Components)
                {
                    adapter.SetVbaModule(component.Name, component.Code, component.Type);
                }
                adapter.BeforeExecuteTool = command =>
                {
                    if (command == null ||
                        !string.Equals(command.ToolId, "excel.vba_install_package_internal", StringComparison.OrdinalIgnoreCase)) return;
                    var foreignMarker = "' RNAssistantPackage: id=foreign; version=1; hash=" +
                        new string('a', 64) + ";\n";
                    foreach (var component in tool.Components)
                    {
                        adapter.SetVbaModule(component.Name, foreignMarker + component.Code, component.Type);
                    }
                    adapter.ThrowOnToolId = command.ToolId;
                };
                var service = CreatePackageService(adapter, new VbaPackageJournalStoreAdapter(store));

                var outcome = service.InstallPersistent(new VbaPackageInstallRequest
                {
                    Source = VbaPackageToolAdapter.ToSource(tool)
                }, CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Unknown, outcome.Status,
                    "marker-only divergence is not classified as unchanged before state");
                AssertEqual("vba_package_mutation_unknown", outcome.ErrorCode,
                    "marker-only divergence error code");
                AssertEqual(VbaMutationStatuses.Unknown,
                    store.ListPackageMutations("Excel", "doc").Single().Terminal.Status,
                    "marker-only divergence remains explicit durable uncertainty");
                AssertContains(adapter.GetVbaModuleCode(tool.Components[0].Name), "id=foreign",
                    "foreign ownership evidence is preserved for review");
            });
        }

        private static void VbaPackageCasRejectsPostPrepareDrift()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetDocumentTitle("Harness.xlsm");
                var store = new VbaJournalStore(paths);
                var tool = BuildVbaPackageToolForTest();
                foreach (var component in tool.Components)
                {
                    adapter.SetVbaModule(component.Name, component.Code, component.Type);
                }
                var drifted = "' RNAssistantPackage: id=foreign; version=1; hash=" +
                    new string('b', 64) + ";\n" + tool.Components[0].Code;
                adapter.BeforeExecuteTool = command =>
                {
                    if (command == null ||
                        !string.Equals(command.ToolId, "excel.vba_install_package_internal", StringComparison.OrdinalIgnoreCase)) return;
                    adapter.SetVbaModule(tool.Components[0].Name, drifted, tool.Components[0].Type);
                };
                var service = CreatePackageService(adapter, new VbaPackageJournalStoreAdapter(store));

                var outcome = service.InstallPersistent(new VbaPackageInstallRequest
                {
                    Source = VbaPackageToolAdapter.ToSource(tool)
                }, CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Unknown, outcome.Status,
                    "post-prepare drift is durable uncertainty");
                AssertEqual("vba_package_mutation_unknown", outcome.ErrorCode,
                    "post-prepare drift outcome code");
                var terminal = store.ListPackageMutations("Excel", "doc").Single().Terminal;
                AssertEqual(VbaMutationStatuses.Unknown,
                    terminal.Status,
                    "post-prepare drift terminal is unknown");
                AssertEqual("stale_vba_package", terminal.ErrorCode,
                    "typed backend reports its compare-and-swap refusal");
                AssertEqual(drifted, adapter.GetVbaModuleCode(tool.Components[0].Name),
                    "backend CAS preserves externally changed source");
            });
        }

        private static void VbaPackageComInstallGuardRejectsDrift()
        {
            var unguardedDocument = new FakeVbaDocumentObject();
            var unguarded = new JArray(new JObject
            {
                ["name"] = "RNA_Unguarded",
                ["type"] = "StdModule",
                ["code"] = "Option Explicit"
            }).ToString();
            AssertEqual(
                "vba_package_guard_invalid",
                VbaProjectSupport.InstallPackage(
                    unguardedDocument,
                    unguarded,
                    "RNAssistantPackage: id=excel.guard; version=1.0.0; hash=test").ErrorCode,
                "COM helper has no unguarded install compatibility path");
            AssertEqual(0, unguardedDocument.VBProject.VBComponents.Count,
                "unguarded install is rejected before mutation");

            var document = new FakeVbaDocumentObject();
            const string before = "Option Explicit\nPublic Sub BeforeState()\nEnd Sub";
            const string drifted = "Option Explicit\nPublic Sub ExternalState()\nEnd Sub";
            const string intended = "Option Explicit\nPublic Sub IntendedState()\nEnd Sub";
            var component = document.VBProject.VBComponents.Seed("RNA_Guarded", drifted);
            var componentsJson = new JArray(new JObject
            {
                ["name"] = "RNA_Guarded",
                ["type"] = "StdModule",
                ["code"] = intended,
                ["expectedBeforeExists"] = true,
                ["expectedBeforeType"] = "StdModule",
                ["expectedBeforeComparableCodeSha256"] = VbaTextCanonicalizer.PackageComparableCodeSha256(before),
                ["expectedBeforeOwnershipMarkerPresent"] = false,
                ["expectedBeforeOwnershipMarker"] = null
            }).ToString();

            var outcome = VbaProjectSupport.InstallPackage(
                document,
                componentsJson,
                "RNAssistantPackage: id=excel.guard; version=1.0.0; hash=test");

            AssertEqual("stale_vba_package", outcome.ErrorCode, "COM helper enforces prepared package state");
            AssertEqual(drifted, component.CodeModule.Code, "COM helper refuses overwrite before first mutation");
        }

        private static void VbaPackageReadBackLossIsUnknown()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new VbaJournalStore(paths);
                var queued = false;
                adapter.BeforeExecuteTool = command =>
                {
                    if (queued || command == null ||
                        !string.Equals(command.ToolId, "excel.vba_install_package_internal", StringComparison.OrdinalIgnoreCase)) return;
                    queued = true;
                    adapter.QueueResult(
                        "excel.vba_read_module",
                        ToolResult.Fail("VBA read-back unavailable.", null, "vba_access_error", false));
                };
                var service = CreatePackageService(adapter, new VbaPackageJournalStoreAdapter(store));

                var outcome = service.Execute(
                    PackageExecution(VbaPackageToolAdapter.ToSource(BuildVbaPackageToolForTest())),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Unknown, outcome.Status, "lost read-back is unknown");
                AssertEqual(0, adapter.RanMacros.Count, "unknown install is not executed");
                AssertEqual(VbaMutationStatuses.Unknown, store.ListPackageMutations("Excel", "doc").Single().Terminal.Status,
                    "unknown component state is terminal evidence");
            });
        }

        private static void VbaPackageCancellationBeforeDispatch()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetDocumentTitle("Harness.xlsm");
                var store = new VbaJournalStore(paths);
                var service = CreatePackageService(adapter, new VbaPackageJournalStoreAdapter(store));
                var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var cancelled = false;
                try
                {
                    service.InstallPersistent(new VbaPackageInstallRequest
                    {
                        Source = VbaPackageToolAdapter.ToSource(BuildVbaPackageToolForTest())
                    }, cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                AssertTrue(cancelled, "cancellation is propagated");
                AssertTrue(!adapter.Executed.Any(item => item.ToolId.EndsWith("vba_install_package_internal")),
                    "cancel before dispatch does not call backend");
                AssertEqual(VbaMutationStatuses.NotApplied, store.ListPackageMutations("Excel", "doc").Single().Terminal.Status,
                    "cancel before dispatch records before state");
            });
        }

        private static void VbaPackageCancellationAfterInstallCleans()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new VbaJournalStore(paths);
                var service = CreatePackageService(adapter, new VbaPackageJournalStoreAdapter(store));
                var cancellation = new CancellationTokenSource();
                adapter.BeforeExecuteTool = command =>
                {
                    if (command != null &&
                        string.Equals(command.ToolId, "excel.vba_install_package_internal", StringComparison.OrdinalIgnoreCase))
                    {
                        cancellation.Cancel();
                    }
                };
                var cancelled = false;
                try
                {
                    service.Execute(
                        PackageExecution(VbaPackageToolAdapter.ToSource(BuildVbaPackageToolForTest())),
                        cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                AssertTrue(cancelled, "post-install cancellation is propagated after cleanup");
                AssertEqual(0, adapter.RanMacros.Count, "cancelled lifecycle does not run macro");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_Echo"), "cancelled lifecycle cleans entry");
                var records = store.ListPackageMutations("Excel", "doc");
                AssertEqual(2, records.Count, "cancelled lifecycle records install and cleanup");
                AssertEqual(records[0].Prepared.LifecycleId, records[1].Prepared.LifecycleId,
                    "cancelled cleanup retains lifecycle correlation");
            });
        }

        private static void VbaPackageCleanupFailureLeavesBlockedOrphan()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var store = new VbaJournalStore(paths);
                var service = CreatePackageService(adapter, new VbaPackageJournalStoreAdapter(store));
                var source = VbaPackageToolAdapter.ToSource(BuildVbaPackageToolForTest());
                adapter.QueueResult(
                    "excel.run_macro",
                    ToolResult.Fail("macro failed", null, "macro_failed", false));
                adapter.ThrowOnToolId = "excel.vba_remove_package_internal";

                var outcome = service.Execute(PackageExecution(source), CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Unknown, outcome.Status, "failed cleanup makes lifecycle unknown");
                AssertEqual("vba_session_cleanup_failed", outcome.ErrorCode, "cleanup failure code");
                AssertContains(adapter.GetVbaModuleCode("RNA_Echo"), "RNAssistantSession:", "failed cleanup leaves observable orphan");
                AssertEqual(VbaMutationStatuses.NotApplied,
                    store.ListPackageMutations("Excel", "doc").Last().Terminal.Status,
                    "cleanup failure records unchanged session state");
                var blocked = service.Execute(PackageExecution(source), CancellationToken.None);
                AssertEqual("vba_session_cleanup_required", blocked.ErrorCode, "later run remains blocked");

                var recovery = service.RemoveOwned(new VbaPackageRemoveRequest { Source = source }, CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Ok, recovery.Status, "fresh explicit cleanup recovers orphan");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_Echo"), "recovery removes orphan");
            });
        }

        private static void VbaPackageSourceMarkerIsReserved()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var tool = BuildVbaPackageToolForTest();
                tool.Components[1].Code = "' RNAssistantPackage: id=spoof; version=1; hash=" +
                    new string('b', 64) + ";\n" + tool.Components[1].Code;
                var service = CreatePackageService(
                    adapter,
                    new VbaPackageJournalStoreAdapter(new VbaJournalStore(paths)));

                var prepared = service.PreparePackage(VbaPackageToolAdapter.ToSource(tool));

                AssertTrue(!prepared.Success, "source ownership marker is rejected");
                AssertEqual("vba_package_source_marker_reserved", prepared.Error.ErrorCode, "reserved marker code");
                AssertTrue(!adapter.Executed.Any(), "reserved marker fails before host reads or writes");
            });
        }

        private static VbaPackageService CreatePackageService(
            FakeOfficeAdapter adapter,
            IVbaPackageJournal journal)
        {
            var reader = new VbaReader(adapter, suffix =>
                (adapter.HostName ?? string.Empty).ToLowerInvariant() + "." + suffix);
            return new VbaPackageService(
                new VbaMutationDocumentContextAdapter(adapter),
                journal,
                new VbaMutationReaderAdapter(reader),
                new VbaPackageBackendAdapter(adapter, suffix =>
                    (adapter.HostName ?? string.Empty).ToLowerInvariant() + "." + suffix));
        }

        private static JObject GuardedPackageComponent(
            string name,
            string type,
            string intendedCode,
            bool beforeExists,
            string beforeType,
            string beforeCode)
        {
            var marker = beforeExists ? VbaPackageOwnershipMarker.Parse(beforeCode) : null;
            return new JObject
            {
                ["name"] = name,
                ["type"] = type,
                ["code"] = intendedCode,
                ["expectedBeforeExists"] = beforeExists,
                ["expectedBeforeType"] = beforeExists ? beforeType : null,
                ["expectedBeforeComparableCodeSha256"] = beforeExists
                    ? VbaTextCanonicalizer.PackageComparableCodeSha256(beforeCode)
                    : null,
                ["expectedBeforeOwnershipMarkerPresent"] = marker != null && marker.Found,
                ["expectedBeforeOwnershipMarker"] = beforeExists
                    ? VbaPackageOwnershipMarker.Evidence(beforeCode)
                    : null
            };
        }

        private static VbaPackageExecutionRequest PackageExecution(VbaPackageSourceDefinition source)
        {
            return new VbaPackageExecutionRequest
            {
                Source = source,
                Arguments = new JObject
                {
                    ["text"] = "hello",
                    ["count"] = 2,
                    ["ratio"] = 1.5
                },
                Correlation = new VbaMutationCorrelation
                {
                    SessionId = "package-session",
                    RunId = "package-run",
                    TurnId = "package-turn",
                    StepId = "package-step",
                    ToolCallId = "package-call"
                }
            };
        }

        private sealed class FaultingPackageJournal : IVbaPackageJournal
        {
            private readonly IVbaPackageJournal _inner;

            public bool FailNextPrepare { get; set; }
            public bool FailNextComplete { get; set; }

            public FaultingPackageJournal(IVbaPackageJournal inner)
            {
                _inner = inner;
            }

            public VbaPackageMutationPreparation PreparePackageMutation(VbaPackageMutationPreparation preparation)
            {
                if (FailNextPrepare)
                {
                    FailNextPrepare = false;
                    throw new IOException("scripted package prepare failure");
                }
                return _inner.PreparePackageMutation(preparation);
            }

            public void CompletePackageMutation(
                string host,
                string documentKey,
                string mutationId,
                string status,
                IEnumerable<VbaPackageMutationComponentAssessment> components,
                string errorCode,
                string message)
            {
                if (FailNextComplete)
                {
                    FailNextComplete = false;
                    throw new IOException("scripted package terminal failure");
                }
                _inner.CompletePackageMutation(
                    host,
                    documentKey,
                    mutationId,
                    status,
                    components,
                    errorCode,
                    message);
            }

            public IReadOnlyList<VbaPackageMutationRecord> ListOpenPackageMutations(
                string host,
                string documentKey)
            {
                return _inner.ListOpenPackageMutations(host, documentKey);
            }

            public IReadOnlyList<VbaPackageMutationRecord> ListPackageMutations(
                string host,
                string documentKey)
            {
                return _inner.ListPackageMutations(host, documentKey);
            }
        }

        private static void VbaCodeOnlyUserFormPackageRoundTrips()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var tool = BuildVbaUserFormPackageForTest();
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetDocumentTitle("Harness.xlsm");
                var journal = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, journal, new SkillStore(paths), new ToolStore(paths));

                AssertTrue(executor.ValidateToolDefinition(tool).Success, "code-only MSForm package validates");
                var saved = new ToolStore(paths).SaveOne(tool);
                AssertTrue(File.Exists(Path.Combine(saved.StoragePath, "src", "RNA_FormTool.bas")), "UserForm package entry uses .bas");
                AssertTrue(File.Exists(Path.Combine(saved.StoragePath, "src", "RNA_FormToolForm.form.vba")), "code-only form uses explicit .form.vba source");
                AssertTrue(!File.Exists(Path.Combine(saved.StoragePath, "src", "RNA_FormToolForm.frm")), "Designer .frm is never persisted");
                AssertEqual("MSForm", saved.Components.Single(component => component.Name == "RNA_FormToolForm").Type, "MSForm type roundtrips");

                var installed = executor.InstallVbaTool(saved, false);
                AssertTrue(installed.Success, "code-only UserForm package installs");
                AssertEqual("installed", executor.GetVbaInstallationStatus(saved), "code-only package read-back includes type/profile");
                AssertContains(adapter.GetVbaModuleCode("RNA_FormToolForm"), "Controls.Add", "installed form keeps code-only builder");
                AssertEqual("MSForm", journal.ListPackageMutations("Excel", "doc").Single().Prepared.Components
                    .Single(component => component.ModuleName == "RNA_FormToolForm").IntendedAfterComponentType,
                    "package journal records MSForm intent");
                AssertTrue(executor.RemoveVbaTool(saved).Success, "owned code-only UserForm package uninstalls");

                var designerExport = BuildVbaUserFormPackageForTest();
                designerExport.Components[1].Code =
                    "VERSION 5.00\nBegin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} RNA_FormToolForm\n" +
                    "   OleObjectBlob = \"RNA_FormToolForm.frx\":0000\nEnd";
                var rejected = executor.ValidateToolDefinition(designerExport);
                AssertEqual("vba_userform_designer_unsupported", rejected.ErrorCode, "exported Designer source is rejected before save/install");
            });
        }

        private static void VbaCodeOnlyUserFormPackageComLifecycle()
        {
            var document = new FakeVbaDocumentObject();
            var formCode =
                "Option Explicit\nPrivate WithEvents btnOK As MSForms.CommandButton\n" +
                "Private Sub UserForm_Initialize()\nSet btnOK = Me.Controls.Add(\"Forms.CommandButton.1\", \"btnOK\", True)\nEnd Sub";
            var componentsJson = new JArray(GuardedPackageComponent(
                "RNA_FormToolForm",
                "MSForm",
                formCode,
                false,
                null,
                null)).ToString();
            var marker = "RNAssistantPackage: id=excel.form_tool; version=1.0.0; hash=test";

            var installed = VbaProjectSupport.InstallPackage(document, componentsJson, marker);

            AssertTrue(installed.Success, "COM package creates blank MSForm without .frm import");
            var form = document.VBProject.VBComponents.Cast<FakeVbaComponent>().Single(component => component.Name == "RNA_FormToolForm");
            AssertEqual(3, form.Type, "COM package component type is MSForm");
            AssertEqual(0, form.Designer.Controls.Count, "created package form has blank Designer");
            AssertContains(form.CodeModule.Code, "RNAssistantPackage: id=excel.form_tool;", "created form has ownership marker");
            AssertContains(form.CodeModule.Code, "Controls.Add", "created form has runtime control source");

            var updatedCode = formCode.Replace("btnOK", "btnApply");
            var updatedJson = new JArray(GuardedPackageComponent(
                "RNA_FormToolForm",
                "MSForm",
                updatedCode,
                true,
                "MSForm",
                form.CodeModule.Code)).ToString();
            AssertTrue(VbaProjectSupport.InstallPackage(document, updatedJson, marker).Success, "owned blank MSForm updates in place");
            AssertContains(form.CodeModule.Code, "btnApply", "MSForm code-behind update applied");

            var guardedOverwriteJson = new JArray(GuardedPackageComponent(
                "RNA_FormToolForm",
                "MSForm",
                formCode,
                true,
                "MSForm",
                form.CodeModule.Code)).ToString();
            form.Designer.Controls.Count = 1;
            var blocked = VbaProjectSupport.InstallPackage(document, guardedOverwriteJson, marker);
            AssertEqual("vba_userform_designer_unsupported", blocked.ErrorCode, "Designer controls block package overwrite");
            AssertContains(form.CodeModule.Code, "btnApply", "blocked overwrite preserves live form source");
            form.Designer.Controls.Count = 0;
            form.Designer.Picture = new object();
            AssertEqual(
                "vba_userform_designer_unsupported",
                VbaProjectSupport.InstallPackage(document, guardedOverwriteJson, marker).ErrorCode,
                "Designer binary assets block package overwrite");
            form.Designer.Picture = null;

            var expected = new JObject
            {
                ["RNA_FormToolForm"] = VbaTextCanonicalizer.PackageComparableCodeSha256(updatedCode)
            }.ToString();
            var removed = VbaProjectSupport.RemovePackage(document, expected, "RNAssistantPackage: id=excel.form_tool;");
            AssertTrue(removed.Success, "owned blank MSForm can be removed internally by package lifecycle");
            AssertTrue(!document.VBProject.VBComponents.Cast<FakeVbaComponent>().Any(component => component.Name == "RNA_FormToolForm"),
                "package form is absent after verified removal");
        }

        private static void VbaPackageAcceptsVbeNormalization()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetDocumentTitle("Harness.xlsm");
                adapter.VbaWriteTransform = code => code
                    .Replace("Option Explicit", "option    explicit")
                    .Replace("Public Function Prefix", "public   function Prefix");
                var journal = new VbaJournalStore(paths);
                var executor = new OfficeToolExecutor(adapter, journal, new SkillStore(paths));
                var tool = BuildVbaPackageToolForTest();

                var installed = executor.InstallVbaTool(tool, false);

                AssertTrue(installed.Success, "VBE-formatted package install succeeds");
                AssertEqual("installed", executor.GetVbaInstallationStatus(tool), "VBE-formatted package probe matches source");
                AssertTrue(
                    !string.Equals(
                        VbaTextCanonicalizer.PackageCodeSha256(tool.Components[1].Code),
                        VbaTextCanonicalizer.PackageCodeSha256(adapter.GetVbaModuleCode("RNA_EchoService")),
                        StringComparison.OrdinalIgnoreCase),
                    "regression setup changes the strict package hash");
                AssertEqual(
                    VbaMutationStatuses.Committed,
                    journal.ListPackageMutations("Excel", "doc").Single().Terminal.Status,
                    "journal accepts VBE-equivalent installed source");
                var installRecord = journal.ListPackageMutations("Excel", "doc").Single();
                AssertTrue(
                    installRecord.Prepared.Components.All(component => !string.IsNullOrWhiteSpace(component.IntendedAfterComparableCodeSha256)),
                    "package preparation persists comparable intended hashes");
                AssertTrue(
                    installRecord.Terminal.Components.All(component => !string.IsNullOrWhiteSpace(component.ActualComparableCodeSha256)),
                    "package terminal persists comparable actual hashes");

                var removed = executor.RemoveVbaTool(tool);

                AssertTrue(removed.Success, "VBE-formatted owned package uninstalls");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RNA_Echo"), "VBE-formatted entry module removed");
            });
        }

        private static void VbaComPackageAcceptsVbeNormalization()
        {
            var document = new FakeVbaDocumentObject();
            document.VBProject.VBComponents.AddedModuleWriteTransform = code =>
                code.Replace("Option Explicit", "option    explicit");
            var source = "Option Explicit\nPublic Sub RunTool()\nEnd Sub";
            var componentsJson = new JArray(GuardedPackageComponent(
                "RNA_FormatForm",
                "MSForm",
                source,
                false,
                null,
                null)).ToString();
            var marker = "RNAssistantPackage: id=excel.format_form; version=1.0.0; hash=test";

            var installed = VbaProjectSupport.InstallPackage(document, componentsJson, marker);

            AssertTrue(installed.Success, "COM install accepts VBE-equivalent package source");
            var form = document.VBProject.VBComponents.Cast<FakeVbaComponent>().Single();
            AssertContains(form.CodeModule.Code, "option    explicit", "COM regression setup reformats source");
            var expected = new JObject
            {
                ["RNA_FormatForm"] = VbaTextCanonicalizer.PackageComparableCodeSha256(source)
            }.ToString();
            AssertTrue(
                VbaProjectSupport.RemovePackage(document, expected, "RNAssistantPackage: id=excel.format_form;").Success,
                "COM remove accepts VBE-equivalent owned package source");
        }

        private static void VbaDocumentToolsAreDiscoveredAndRunnable()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                var tool = BuildVbaPackageToolForTest();
                adapter.SetVbaModule("RNA_Echo", tool.Components[0].Code, "StdModule");
                adapter.SetVbaModule("RNA_EchoService", tool.Components[1].Code, "ClassModule");
                var store = new ToolStore(paths);
                var executor = new OfficeToolExecutor(adapter, new VbaJournalStore(paths), new SkillStore(paths), store);
                var catalogService = new ToolCatalogService(adapter, executor, store);
                var catalog = catalogService.GetVisibleTools();
                var discovered = catalog.FirstOrDefault(item => string.Equals(item.Id, tool.Id, StringComparison.OrdinalIgnoreCase));

                AssertTrue(discovered != null, "document VBA tool discovered");
                AssertEqual("document", discovered.Scope, "document scope");
                AssertEqual(2, discovered.Components.Count, "document components resolved");
                AssertTrue(!adapter.Executed.Any(item =>
                    string.Equals(item.ToolId, "excel.vba_read_module", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Convert.ToString(item.Arguments["moduleName"]), "Module1", StringComparison.OrdinalIgnoreCase)),
                    "document VBA discovery skips standard modules without a manifest");
                var discoveryCalls = adapter.Executed.Count(item =>
                    string.Equals(item.ToolId, "excel.vba_list_project_components_internal", StringComparison.OrdinalIgnoreCase));
                catalogService.GetVisibleTools();
                AssertEqual(discoveryCalls, adapter.Executed.Count(item =>
                    string.Equals(item.ToolId, "excel.vba_list_project_components_internal", StringComparison.OrdinalIgnoreCase)),
                    "document VBA discovery uses short cache");
                catalogService.GetFreshConversationTools();
                AssertEqual(discoveryCalls + 1, adapter.Executed.Count(item =>
                    string.Equals(item.ToolId, "excel.vba_list_project_components_internal", StringComparison.OrdinalIgnoreCase)),
                    "each conversation boundary refreshes document VBA discovery");
                adapter.RuntimeDocumentKeyValue = "runtime-reopened-document";
                catalogService.GetVisibleTools();
                AssertEqual(discoveryCalls + 2, adapter.Executed.Count(item =>
                    string.Equals(item.ToolId, "excel.vba_list_project_components_internal", StringComparison.OrdinalIgnoreCase)),
                    "document VBA discovery cache is scoped to the runtime document");
                catalogService.InvalidateDocumentVbaTools();
                catalogService.GetVisibleTools();
                AssertEqual(discoveryCalls + 3, adapter.Executed.Count(item =>
                    string.Equals(item.ToolId, "excel.vba_list_project_components_internal", StringComparison.OrdinalIgnoreCase)),
                    "document VBA discovery cache invalidates");

                var result = executor.Execute(
                    Command(discovered.Id, "text", "hello", "count", 2, "ratio", 1.5),
                    catalog,
                    new AppSettings { AutoConfirmToolActions = true },
                    false,
                    false);
                AssertTrue(result.Success, "document VBA tool runs");
                AssertContains(adapter.GetVbaModuleCode("RNA_Echo"), "<RNAssistantTool>", "document source is not removed after run");

                store.SaveOne(tool);
                var duplicateCode = tool.Components[0].Code.Replace(
                    "\"components\":[\"RNA_Echo\",",
                    "\"components\":[\"RNA_EchoDuplicate\",");
                adapter.SetVbaModule("RNA_EchoDuplicate", duplicateCode, "StdModule");
                catalogService.InvalidateDocumentVbaTools();
                var duplicateCatalog = catalogService.GetVisibleTools();
                AssertTrue(duplicateCatalog.Any(item =>
                    item.Id.StartsWith(tool.Id + "#document", StringComparison.OrdinalIgnoreCase) &&
                    !item.Enabled && string.Equals(item.CapabilityStatus, "id_collision", StringComparison.OrdinalIgnoreCase)),
                    "second document manifest cannot hide behind matched global package");
            });
        }

        private static void VbaCodeHashIgnoresExportHeadersAndRuntimeMarkers()
        {
            var source = "Option Explicit\nPublic Function Value() As String\n    Value = \"ok\"\nEnd Function";
            var exported =
                "VERSION 1.0 CLASS\nBEGIN\n  MultiUse = -1\nEND\n" +
                "Attribute VB_Name = \"RNA_Class\"\n" +
                "Attribute VB_GlobalNameSpace = False\n" +
                "Attribute VB_Creatable = False\n" +
                "Attribute VB_PredeclaredId = False\n" +
                "Attribute VB_Exposed = False\n" +
                "' RNAssistantSession: id=excel.echo_vba; version=1.0.0\n" + source;
            AssertEqual(VbaTextCanonicalizer.PackageCodeSha256(source), VbaTextCanonicalizer.PackageCodeSha256(exported), "normalized export hash");

            var versionedWithoutAttributes = "VERSION 1.0 CLASS\n" + source;
            AssertContains(VbaTextCanonicalizer.NormalizePackageCode(versionedWithoutAttributes), "VERSION 1.0 CLASS", "non-export VERSION source is preserved");
        }

        private static ToolDefinition BuildVbaPackageToolForTest()
        {
            var entryCode =
                "Option Explicit\n" +
                "' <RNAssistantTool>\n" +
                "' {\"protocolVersion\":1,\"id\":\"excel.echo_vba\",\"name\":\"Echo VBA\",\"description\":\"Return typed arguments.\",\"host\":\"Excel\",\"packageVersion\":\"1.0.0\",\"entryPoint\":\"Echo\",\"components\":[\"RNA_Echo\",\"RNA_EchoService\"],\"argumentOrder\":[\"text\",\"count\",\"ratio\",\"enabled\"],\"parameters\":{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"Text value.\"},\"count\":{\"type\":\"integer\",\"description\":\"Integer value.\"},\"ratio\":{\"type\":\"number\",\"description\":\"Numeric value.\"},\"enabled\":{\"type\":\"boolean\",\"description\":\"Boolean value.\",\"default\":true}},\"required\":[\"text\",\"count\",\"ratio\"],\"additionalProperties\":false},\"mutatesDocument\":true,\"agentCanRun\":false,\"requiresConfirmation\":true}\n" +
                "' </RNAssistantTool>\n" +
                "Public Function Echo(ByVal text As String, ByVal count As Long, ByVal ratio As Double, ByVal enabled As Boolean) As String\n" +
                "    Echo = text & CStr(count) & CStr(ratio) & CStr(enabled)\n" +
                "End Function";
            var classCode = "Option Explicit\nPublic Function Prefix(ByVal value As String) As String\n    Prefix = value\nEnd Function";
            return new ToolDefinition
            {
                Id = "excel.echo_vba",
                Host = "Excel",
                Name = "Echo VBA",
                Description = "Return typed arguments.",
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"Text value.\"},\"count\":{\"type\":\"integer\",\"description\":\"Integer value.\"},\"ratio\":{\"type\":\"number\",\"description\":\"Numeric value.\"},\"enabled\":{\"type\":\"boolean\",\"description\":\"Boolean value.\",\"default\":true}},\"required\":[\"text\",\"count\",\"ratio\"],\"additionalProperties\":false}",
                Executor = "vba",
                Code = entryCode,
                Enabled = true,
                BuiltIn = false,
                MutatesDocument = true,
                RequiresConfirmation = true,
                AgentCanRun = false,
                RiskLevel = 3,
                PackageVersion = "1.0.0",
                EntryPoint = "Echo",
                ArgumentOrder = new List<string> { "text", "count", "ratio", "enabled" },
                Components = new List<VbaToolComponent>
                {
                    new VbaToolComponent { Name = "RNA_Echo", Type = "StdModule", FileName = "RNA_Echo.bas", Code = entryCode },
                    new VbaToolComponent { Name = "RNA_EchoService", Type = "ClassModule", FileName = "RNA_EchoService.cls", Code = classCode }
                }
            };
        }

        private static ToolDefinition BuildVbaUserFormPackageForTest()
        {
            var entryCode =
                "Option Explicit\n" +
                "' <RNAssistantTool>\n" +
                "' {\"protocolVersion\":1,\"id\":\"excel.form_tool\",\"name\":\"Form Tool\",\"description\":\"Show a code-only form.\",\"host\":\"Excel\",\"packageVersion\":\"1.0.0\",\"entryPoint\":\"ShowForm\",\"components\":[\"RNA_FormTool\",\"RNA_FormToolForm\"],\"argumentOrder\":[],\"parameters\":{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false},\"mutatesDocument\":true,\"agentCanRun\":false,\"requiresConfirmation\":true}\n" +
                "' </RNAssistantTool>\n" +
                "Public Function ShowForm() As String\n" +
                "    RNA_FormToolForm.Show\n" +
                "    ShowForm = \"shown\"\n" +
                "End Function";
            var formCode =
                "Option Explicit\n" +
                "Private WithEvents btnOK As MSForms.CommandButton\n" +
                "Private Sub UserForm_Initialize()\n" +
                "    Me.Caption = \"Parameters\"\n" +
                "    Set btnOK = Me.Controls.Add(\"Forms.CommandButton.1\", \"btnOK\", True)\n" +
                "End Sub\n" +
                "Private Sub btnOK_Click()\n" +
                "    Unload Me\n" +
                "End Sub";
            return new ToolDefinition
            {
                Id = "excel.form_tool",
                Host = "Excel",
                Name = "Form Tool",
                Description = "Show a code-only form.",
                ArgumentSchemaJson = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}",
                Executor = "vba",
                Code = entryCode,
                Enabled = true,
                BuiltIn = false,
                MutatesDocument = true,
                RequiresConfirmation = true,
                AgentCanRun = false,
                RiskLevel = 3,
                PackageVersion = "1.0.0",
                EntryPoint = "ShowForm",
                Components = new List<VbaToolComponent>
                {
                    new VbaToolComponent { Name = "RNA_FormTool", Type = "StdModule", Code = entryCode },
                    new VbaToolComponent { Name = "RNA_FormToolForm", Type = "MSForm", Code = formCode }
                }
            };
        }
    }
}
