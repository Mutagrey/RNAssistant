using System;
using System.Diagnostics;
using RNAssistant.Office;
using RNAssistant.OfficeHosts.Identity;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    internal sealed class ExcelDocumentSession : IOfficeDocumentSession, IOfficeDocumentClosureProbe
    {
        private readonly Excel.Workbook _workbook;
        private readonly Excel.Application _application;
        private readonly int _processId;
        private readonly DateTime? _processStartedUtc;

        internal ExcelDocumentSession(
            Excel.Workbook workbook,
            string runtimeDocumentId,
            IOfficeStaDispatcher dispatcher)
        {
            _workbook = workbook ?? throw new ArgumentNullException(nameof(workbook));
            if (string.IsNullOrWhiteSpace(runtimeDocumentId))
                throw new ArgumentException("A runtime Excel document id is required.", nameof(runtimeDocumentId));
            StaDispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            RuntimeDocumentId = runtimeDocumentId;
            MutationGate = new object();
            if (!StaDispatcher.CheckAccess)
                throw new InvalidOperationException("Excel document sessions must be created on their owner STA.");
            _application = _workbook.Application;
            try
            {
                _processId = NativeWindowInfo.GetProcessId(
                    NativeWindowInfo.ReadLongMemberPath(_application, "Hwnd"));
                if (_processId > 0)
                    using (var process = Process.GetProcessById(_processId))
                        _processStartedUtc = process.StartTime.ToUniversalTime();
            }
            catch (Exception) { }
            if (string.IsNullOrWhiteSpace(StableDocumentId))
                throw new InvalidOperationException("A stable Excel document id is required.");
        }

        public string Host { get { return "Excel"; } }
        public string StableDocumentId
        {
            get
            {
                RequireOwnerAccess();
                return StableKey(_workbook, RuntimeDocumentId);
            }
        }
        public string RuntimeDocumentId { get; private set; }
        public IOfficeStaDispatcher StaDispatcher { get; private set; }
        public object MutationGate { get; private set; }
        public object BoundDocumentObject
        {
            get
            {
                RequireOwnerAccess();
                return _workbook;
            }
        }

        public bool IsAlive
        {
            get
            {
                RequireOwnerAccess();
                try
                {
                    var name = _workbook.Name;
                    return !string.IsNullOrWhiteSpace(name);
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool? ProbeDocumentClosed()
        {
            RequireOwnerAccess();
            // A terminated/replaced exact application process is positive closure
            // evidence. Access denial or an unavailable process property is not.
            if (_processId > 0 && _processStartedUtc.HasValue)
            {
                try
                {
                    using (var process = Process.GetProcessById(_processId))
                        if (process.HasExited || process.StartTime.ToUniversalTime() != _processStartedUtc.Value)
                            return true;
                }
                catch (ArgumentException) { return true; }
                catch (Exception) { }
            }
            try
            {
                var books = _application.Workbooks;
                foreach (Excel.Workbook workbook in books)
                    if (string.Equals(DocumentIdentity.RuntimeKey("Excel", workbook),
                            RuntimeDocumentId, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            catch (Exception)
            {
                // Excel may reject calls while busy or display a modal prompt.
                // BeforeClose and generic COM failures do not prove closure.
                return null;
            }
        }

        internal static string StableKey(Excel.Workbook workbook, string runtimeDocumentId)
        {
            if (workbook == null) return "Excel:NoWorkbook";
            var path = string.IsNullOrWhiteSpace(SafeString(delegate { return workbook.Path; }))
                ? string.Empty
                : SafeString(delegate { return workbook.FullName; });
            return DocumentIdentity.ForOfficeDocument(
                "Excel",
                path,
                runtimeDocumentId,
                delegate { return workbook.CustomDocumentProperties; });
        }

        private void RequireOwnerAccess()
        {
            if (!StaDispatcher.CheckAccess)
                throw new InvalidOperationException("Excel document session access requires its owner STA.");
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }
    }
}
