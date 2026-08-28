using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office
{
    public sealed class UiThreadOfficeApplicationAdapter : IOfficeApplicationAdapter, IOfficeContextProvider, IOfficeBuiltInSkillProvider, IOfficeDocumentCatalog, IOfficeDocumentExecutionGuard, IOfficeDispatcherProvider, IOfficeDocumentSessionProvider
    {
        private readonly IOfficeApplicationAdapter _inner;
        private readonly OfficeUiDispatcher _dispatcher;
        private readonly IOfficeDocumentSession _documentSession;
        private readonly OfficeDocumentExecutionGuardState _documentGuard = new OfficeDocumentExecutionGuardState();

        public UiThreadOfficeApplicationAdapter(IOfficeApplicationAdapter inner, OfficeUiDispatcher dispatcher)
        {
            _inner = inner ?? throw new ArgumentNullException("inner");
            _dispatcher = dispatcher ?? throw new ArgumentNullException("dispatcher");
            _documentSession = _dispatcher.Invoke(delegate
            {
                var provider = _inner as IOfficeDocumentSessionProvider;
                return provider == null ? null : provider.DocumentSession;
            });
        }

        public string HostName { get { return ReadExpected(delegate { return _inner.HostName; }); } }
        public string DocumentKey { get { return ReadExpected(delegate { return _inner.DocumentKey; }); } }
        public string RuntimeDocumentKey { get { return ReadExpected(delegate { return _inner.RuntimeDocumentKey; }); } }
        public string DocumentTitle { get { return ReadExpected(delegate { return _inner.DocumentTitle; }); } }

        public IOfficeStaDispatcher StaDispatcher { get { return _dispatcher; } }

        public IOfficeDocumentSession DocumentSession
        {
            get
            {
                return _documentSession;
            }
        }

        public string GetDocumentSnapshot(int maxChars)
        {
            return ReadExpected(delegate { return _inner.GetDocumentSnapshot(maxChars); });
        }

        public void PrepareForContextCapture()
        {
            ReadExpected(delegate
            {
                _inner.PrepareForContextCapture();
                return true;
            });
        }

        public ContextNote CaptureSelectionContext(string mode, int maxChars)
        {
            return ReadExpected(delegate { return _inner.CaptureSelectionContext(mode, maxChars); });
        }

        public IEnumerable<ToolDefinition> GetBuiltInTools()
        {
            return _dispatcher.Invoke(delegate { return _inner.GetBuiltInTools().ToArray(); });
        }

        public ToolResult ExecuteTool(ToolCommand command)
        {
            var expectation = _documentGuard.Current;
            return _dispatcher.Invoke(delegate
            {
                var mismatch = OfficeDocumentExecutionGuardState.Validate(_inner, expectation);
                return mismatch ?? _inner.ExecuteTool(command);
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
                var provider = _inner as IOfficeContextProvider;
                return provider == null ? null : provider.GetOfficeContext();
            });
        }

        private T ReadExpected<T>(Func<T> action)
        {
            var expectation = _documentGuard.Current;
            return _dispatcher.Invoke(delegate
            {
                OfficeDocumentExecutionGuardState.ThrowIfMismatch(_inner, expectation);
                return action();
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
