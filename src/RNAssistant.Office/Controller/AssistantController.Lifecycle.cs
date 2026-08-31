using System;
using System.Threading;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public InitResponse ClearRuntimeData()
        {
            using (_chatRuns.ReserveMaintenance())
            {
                EnsureNoActiveRuns();
                _paths.ClearRuntimeData();
                _chatSessions.Reset();
                lock (_syncRoot)
                {
                    _pendingAgentTools.Clear();
                }
            }
            return Initialize();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try { _lifetimeCancellation.Cancel(); } catch (ObjectDisposedException) { }
            // Keep per-chat locks until each cancelled run actually leaves its lease. A COM/tool
            // call may not observe cancellation immediately, so releasing here would allow overlap.
            _chatRuns.CancelAll();
            try { _qualification.Dispose(); } catch { }
            lock (_syncRoot)
            {
                _pendingAgentTools.Clear();
            }
            _lifetimeCancellation.Dispose();
        }
    }
}
