using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Persistence;
using RNAssistant.Core.Tools;
using RNAssistant.Core.Tools.Contracts;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Services
{
    // One owner per bound workbook, shared by every chat and adapter wrapper.
    // This is a retained native call, not a durable job scheduler or replay queue.
    internal sealed partial class ExcelPowerQueryExecutionService
    {
        private const int MaximumRetainedOperations = 64;
        private static readonly ConditionalWeakTable<object, ExcelPowerQueryExecutionService> Owners =
            new ConditionalWeakTable<object, ExcelPowerQueryExecutionService>();
        private readonly object _sync = new object();
        private readonly string _runtimeId;
        private readonly List<Operation> _operations = new List<Operation>();

        private ExcelPowerQueryExecutionService(string runtimeId) { _runtimeId = runtimeId; }

        internal static ExcelPowerQueryExecutionService For(IOfficeApplicationAdapter adapter)
        {
            var session = (adapter as IOfficeDocumentSessionProvider)?.DocumentSession;
            if (session == null || session.MutationGate == null || session.Host != "Excel" ||
                string.IsNullOrWhiteSpace(session.RuntimeDocumentId))
                throw new ResourceRequestException("Power Query requires one bound Excel document session.",
                    "excel_query_session_required", false);
            var owner = Owners.GetValue(session.MutationGate,
                _ => new ExcelPowerQueryExecutionService(session.RuntimeDocumentId));
            if (owner._runtimeId != session.RuntimeDocumentId)
                throw new ResourceRequestException("Power Query session ownership changed.", "excel_query_session_changed", false);
            return owner;
        }

        internal Task<ToolHandlerResult> RefreshAsync(HostRuntime host, ChatSession session,
            ResourceGatewayService gateway, ToolHandlerContext context, ExcelPowerQueryRefreshRequest request,
            int timeoutMs, CancellationToken token)
        {
            return StartAsync(host, session, gateway, context, request.QueryName, request.ExpectedSourceVersion,
                request.Sheet, request.Table, timeoutMs, token, dispatch => {
                    var refreshed = gateway.PowerQueryBackend.RefreshWorksheet(request, dispatch);
                    return new NativeResult { Refresh = refreshed };
                });
        }

        internal Task<ToolHandlerResult> LoadAsync(HostRuntime host, ChatSession session,
            ResourceGatewayService gateway, ToolHandlerContext context, ExcelPowerQueryLoadRequest request,
            int timeoutMs, CancellationToken token)
        {
            return StartAsync(host, session, gateway, context, request.QueryName, request.ExpectedSourceVersion,
                request.Sheet, request.Table, timeoutMs, token, dispatch => {
                    var loaded = gateway.PowerQueryBackend.CreateWorksheetLoad(request, dispatch);
                    return new NativeResult { Load = loaded, Refresh = loaded?.Refresh };
                });
        }

        private async Task<ToolHandlerResult> StartAsync(HostRuntime host, ChatSession session,
            ResourceGatewayService gateway, ToolHandlerContext context, string query, string sourceVersion,
            string sheet, string table, int timeoutMs, CancellationToken token, Func<Action, NativeResult> native)
        {
            if (host == null || gateway == null || session == null || context == null || gateway.PowerQueryBackend == null)
                throw new ArgumentException("A bound Power Query runtime and backend are required.");
            if (timeoutMs < 1 || timeoutMs > 300000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            RequireSession(session);
            token.ThrowIfCancellationRequested();
            HostRuntime.RetainedDocumentOperation lease;
            try
            {
                lease = host.ReserveDocumentOperation(new OfficeDocumentExecutionExpectation {
                    Host = session.Host, DocumentKey = session.DocumentKey, RuntimeDocumentKey = _runtimeId }, token);
            }
            catch (HostRuntime.MutationLockException ex)
            { return Failure(ex.Message, "excel_query_busy", true); }
            catch (OfficeDocumentGuardException ex)
            { return Failure(ex.Message, ex.ErrorCode, ex.Retryable); }
            Operation operation = null;
            var observationLimit = false;
            Task<ToolHandlerResult> execution = null;
            try
            {
                lock (_sync)
                {
                    // Known completed non-timeout results need no separate retained
                    // correlation. Unresolved calls are never silently evicted.
                    while (_operations.Count >= MaximumRetainedOperations)
                    {
                        var removable = _operations.FirstOrDefault(item => item.Finished &&
                            (!item.UnknownReturned || HasDurableResolution(gateway, session, item)));
                        if (removable == null)
                        {
                            observationLimit = true;
                            break;
                        }
                        _operations.Remove(removable);
                    }
                    if (!observationLimit) operation = new Operation {
                        Query = query, Sheet = sheet, Table = table, SourceVersion = sourceVersion,
                        DocumentAuthorityId = session.DocumentAuthorityId, NativeObservedAtUtc = DateTime.UtcNow,
                        State = ToolOperationState.Pending, WaitState = "active", Lease = lease, OwnerSession = session,
                        Reference = ToolOperationReference.Capture(context.Execution, _runtimeId, Guid.NewGuid().ToString("N"),
                            ExcelResourceProvider.QueryStatusIdentity(session.DocumentAuthorityId, query), "status")
                    };
                    if (operation != null) _operations.Add(operation);
                }
                // Scheduling transfers the reservation to the native worker.
                // All admission failures before that transfer release it here.
                if (!observationLimit)
                    execution = Task.Run(() => ExecuteNative(operation, gateway, session, context, native));
            }
            catch
            {
                lock (_sync) if (operation != null) _operations.Remove(operation);
                lease.Dispose();
                throw;
            }
            if (observationLimit)
            {
                lease.Dispose();
                return Failure("The session's retained Power Query observation limit was reached. Read status in the originating chats so completed operations can be durably reconciled before continuing.",
                    "excel_query_observation_limit", false);
            }

            // Execute marshals the entire guarded native action onto its owner
            // dispatcher; the worker owns the reservation after caller timeout.
            // A caller can leave before terminal persistence fails. Observe that
            // background fault without retrying the already-attempted callback.
            _ = execution.ContinueWith(faulted => { _ = faulted.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var timer = new CancellationTokenSource())
            using (token.Register(() => stopped.TrySetResult(true)))
            {
                var deadline = Task.Delay(timeoutMs, timer.Token);
                var completed = await Task.WhenAny(execution, deadline, stopped.Task).ConfigureAwait(false);
                timer.Cancel();
                if (completed == execution) return await execution.ConfigureAwait(false);
                lock (_sync)
                {
                    if (operation.PublicationError != null) ExceptionDispatchInfo.Capture(operation.PublicationError).Throw();
                    if (operation.Published != null) return operation.Published;
                    operation.WaitState = token.IsCancellationRequested ? "stopped" : "timed_out";
                    operation.AbandonedBeforeDispatch = !operation.Dispatched;
                    operation.UnknownReturned = operation.Dispatched;
                    if (!operation.Dispatched)
                    {
                        PublishOnce(operation, context, Failure(
                            "Waiting ended before a native Power Query dispatch. No refresh was started.",
                            "excel_query_not_started", true));
                    }
                    else
                    {
                        var result = ResultFor(operation, gateway, session, false,
                            "Waiting ended. Excel execution has not yet returned; this is not cancellation or successful refresh.");
                        PublishOnce(operation, context, result);
                    }
                    return operation.Published;
                }
            }
        }

        private ToolHandlerResult ExecuteNative(Operation operation, ResourceGatewayService gateway,
            ChatSession session, ToolHandlerContext context, Func<Action, NativeResult> native)
        {
            try
            {
                return operation.Lease.Execute(() => {
                    NativeResult result = null;
                    Exception error = null;
                    try
                    {
                        lock (_sync)
                            if (operation.AbandonedBeforeDispatch) throw new OperationCanceledException();
                        result = native(() => {
                            lock (_sync)
                            {
                                if (operation.AbandonedBeforeDispatch) throw new OperationCanceledException();
                                context.MarkDispatchPossible();
                                operation.Dispatched = true;
                                operation.State = ToolOperationState.Running;
                                operation.NativeObservedAtUtc = DateTime.UtcNow;
                            }
                        });
                    }
                    catch (Exception ex) { error = ex; }
                    lock (_sync)
                    {
                        operation.Native = result;
                        operation.Error = error;
                        operation.Finished = true;
                        var refresh = result?.Refresh;
                        operation.NativeObservedAtUtc = refresh?.ObservedAtUtc ?? DateTime.UtcNow;
                        operation.ExecutionStopped = !operation.Dispatched || (refresh?.NativeRunning ?? result?.Load?.NativeRunning) == false;
                        operation.State = !operation.Dispatched ? ToolOperationState.Failed :
                            refresh == null ? operation.ExecutionStopped ? ToolOperationState.Failed : ToolOperationState.Unresolved :
                            refresh.State == ExcelPowerQueryRefreshState.Succeeded ? ToolOperationState.Succeeded :
                            refresh.State == ExcelPowerQueryRefreshState.Failed ? ToolOperationState.Failed :
                            refresh.State == ExcelPowerQueryRefreshState.Cancelled ? ToolOperationState.Cancelled : ToolOperationState.Unresolved;
                        if (!operation.PublicationAttempted)
                        {
                            operation.WaitState = "completed";
                            var verified = refresh != null && refresh.State == ExcelPowerQueryRefreshState.Succeeded &&
                                refresh.NativeRunning == false && refresh.SourceStillCurrent && refresh.BindingStillCurrent &&
                                (result.Load == null || result.Load.BindingVerified);
                            var handlerResult = !operation.Dispatched
                                ? Failure(error?.Message ?? "No native operation was dispatched.",
                                    (error as ExcelPowerQueryBackendException)?.ErrorCode ?? "excel_query_not_started",
                                    (error as ExcelPowerQueryBackendException)?.Retryable ?? false)
                                : ResultFor(operation, gateway, session, verified,
                                    result?.Load?.Message ?? refresh?.Message ?? "Native completion could not be verified. Inspect operation status and loaded data.");
                            operation.UnknownReturned = handlerResult.Result.Status == ToolResultStatus.Unknown;
                            PublishOnce(operation, context, handlerResult);
                        }
                        // A timed-out terminal is immutable. Late completion is
                        // observed by a later core.read status fact, never a second
                        // terminal callback for the original tool call.
                        if (operation.PublicationError != null) ExceptionDispatchInfo.Capture(operation.PublicationError).Throw();
                        return operation.Published;
                    }
                });
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    // Publication failure cannot erase a native completion that
                    // has already established whether the reservation may end.
                    if (!operation.Finished)
                    {
                        operation.Finished = true;
                        operation.Error = ex;
                        operation.ExecutionStopped = !operation.Dispatched;
                        operation.State = operation.Dispatched ? ToolOperationState.Unresolved : ToolOperationState.Failed;
                        operation.NativeObservedAtUtc = DateTime.UtcNow;
                    }
                    if (operation.PublicationAttempted) throw;
                    if (operation.Published == null)
                    {
                        operation.UnknownReturned = operation.Dispatched;
                        PublishOnce(operation, context, operation.Dispatched
                            ? ResultFor(operation, gateway, session, false, "Native result publication failed; inspect status before another operation.")
                            : Failure(ex.Message, "excel_query_not_started", false));
                    }
                    return operation.Published;
                }
            }
            finally
            {
                var observeClosure = false;
                var release = false;
                lock (_sync)
                {
                    if (operation.ExecutionStopped) release = true;
                    else observeClosure = operation.Finished && operation.Dispatched;
                }
                // Dispatcher teardown can invoke its owner STA. Never do that
                // while holding the status-observation lock.
                if (release) operation.Lease.Dispose();
                if (observeClosure) _ = ObserveClosureAsync(operation);
            }
        }

        private static void PublishOnce(Operation operation, ToolHandlerContext context, ToolHandlerResult result)
        {
            if (operation.PublicationAttempted)
                throw new InvalidOperationException("Power Query terminal publication was already attempted.");
            operation.PublicationAttempted = true;
            try { operation.Published = context.Complete(result); }
            catch (Exception ex)
            {
                operation.PublicationError = ex;
                operation.UnknownReturned |= operation.Dispatched;
                throw;
            }
        }

        private async Task ObserveClosureAsync(Operation operation)
        {
            // A single sparse probe is queued only after the original synchronous
            // native callback returned unresolved. No concurrent COM access and
            // no repeated refresh. Ordinary polls never write journal facts.
            while (true)
            {
                await Task.Delay(5000).ConfigureAwait(false);
                lock (_sync) if (operation.ExecutionStopped || operation.SessionClosed) return;
                if (ObserveClosedOperation(operation)) return;
            }
        }

        internal bool ObserveClosedSession()
        {
            Operation[] pending;
            lock (_sync) pending = _operations.Where(item => item.Finished && !item.ExecutionStopped && !item.SessionClosed).ToArray();
            var observed = false;
            foreach (var operation in pending) observed |= ObserveClosedOperation(operation);
            return observed;
        }

        private bool ObserveClosedOperation(Operation operation)
        {
            bool? closed;
            try { closed = operation.Lease.TryObserveDocumentClosed(); }
            catch (InvalidOperationException) { return false; }
            catch (System.Runtime.InteropServices.COMException) { return false; }
            if (closed != true) return false;
            lock (_sync)
            {
                operation.SessionClosed = true;
                operation.ExecutionStopped = true;
                operation.State = ToolOperationState.Unresolved;
                operation.NativeObservedAtUtc = DateTime.UtcNow;
            }
            operation.Lease.Dispose();
            // Closure ends ownership, not uncertainty about effects before close.
            return true;
        }

        internal void RequireSession(ChatSession session)
        {
            if (session == null || session.Host != "Excel" || string.IsNullOrWhiteSpace(session.DocumentAuthorityId) ||
                !string.IsNullOrWhiteSpace(session.LastRun?.DocumentRuntimeKey) && session.LastRun.DocumentRuntimeKey != _runtimeId)
                throw new ResourceRequestException("The selected chat does not belong to this Excel runtime session.", "excel_query_session_changed", false);
        }

        private static bool HasDurableResolution(ResourceGatewayService gateway, ChatSession current, Operation operation)
        {
            var reader = gateway.PowerQueryOperationResolutions;
            if (reader == null || !operation.ExecutionStopped) return false;
            var session = current.Id == operation.OwnerSession.Id ? current : operation.OwnerSession;
            try
            {
                var evidence = reader.ReadOperationResolution(ConversationMetadata.From(session), operation.Reference);
                return evidence?.OperationObservation?.Operation.Matches(operation.Reference) == true;
            }
            catch (InvalidOperationException) { return false; }
            catch (System.IO.IOException) { return false; }
            // Stale metadata or unavailable history never permits silent eviction.
        }

        private static ToolHandlerResult Failure(string message, string code, bool retryable)
        {
            return new ToolHandlerResult(ToolResult.Error(message,
                Newtonsoft.Json.JsonConvert.SerializeObject(new PowerQueryFailure { Code = code })), ToolEffectEvidence.None,
                recovery: new ToolRecoveryContract(retryable ? ToolFailureKind.BusyNoEffect : ToolFailureKind.RejectedNoEffect,
                    retryable ? ToolRetryPolicy.RetryLater : ToolRetryPolicy.Replan));
        }

        private sealed class NativeResult
        { internal ExcelPowerQueryRefreshResult Refresh; internal ExcelPowerQueryLoadResult Load; }
        private sealed class Operation
        {
            internal string Query, Sheet, Table, SourceVersion, DocumentAuthorityId, WaitState;
            internal ToolOperationReference Reference;
            internal ToolOperationState State;
            internal DateTime NativeObservedAtUtc;
            internal bool Dispatched, Finished, ExecutionStopped, AbandonedBeforeDispatch, UnknownReturned, SessionClosed, PublicationAttempted;
            internal Exception Error;
            internal Exception PublicationError;
            internal NativeResult Native;
            internal ToolHandlerResult Published;
            internal HostRuntime.RetainedDocumentOperation Lease;
            internal ChatSession OwnerSession;
        }
        private sealed class PowerQueryFailure
        { [Newtonsoft.Json.JsonProperty("code")] public string Code { get; set; } }
    }
}
