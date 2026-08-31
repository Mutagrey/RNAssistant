using System;
using System.Collections.Generic;
using System.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Office.Contracts;
using RNAssistant.Office.Domains.Excel;
using RNAssistant.Office.Qualification;

namespace RNAssistant.Office
{
    public sealed class UiThreadOfficeApplicationAdapter : IOfficeApplicationAdapter, IOfficeContextProvider, IOfficeBuiltInSkillProvider, IOfficeDocumentCatalog, IOfficeDocumentExecutionGuard, IOfficeDispatcherProvider, IOfficeDocumentSessionProvider, IExcelBackendProvider, IQualificationHostPort
    {
        private readonly IOfficeApplicationAdapter _inner;
        private readonly OfficeUiDispatcher _dispatcher;
        private readonly IOfficeDocumentSession _documentSession;
        private readonly IExcelReadBackend _excelReadBackend;
        private readonly IExcelWriteBackend _excelWriteBackend;
        private readonly IExcelFindReplaceBackend _excelFindReplaceBackend;
        private readonly IExcelSheetBackend _excelSheetBackend;
        private readonly IExcelRangeMutationBackend _excelRangeMutationBackend;
        private readonly IExcelTableBackend _excelTableBackend;
        private readonly IExcelChartBackend _excelChartBackend;
        private readonly OfficeDocumentExecutionGuardState _documentGuard = new OfficeDocumentExecutionGuardState();

        public UiThreadOfficeApplicationAdapter(IOfficeApplicationAdapter inner, OfficeUiDispatcher dispatcher)
        {
            _inner = inner ?? throw new ArgumentNullException("inner");
            _dispatcher = dispatcher ?? throw new ArgumentNullException("dispatcher");
            IOfficeDocumentSession documentSession = null;
            IExcelReadBackend excelReadBackend = null;
            IExcelWriteBackend excelWriteBackend = null;
            IExcelFindReplaceBackend excelFindReplaceBackend = null;
            IExcelSheetBackend excelSheetBackend = null;
            IExcelRangeMutationBackend excelRangeMutationBackend = null;
            IExcelTableBackend excelTableBackend = null;
            IExcelChartBackend excelChartBackend = null;
            _dispatcher.Invoke(delegate
            {
                var provider = _inner as IOfficeDocumentSessionProvider;
                documentSession = provider == null ? null : provider.DocumentSession;
                var excel = _inner as IExcelBackendProvider;
                excelReadBackend = excel == null ? null : excel.ExcelReadBackend;
                excelWriteBackend = excel == null ? null : excel.ExcelWriteBackend;
                excelFindReplaceBackend = excel == null ? null : excel.ExcelFindReplaceBackend;
                excelSheetBackend = excel == null ? null : excel.ExcelSheetBackend;
                excelRangeMutationBackend = excel == null
                    ? null : excel.ExcelRangeMutationBackend;
                excelTableBackend = excel == null ? null : excel.ExcelTableBackend;
                excelChartBackend = excel == null ? null : excel.ExcelChartBackend;
                return true;
            });
            _documentSession = documentSession;
            _excelReadBackend = excelReadBackend;
            _excelWriteBackend = excelWriteBackend;
            _excelFindReplaceBackend = excelFindReplaceBackend;
            _excelSheetBackend = excelSheetBackend;
            _excelRangeMutationBackend = excelRangeMutationBackend;
            _excelTableBackend = excelTableBackend;
            _excelChartBackend = excelChartBackend;
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

        public IExcelReadBackend ExcelReadBackend { get { return _excelReadBackend; } }
        public IExcelWriteBackend ExcelWriteBackend { get { return _excelWriteBackend; } }
        public IExcelFindReplaceBackend ExcelFindReplaceBackend
        {
            get { return _excelFindReplaceBackend; }
        }
        public IExcelSheetBackend ExcelSheetBackend { get { return _excelSheetBackend; } }
        public IExcelRangeMutationBackend ExcelRangeMutationBackend
        {
            get { return _excelRangeMutationBackend; }
        }
        public IExcelTableBackend ExcelTableBackend { get { return _excelTableBackend; } }
        public IExcelChartBackend ExcelChartBackend { get { return _excelChartBackend; } }

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

        public IReadOnlyList<string> QualificationCapabilities
        {
            get
            {
                return _dispatcher.Invoke(delegate
                {
                    var provider = _inner as IQualificationHostPort;
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
                var provider = _inner as IQualificationHostPort;
                return provider != null && provider.SupportsQualificationAction(step);
            });
        }

        public QualificationActionResult ExecuteQualificationAction(
            QualificationStepExecutionContext context,
            System.Threading.CancellationToken cancellationToken)
        {
            return _dispatcher.Invoke(delegate
            {
                var provider = _inner as IQualificationHostPort;
                if (provider == null) throw new InvalidOperationException("Host qualification is unavailable.");
                return provider.ExecuteQualificationAction(context, cancellationToken);
            });
        }

        public bool SupportsQualificationAssertion(QualificationStep step)
        {
            return _dispatcher.Invoke(delegate
            {
                var provider = _inner as IQualificationHostPort;
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
                var provider = _inner as IQualificationHostPort;
                if (provider == null) throw new InvalidOperationException("Host qualification is unavailable.");
                return provider.VerifyQualificationAssertion(context, evidence, cancellationToken);
            });
        }

        public void ReleaseQualificationResources()
        {
            _dispatcher.Invoke(delegate
            {
                var provider = _inner as IQualificationHostPort;
                if (provider != null) provider.ReleaseQualificationResources();
                return true;
            });
        }
    }

}
