using System;
using RNAssistant.Office;
using RNAssistant.OfficeHosts.Identity;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.OfficeHosts
{
    internal sealed class ExcelDocumentSession : IOfficeDocumentSession
    {
        private readonly Excel.Workbook _workbook;

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
