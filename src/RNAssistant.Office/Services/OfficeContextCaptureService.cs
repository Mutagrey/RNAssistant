using RNAssistant.Core.Models;
using RNAssistant.Office.Runtime;

namespace RNAssistant.Office.Services
{
    internal sealed class OfficeContextCaptureService
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private readonly HostRuntime _hostRuntime;
        private readonly ResourceAuthorityService _authority;
        private readonly RNAssistant.Core.Storage.ChatBlobStore _payloads;

        internal OfficeContextCaptureService(IOfficeApplicationAdapter adapter, HostRuntime hostRuntime,
            ResourceAuthorityService authority = null, RNAssistant.Core.Storage.ChatBlobStore payloads = null)
        {
            _adapter = adapter;
            _hostRuntime = hostRuntime;
            _authority = authority;
            _payloads = payloads;
        }

        internal ContextNote CaptureSelection(OfficeDocumentExecutionExpectation target, string mode, int maxChars, ChatSession session = null)
        {
            return _hostRuntime.ReadDocument(target, () =>
            {
                try { _adapter.PrepareForContextCapture(); }
                catch (OfficeDocumentGuardException) { throw; }
                catch (HostRuntime.MutationLockException) { throw; }
                catch { }
                var note = _adapter.CaptureSelectionContext(mode, maxChars);
                if (note != null)
                {
                    note.Role = ContextNoteRole.OfficeObservation;
                    if (session != null && _authority != null) _authority.ObserveNote(session, note, _payloads);
                }
                return note;
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
