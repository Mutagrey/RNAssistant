using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Storage;
using RNAssistant.Office.Tools;
using RNAssistant.Office.Vba;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void VbaRenameServiceOwnsWorkflow()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                const string source = "Option Explicit\nPublic Sub RenameOwner()\nEnd Sub";
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetVbaModule("RenameOwner", source, "ClassModule");
                var store = new VbaJournalStore(paths);
                var service = CreateTypedRenameService(adapter, store);
                var request = PrepareTypedRename(
                    service,
                    "rename-owner-session",
                    "RenameOwner",
                    "Renamed owner!");

                AssertEqual(4, request.Guard.Version, "typed rename guard version");
                AssertEqual("ClassModule", request.Guard.ComponentType,
                    "rename guard binds source component type");
                AssertTrue(!string.Equals(
                    "Renamed owner!",
                    request.NewModuleName,
                    StringComparison.Ordinal),
                    "typed owner normalizes the destination before confirmation");

                request.DryRun = true;
                var dryRun = service.RenameModule(request, CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Ok, dryRun.Status, "rename dry-run succeeds");
                AssertEqual(source, adapter.GetVbaModuleCode("RenameOwner"),
                    "rename dry-run preserves source identity");
                AssertEqual(0, store.ListPackageMutations("Excel", "doc").Count,
                    "rename dry-run does not persist a journal record");

                request.DryRun = false;
                var outcome = service.RenameModule(request, CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Ok, outcome.Status,
                    "typed rename owner returns verified ok");
                AssertEqual(string.Empty, adapter.GetVbaModuleCode("RenameOwner"),
                    "typed rename removes the old identity");
                AssertEqual(source, adapter.GetVbaModuleCode(request.NewModuleName),
                    "typed rename preserves exact source under the new identity");
                AssertEqual("rename", (string)outcome.Data["mode"],
                    "typed outcome preserves public rename mode");
                AssertTrue(!string.IsNullOrWhiteSpace((string)outcome.Data["mutationId"]),
                    "typed outcome exposes durable mutation correlation");
                AssertTrue(outcome.Data["journalStatus"] == null &&
                    outcome.Data["packageJournalStatus"] == null,
                    "typed outcome hides internal journal states");

                var record = store.ListPackageMutations("Excel", "doc").Single();
                AssertEqual("rename", record.Prepared.Operation,
                    "rename retains the existing two-identity journal wire");
                AssertEqual(2, record.Prepared.Components.Count,
                    "rename journal binds both identities");
                AssertEqual(VbaMutationStatuses.Committed, record.Terminal.Status,
                    "rename ok requires committed read-back");
                var backend = adapter.Executed.Single(command => command.ToolId.EndsWith(
                    ".vba_rename_module_internal",
                    StringComparison.OrdinalIgnoreCase));
                AssertEqual("ClassModule", Convert.ToString(backend.Arguments["expectedComponentType"]),
                    "typed backend receives the source type CAS guard");

                adapter.SetVbaModule("TypeRace", source, "ClassModule");
                var typeRace = PrepareTypedRename(
                    service,
                    "rename-type-session",
                    "TypeRace",
                    "TypeRaceTarget");
                var dispatchesBefore = adapter.Executed.Count(command => command.ToolId.EndsWith(
                    ".vba_rename_module_internal",
                    StringComparison.OrdinalIgnoreCase));
                adapter.SetVbaModule("TypeRace", source, "StdModule");
                var stale = service.RenameModule(typeRace, CancellationToken.None);
                AssertEqual(VbaMutationOutcomeStatus.Error, stale.Status,
                    "source type race is rejected before rename preparation");
                AssertEqual("stale_vba_module", stale.ErrorCode,
                    "source type race has the stale snapshot code");
                AssertEqual(dispatchesBefore, adapter.Executed.Count(command => command.ToolId.EndsWith(
                    ".vba_rename_module_internal",
                    StringComparison.OrdinalIgnoreCase)),
                    "source type race never reaches the backend");
            });
        }

        private static void VbaRenameFaultMatrixClassifiesEffects()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetVbaModule("PrepareSource", "Sub Main()\nEnd Sub", "StdModule");
                var store = new VbaJournalStore(paths);
                var journal = new FaultingVbaRenameJournal(store) { FailPrepare = true };
                var backend = new ScriptedVbaMutationBackend(null, backendRequest =>
                    VbaMutationActionResult.Succeeded("unexpected dispatch"));
                var service = CreateTypedRenameService(adapter, store, journal, backend);
                var outcome = service.RenameModule(
                    PrepareTypedRename(service, "prepare-fault", "PrepareSource", "PrepareTarget"),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Error, outcome.Status,
                    "rename prepare persistence failure is definite error");
                AssertEqual("vba_journal_prepare_failed", outcome.ErrorCode,
                    "rename prepare failure has stable code");
                AssertEqual(0, backend.DispatchCount,
                    "rename prepare failure blocks backend dispatch");
            });

            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetVbaModule("ThrowSource", "Sub Main()\nEnd Sub", "StdModule");
                var store = new VbaJournalStore(paths);
                var backend = new ScriptedVbaMutationBackend(null, request =>
                {
                    throw new InvalidOperationException("scripted pre-effect failure");
                });
                var service = CreateTypedRenameService(adapter, store, null, backend);
                var outcome = service.RenameModule(
                    PrepareTypedRename(service, "throw-before", "ThrowSource", "ThrowTarget"),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Error, outcome.Status,
                    "backend throw with complete before state is definite error");
                AssertEqual(VbaMutationStatuses.NotApplied,
                    store.ListPackageMutations("Excel", "doc").Single().Terminal.Status,
                    "backend throw persists complete-before assessment");
                AssertEqual(1, backend.DispatchCount, "backend throw is not retried");
            });

            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetVbaModule("CommitSource", "Sub Main()\nEnd Sub", "ClassModule");
                var store = new VbaJournalStore(paths);
                var realBackend = TypedRenameBackend(adapter);
                var backend = new ScriptedVbaMutationBackend(null, request =>
                {
                    realBackend.RenameModule(request);
                    throw new InvalidOperationException("scripted post-effect failure");
                });
                var service = CreateTypedRenameService(adapter, store, null, backend);
                var outcome = service.RenameModule(
                    PrepareTypedRename(service, "throw-after", "CommitSource", "CommitTarget"),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Ok, outcome.Status,
                    "verified intended state wins over backend throw");
                AssertEqual(true, outcome.Data["backendReportedError"].Value<bool>(),
                    "committed-after-error keeps compact backend evidence");
                AssertEqual(VbaMutationStatuses.Committed,
                    store.ListPackageMutations("Excel", "doc").Single().Terminal.Status,
                    "mutate-then-throw persists committed state");
                AssertEqual(1, backend.DispatchCount, "mutate-then-throw is not retried");
            });

            WithTempPaths(delegate(AppDataPaths paths)
            {
                const string source = "Sub Main()\nEnd Sub";
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetVbaModule("CollisionSource", source, "StdModule");
                var store = new VbaJournalStore(paths);
                var realBackend = TypedRenameBackend(adapter);
                var backend = new ScriptedVbaMutationBackend(null, request =>
                {
                    adapter.SetVbaModule(request.NewModuleName, "Sub Foreign()\nEnd Sub", "StdModule");
                    return realBackend.RenameModule(request);
                });
                var service = CreateTypedRenameService(adapter, store, null, backend);
                var outcome = service.RenameModule(
                    PrepareTypedRename(service, "post-prepare-collision", "CollisionSource", "CollisionTarget"),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Unknown, outcome.Status,
                    "post-prepare destination collision produces mixed unknown state");
                AssertEqual(false, outcome.Retryable,
                    "mixed collision is never automatically retried");
                AssertEqual(source, adapter.GetVbaModuleCode("CollisionSource"),
                    "post-prepare collision preserves the source identity");
                AssertContains(adapter.GetVbaModuleCode("CollisionTarget"), "Foreign",
                    "post-prepare collision preserves the racing target");
                AssertEqual(VbaMutationStatuses.Unknown,
                    store.ListPackageMutations("Excel", "doc").Single().Terminal.Status,
                    "mixed collision is durably unknown");
            });

            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetVbaModule("UnreadableSource", "Sub Main()\nEnd Sub", "StdModule");
                var store = new VbaJournalStore(paths);
                var service = CreateTypedRenameService(adapter, store);
                var request = PrepareTypedRename(
                    service,
                    "unreadable-readback",
                    "UnreadableSource",
                    "UnreadableTarget");
                adapter.BeforeExecuteTool = command =>
                {
                    if (command == null || !command.ToolId.EndsWith(
                        ".vba_rename_module_internal",
                        StringComparison.OrdinalIgnoreCase)) return;
                    adapter.QueueResult(
                        "excel.vba_read_module",
                        ToolResult.Fail("read unavailable", null, "vba_read_unavailable", false));
                    adapter.QueueResult(
                        "excel.vba_read_module",
                        ToolResult.Fail("read unavailable", null, "vba_read_unavailable", false));
                };
                var outcome = service.RenameModule(request, CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Unknown, outcome.Status,
                    "unavailable rename read-back is unknown");
                AssertEqual(VbaMutationStatuses.Unknown,
                    store.ListPackageMutations("Excel", "doc").Single().Terminal.Status,
                    "unavailable rename read-back is durably unknown");
            });
        }

        private static void VbaRenameRecoveryClassifiesCompleteStates()
        {
            AssertRenameRecoveryState(
                "recovery-before",
                delegate(FakeOfficeAdapter adapter, VbaRenameBackendRequest request)
                {
                    return VbaMutationActionResult.Error(
                        "effect not applied",
                        null,
                        "rename_not_applied",
                        false);
                },
                VbaMutationStatuses.NotApplied);

            AssertRenameRecoveryState(
                "recovery-intended",
                delegate(FakeOfficeAdapter adapter, VbaRenameBackendRequest request)
                {
                    return TypedRenameBackend(adapter).RenameModule(request);
                },
                VbaMutationStatuses.Committed);

            AssertRenameRecoveryState(
                "recovery-mixed",
                delegate(FakeOfficeAdapter adapter, VbaRenameBackendRequest request)
                {
                    adapter.SetVbaModule(
                        request.NewModuleName,
                        adapter.GetVbaModuleCode(request.ModuleName),
                        request.ExpectedComponentType);
                    return VbaMutationActionResult.Error(
                        "mixed rename state",
                        null,
                        "rename_mixed",
                        false);
                },
                VbaMutationStatuses.Unknown);
        }

        private static void VbaRenameCancellationBoundaries()
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetVbaModule("CancelBeforeSource", "Sub Main()\nEnd Sub", "StdModule");
                var store = new VbaJournalStore(paths);
                var cancellation = new CancellationTokenSource();
                var journal = new FaultingVbaRenameJournal(store)
                {
                    AfterPrepare = cancellation.Cancel
                };
                var backend = new ScriptedVbaMutationBackend(null, backendRequest =>
                    VbaMutationActionResult.Succeeded("unexpected dispatch"));
                var service = CreateTypedRenameService(adapter, store, journal, backend);
                var request = PrepareTypedRename(
                    service,
                    "cancel-before",
                    "CancelBeforeSource",
                    "CancelBeforeTarget");

                try
                {
                    service.RenameModule(request, cancellation.Token);
                    throw new InvalidOperationException("rename cancellation before dispatch was ignored");
                }
                catch (OperationCanceledException)
                {
                }
                AssertEqual(0, backend.DispatchCount,
                    "rename cancellation after prepare stops before backend dispatch");
                AssertEqual(VbaMutationStatuses.NotApplied,
                    store.ListPackageMutations("Excel", "doc").Single().Terminal.Status,
                    "rename cancellation before dispatch records complete-before state");
            });

            WithTempPaths(delegate(AppDataPaths paths)
            {
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetVbaModule("CancelAfterSource", "Sub Main()\nEnd Sub", "ClassModule");
                var store = new VbaJournalStore(paths);
                var cancellation = new CancellationTokenSource();
                var realBackend = TypedRenameBackend(adapter);
                var backend = new ScriptedVbaMutationBackend(null, request =>
                {
                    realBackend.RenameModule(request);
                    cancellation.Cancel();
                    throw new OperationCanceledException("scripted cancellation after dispatch");
                });
                var service = CreateTypedRenameService(adapter, store, null, backend);
                var outcome = service.RenameModule(
                    PrepareTypedRename(
                        service,
                        "cancel-after",
                        "CancelAfterSource",
                        "CancelAfterTarget"),
                    cancellation.Token);

                AssertEqual(VbaMutationOutcomeStatus.Ok, outcome.Status,
                    "cancellation after a verified rename returns committed ok");
                AssertEqual(VbaMutationStatuses.Committed,
                    store.ListPackageMutations("Excel", "doc").Single().Terminal.Status,
                    "cancellation after dispatch persists intended state");
                AssertEqual(1, backend.DispatchCount,
                    "cancellation after dispatch never repeats rename");
            });
        }

        private static void AssertRenameRecoveryState(
            string sessionId,
            Func<FakeOfficeAdapter, VbaRenameBackendRequest, VbaMutationActionResult> action,
            string expectedStatus)
        {
            WithTempPaths(delegate(AppDataPaths paths)
            {
                var sourceName = sessionId.Replace('-', '_') + "_source";
                var targetName = sessionId.Replace('-', '_') + "_target";
                var adapter = FakeOfficeAdapter.ForHost("Excel");
                adapter.SetVbaModule(sourceName, "Sub Main()\nEnd Sub", "StdModule");
                var store = new VbaJournalStore(paths);
                var journal = new FaultingVbaRenameJournal(store) { FailComplete = true };
                var backend = new ScriptedVbaMutationBackend(null, request =>
                    action(adapter, request));
                var service = CreateTypedRenameService(adapter, store, journal, backend);
                var outcome = service.RenameModule(
                    PrepareTypedRename(service, sessionId, sourceName, targetName),
                    CancellationToken.None);

                AssertEqual(VbaMutationOutcomeStatus.Unknown, outcome.Status,
                    sessionId + " terminal loss returns unknown");
                AssertEqual(false, outcome.Retryable,
                    sessionId + " terminal loss is non-retryable");
                AssertEqual(false, outcome.Data["terminalRecorded"].Value<bool>(),
                    sessionId + " exposes missing terminal evidence");
                AssertTrue(store.ListPackageMutations("Excel", "doc").Single().Terminal == null,
                    sessionId + " leaves the prepared record open");

                journal.FailComplete = false;
                AssertTrue(service.ReconcilePendingRenames() == null,
                    sessionId + " recovery completes without mutation replay");
                var record = store.ListPackageMutations("Excel", "doc").Single();
                AssertEqual(expectedStatus, record.Terminal.Status,
                    sessionId + " recovery classifies the exact live state");
                AssertEqual(1, backend.DispatchCount,
                    sessionId + " recovery never dispatches rename again");
            });
        }

        private static VbaMutationService CreateTypedRenameService(
            FakeOfficeAdapter adapter,
            VbaJournalStore store,
            IVbaRenameJournal renameJournal = null,
            IVbaMutationBackend backend = null)
        {
            var reader = new VbaMutationReaderAdapter(new VbaReader(
                adapter,
                suffix => adapter.HostName.ToLowerInvariant() + "." + suffix));
            return new VbaMutationService(
                new VbaMutationDocumentContextAdapter(adapter),
                new VbaMutationJournalStoreAdapter(store),
                reader,
                backend ?? TypedRenameBackend(adapter),
                renameJournal ?? new VbaRenameJournalStoreAdapter(store));
        }

        private static IVbaMutationBackend TypedRenameBackend(FakeOfficeAdapter adapter)
        {
            return new VbaMutationBackendAdapter(
                adapter,
                suffix => adapter.HostName.ToLowerInvariant() + "." + suffix);
        }

        private static VbaRenameRequest PrepareTypedRename(
            VbaMutationService service,
            string sessionId,
            string sourceName,
            string targetName)
        {
            var correlation = new VbaMutationCorrelation
            {
                SessionId = sessionId,
                RunId = "run-" + sessionId,
                TurnId = "turn-" + sessionId,
                StepId = "step-" + sessionId,
                ToolCallId = "call-" + sessionId
            };
            var preparation = service.PrepareRenameGuard(new VbaRenameGuardRequest
            {
                RequestedModuleName = sourceName,
                RequestedTargetModuleName = targetName,
                Correlation = correlation
            });
            AssertTrue(preparation.Success,
                "typed rename guard preparation succeeds: " +
                (preparation.Error == null ? string.Empty : preparation.Error.Message));
            return new VbaRenameRequest
            {
                ModuleName = preparation.ResolvedModuleName,
                NewModuleName = preparation.ResolvedTargetModuleName,
                Guard = preparation.Guard,
                Correlation = correlation
            };
        }

        private sealed class FaultingVbaRenameJournal : IVbaRenameJournal
        {
            private readonly IVbaRenameJournal _inner;

            public FaultingVbaRenameJournal(VbaJournalStore store)
            {
                _inner = new VbaRenameJournalStoreAdapter(store);
            }

            public bool FailPrepare { get; set; }
            public bool FailComplete { get; set; }
            public Action AfterPrepare { get; set; }

            public VbaPackageMutationPreparation PrepareRename(
                VbaPackageMutationPreparation preparation)
            {
                if (FailPrepare) throw new IOException("scripted rename prepare failure");
                var prepared = _inner.PrepareRename(preparation);
                var afterPrepare = AfterPrepare;
                if (afterPrepare != null) afterPrepare();
                return prepared;
            }

            public void CompleteRename(
                string host,
                string documentKey,
                string mutationId,
                string status,
                IEnumerable<VbaPackageMutationComponentAssessment> components,
                string errorCode,
                string message)
            {
                if (FailComplete) throw new IOException("scripted rename terminal failure");
                _inner.CompleteRename(
                    host,
                    documentKey,
                    mutationId,
                    status,
                    components,
                    errorCode,
                    message);
            }

            public IReadOnlyList<VbaPackageMutationRecord> ListOpenRenames(
                string host,
                string documentKey)
            {
                return _inner.ListOpenRenames(host, documentKey);
            }
        }
    }
}
