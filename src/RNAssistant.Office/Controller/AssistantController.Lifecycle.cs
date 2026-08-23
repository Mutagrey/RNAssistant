using System;
using System.Threading;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
        public InitResponse ClearRuntimeData()
        {
            if (_chatRuns.HasRuns())
            {
                throw new InvalidOperationException("Сначала остановите выполняющиеся запросы.");
            }
            _paths.ClearRuntimeData();
            _chatSessions.Reset();
            _chatRuns.Clear();
            lock (_syncRoot)
            {
                _pendingAgentTools.Clear();
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
            _chatRuns.Clear();
            lock (_syncRoot)
            {
                _pendingAgentTools.Clear();
            }
            _lifetimeCancellation.Dispose();
        }
    }
}
