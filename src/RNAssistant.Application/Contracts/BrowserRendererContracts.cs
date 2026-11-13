using System;
using System.Threading;
using System.Threading.Tasks;

namespace RNAssistant.Application.Contracts
{
    // Native adapter; session identity/history/authority stay with LocalBrowserPageRuntime.
    public interface IBrowserRenderer : IBrowserActionSource, IBrowserMediaSource, IDisposable
    {
        event Action<BrowserPageStateDto> Changed;
        Task<BrowserPageStateDto> UpdateAsync(BrowserCommandPayload command, BrowserPageStateDto state, CancellationToken token);
        Task ResetAsync();
    }
}
