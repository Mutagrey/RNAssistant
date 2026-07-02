using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office
{
    public sealed class DispatchedOfficeApplicationAdapter : IOfficeApplicationAdapter, IOfficeContextProvider, IOfficeDocumentCatalog, IDisposable
    {
        private readonly Func<IOfficeApplicationAdapter> _adapterFactory;
        private readonly OfficeStaDispatcher _dispatcher;
        private IOfficeApplicationAdapter _inner;
        private bool _disposed;

        public DispatchedOfficeApplicationAdapter(Func<IOfficeApplicationAdapter> adapterFactory)
        {
            if (adapterFactory == null)
            {
                throw new ArgumentNullException("adapterFactory");
            }

            _adapterFactory = adapterFactory;
            _dispatcher = new OfficeStaDispatcher();
        }

        public string HostName
        {
            get { return _dispatcher.Invoke(delegate { return Inner.HostName; }); }
        }

        public string DocumentKey
        {
            get { return _dispatcher.Invoke(delegate { return Inner.DocumentKey; }); }
        }

        public string RuntimeDocumentKey
        {
            get { return _dispatcher.Invoke(delegate { return Inner.RuntimeDocumentKey; }); }
        }

        public string DocumentTitle
        {
            get { return _dispatcher.Invoke(delegate { return Inner.DocumentTitle; }); }
        }

        private IOfficeApplicationAdapter Inner
        {
            get
            {
                if (_inner == null)
                {
                    _inner = _adapterFactory();
                }

                return _inner;
            }
        }

        public string GetDocumentSnapshot(int maxChars)
        {
            return _dispatcher.Invoke(delegate { return Inner.GetDocumentSnapshot(maxChars); });
        }

        public string GetVbaSnapshot(int maxChars)
        {
            return _dispatcher.Invoke(delegate { return Inner.GetVbaSnapshot(maxChars); });
        }

        public void PrepareForContextCapture()
        {
            _dispatcher.Invoke(delegate { Inner.PrepareForContextCapture(); });
        }

        public ContextNote CaptureSelectionContext(string mode, int maxChars)
        {
            return _dispatcher.Invoke(delegate { return Inner.CaptureSelectionContext(mode, maxChars); });
        }

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return _dispatcher.Invoke(delegate { return Inner.GetBuiltInTools().ToArray(); });
        }

        public ToolResult ExecuteTool(ToolCommand command)
        {
            return _dispatcher.Invoke(delegate { return Inner.ExecuteTool(command); });
        }

        public OfficeContext GetOfficeContext()
        {
            return _dispatcher.Invoke(delegate
            {
                var provider = Inner as IOfficeContextProvider;
                return provider == null ? null : provider.GetOfficeContext();
            });
        }

        public IReadOnlyList<OpenOfficeDocumentDto> ListOpenDocuments()
        {
            return _dispatcher.Invoke(delegate
            {
                var catalog = Inner as IOfficeDocumentCatalog;
                return catalog == null
                    ? (IReadOnlyList<OpenOfficeDocumentDto>)new OpenOfficeDocumentDto[0]
                    : catalog.ListOpenDocuments().ToArray();
            });
        }

        public bool ActivateDocument(string documentKey)
        {
            return _dispatcher.Invoke(delegate
            {
                var catalog = Inner as IOfficeDocumentCatalog;
                return catalog != null && catalog.ActivateDocument(documentKey);
            });
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _dispatcher.Invoke(delegate
                {
                    var disposable = _inner as IDisposable;
                    if (disposable != null)
                    {
                        disposable.Dispose();
                    }
                });
            }
            finally
            {
                _dispatcher.Dispose();
            }
        }
    }
}
