using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Agent;
using RNAssistant.Core.Services;
using RNAssistant.Core.Tools;
using RNAssistant.Office;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;
using RNAssistant.Office.Services;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void PowerQueryReservationRetainsOwnerAndDispatcher()
        {
            using (var dispatcher = new OfficeStaDispatcher())
            {
                var adapter = new PowerQueryLifetimeAdapter(dispatcher);
                var runtime = new HostRuntime(adapter, null);
                var second = new HostRuntime(adapter, null);
                var target = new OfficeDocumentExecutionExpectation { Host = "Excel", DocumentKey = adapter.DocumentKey,
                    RuntimeDocumentKey = adapter.RuntimeDocumentKey };
                using (var retained = runtime.ReserveDocumentOperation(target, CancellationToken.None))
                {
                    var completion = dispatcher.DocumentOperationCompletion;
                    AssertTrue(!completion.IsCompleted, "dispatcher disposal tracks the outstanding document operation");
                    RuntimeThrows<HostRuntime.MutationLockException>(() => second.ReadDocument(target, () => 1));
                    Task.Run(() => RuntimeThrows<HostRuntime.MutationLockException>(() => second.ReadDocument(target, () => 1)))
                        .GetAwaiter().GetResult();
                    AssertTrue(retained.Execute(() => { using (runtime.BeginDocumentAccess(target)) return dispatcher.CheckAccess; }),
                        "only the explicit retained owner can enter nested reads on the bound dispatcher");
                    RuntimeThrows<HostRuntime.MutationLockException>(() => runtime.ReadDocument(target, () => 1));
                    AssertTrue(retained.TryObserveDocumentClosed() == null, "unavailable closure evidence is not treated as native stop");
                    adapter.Closed = false;
                    AssertEqual((bool?)false, retained.TryObserveDocumentClosed(), "an exact open workbook is observed on its dispatcher");
                    adapter.Closed = true;
                    AssertEqual((bool?)true, retained.TryObserveDocumentClosed(), "closure is a separate positive owner observation");
                    adapter.Closed = false;
                    dispatcher.Dispose();
                    AssertTrue(!completion.IsCompleted, "a panel's dispose request does not end native ownership");
                    RuntimeThrows<ObjectDisposedException>(() => dispatcher.RetainForDocumentOperation());
                    AssertTrue(retained.Execute(() => dispatcher.CheckAccess), "the retained native owner can still finish after panel disposal");
                    retained.Dispose();
                    AssertTrue(completion.Wait(5000), "final native lease release completes the dispatcher lifetime");
                    RuntimeThrows<ObjectDisposedException>(() => dispatcher.Invoke(() => true));
                }
            }

            using (var dispatcher = new OfficeStaDispatcher())
            using (var entered = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                var adapter = new PowerQueryLifetimeAdapter(dispatcher);
                var runtime = new HostRuntime(adapter, null);
                var target = new OfficeDocumentExecutionExpectation { Host = "Excel", DocumentKey = adapter.DocumentKey,
                    RuntimeDocumentKey = adapter.RuntimeDocumentKey };
                using (var retained = runtime.ReserveDocumentOperation(target, CancellationToken.None))
                {
                    var completion = dispatcher.DocumentOperationCompletion;
                    var pending = Task.Run(() => retained.Execute(() => {
                        entered.Set();
                        if (!release.Wait(5000)) throw new InvalidOperationException("Retained fixture was not released.");
                        return true;
                    }));
                    try
                    {
                        AssertTrue(entered.Wait(5000), "the retained action starts on its owner");
                        retained.Dispose();
                        AssertTrue(retained.IsExecuting && !completion.IsCompleted,
                            "disposing an executing lease defers gate and dispatcher release until unwind");
                        RuntimeThrows<HostRuntime.MutationLockException>(() => runtime.ReadDocument(target, () => 1));
                    }
                    finally { release.Set(); }
                    AssertTrue(pending.Wait(5000) && pending.GetAwaiter().GetResult(), "in-flight action completes after deferred disposal");
                    AssertTrue(completion.Wait(5000), "deferred release ends after the native stack unwinds");
                    AssertTrue(runtime.ReadDocument(target, () => true), "the shared document gate is available after unwind");
                }
            }

            using (var dispatcher = new OfficeStaDispatcher())
            {
                var backend = new StubPowerQueryBackend(); backend.Seed("Sales", "42");
                var adapter = new PowerQueryLifetimeAdapter(dispatcher) { Closed = false, ExcelPowerQueryBackend = backend };
                var reader = ((IExcelBackendProvider)FakeOfficeAdapter.ForHost("Excel")).ExcelReadBackend;
                var gateway = new ResourceGatewayService(new IResourceProvider[] { new ExcelResourceProvider(adapter, reader, null) });
                var session = new ChatSession { Id = "pq-closure-" + Guid.NewGuid().ToString("N"), Host = "Excel",
                    DocumentKey = adapter.DocumentKey, DocumentAuthorityId = DocumentAuthorityId.Create().Id };
                var runtime = new HostRuntime(adapter, null);
                var owner = ExcelPowerQueryExecutionService.For(adapter);
                var call = new ToolCall("closure-unknown", "excel.query.refresh", "{\"target\":\"Excel query: Sales\"}");
                var context = new ToolHandlerContext(new ToolExecutionContext(call,
                    new ToolPolicySnapshot(call.Name, "pq-test", Policy(ToolEffect.Write, ToolVerification.Tool)),
                    "run", "turn", call.Id, DateTime.UtcNow, false, 10), new Dictionary<string, object>());
                backend.RefreshOverride = (request, dispatch) => {
                    dispatch();
                    return new ExcelPowerQueryRefreshResult { State = ExcelPowerQueryRefreshState.Unresolved,
                        NativeRunning = null, ObservedAtUtc = DateTime.UtcNow };
                };
                var unknown = owner.RefreshAsync(runtime, session, gateway, context, new ExcelPowerQueryRefreshRequest {
                    QueryName = "Sales", ExpectedQueryIdentity = "fixture-query:Sales", ExpectedSourceVersion = TextPatternEngine.Sha256("42"),
                    ExpectedBindingVersion = "binding", Sheet = "Data", Table = "Result"
                }, 1000, CancellationToken.None).GetAwaiter().GetResult();
                var completion = dispatcher.DocumentOperationCompletion;
                AssertTrue(!completion.IsCompleted, "an unresolved native stop retains dispatcher ownership");
                var pending = new ToolExecutionProgress(ToolExecutionOutcome.Unknown, completedUtc: DateTime.UtcNow,
                    operation: unknown.ResourceEvidence.Single().OperationObservation.Operation);
                adapter.Closed = true;
                AssertTrue(owner.ObserveClosedSession(), "the exact workbook closure is positively observed");
                AssertTrue(completion.Wait(5000), "confirmed closure ends the native reservation");
                var status = owner.ReadStatus("Sales");
                AssertTrue(status.ExecutionStopped && status.NativeState == "session_closed",
                    "closure ends ownership without claiming native result verification");
                var evidence = owner.ReadStatusTool(gateway, session, "Sales", 32000).ResourceEvidence.Single();
                AssertTrue(evidence.OperationObservation.State == ToolOperationState.Unresolved &&
                    !evidence.OperationObservation.ExecutionStopped && !evidence.OperationObservation.SafeToInvokeAgain,
                    "session closure never becomes stopped-unverified replay authority");
                AssertTrue(!evidence.OperationObservation.Resolves(pending, evidence),
                    "a fresh accepted closed-session observation cannot clear the original Unknown guard");
            }
        }

        private sealed class PowerQueryLifetimeAdapter : IOfficeApplicationAdapter, IOfficeDocumentSessionProvider,
            IOfficeDocumentSession, IOfficeDocumentClosureProbe, IExcelPowerQueryBackendProvider
        {
            private readonly object _document = new object();
            internal bool? Closed;
            public IExcelPowerQueryBackend ExcelPowerQueryBackend { get; set; }
            internal PowerQueryLifetimeAdapter(IOfficeStaDispatcher dispatcher)
            { StaDispatcher = dispatcher; RuntimeDocumentKey = "pq-retained-" + Guid.NewGuid().ToString("N"); }
            public string HostName { get { return "Excel"; } }
            public string DocumentKey { get { return "Excel:Path:retained-test.xlsx"; } }
            public string RuntimeDocumentKey { get; private set; }
            public string DocumentTitle { get { return "Retained workbook"; } }
            public string GetDocumentSnapshot(int maximumCharacters) { throw new NotSupportedException(); }
            public void PrepareForContextCapture() { throw new NotSupportedException(); }
            public ContextNote CaptureSelectionContext(string mode, int maximumCharacters) { throw new NotSupportedException(); }
            public IOfficeDocumentSession DocumentSession { get { return this; } }
            public string Host { get { return HostName; } }
            public string StableDocumentId { get { AssertTrue(StaDispatcher.CheckAccess, "stable identity stays on owner dispatcher"); return DocumentKey; } }
            public string RuntimeDocumentId { get { return RuntimeDocumentKey; } }
            public object BoundDocumentObject { get { AssertTrue(StaDispatcher.CheckAccess, "bound object stays on owner dispatcher"); return _document; } }
            public bool IsAlive { get { AssertTrue(StaDispatcher.CheckAccess, "liveness stays on owner dispatcher"); return Closed != true; } }
            public IOfficeStaDispatcher StaDispatcher { get; private set; }
            public object MutationGate { get; } = new object();
            public bool? ProbeDocumentClosed() { AssertTrue(StaDispatcher.CheckAccess, "closure stays on owner dispatcher"); return Closed; }
        }
    }
}
