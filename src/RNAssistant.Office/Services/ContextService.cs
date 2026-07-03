using RNAssistant.Core.Models;
using RNAssistant.Core.Services;

namespace RNAssistant.Office.Services
{
    public sealed class ContextService
    {
        private readonly IOfficeApplicationAdapter _adapter;

        public ContextService(IOfficeApplicationAdapter adapter)
        {
            _adapter = adapter;
        }

        public DocumentContext LoadContext(ChatSession session)
        {
            return CreateNormalizer().LoadContext(session);
        }

        public DocumentContext CreateEmptyContext()
        {
            return CreateNormalizer().CreateEmptyContext();
        }

        public void NormalizeContext(DocumentContext context, ChatSession session)
        {
            CreateNormalizer().NormalizeContext(context, session);
        }

        public void NormalizeContextNote(ContextNote note, string mode)
        {
            CreateNormalizer().NormalizeContextNote(note, mode);
        }

        private ContextNormalizer CreateNormalizer()
        {
            return new ContextNormalizer(_adapter.HostName, _adapter.DocumentKey, _adapter.DocumentTitle);
        }
    }
}
