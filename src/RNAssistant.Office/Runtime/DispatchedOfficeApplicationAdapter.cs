using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Qualification;

namespace RNAssistant.Office
{
    public sealed class DispatchedOfficeApplicationAdapter : IOfficeApplicationAdapter, IOfficeContextProvider, IOfficeBuiltInSkillProvider, IOfficeDocumentCatalog, IOfficeDocumentExecutionGuard, IOfficeDispatcherProvider, IOfficeDocumentSessionProvider, IQualificationHostPort, IDisposable
    {
        private readonly Func<IOfficeApplicationAdapter> _adapterFactory;
        private readonly OfficeStaDispatcher _dispatcher;
        private readonly OfficeDocumentExecutionGuardState _documentGuard = new OfficeDocumentExecutionGuardState();
        private IOfficeApplicationAdapter _inner;
        private IOfficeDocumentSession _documentSession;
        private volatile bool _innerInitialized;
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

        public IOfficeStaDispatcher StaDispatcher { get { return _dispatcher; } }

        public IOfficeDocumentSession DocumentSession
        {
            get
            {
                if (!_innerInitialized)
                {
                    _dispatcher.Invoke(delegate
                    {
                        var initialized = Inner;
                        return initialized != null;
                    });
                }
                return _documentSession;
            }
        }

        private IOfficeApplicationAdapter Inner
        {
            get
            {
                if (!_innerInitialized)
                {
                    if (_inner == null) _inner = _adapterFactory();
                    var provider = _inner as IOfficeDocumentSessionProvider;
                    _documentSession = provider == null ? null : provider.DocumentSession;
                    // Publish the owner-initialized session once. Rebinding requires
                    // another adapter; metadata access must not queue behind a run.
                    _innerInitialized = true;
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

        public IReadOnlyList<string> QualificationCapabilities
        {
            get
            {
                return _dispatcher.Invoke(delegate
                {
                    var provider = Inner as IQualificationHostPort;
                    return provider == null
                        ? (IReadOnlyList<string>)new string[0]
                        : provider.QualificationCapabilities.ToArray();
                });
            }
        }

        public bool SupportsQualificationAction(QualificationStep step)
        {
            return _dispatcher.Invoke(delegate
            {
                var provider = Inner as IQualificationHostPort;
                return provider != null && provider.SupportsQualificationAction(step);
            });
        }

        public QualificationActionResult ExecuteQualificationAction(
            QualificationStepExecutionContext context,
            System.Threading.CancellationToken cancellationToken)
        {
            return _dispatcher.Invoke(delegate
            {
                var provider = Inner as IQualificationHostPort;
                if (provider == null) throw new InvalidOperationException("Host qualification is unavailable.");
                return provider.ExecuteQualificationAction(context, cancellationToken);
            });
        }

        public bool SupportsQualificationAssertion(QualificationStep step)
        {
            return _dispatcher.Invoke(delegate
            {
                var provider = Inner as IQualificationHostPort;
                return provider != null && provider.SupportsQualificationAssertion(step);
            });
        }

        public QualificationVerificationResult VerifyQualificationAssertion(
            QualificationStepExecutionContext context,
            QualificationEvidenceSnapshot evidence,
            System.Threading.CancellationToken cancellationToken)
        {
            return _dispatcher.Invoke(delegate
            {
                var provider = Inner as IQualificationHostPort;
                if (provider == null) throw new InvalidOperationException("Host qualification is unavailable.");
                return provider.VerifyQualificationAssertion(context, evidence, cancellationToken);
            });
        }

        public void ReleaseQualificationResources()
        {
            _dispatcher.Invoke(delegate
            {
                var provider = Inner as IQualificationHostPort;
                if (provider != null) provider.ReleaseQualificationResources();
                return true;
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
