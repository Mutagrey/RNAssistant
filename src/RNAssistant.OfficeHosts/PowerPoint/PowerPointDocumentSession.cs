using System;
using RNAssistant.Office;
using RNAssistant.OfficeHosts.Identity;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;

namespace RNAssistant.OfficeHosts
{
    internal sealed class PowerPointDocumentSession : IOfficeDocumentSession
    {
        private readonly PowerPoint.Presentation _presentation;
        private readonly PowerPoint.DocumentWindow _window;

        internal PowerPointDocumentSession(
            PowerPoint.Presentation presentation,
            PowerPoint.DocumentWindow window,
            string runtimeDocumentId,
            IOfficeStaDispatcher dispatcher)
        {
            _presentation = presentation ??
                throw new ArgumentNullException(nameof(presentation));
            _window = window;
            if (string.IsNullOrWhiteSpace(runtimeDocumentId))
                throw new ArgumentException(
                    "A runtime PowerPoint presentation id is required.",
                    nameof(runtimeDocumentId));
            StaDispatcher = dispatcher ??
                throw new ArgumentNullException(nameof(dispatcher));
            RuntimeDocumentId = runtimeDocumentId;
            MutationGate = new object();
            if (!StaDispatcher.CheckAccess)
                throw new InvalidOperationException(
                    "PowerPoint document sessions must be created on their owner STA.");
            if (string.IsNullOrWhiteSpace(StableDocumentId))
                throw new InvalidOperationException(
                    "A stable PowerPoint presentation id is required.");
        }

        public string Host { get { return "PowerPoint"; } }
        public string StableDocumentId
        {
            get
            {
                RequireOwnerAccess();
                return StableKey(_presentation, RuntimeDocumentId);
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
                return _presentation;
            }
        }

        public bool IsAlive
        {
            get
            {
                RequireOwnerAccess();
                try
                {
                    var name = _presentation.Name;
                    if (string.IsNullOrWhiteSpace(name)) return false;
                    return _window == null ||
                        NativeWindowInfo.ReadLongMemberPath(_window, "HWND") != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        internal PowerPoint.Presentation Presentation
        {
            get
            {
                RequireOwnerAccess();
                return _presentation;
            }
        }

        internal PowerPoint.DocumentWindow Window
        {
            get
            {
                RequireOwnerAccess();
                return _window;
            }
        }

        internal static string StableKey(
            PowerPoint.Presentation presentation, string runtimeDocumentId)
        {
            if (presentation == null) return "PowerPoint:NoPresentation";
            var path = string.IsNullOrWhiteSpace(
                SafeString(delegate { return presentation.Path; }))
                ? string.Empty
                : SafeString(delegate { return presentation.FullName; });
            return DocumentIdentity.ForOfficeDocument(
                "PowerPoint",
                path,
                runtimeDocumentId,
                delegate { return presentation.CustomDocumentProperties; });
        }

        private void RequireOwnerAccess()
        {
            if (!StaDispatcher.CheckAccess)
                throw new InvalidOperationException(
                    "PowerPoint document session access requires its owner STA.");
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }
    }
}
