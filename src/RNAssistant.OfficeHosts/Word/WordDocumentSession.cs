using System;
using RNAssistant.Office;
using RNAssistant.OfficeHosts.Identity;
using Word = Microsoft.Office.Interop.Word;

namespace RNAssistant.OfficeHosts
{
    internal sealed class WordDocumentSession : IOfficeDocumentSession
    {
        private readonly Word.Document _document;

        internal WordDocumentSession(
            Word.Document document,
            string runtimeDocumentId,
            IOfficeStaDispatcher dispatcher)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrWhiteSpace(runtimeDocumentId))
                throw new ArgumentException(
                    "A runtime Word document id is required.",
                    nameof(runtimeDocumentId));
            StaDispatcher = dispatcher ??
                throw new ArgumentNullException(nameof(dispatcher));
            RuntimeDocumentId = runtimeDocumentId;
            MutationGate = new object();
            if (!StaDispatcher.CheckAccess)
                throw new InvalidOperationException(
                    "Word document sessions must be created on their owner STA.");
            if (string.IsNullOrWhiteSpace(StableDocumentId))
                throw new InvalidOperationException(
                    "A stable Word document id is required.");
        }

        public string Host { get { return "Word"; } }
        public string StableDocumentId
        {
            get
            {
                RequireOwnerAccess();
                return StableKey(_document, RuntimeDocumentId);
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
                return _document;
            }
        }

        public bool IsAlive
        {
            get
            {
                RequireOwnerAccess();
                try
                {
                    var name = _document.Name;
                    return !string.IsNullOrWhiteSpace(name);
                }
                catch
                {
                    return false;
                }
            }
        }

        internal static string StableKey(
            Word.Document document, string runtimeDocumentId)
        {
            if (document == null) return "Word:NoDocument";
            var path = string.IsNullOrWhiteSpace(
                SafeString(delegate { return document.Path; }))
                ? string.Empty
                : SafeString(delegate { return document.FullName; });
            return DocumentIdentity.ForOfficeDocument(
                "Word",
                path,
                runtimeDocumentId,
                delegate { return document.CustomDocumentProperties; });
        }

        private void RequireOwnerAccess()
        {
            if (!StaDispatcher.CheckAccess)
                throw new InvalidOperationException(
                    "Word document session access requires its owner STA.");
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }
    }
}
