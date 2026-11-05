using System;
using System.Threading;
using System.Threading.Tasks;

namespace RNAssistant.Core.Agent
{
    public sealed partial class AgentKernel
    {
        private sealed class ExecutionPermit : IDisposable
        {
            internal IDisposable Lease;
            internal bool Acquired;
            internal string Error;
            public void Dispose() { Lease?.Dispose(); }
        }

        private async Task<ExecutionPermit> EnterExecutionAsync(State state, CancellationToken token)
        {
            if (_admission == null) return new ExecutionPermit { Acquired = true };
            if (token.IsCancellationRequested || (_input as IRunPauseChannel)?.PauseId != null)
                return new ExecutionPermit();
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task<IDisposable> pending;
                try { pending = _admission.EnterAsync(waiting.Token); }
                catch (OperationCanceledException) { return new ExecutionPermit(); }
                catch (Exception error) { return new ExecutionPermit { Error = error.Message }; }
                var handedOff = false;
                Task pause = null;
                try
                {
                    var queued = !pending.IsCompleted;
                    if (queued)
                    {
                        await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.ExecutionQueued,
                            state.Summary(message: "Ожидаю свободный слот выполнения…"))).ConfigureAwait(false);
                        var signal = _input as IRunPauseSignal;
                        if (signal != null)
                        {
                            pause = signal.WaitForPauseRequestAsync(waiting.Token);
                            if (await Task.WhenAny(pending, pause).ConfigureAwait(false) == pause)
                                waiting.Cancel();
                        }
                    }
                    IDisposable lease;
                    try { lease = await pending.ConfigureAwait(false); }
                    catch (OperationCanceledException) { return new ExecutionPermit(); }
                    catch (Exception error) { return new ExecutionPermit { Error = error.Message }; }
                    if (queued)
                        await AppendAsync(state, new AgentRunEvent(AgentRunEventKind.ExecutionAdmitted, state.Summary())).ConfigureAwait(false);
                    handedOff = true;
                    return new ExecutionPermit { Lease = lease, Acquired = true };
                }
                finally
                {
                    waiting.Cancel();
                    // A failed journal append or a cancellation/grant race must not
                    // leave a queued request or an acquired slot behind.
                    if (!handedOff)
                        try { (await pending.ConfigureAwait(false)).Dispose(); }
                        catch (Exception) { /* Original admission/store failure is preserved. */ }
                    if (pause != null)
                        try { await pause.ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                }
            }
        }
    }
}
