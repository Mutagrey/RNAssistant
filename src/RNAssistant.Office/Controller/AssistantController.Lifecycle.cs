using System;
using System.Threading;

namespace RNAssistant.Office
{
    public sealed partial class AssistantController
    {
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
