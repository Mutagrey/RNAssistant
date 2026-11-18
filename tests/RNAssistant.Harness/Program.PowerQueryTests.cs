using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Models;
using RNAssistant.Core.ModelProtocol;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;
using RNAssistant.Core.Tools;
using ToolResult = RNAssistant.Core.Tools.Contracts.ToolResult;
using RNAssistant.Office;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;
using RNAssistant.Office.Tools;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void PowerQuerySourceWritesVerifyReadback()
        {
            var backend = new StubPowerQueryBackend(); backend.Seed("Sales", "let x = 1 in x");
            var adapter = FakeOfficeAdapter.ForHost("Excel"); adapter.ExcelPowerQueryBackend = backend;
            WithTempExecutor(adapter, (executor, ignored) =>
            {
                var session = NewSession(adapter); executor.BindResourceAuthority(session);
                var tools = OfficeToolCatalog.ForHost("Excel").Concat(executor.GetControllerTools()).ToList();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings { AutoConfirmToolActions = true }, "agent", false, manualRun: true);
                var patch = ExecuteHtmlNative(runtime, ResourceMutationToolCatalog.Patch, new JObject {
                    ["targets"] = new JArray(new JObject { ["target"] = "Excel query: Sales",
                        ["edits"] = new JArray(new JObject { ["old"] = "x = 1", ["new"] = "x = 2" }) }) });
                AssertEqual(ToolExecutionOutcome.Ok, patch.Outcome, "the shared core.patch route writes M successfully: " + patch.Message);
                AssertEqual("let x = 2 in x", backend.Formula("Sales"), "only the literal observed fragment changes");
                AssertEqual(1, backend.Dispatches, "the common patch has one native source dispatch");
                Func<string, ToolExecutionRecord> write = source => ExecuteHtmlNative(runtime, ResourceMutationToolCatalog.Write,
                    new JObject { ["target"] = "Excel query: Sales", ["content"] = source });
                var unchanged = write("let x = 2 in x");
                AssertEqual(ToolExecutionOutcome.Ok, unchanged.Outcome, "same source is a verified no-op");
                AssertEqual(ToolEffectEvidence.VerifiedNoChange, unchanged.Evidence.Effect, "no-op evidence does not claim a native write");
                AssertEqual(1, backend.Dispatches, "no-op does not call Formula setter");
                backend.AfterWrite = request => { throw new InvalidOperationException("Setter acknowledgement failed after changing Formula."); };
                var readBack = write("let x = 3 in x");
                AssertEqual(ToolExecutionOutcome.Ok, readBack.Outcome, "exact read-back establishes the effect despite setter acknowledgement failure");
                AssertEqual(ToolEffectEvidence.VerifiedChange, readBack.Evidence.Effect, "successful read-back is the source effect evidence");
                AssertContains(readBack.Result.DataJson, "Setter acknowledgement failed", "the original native warning is retained");
                backend.AfterWrite = null;
                backend.BeforeWrite = request => backend.Seed("Sales", "external edit");
                var before = backend.Dispatches;
                var conflict = write("let x = 4 in x");
                AssertTrue(conflict.Outcome == ToolExecutionOutcome.Error && !conflict.MayHaveDispatched,
                    "source drift at the native boundary refuses overwrite before dispatch");
                AssertEqual(before, backend.Dispatches, "a race does not pass the source comparison guard");
                AssertEqual("external edit", backend.Formula("Sales"), "the concurrent edit is preserved");
                backend.BeforeWrite = null;
                backend.AfterWrite = request => { backend.ReadFailure = new ExcelPowerQueryBackendException("Read-back unavailable.", "excel_query_read_failed"); };
                var uncertain = write("let x = 5 in x");
                AssertEqual(ToolExecutionOutcome.Unknown, uncertain.Outcome, "post-dispatch missing read-back remains Unknown");
                AssertTrue(uncertain.MayHaveDispatched && uncertain.Recovery?.ResourceIdentity != null &&
                    uncertain.Recovery.View == ResourceRepresentations.Source, "uncertain M write names its exact source inspection scope");
                backend.ReadFailure = null; backend.AfterWrite = null;
                AssertEqual("let x = 5 in x", backend.Formula("Sales"), "unconfirmed writes are not automatically rolled back");
                var owner = new ExcelPowerQuerySourceToolHandler(executor.ResourceGateway, new HostRuntime(adapter, null),
                    session, TextPatternEngine.Sha256(backend.Formula("Sales")));
                var sourceCall = new ToolCall("binding-guard", ExcelPowerQuerySourceToolHandler.Operation, "{}");
                var sourcePolicy = new ToolPolicySnapshot(sourceCall.Name, "pq-test", Policy(ToolEffect.Write, ToolVerification.Tool));
                var sourceArguments = new Dictionary<string, object> { ["queryName"] = "Sales", ["content"] = backend.Formula("Sales") };
                var preparation = owner.PrepareAsync(new ToolHandlerContext(new ToolExecutionContext(sourceCall, sourcePolicy,
                    "run", "turn", "binding-guard", DateTime.UtcNow, true, 4), sourceArguments), CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Ok, preparation.Result.Status, "source no-op captures its actual binding set before execution");
                var originalBinding = backend.ReadBindings("Sales");
                backend.BindingsOverride = queryName => new ExcelPowerQueryBindingsSnapshot { QueryName = queryName,
                    QueryIdentity = originalBinding.QueryIdentity, BindingVersion = "rebound-after-preparation", IsComplete = true,
                    Connections = originalBinding.Connections, Loads = originalBinding.Loads };
                var staleContext = new ToolHandlerContext(new ToolExecutionContext(sourceCall, sourcePolicy,
                    "run", "turn", "binding-guard", DateTime.UtcNow, true, 4, preparation.PreparedStateJson), sourceArguments);
                var stale = owner.ExecuteAsync(staleContext, CancellationToken.None).GetAwaiter().GetResult();
                AssertTrue(stale.Result.Status == RNAssistant.Core.Tools.Contracts.ToolResultStatus.Error && !staleContext.MayHaveDispatched,
                    "even an unchanged source cannot reuse stale prepared load bindings");
                backend.BindingsOverride = null;
                var created = ExecuteHtmlNative(runtime, ExcelPowerQueryToolCatalog.Create,
                    new JObject { ["name"] = "Parameter", ["source"] = "2026", ["description"] = "Year parameter" });
                AssertEqual(ToolExecutionOutcome.Ok, created.Outcome, "explicit create writes a separate definition");
                AssertEqual("2026", backend.Formula("Parameter"), "scalar query definitions are supported without evaluation");
                var duplicate = ExecuteHtmlNative(runtime, ExcelPowerQueryToolCatalog.Create,
                    new JObject { ["name"] = "Parameter", ["source"] = "2027" });
                AssertTrue(duplicate.Outcome == ToolExecutionOutcome.Error && !duplicate.MayHaveDispatched,
                    "create is never an upsert over an existing definition");
                AssertEqual("2026", backend.Formula("Parameter"), "duplicate create preserves the existing source");
                AssertEqual(0, backend.Refreshes.Count + backend.Loads.Count, "patch/write/create never execute or load Power Query");
            });
        }

        private static void PowerQueryNativeLifetimeSurvivesTimeout()
        {
            var backend = new StubPowerQueryBackend(); backend.Seed("Sales", "42");
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            adapter.RuntimeDocumentKeyValue = "pq-lifetime-" + Guid.NewGuid().ToString("N");
            adapter.ExcelPowerQueryBackend = backend;
            WithTempExecutor(adapter, (executor, ignored) =>
            {
                var session = NewSession(adapter); executor.BindResourceAuthority(session);
                var gateway = executor.ResourceGateway;
                var host = new HostRuntime(adapter, null);
                var otherHost = new HostRuntime(adapter, null);
                var owner = ExcelPowerQueryExecutionService.For(adapter);
                var target = new OfficeDocumentExecutionExpectation { Host = "Excel", DocumentKey = adapter.DocumentKey,
                    RuntimeDocumentKey = adapter.RuntimeDocumentKey };
                var callbacks = 0;
                Func<string, ToolHandlerContext> context = id => {
                    var call = new ToolCall(id, ExcelPowerQueryToolCatalog.Refresh, "{\"target\":\"Excel query: Sales\"}");
                    return new ToolHandlerContext(new ToolExecutionContext(call, new ToolPolicySnapshot(call.Name, "pq-test",
                        Policy(ToolEffect.Write, ToolVerification.Tool)), "run", "turn", id, DateTime.UtcNow, false, 10),
                        new Dictionary<string, object> { ["target"] = "Excel query: Sales" },
                        onCompleted: result => Interlocked.Increment(ref callbacks));
                };
                var request = new ExcelPowerQueryRefreshRequest { QueryName = "Sales", Sheet = "Data", Table = "Result",
                    ExpectedQueryIdentity = "fixture-query:Sales", ExpectedSourceVersion = TextPatternEngine.Sha256("42"),
                    ExpectedBindingVersion = "binding-1" };
                using (var started = new ManualResetEventSlim(false))
                using (var release = new ManualResetEventSlim(false))
                {
                    backend.RefreshOverride = (nativeRequest, dispatch) => {
                        AssertTrue(adapter.StaDispatcher.CheckAccess, "native refresh executes through the bound dispatcher");
                        dispatch(); started.Set();
                        if (!release.Wait(15000)) throw new InvalidOperationException("Fixture was not released.");
                        return new ExcelPowerQueryRefreshResult { State = ExcelPowerQueryRefreshState.Succeeded,
                            NativeRunning = false, SourceStillCurrent = true, BindingStillCurrent = true,
                            ObservedAtUtc = DateTime.UtcNow, Message = "Confirmed native completion.",
                            Binding = new ExcelPowerQueryLoadBinding { Sheet = "Data", Table = "Result", Rows = 3, Columns = 2 } };
                    };
                    var runningContext = context("refresh-timeout");
                    var pending = owner.RefreshAsync(host, session, gateway, runningContext, request, 700, CancellationToken.None);
                    try
                    {
                        AssertTrue(started.Wait(5000), "native fixture reaches its dispatch boundary");
                        var nativeObserved = owner.ReadStatus("Sales").NativeObservedAtUtc;
                        AssertTrue(pending.Wait(5000), "caller deadline completes while native work remains blocked");
                        var timeout = pending.GetAwaiter().GetResult();
                        AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Unknown, timeout.Result.Status,
                            "timeout after dispatch is an uncertain effect, not native cancellation");
                        AssertEqual(1, callbacks, "one terminal is published for the timed-out tool call");
                        var status = owner.ReadStatus("Sales");
                        AssertTrue(status.NativeState == "running" && status.WaitState == "timed_out" && !status.ExecutionStopped,
                            "native execution and caller wait have separate states");
                        AssertEqual(nativeObserved, status.NativeObservedAtUtc, "cached status preserves the native observation timestamp");
                        AssertTrue(!status.NativeProbeAvailable && !status.NativeCancellationAvailable,
                            "synchronous owner reports unavailable probe/cancel honestly");
                        RuntimeThrows<HostRuntime.MutationLockException>(() => otherHost.ReadDocument(target, () => 42));
                        var duplicate = owner.RefreshAsync(otherHost, session, gateway, context("refresh-busy"), request,
                            700, CancellationToken.None).GetAwaiter().GetResult();
                        AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Error, duplicate.Result.Status,
                            "another chat cannot start a refresh while the first native operation is outstanding");
                        AssertEqual(1, backend.Dispatches, "timeout cannot release native ownership or dispatch a duplicate");
                        var sourceReads = backend.SourceReads;
                        var statusUri = ExcelResourceProvider.QueryStatusIdentity(session.DocumentAuthorityId, "Sales").Uri;
                        var descriptor = gateway.Resolve(session, statusUri).Resource;
                        AssertEqual("not_checked", descriptor.Metadata["queryExistence"],
                            "status resolution does not invent a live source-existence observation");
                        var currentStatus = gateway.Read(session, new ResourceReadRequest {
                            Reference = descriptor.Reference, Representation = "status", MaxChars = 32000 }).Result;
                        var historicalStatus = gateway.Read(session, new ResourceReadRequest {
                            Reference = currentStatus.Resource.Reference, Representation = "status", MaxChars = 32000 }).Result;
                        AssertEqual(currentStatus.Text, historicalStatus.Text, "exact status remains available from the retained snapshot while native work holds the gate");
                        var foreign = NewSession(adapter); foreign.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
                        RuntimeThrows<ResourceRequestException>(() => gateway.Resolve(foreign, statusUri));
                        var previousRun = session.LastRun;
                        session.LastRun = new ChatRunRecord { DocumentRuntimeKey = "foreign-runtime" };
                        try { RuntimeThrows<ResourceRequestException>(() => gateway.Resolve(session, statusUri)); }
                        finally { session.LastRun = previousRun; }
                        AssertEqual(sourceReads, backend.SourceReads, "current/exact status and rejected owners never probe the native source");
                        var read = owner.ReadStatusTool(gateway, session, "Sales", 32000);
                        var tracker = new AgentProgressTracker();
                        var progress = new ToolExecutionProgress(ToolExecutionOutcome.Unknown, completedUtc: DateTime.UtcNow.AddMilliseconds(-1),
                            operation: timeout.ResourceEvidence.Single().OperationObservation.Operation);
                        tracker.Observe(runningContext.Execution.Call, progress, "Timed out.", timeout.ResourceEvidence, null);
                        tracker.Observe(new ToolCall("status-read", "core.read", "{}"),
                            new ToolExecutionProgress(ToolExecutionOutcome.Ok, independentLocalRead: true), "Running.", read.ResourceEvidence, null);
                        string reason, detail; int delay;
                        AssertTrue(!tracker.CanDispatch(runningContext.Execution.Call, out reason, out detail, out delay),
                            "reading cached running state never resolves the refresh replay guard");
                        release.Set();
                        AssertTrue(SpinWait.SpinUntil(() => owner.ReadStatus("Sales").ExecutionStopped, 5000), "late completion remains observed");
                        AssertTrue(SpinWait.SpinUntil(() => {
                            try { return otherHost.ReadDocument(target, () => true); }
                            catch (HostRuntime.MutationLockException) { return false; }
                        }, 5000), "native reservation is released only after confirmed completion unwinds");
                        AssertEqual(1, callbacks, "late completion does not publish a second terminal");
                        var completed = owner.ReadStatusTool(gateway, session, "Sales", 32000);
                        tracker.Observe(new ToolCall("late-status", "core.read", "{}"),
                            new ToolExecutionProgress(ToolExecutionOutcome.Ok, independentLocalRead: true), "Completed.", completed.ResourceEvidence, null);
                        AssertTrue(tracker.CanDispatch(runningContext.Execution.Call, out reason, out detail, out delay),
                            "fresh exact late status permits a new authorized refresh");
                        AssertEqual("timed_out", owner.ReadStatus("Sales").WaitState,
                            "late native completion does not rewrite the old caller's timeout");
                    }
                    finally { release.Set(); }
                }
                backend.RefreshOverride = (nativeRequest, dispatch) => {
                    dispatch();
                    return new ExcelPowerQueryRefreshResult { State = ExcelPowerQueryRefreshState.Succeeded,
                        NativeRunning = false, SourceStillCurrent = false, BindingStillCurrent = true,
                        ObservedAtUtc = DateTime.UtcNow, Message = "Source changed during execution." };
                };
                var drift = owner.RefreshAsync(host, session, gateway, context("refresh-source-drift"), request,
                    1000, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Unknown, drift.Result.Status,
                    "native success for an old source does not verify current source/data");
                AssertEqual(ToolEffectEvidence.Unknown, drift.Effect, "source drift never becomes a verified refresh effect");
                backend.RefreshOverride = (nativeRequest, dispatch) => {
                    throw new ExcelPowerQueryBackendException("Binding changed before dispatch.", "excel_query_binding_changed");
                };
                var beforeDispatch = backend.Dispatches;
                var rejected = owner.RefreshAsync(host, session, gateway, context("refresh-rejected"), request,
                    1000, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Error, rejected.Result.Status, "pre-dispatch failure remains an ordinary error");
                AssertEqual(beforeDispatch, backend.Dispatches, "pre-dispatch rejection performs no refresh");
                AssertTrue(host.ReadDocument(target, () => true), "pre-dispatch failure releases its reservation");
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    RuntimeThrows<OperationCanceledException>(() => owner.RefreshAsync(host, session, gateway,
                        context("refresh-pre-cancelled"), request, 1000, cancellation.Token).GetAwaiter().GetResult());
                    AssertEqual(beforeDispatch, backend.Dispatches, "a cancelled admission never reaches the native dispatch marker");
                }
                backend.RefreshOverride = (nativeRequest, dispatch) => {
                    dispatch();
                    return new ExcelPowerQueryRefreshResult { State = ExcelPowerQueryRefreshState.Failed, NativeRunning = false,
                        SourceStillCurrent = true, BindingStillCurrent = true, ObservedAtUtc = DateTime.UtcNow,
                        ErrorCode = "native_refresh_failed", Message = "The provider failed after refresh started." };
                };
                var failed = owner.RefreshAsync(host, session, gateway, context("refresh-native-failed"), request,
                    1000, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Unknown, failed.Result.Status,
                    "a native failure after dispatch does not assert unchanged worksheet contents");
                AssertEqual(ToolEffectEvidence.Unknown, failed.Effect, "confirmed failure is separate from effect certainty");

                backend.RefreshOverride = (nativeRequest, dispatch) => {
                    dispatch();
                    return new ExcelPowerQueryRefreshResult { State = ExcelPowerQueryRefreshState.Unresolved, NativeRunning = false,
                        SourceStillCurrent = true, BindingStillCurrent = true, ObservedAtUtc = DateTime.UtcNow,
                        ErrorCode = "excel_query_overflow_observation_unavailable",
                        Message = "Native work stopped, but the overflow observation is unavailable." };
                };
                var unverifiedContext = context("refresh-stopped-unverified");
                var unverified = owner.RefreshAsync(host, session, gateway, unverifiedContext, request,
                    1000, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Unknown, unverified.Result.Status,
                    "positive native stop does not turn an unverified result into success");
                AssertEqual(ToolEffectEvidence.Unknown, unverified.Effect, "business effect remains unknown after native stop");
                var unverifiedStatus = owner.ReadStatus("Sales");
                AssertTrue(unverifiedStatus.ExecutionStopped && unverifiedStatus.NativeState == "unresolved",
                    "status keeps result uncertainty separate from known native stop");
                AssertTrue(host.ReadDocument(target, () => true), "the positively stopped native operation releases the gate");
                var unverifiedOperation = unverified.ResourceEvidence.Single().OperationObservation.Operation;
                var unverifiedProgress = new ToolExecutionProgress(ToolExecutionOutcome.Unknown, completedUtc: DateTime.UtcNow,
                    operation: unverifiedOperation);
                var unverifiedTracker = new AgentProgressTracker();
                unverifiedTracker.Observe(unverifiedContext.Execution.Call, unverifiedProgress, "Unverified result.", unverified.ResourceEvidence, null);
                string unverifiedReason, unverifiedDetail; int unverifiedDelay;
                AssertTrue(!unverifiedTracker.CanDispatch(unverifiedContext.Execution.Call, out unverifiedReason, out unverifiedDetail, out unverifiedDelay),
                    "the original uncertain terminal cannot authorize its own repeat");
                var unverifiedRead = owner.ReadStatusTool(gateway, session, "Sales", 32000);
                var stopEvidence = unverifiedRead.ResourceEvidence.Single(item => item.OperationObservation.Operation.Matches(unverifiedOperation));
                AssertEqual(ToolOperationState.StoppedUnverified, stopEvidence.OperationObservation.State,
                    "a fresh positive stop gets an explicit lifecycle proof without claiming success");
                AssertTrue(stopEvidence.OperationObservation.ExecutionStopped && stopEvidence.OperationObservation.SafeToInvokeAgain,
                    "known stopped work permits a later invocation through normal guards");
                var staleStop = new ResourceEvidence("stale-stop", stopEvidence.ScopeId, stopEvidence.Resource, "status",
                    ResourceCoverage.Whole(), true, stopEvidence.AuthorityGeneration, observedAt: unverifiedProgress.CompletedUtc.Value,
                    operationObservation: stopEvidence.OperationObservation);
                AssertTrue(!stopEvidence.OperationObservation.Resolves(unverifiedProgress, staleStop),
                    "an old stopped observation cannot clear the uncertain call");
                unverifiedTracker.Observe(new ToolCall("non-independent-stop", "core.read", "{}"),
                    new ToolExecutionProgress(ToolExecutionOutcome.Ok, independentLocalRead: false), "Stopped.", new[] { stopEvidence }, null);
                AssertTrue(!unverifiedTracker.CanDispatch(unverifiedContext.Execution.Call, out unverifiedReason, out unverifiedDetail, out unverifiedDelay),
                    "only an independent accepted status read can resolve the native guard");
                var dispatchesBeforeRead = backend.Dispatches;
                unverifiedTracker.Observe(new ToolCall("accepted-stopped-status", "core.read", "{}"),
                    new ToolExecutionProgress(ToolExecutionOutcome.Ok, independentLocalRead: true), "Stopped.", new[] { stopEvidence }, null);
                AssertTrue(unverifiedTracker.CanDispatch(unverifiedContext.Execution.Call, out unverifiedReason, out unverifiedDetail, out unverifiedDelay),
                    "exact fresh stopped-but-unverified evidence allows a new requested refresh");
                AssertEqual(dispatchesBeforeRead, backend.Dispatches, "reconciliation never automatically dispatches another refresh");
                AssertEqual(ToolExecutionOutcome.Unknown, unverifiedProgress.Outcome, "reconciliation preserves the original Unknown fact");

                backend.LoadOverride = (loadRequest, dispatch) => {
                    dispatch();
                    return new ExcelPowerQueryLoadResult { SheetCreated = true, TableCreated = true,
                        Sheet = "CreatedSheet", Table = "TableGenerated1", NativeRunning = false, BindingVerified = false,
                        ErrorCode = "excel_query_load_partial", Message = "Table exists under its generated name; final name assignment failed." };
                };
                var loadCall = new ToolCall("partial-load", ExcelPowerQueryToolCatalog.Load,
                    "{\"target\":\"Excel query: Sales\",\"sheet\":\"CreatedSheet\",\"address\":\"A1\",\"table\":\"RequestedResult\",\"createSheet\":true}");
                var loadContext = new ToolHandlerContext(new ToolExecutionContext(loadCall,
                    new ToolPolicySnapshot(loadCall.Name, "pq-test", Policy(ToolEffect.Write, ToolVerification.Tool)),
                    "run", "turn", loadCall.Id, DateTime.UtcNow, false, 10), new Dictionary<string, object>());
                var partial = owner.LoadAsync(host, session, gateway, loadContext, new ExcelPowerQueryLoadRequest {
                    QueryName = "Sales", Sheet = "CreatedSheet", Address = "A1", Table = "RequestedResult", CreateSheet = true,
                    ExpectedQueryIdentity = request.ExpectedQueryIdentity, ExpectedSourceVersion = request.ExpectedSourceVersion,
                    ExpectedBindingVersion = request.ExpectedBindingVersion }, 1000, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(RNAssistant.Core.Tools.Contracts.ToolResultStatus.Unknown, partial.Result.Status,
                    "a partial load preserves uncertain created objects rather than reporting success or rollback");
                var partialStatus = owner.ReadStatus("Sales");
                AssertTrue(partialStatus.SheetCreated == true && partialStatus.TableCreated == true &&
                    partialStatus.LoadedTableTarget == "Excel table: TableGenerated1" && partialStatus.Sheet == "CreatedSheet",
                    "partial load status reports actual created object identities, not requested names");
                AssertEqual("Excel table: RequestedResult", partialStatus.RequestedTableTarget,
                    "the requested destination remains distinguishable from the observed partial result");
                AssertTrue(host.ReadDocument(target, () => true), "confirmed non-running partial load releases ownership without claiming a full result");
                for (var mode = 0; mode < 2; mode++)
                {
                    var late = mode == 1;
                    using (var nativeStarted = new ManualResetEventSlim(false))
                    using (var nativeRelease = new ManualResetEventSlim(!late))
                    {
                        backend.RefreshOverride = (nativeRequest, dispatch) => {
                            dispatch(); nativeStarted.Set();
                            if (!nativeRelease.Wait(5000)) throw new InvalidOperationException("Publication fixture was not released.");
                            return new ExcelPowerQueryRefreshResult { State = ExcelPowerQueryRefreshState.Succeeded,
                                NativeRunning = false, SourceStillCurrent = true, BindingStillCurrent = true,
                                ObservedAtUtc = DateTime.UtcNow };
                        };
                        var attempts = 0;
                        var publicationCall = new ToolCall("publication-failure-" + mode, ExcelPowerQueryToolCatalog.Refresh,
                            "{\"target\":\"Excel query: Sales\"}");
                        var publicationContext = new ToolHandlerContext(new ToolExecutionContext(publicationCall,
                            new ToolPolicySnapshot(publicationCall.Name, "pq-test", Policy(ToolEffect.Write, ToolVerification.Tool)),
                            "run", "turn", publicationCall.Id, DateTime.UtcNow, false, 10), new Dictionary<string, object>(),
                            onCompleted: result => { Interlocked.Increment(ref attempts); throw new InvalidOperationException("Injected publication failure."); });
                        var publication = owner.RefreshAsync(host, session, gateway, publicationContext, request,
                            late ? 250 : 1000, CancellationToken.None);
                        try
                        {
                            AssertTrue(nativeStarted.Wait(5000), "publication fixture crosses its native dispatch boundary");
                            RuntimeThrows<InvalidOperationException>(() => publication.GetAwaiter().GetResult());
                            AssertEqual(1, attempts, "a failed completion callback is never attempted a second time");
                            if (late) RuntimeThrows<HostRuntime.MutationLockException>(() => host.ReadDocument(target, () => true));
                        }
                        finally { nativeRelease.Set(); }
                        AssertTrue(SpinWait.SpinUntil(() => owner.ReadStatus("Sales").ExecutionStopped, 5000),
                            "publication failure preserves known native completion and late observation");
                        AssertTrue(SpinWait.SpinUntil(() => {
                            try { return host.ReadDocument(target, () => true); }
                            catch (HostRuntime.MutationLockException) { return false; }
                        }, 5000), "publication failure cannot leak a lease after confirmed native completion");
                        AssertEqual(1, attempts, "late completion does not retry a previously failed publication callback");
                    }
                }
            });
        }

        private static void PowerQueryLateResolutionSurvivesBoundedRestore()
        {
            WithTempPaths(paths =>
            {
                const string chat = "pq-chat", run = "pq-run";
                var journal = new ConversationJournal(paths, "document:power-query");
                var target = new ResourceIdentity(ResourceUri.Create("excel", "fixture", "query", "Sales"));
                var scope = new ResourceAuthorityScopeId("document", "fixture");
                var call = new ToolCall("refresh-1", "excel.query.refresh", "{\"target\":\"Excel query: Sales\"}");
                var completed = DateTime.UtcNow;
                var operation = new ToolOperationReference("workbook-session", "generation-1", run, call.Id,
                    AgentProgressTracker.Signature(call), target, "status");
                var pending = new ToolExecutionProgress(ToolExecutionOutcome.Unknown, completedUtc: completed,
                    operation: operation);
                var original = AppendIndexedContext(journal, chat, call.Id, run,
                    new ToolResultMaterialization(ToolResult.Unknown("Native refresh outlived its caller.")) {
                        ExecutionProgress = pending }, call.Name, JObject.Parse(call.ArgumentsJson));
                var history = new List<AgentMessage> {
                    AgentMessage.Assistant(new AgentResponse(ConversationResponse.ExecuteAction, "Refresh.", "", new[] { call })),
                    AgentMessage.AcceptedToolResult(call.Id, "Pending.", "{}", pending)
                };
                var tracker = new AgentProgressTracker(); tracker.Restore(history);
                var ordinal = 0;
                Action<ToolOperationReference, ToolOperationState, bool, bool, bool> observe = (reference, state, stopped, safe, immutable) =>
                {
                    var observation = new ToolOperationObservation(reference, state, completed.AddSeconds(1), stopped, safe);
                    var evidence = new ResourceEvidence("status-" + ++ordinal, scope, new ResourceRef(target.Uri, "status-" + ordinal),
                        "status", ResourceCoverage.Whole(), true, 1, observedAt: completed.AddSeconds(2),
                        immutable: immutable, operationObservation: observation);
                    var id = "status-" + ordinal;
                    var progress = new ToolExecutionProgress(ToolExecutionOutcome.Ok, independentLocalRead: true);
                    AppendIndexedContext(journal, chat, id, run,
                        new ToolResultMaterialization(ToolResult.Ok("Observed status."), resourceEvidence: new[] { evidence }) {
                            ExecutionProgress = progress }, "core.read", new JObject { ["target"] = "Excel query: Sales", ["representation"] = "status" });
                    tracker.Observe(new ToolCall(id, "core.read", "{}"), progress, "Status.", new[] { evidence }, null);
                };
                Action<string> blocked = message => {
                    string blockedReason, blockedDetail; int blockedDelay;
                    AssertTrue(!tracker.CanDispatch(call, out blockedReason, out blockedDetail, out blockedDelay), message + " (memory)");
                    AssertTrue(journal.FindContextReplayGuard(chat, call) != null, message + " (journal)");
                };
                observe(operation, ToolOperationState.Running, false, false, false);
                blocked("reading running status cannot replay the refresh");
                var wrongGeneration = new ToolOperationReference(operation.SessionId, "another-generation", run, call.Id,
                    operation.CallSignature, target, "status");
                observe(wrongGeneration, ToolOperationState.Succeeded, true, true, false);
                blocked("a different native generation cannot resolve this call");
                var wrongSession = new ToolOperationReference("other-workbook-session", operation.Generation, run, call.Id,
                    operation.CallSignature, target, "status");
                observe(wrongSession, ToolOperationState.Succeeded, true, true, false);
                blocked("a different document session cannot resolve this call");
                var wrongCall = new ToolOperationReference(operation.SessionId, operation.Generation, run, "other-call",
                    operation.CallSignature, target, "status");
                observe(wrongCall, ToolOperationState.Succeeded, true, true, false);
                blocked("a different accepted call cannot resolve this call");
                observe(operation, ToolOperationState.Succeeded, true, false, false);
                blocked("stopped execution without safe invocation evidence keeps replay protected");
                observe(operation, ToolOperationState.Succeeded, true, true, true);
                blocked("historical immutable observations cannot grant a live repeat");
                RuntimeThrows<ArgumentException>(() => new ToolOperationObservation(operation,
                    ToolOperationState.Unresolved, completed.AddSeconds(1), true, true));
                RuntimeThrows<ArgumentException>(() => new ToolOperationObservation(operation,
                    ToolOperationState.StoppedUnverified, completed.AddSeconds(1), false, false));
                observe(operation, ToolOperationState.StoppedUnverified, true, true, true);
                blocked("a historical unverified-stop observation cannot grant a live repeat");
                RuntimeThrows<ArgumentException>(() => new ResourceEvidence("wrong-view", scope, new ResourceRef(target.Uri, "r1"),
                    "source", ResourceCoverage.Whole(), true, 1, operationObservation:
                        new ToolOperationObservation(operation, ToolOperationState.Succeeded, completed.AddSeconds(1), true, true)));

                observe(operation, ToolOperationState.StoppedUnverified, true, true, false);
                string reason, detail; int delay;
                AssertTrue(tracker.CanDispatch(call, out reason, out detail, out delay), "exact late completion allows a new authorized invocation");
                AssertTrue(journal.FindContextReplayGuard(chat, call) == null, "durable replay projection agrees with live tracker");
                for (var i = 0; i < 80; i++) AppendIndexedContext(journal, chat, "noise-" + i, "later-run", IndexedNoise());
                var restarted = new ConversationJournal(paths, "document:power-query");
                var window = restarted.RestoreContext(chat, "later-run", 1, 0);
                AssertTrue(window.Recent.All(item => item.ToolCallId != "status-" + ordinal), "bounded context evicts the old resolution read");
                var restored = new AgentProgressTracker(); restored.Restore(history);
                AssertTrue(!restored.CanDispatch(call, out reason, out detail, out delay), "restoring just the old Unknown reproduces the stale prompt guard");
                var receipt = restarted.ReadOperationResolution(chat, restored.PendingNativeOperation(call));
                AssertTrue(receipt != null && receipt.OperationObservation.Operation.Matches(operation),
                    "the durable exact call locator recovers canonical resolution outside the prompt window");
                AssertEqual(ToolOperationState.StoppedUnverified, receipt.OperationObservation.State,
                    "durable restoration preserves stopped lifecycle evidence without upgrading the business result");
                restored.ObserveOperationResolution(receipt);
                AssertTrue(restored.CanDispatch(call, out reason, out detail, out delay), "canonical resolution releases only that restored native guard");
                AssertTrue(restarted.ReadOperationResolution(chat, wrongGeneration) == null &&
                    restarted.ReadOperationResolution("foreign-chat", operation) == null,
                    "resolution lookup does not trust a caller's generation or another chat");
                restarted.Rebuild();
                AssertTrue(restarted.ReadOperationResolution(chat, operation) != null && restarted.FindContextReplayGuard(chat, call) == null,
                    "index rebuild restores typed resolution from original append-only facts");
                AssertEqual(original.Result.Position.Hash, restarted.ReadContextOperation(chat, original.Result.OperationId).Result.Position.Hash,
                    "late observation never rewrites the original terminal bytes");
                AssertEqual(ToolExecutionOutcome.Unknown, restarted.ReadContextOperation(chat, original.Result.OperationId).Result.Fact.Result.ExecutionProgress.Outcome,
                    "original tool outcome remains Unknown after execution reconciliation");

                var next = new ToolCall("refresh-2", call.Name, call.ArgumentsJson);
                var nextOperation = new ToolOperationReference(operation.SessionId, "generation-2", run, next.Id,
                    operation.CallSignature, target, "status");
                var nextPending = new ToolExecutionProgress(ToolExecutionOutcome.Unknown, completedUtc: completed.AddSeconds(3), operation: nextOperation);
                AppendIndexedContext(restarted, chat, next.Id, run,
                    new ToolResultMaterialization(ToolResult.Unknown("Next native operation pending.")) { ExecutionProgress = nextPending },
                    next.Name, JObject.Parse(next.ArgumentsJson));
                restored.Observe(next, nextPending, "Pending.", null, null);
                restored.ObserveOperationResolution(receipt);
                AssertTrue(!restored.CanDispatch(next, out reason, out detail, out delay), "old resolution cannot release a newer identical refresh");
                AssertTrue(restarted.FindContextReplayGuard(chat, next) != null && restarted.ReadOperationResolution(chat, nextOperation) == null,
                    "new generation keeps a distinct unresolved durable guard");
                restarted.Rebuild();
                AssertEqual("generation-2", restarted.FindContextReplayGuard(chat, next).Result.Fact.Result.ExecutionProgress.Operation.Generation,
                    "restart preserves the latest exact generation instead of reusing an old resolution");
            });
        }

        private static void PowerQueryResourcesKeepBoundsAndRevisionCursors()
        {
            var backend = new StubPowerQueryBackend();
            backend.Seed("Paged", "let Source = #table({\"Id\"}, {{1}}) in Source");
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            adapter.ExcelPowerQueryBackend = backend;
            var provider = new ExcelResourceProvider(adapter, adapter, null);
            var session = NewSession(adapter);
            session.DocumentAuthorityId = DocumentAuthorityId.Create().Id;
            var reference = provider.ResolveQuery(session, "Paged").Reference;
            var first = provider.Read(session, new ResourceReadRequest { Reference = reference,
                Representation = ResourceRepresentations.Source, MaxChars = 12 }).Result;
            AssertTrue(first.Text.Length == 12 && first.Truncated && !first.Complete && first.NextCursor != null,
                "query source uses bounded continuation instead of dumping the entire Formula");
            AssertTrue(first.RawContentIncluded && first.Offset == 0 && first.ReturnedCharacters == 12 && !first.Complete,
                "delivered M source retains its exact partial-read boundary and never gains whole-source coverage");
            var next = provider.Read(session, new ResourceReadRequest { Reference = reference,
                Representation = ResourceRepresentations.Source, MaxChars = 12, Cursor = first.NextCursor }).Result;
            AssertEqual(12, next.Offset, "query continuation advances within one source revision");
            backend.Seed("Paged", "let Source = #table({\"Id\"}, {{2}}) in Source");
            AssertEqual("resource_revision_changed", RuntimeThrows<ResourceRequestException>(() => provider.Read(session,
                new ResourceReadRequest { Reference = reference, Representation = ResourceRepresentations.Source,
                    MaxChars = 12, Cursor = first.NextCursor })).ErrorCode,
                "live source drift cannot continue an old cursor against new M");
            backend.Seed("TooLarge", new string('x', ExcelResourceProvider.MaximumQuerySourceCharacters + 1));
            AssertEqual("RESOURCE_SNAPSHOT_TOO_LARGE", RuntimeThrows<ResourceRequestException>(() =>
                provider.ResolveQuery(session, "TooLarge")).ErrorCode, "oversized source capture fails explicitly");
            for (var i = 0; i < ExcelResourceProvider.MaximumQueries; i++) backend.Seed("Q" + i.ToString("D4"), "42");
            var sourceReads = backend.SourceReads;
            var page = provider.List(session, ExcelResourceProvider.QueryKind, null, 50);
            var total = page.Items.Count;
            while (page.NextCursor != null)
            {
                page = provider.List(session, ExcelResourceProvider.QueryKind, page.NextCursor, 50);
                total += page.Items.Count;
            }
            AssertEqual(ExcelResourceProvider.MaximumQueries, total, "discovery preserves its total capture bound across pages");
            AssertTrue(page.Truncated, "end of available catalog pages does not claim complete discovery beyond the native bound");
            AssertEqual(sourceReads, backend.SourceReads, "bounded discovery never fetches source bodies");
            AssertEqual(0, backend.Dispatches, "all cursor and bound failures precede any effect");
        }

        private static void PowerQueryResourcesReadExactSourceWithoutExecution()
        {
            var backend = new StubPowerQueryBackend();
            const string name = "Продажи; O'Brien";
            const string formula = "let\r\n    // keep spacing and culture\r\n    Source = #table({\"Сумма\"}, {{42}})\r\nin\r\n    Source\r\n";
            backend.Seed(name, formula, "A source definition only");
            backend.Seed("Parameter", "2026 meta [IsParameterQuery=true, Type=\"Number\"]");
            backend.Seed("Transform", "(value as text) => value");
            var adapter = FakeOfficeAdapter.ForHost("Excel");
            adapter.ExcelPowerQueryBackend = backend;
            WithTempExecutor(adapter, (executor, ignored) =>
            {
                var session = NewSession(adapter);
                executor.BindResourceAuthority(session);
                var gateway = executor.ResourceGateway;
                var catalog = gateway.List(session, "excel", ExcelResourceProvider.QueryKind, null, 2);
                AssertEqual(2, catalog.Items.Count, "query discovery respects the requested page bound");
                AssertTrue(catalog.NextCursor != null, "query catalog provides a continuation for remaining names");
                AssertEqual(0, backend.SourceReads, "enumerating names does not fetch every Formula");
                var last = gateway.List(session, "excel", ExcelResourceProvider.QueryKind, catalog.NextCursor, 2);
                AssertEqual(1, last.Items.Count, "query continuation returns only the remaining definition");
                var found = gateway.Find(session, name, "document").Items.Single(item => item.Target == "Excel query: " + name);
                AssertEqual(0, backend.SourceReads, "core.find discovers exact query names from metadata only");
                AssertTrue(found.Representations.Contains(ResourceRepresentations.Source), "query advertises the common source representation");

                var tools = OfficeToolCatalog.ForHost("Excel").Concat(executor.GetControllerTools()).ToList();
                var runtime = executor.CreateNativeRuntime(session, tools, new AppSettings(), "agent", false);
                var read = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel query: " + name, ["representation"] = ResourceRepresentations.Source });
                AssertEqual(ToolExecutionOutcome.Ok, read.Outcome, "generic core.read returns M source: " + read.Message);
                AssertEqual(formula, (string)JObject.Parse(read.Result.DataJson)["text"], "M bytes, comments and line endings remain exact");
                AssertEqual(true, (bool)JObject.Parse(read.Result.DataJson)["rawContentIncluded"],
                    "core.read declares the exact M source body delivered for the shared observation guard");
                var evidence = read.ResourceEvidence.Single();
                AssertEqual(ResourceRepresentations.Source, evidence.View, "source evidence uses the common exact representation");
                AssertTrue(evidence.Complete && evidence.Coverage.Kind == ResourceCoverageKinds.Whole,
                    "complete source delivery retains its explicit whole-source coverage");
                AssertEqual(TextPatternEngine.Sha256(formula), evidence.ContentSha256, "source evidence hashes the exact Formula bytes");
                AssertTrue(evidence.Resource.IsExact, "logical authority pins the observed source revision");
                var search = gateway.SearchWithin(session, "Excel query: " + name, "#table", 5, true, CancellationToken.None);
                AssertTrue(search.ScanComplete && search.TotalMatches == 1, "generic literal source search examines M without execution");

                var metadata = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel query: Transform", ["representation"] = "metadata" });
                AssertEqual(ToolExecutionOutcome.Ok, metadata.Outcome, "query metadata reads through core.read: " + metadata.Message + " " + metadata.Result?.DataJson);
                AssertEqual(false, (bool)JObject.Parse(metadata.Result.DataJson)["rawContentIncluded"],
                    "query metadata is not declared as a delivered M source body");
                var metadataBody = JObject.Parse((string)JObject.Parse(metadata.Result.DataJson)["text"]);
                AssertEqual("unknown", (string)metadataBody["returnType"], "function/scalar shape is not inferred by hidden evaluation");
                var bindings = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel query: Transform", ["representation"] = "bindings" });
                AssertEqual(ToolExecutionOutcome.Ok, bindings.Outcome, "query binding metadata reads through core.read: " + bindings.Message);
                var bindingBody = JObject.Parse((string)JObject.Parse(bindings.Result.DataJson)["text"]);
                AssertEqual(0, ((JArray)bindingBody["loads"]).Count, "a function definition without a worksheet load remains unloaded");
                var analysis = ExecuteHtmlNative(runtime, ResourceToolCatalog.ReadToolId,
                    new JObject { ["target"] = "Excel query: Transform", ["representation"] = "dependencies" });
                AssertEqual(ToolExecutionOutcome.Ok, analysis.Outcome, "query dependency diagnostics read through core.read: " + analysis.Message);
                var analysisBody = JObject.Parse((string)JObject.Parse(analysis.Result.DataJson)["text"]);
                AssertEqual("not_checked", (string)analysisBody["syntax"], "unavailable parser is not reported as syntax success");
                AssertTrue(!(bool)analysisBody["complete"], "dependency analysis explicitly retains incomplete coverage");

                backend.Seed(name, "42");
                var sourceReads = backend.SourceReads;
                var retained = gateway.Read(session, new ResourceReadRequest { Reference = evidence.Resource,
                    Representation = ResourceRepresentations.Source, MaxChars = 32000 }).Result;
                AssertEqual(formula, retained.Text, "historical source uses retained CAS bytes after the live definition changes");
                AssertEqual(sourceReads, backend.SourceReads, "historical evidence performs no new Office read");
                backend.Seed("Bad/Name", "42");
                AssertTrue(gateway.Find(session, name, "document").Items.Any(item => item.Target == "Excel query: " + name),
                    "an unsupported catalog name does not suppress healthy query discovery");
                RuntimeThrows<ResourceRequestException>(() => gateway.ResolveIntentTarget(session, "Excel query: " + name + " "));
                AssertEqual(sourceReads, backend.SourceReads, "an unsupported boundary-space name cannot redirect to the trimmed existing query");
                var foreign = NewSession(adapter); foreign.DocumentKey = "another-workbook";
                RuntimeThrows<ResourceRequestException>(() => gateway.ResolveIntentTarget(foreign, "Excel query: " + name));
                AssertEqual(sourceReads, backend.SourceReads, "wrong workbook is refused before native query access");
                AssertEqual(0, backend.Dispatches, "discovery, source reads and analysis have no native execution boundary");
                AssertEqual(0, backend.Refreshes.Count + backend.Loads.Count + backend.Writes.Count + backend.Creates.Count,
                    "generic readers never refresh, load or edit queries");
            });
        }

        private static void PowerQueryConnectionUsesExactMashupLocation()
        {
            string location;
            AssertTrue(ExcelPowerQueryConnection.TryGetMashupLocation(
                "OLEDB;Provider=Microsoft.Mashup.OleDb.1;Data Source=$Workbook$;Location=\"Sales; Quarter \"\"A\"\"\";Extended Properties=\"\";",
                out location), "quoted Mashup connection is parsed without splitting the query name");
            AssertEqual("Sales; Quarter \"A\"", location, "semicolons and doubled quotes are query-name content");
            AssertTrue(ExcelPowerQueryConnection.TryGetMashupLocation(
                "  oledb;provider=MICROSOFT.MASHUP.OLEDB.1;location=MiXeD_query;", out location),
                "connection syntax/provider are case insensitive");
            AssertEqual("MiXeD_query", location, "M query identity casing is preserved");
            AssertTrue(!ExcelPowerQueryConnection.TryGetMashupLocation(
                "Provider=MSOLEDBSQL;Location=Sales;", out location) && location == null,
                "unrelated provider cannot bind to a query through a coincidentally named Location");
            AssertTrue(!ExcelPowerQueryConnection.TryGetMashupLocation(
                "Provider=Microsoft.Mashup.OleDb.1;Location=\"unterminated", out location) && location == null,
                "malformed connection does not produce a partial binding");
            AssertTrue(!ExcelPowerQueryConnection.TryGetMashupLocation(
                "Provider=Microsoft.Mashup.OleDb.1;Extended Properties=Location=Sales;", out location),
                "Location embedded in a different property does not identify a query");
            foreach (var name in new[] { "Sales", "Продажи O'Brien", "Sales;Provider=MSOLEDBSQL", "OLEDB;MiXeD", "a\"b'c]d" })
            {
                var connection = ExcelPowerQueryConnection.BuildMashupConnection(name);
                AssertTrue(ExcelPowerQueryConnection.TryGetMashupLocation(connection, out location),
                    "generated native connection uses the standard grammar");
                AssertEqual(name, location, "generated connection preserves the entire exact query name");
            }
            AssertEqual("SELECT * FROM [Sales]]; DROP TABLE Data; --]",
                ExcelPowerQueryConnection.BuildSelectCommand("Sales]; DROP TABLE Data; --"),
                "query names are quoted as one Mashup identifier, including closing brackets");
            RuntimeThrows<ArgumentException>(() => ExcelPowerQueryConnection.BuildMashupConnection(" "));
            RuntimeThrows<ArgumentException>(() => ExcelPowerQueryConnection.BuildSelectCommand(null));
        }

        private static void PowerQueryInspectionRequiresMatchingView()
        {
            var identity = new ResourceIdentity(ResourceUri.Create("excel", "fixture", "query", "Sales"));
            var scope = new ResourceAuthorityScopeId("document", "power-query-fixture");
            var completed = DateTime.UtcNow;
            var write = new ToolCall("write", "core.write", "{\"target\":\"Excel query: Sales\"}");
            var refresh = new ToolCall("refresh", "excel.query.refresh", "{\"query\":\"Sales\"}");
            var history = new List<AgentMessage>();
            foreach (var item in new[] { new { Call = write, View = "text" }, new { Call = refresh, View = "status" } })
            {
                var progress = JsonConvert.DeserializeObject<ToolExecutionProgress>(JsonConvert.SerializeObject(
                    new ToolExecutionProgress(ToolExecutionOutcome.Unknown,
                        new ToolRecoveryContract(ToolFailureKind.ToolDefect, ToolRetryPolicy.RefreshRequired,
                            identity, item.View, "Excel query: Sales", readScope: ToolRecoveryReadScope.Whole),
                        completedUtc: completed)));
                history.Add(AgentMessage.Assistant(new AgentResponse(ConversationResponse.ExecuteAction,
                    "Update query.", "", new[] { item.Call })));
                history.Add(AgentMessage.AcceptedToolResult(item.Call.Id, "Unresolved operation.", "{}", progress,
                    effect: new ResourceEffect(item.Call.Id, item.Call.Name, ResourceEffectOutcome.UnknownAfterDispatch,
                        new[] { new ResourceImpact(identity, ResourceImpactRelation.Exact) })));
            }
            var tracker = new AgentProgressTracker();
            tracker.Restore(history);
            var reads = 0;
            Action<string, DateTime, bool, bool, bool> read = (view, observed, whole, immutable, independent) =>
            {
                var call = new ToolCall("read-" + ++reads, "core.read", "{\"representation\":\"" + view + "\"}");
                var evidence = new[] { new ResourceEvidence(call.Id, scope, new ResourceRef(identity.Uri, "observed"),
                    view, whole ? ResourceCoverage.Whole() : new ResourceCoverage(ResourceCoverageKinds.CharacterRange,
                        start: 0, end: 10), true, 1, observedAt: observed, immutable: immutable) };
                var progress = new ToolExecutionProgress(ToolExecutionOutcome.Ok, independentLocalRead: independent);
                tracker.Observe(call, progress, "Read.", evidence, null);
                history.Add(AgentMessage.Assistant(new AgentResponse(ConversationResponse.ExecuteAction,
                    "Inspect query.", "", new[] { call })));
                history.Add(AgentMessage.AcceptedToolResult(call.Id, "Read.", "{}", progress, evidence: evidence));
            };

            read("metadata", completed.AddSeconds(1), true, false, true);
            AssertTrue(tracker.InspectionRequired, "query metadata is neither source nor execution status evidence");
            read("status", completed.AddSeconds(-1), true, false, true);
            AssertTrue(tracker.InspectionRequired, "cached running status retains its old observation time");
            read("status", completed.AddSeconds(1), false, false, true);
            read("status", completed.AddSeconds(1), true, true, true);
            read("status", completed.AddSeconds(1), true, false, false);
            AssertTrue(tracker.InspectionRequired, "partial, historical and non-independent observations do not inspect execution");
            read("status", completed.AddSeconds(1), true, false, true);
            AssertTrue(tracker.InspectionRequired, "complete execution status cannot inspect an uncertain M source write");
            var restored = new AgentProgressTracker();
            restored.Restore(history);
            AssertTrue(restored.InspectionRequired, "continuation retains the separate source obligation after status read");
            read("text", completed.AddSeconds(2), true, false, true);
            AssertTrue(!tracker.InspectionRequired, "fresh complete source and status observations satisfy their separate scopes");
            restored.Restore(new AgentMessage[0]);
            AssertTrue(restored.InspectionRequired, "a tracker does not borrow another owner's observations");
            restored = new AgentProgressTracker();
            restored.Restore(history);
            AssertTrue(!restored.InspectionRequired, "replayed observations preserve both resolved inspection scopes");
            foreach (var state in new[] { tracker, restored })
            foreach (var call in new[] { write, refresh })
            {
                string reason, message; int delay;
                AssertTrue(!state.CanDispatch(call, out reason, out message, out delay),
                    "read inspection alone never releases uncertain source or refresh replay");
                AssertEqual("repeated_unknown_tool_call", reason, "exact replay protection is retained");
            }

            var unqualified = new AgentProgressTracker();
            unqualified.Observe(write, new ToolExecutionProgress(ToolExecutionOutcome.Unknown, completedUtc: completed),
                "Unknown legacy view.", null, new ResourceEffect("legacy", write.Name, ResourceEffectOutcome.UnknownAfterDispatch,
                    new[] { new ResourceImpact(identity, ResourceImpactRelation.Exact) }));
            unqualified.Observe(new ToolCall("legacy-read", "core.read", "{}"),
                new ToolExecutionProgress(ToolExecutionOutcome.Ok, independentLocalRead: true), "Read.",
                new[] { new ResourceEvidence("legacy-read", scope, new ResourceRef(identity.Uri, "r2"),
                    "metadata", ResourceCoverage.Whole(), true, 1, observedAt: completed.AddSeconds(1)) }, null);
            AssertTrue(!unqualified.InspectionRequired, "existing effects without an explicit inspection view retain their contract");
        }
    }
}
