using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office
{
    public sealed class DispatchedOfficeApplicationAdapter : IOfficeApplicationAdapter, IOfficeContextProvider, IOfficeBuiltInSkillProvider, IOfficeDocumentCatalog, IOfficeDocumentExecutionGuard, IDisposable
    {
        private readonly Func<IOfficeApplicationAdapter> _adapterFactory;
        private readonly OfficeStaDispatcher _dispatcher;
        private readonly OfficeDocumentExecutionGuardState _documentGuard = new OfficeDocumentExecutionGuardState();
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
            get { return ReadExpected(delegate { return Inner.HostName; }); }
        }

        public string DocumentKey
        {
            get { return ReadExpected(delegate { return Inner.DocumentKey; }); }
        }

        public string RuntimeDocumentKey
        {
            get { return ReadExpected(delegate { return Inner.RuntimeDocumentKey; }); }
        }

        public string DocumentTitle
        {
            get { return ReadExpected(delegate { return Inner.DocumentTitle; }); }
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
            return ReadExpected(delegate { return Inner.GetDocumentSnapshot(maxChars); });
        }

        public void PrepareForContextCapture()
        {
            ReadExpected(delegate
            {
                Inner.PrepareForContextCapture();
                return true;
            });
        }

        public ContextNote CaptureSelectionContext(string mode, int maxChars)
        {
            return ReadExpected(delegate { return Inner.CaptureSelectionContext(mode, maxChars); });
        }

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return _dispatcher.Invoke(delegate { return Inner.GetBuiltInTools().ToArray(); });
        }

        public IEnumerable<SkillDefinition> GetBuiltInSkills()
        {
            return _dispatcher.Invoke(delegate
            {
                var provider = Inner as IOfficeBuiltInSkillProvider;
                var skills = provider == null ? null : provider.GetBuiltInSkills();
                return (skills ?? new SkillDefinition[0]).ToArray();
            });
        }

        public ToolResult ExecuteTool(ToolCommand command)
        {
            var expectation = _documentGuard.Current;
            return _dispatcher.Invoke(delegate
            {
                var mismatch = OfficeDocumentExecutionGuardState.Validate(Inner, expectation);
                return mismatch ?? Inner.ExecuteTool(command);
            });
        }

        public IDisposable BeginExpectedDocument(string host, string documentKey, string runtimeDocumentKey)
        {
            return _documentGuard.Begin(host, documentKey, runtimeDocumentKey);
        }

        public OfficeContext GetOfficeContext()
        {
            return ReadExpected(delegate
            {
                var provider = Inner as IOfficeContextProvider;
                return provider == null ? null : provider.GetOfficeContext();
            });
        }

        private T ReadExpected<T>(Func<T> action)
        {
            var expectation = _documentGuard.Current;
            return _dispatcher.Invoke(delegate
            {
                OfficeDocumentExecutionGuardState.ThrowIfMismatch(Inner, expectation);
                return action();
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

        public bool OpenDocument(string path)
        {
            return _dispatcher.Invoke(delegate
            {
                var catalog = Inner as IOfficeDocumentCatalog;
                return catalog != null && catalog.OpenDocument(path);
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
