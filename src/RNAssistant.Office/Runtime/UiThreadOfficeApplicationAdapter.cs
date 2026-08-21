using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office
{
    public sealed class UiThreadOfficeApplicationAdapter : IOfficeApplicationAdapter, IOfficeContextProvider, IOfficeBuiltInSkillProvider, IOfficeDocumentCatalog
    {
        private readonly IOfficeApplicationAdapter _inner;
        private readonly OfficeUiDispatcher _dispatcher;

        public UiThreadOfficeApplicationAdapter(IOfficeApplicationAdapter inner, OfficeUiDispatcher dispatcher)
        {
            _inner = inner ?? throw new ArgumentNullException("inner");
            _dispatcher = dispatcher ?? throw new ArgumentNullException("dispatcher");
        }

        public string HostName { get { return _dispatcher.Invoke(delegate { return _inner.HostName; }); } }
        public string DocumentKey { get { return _dispatcher.Invoke(delegate { return _inner.DocumentKey; }); } }
        public string RuntimeDocumentKey { get { return _dispatcher.Invoke(delegate { return _inner.RuntimeDocumentKey; }); } }
        public string DocumentTitle { get { return _dispatcher.Invoke(delegate { return _inner.DocumentTitle; }); } }

        public string GetDocumentSnapshot(int maxChars)
        {
            return _dispatcher.Invoke(delegate { return _inner.GetDocumentSnapshot(maxChars); });
        }

        public void PrepareForContextCapture()
        {
            _dispatcher.Invoke(delegate { _inner.PrepareForContextCapture(); });
        }

        public ContextNote CaptureSelectionContext(string mode, int maxChars)
        {
            return _dispatcher.Invoke(delegate { return _inner.CaptureSelectionContext(mode, maxChars); });
        }

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return _dispatcher.Invoke(delegate { return _inner.GetBuiltInTools().ToArray(); });
        }

        public ToolResult ExecuteTool(ToolCommand command)
        {
            return _dispatcher.Invoke(delegate { return _inner.ExecuteTool(command); });
        }

        public OfficeContext GetOfficeContext()
        {
            return _dispatcher.Invoke(delegate
            {
                var provider = _inner as IOfficeContextProvider;
                return provider == null ? null : provider.GetOfficeContext();
            });
        }

        public IEnumerable<SkillDefinition> GetBuiltInSkills()
        {
            return _dispatcher.Invoke(delegate
            {
                var provider = _inner as IOfficeBuiltInSkillProvider;
                var skills = provider == null ? null : provider.GetBuiltInSkills();
                return (skills ?? new SkillDefinition[0]).ToArray();
            });
        }

        public IReadOnlyList<OpenOfficeDocumentDto> ListOpenDocuments()
        {
            return _dispatcher.Invoke(delegate
            {
                var catalog = _inner as IOfficeDocumentCatalog;
                return catalog == null
                    ? (IReadOnlyList<OpenOfficeDocumentDto>)new OpenOfficeDocumentDto[0]
                    : catalog.ListOpenDocuments().ToArray();
            });
        }

        public bool ActivateDocument(string documentKey)
        {
            return _dispatcher.Invoke(delegate
            {
                var catalog = _inner as IOfficeDocumentCatalog;
                return catalog != null && catalog.ActivateDocument(documentKey);
            });
        }

        public bool OpenDocument(string path)
        {
            return _dispatcher.Invoke(delegate
            {
                var catalog = _inner as IOfficeDocumentCatalog;
                return catalog != null && catalog.OpenDocument(path);
            });
        }
    }
}
