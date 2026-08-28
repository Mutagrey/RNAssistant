using RNAssistant.Core.Models;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Services
{
    internal sealed class OfficeContextCaptureService
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly HostRuntime _hostRuntime;

        internal OfficeContextCaptureService(IOfficeApplicationAdapter adapter, HostRuntime hostRuntime)
        {
            _adapter = adapter;
            _hostRuntime = hostRuntime;
        }

        internal ContextNote CaptureSelection(OfficeDocumentExecutionExpectation target, string mode, int maxChars)
        {
            return _hostRuntime.ReadDocument(target, () =>
            {
                try { _adapter.PrepareForContextCapture(); }
                catch (OfficeDocumentGuardException) { throw; }
                catch (HostRuntime.MutationLockException) { throw; }
                catch { }
                return _adapter.CaptureSelectionContext(mode, maxChars);
            });
        }

        internal OfficeContext CaptureOfficeContext()
        {
            var provider = _adapter as IOfficeContextProvider;
            if (provider == null) return null;
            try { return _hostRuntime.ReadDocument(null, provider.GetOfficeContext); }
            catch { return null; }
        }
    }
}
