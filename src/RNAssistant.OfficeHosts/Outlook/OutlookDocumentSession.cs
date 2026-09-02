using System;
using RNAssistant.Office;
using RNAssistant.OfficeHosts.Identity;
using RNAssistant.Office.Domains.Outlook;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace RNAssistant.OfficeHosts
{
    internal sealed class OutlookDocumentSession : IOfficeDocumentSession
    {
        private readonly Outlook.Application _application;
        private readonly Outlook.MailItem _mail;
        private readonly Outlook.MAPIFolder _folder;
        private readonly Outlook.Inspector _inspector;
        private readonly Outlook.Explorer _explorer;
        private readonly string _stableDocumentId;

        internal OutlookDocumentSession(
            Outlook.Application application,
            Outlook.MailItem mail,
            Outlook.MAPIFolder folder,
            Outlook.Inspector inspector,
            Outlook.Explorer explorer,
            string runtimeDocumentId,
            IOfficeStaDispatcher dispatcher)
        {
            _application = application ??
                throw new ArgumentNullException(nameof(application));
            if ((mail == null) == (folder == null))
                throw new ArgumentException(
                    "An Outlook session requires exactly one mail or folder target.");
            if (mail != null && inspector == null)
                throw new ArgumentException(
                    "A mail-bound Outlook session requires its exact inspector.");
            if (folder != null && explorer == null)
                throw new ArgumentException(
                    "A folder-bound Outlook session requires its exact explorer.");
            if (string.IsNullOrWhiteSpace(runtimeDocumentId))
                throw new ArgumentException(
                    "A runtime Outlook target id is required.",
                    nameof(runtimeDocumentId));
            StaDispatcher = dispatcher ??
                throw new ArgumentNullException(nameof(dispatcher));
            if (!StaDispatcher.CheckAccess)
                throw new InvalidOperationException(
                    "Outlook document sessions must be created on their owner STA.");
            _mail = mail;
            _folder = folder;
            _inspector = inspector;
            _explorer = explorer;
            RuntimeDocumentId = runtimeDocumentId;
            _stableDocumentId = mail != null
                ? MailStableId(mail, runtimeDocumentId)
                : FolderStableId(folder, runtimeDocumentId);
            if (string.IsNullOrWhiteSpace(_stableDocumentId))
                throw new InvalidOperationException(
                    "A stable Outlook target id is required.");
            MutationGate = new object();
        }

        public string Host { get { return "Outlook"; } }
        public string StableDocumentId
        {
            get
            {
                RequireOwnerAccess();
                return _stableDocumentId;
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
                return (object)_mail ?? _folder;
            }
        }

        public bool IsAlive
        {
            get
            {
                RequireOwnerAccess();
                try
                {
                    if (_mail != null)
                    {
                        if (ReadWindowHwnd(_inspector) == 0) return false;
                        var current = _inspector.CurrentItem as Outlook.MailItem;
                        return SameMail(_mail, current);
                    }
                    if (ReadWindowHwnd(_explorer) == 0) return false;
                    var currentFolder = _explorer.CurrentFolder as Outlook.MAPIFolder;
                    return SameFolder(_folder, currentFolder);
                }
                catch { return false; }
            }
        }

        internal bool IsMailTarget { get { return _mail != null; } }
        internal Outlook.Application Application
        {
            get { RequireOwnerAccess(); return _application; }
        }
        internal Outlook.Inspector Inspector
        {
            get { RequireOwnerAccess(); return _inspector; }
        }
        internal Outlook.Explorer Explorer
        {
            get { RequireOwnerAccess(); return _explorer; }
        }
        internal Outlook.MAPIFolder Folder
        {
            get
            {
                RequireAlive();
                if (_folder == null)
                    throw new OutlookBackendException(
                        "This Outlook runtime is bound to a mail inspector, not a folder.",
                        "outlook_folder_target_missing", true);
                return _folder;
            }
        }

        internal Outlook.MailItem SelectedMail()
        {
            RequireAlive();
            if (_mail != null) return _mail;
            try
            {
                var selection = _explorer.Selection;
                return selection == null || selection.Count == 0
                    ? null : selection[1] as Outlook.MailItem;
            }
            catch { return null; }
        }

        internal Outlook.MailItem ResolveMail(string entryId)
        {
            RequireAlive();
            if (string.IsNullOrWhiteSpace(entryId)) return SelectedMail();
            try
            {
                return _application.Session.GetItemFromID(
                    entryId, Type.Missing) as Outlook.MailItem;
            }
            catch { return null; }
        }

        internal string Title
        {
            get
            {
                RequireAlive();
                return _mail != null
                    ? SafeString(delegate { return _mail.Subject; })
                    : SafeString(delegate { return _folder.Name; });
            }
        }

        internal string FolderPath
        {
            get
            {
                RequireAlive();
                if (_folder != null)
                    return SafeString(delegate { return _folder.FolderPath; });
                try
                {
                    var parent = _mail.Parent as Outlook.MAPIFolder;
                    return parent == null ? string.Empty :
                        SafeString(delegate { return parent.FolderPath; });
                }
                catch { return string.Empty; }
            }
        }

        internal long WindowHwnd
        {
            get
            {
                RequireOwnerAccess();
                return _inspector != null
                    ? ReadWindowHwnd(_inspector) : ReadWindowHwnd(_explorer);
            }
        }

        internal void Activate()
        {
            RequireAlive();
            if (_inspector != null) _inspector.Activate();
            else _explorer.Activate();
        }

        internal static string MailIdentity(Outlook.MailItem mail)
        {
            if (mail == null) return string.Empty;
            var entryId = SafeString(delegate { return mail.EntryID; });
            return string.IsNullOrWhiteSpace(entryId)
                ? DocumentIdentity.RuntimeKey("Outlook", mail)
                : entryId;
        }

        private void RequireAlive()
        {
            RequireOwnerAccess();
            if (!IsAlive)
                throw new OutlookBackendException(
                    "The bound Outlook target is closed or changed.",
                    "outlook_target_closed", true);
        }

        private void RequireOwnerAccess()
        {
            if (!StaDispatcher.CheckAccess)
                throw new InvalidOperationException(
                    "Outlook document session access requires its owner STA.");
        }

        private static bool SameMail(
            Outlook.MailItem expected, Outlook.MailItem actual)
        {
            if (expected == null || actual == null) return false;
            return string.Equals(
                MailIdentity(expected), MailIdentity(actual),
                StringComparison.Ordinal);
        }

        private static bool SameFolder(
            Outlook.MAPIFolder expected, Outlook.MAPIFolder actual)
        {
            if (expected == null || actual == null) return false;
            return string.Equals(
                FolderKey(expected), FolderKey(actual),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string MailStableId(
            Outlook.MailItem mail, string runtimeDocumentId)
        {
            var entryId = SafeString(delegate { return mail.EntryID; });
            return string.IsNullOrWhiteSpace(entryId)
                ? runtimeDocumentId : entryId;
        }

        private static string FolderStableId(
            Outlook.MAPIFolder folder, string runtimeDocumentId)
        {
            var path = SafeString(delegate { return folder.FolderPath; });
            return string.IsNullOrWhiteSpace(path)
                ? runtimeDocumentId : path;
        }

        private static string FolderKey(Outlook.MAPIFolder folder)
        {
            if (folder == null) return string.Empty;
            var store = SafeString(delegate { return folder.StoreID; });
            var path = SafeString(delegate { return folder.FolderPath; });
            return store + "\n" + path;
        }

        private static long ReadWindowHwnd(object window)
        {
            return NativeWindowInfo.ReadLongMemberPath(window, "HWND");
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter(); }
            catch { return string.Empty; }
        }
    }
}
