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
        private readonly Outlook.Store _store;
        private readonly string _storeId;
        private readonly string _stableDocumentId;

        internal OutlookDocumentSession(
            Outlook.Application application,
            Outlook.MailItem mail,
            Outlook.MAPIFolder folder,
            Outlook.Inspector inspector,
            Outlook.Explorer explorer,
            string runtimeDocumentId,
            IOfficeStaDispatcher dispatcher,
            Outlook.Store store = null)
        {
            _application = application ??
                throw new ArgumentNullException(nameof(application));
            if ((mail == null ? 0 : 1) + (folder == null ? 0 : 1) + (store == null ? 0 : 1) != 1)
                throw new ArgumentException(
                    "An Outlook session requires exactly one mail, folder or mailbox target.");
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
            _store = store;
            _storeId = store == null ? null : store.StoreID;
            RuntimeDocumentId = runtimeDocumentId;
            _stableDocumentId = store != null
                ? OfficeTargetEnumerator.OutlookMailboxKey(
                    SafeString(delegate { return application.Session.CurrentProfileName; }), _storeId)
                : mail != null ? MailStableId(mail, runtimeDocumentId)
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
                return (object)_mail ?? _folder ?? _store;
            }
        }

        public bool IsAlive
        {
            get
            {
                RequireOwnerAccess();
                try
                {
                    if (_store != null)
                    {
                        foreach (Outlook.Store current in _application.Session.Stores)
                            if (string.Equals(current.StoreID, _storeId, StringComparison.Ordinal)) return true;
                        return false;
                    }
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
        internal bool IsMailboxTarget { get { return _store != null; } }
        internal string StoreId { get { RequireOwnerAccess(); return _storeId; } }
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
                return _folder ?? _store.GetRootFolder();
            }
        }

        internal Outlook.MailItem SelectedMail()
        {
            RequireAlive();
            if (_mail != null) return _mail;
            if (_store != null)
            {
                try
                {
                    var explorer = _application.ActiveExplorer();
                    var selection = explorer == null ? null : explorer.Selection;
                    var current = selection == null || selection.Count == 0 ? null : selection[1] as Outlook.MailItem;
                    return current != null && MailBelongsToStore(current, _storeId) ? current : null;
                }
                catch { return null; }
            }
            try
            {
                var selection = _explorer.Selection;
                var mail = selection == null || selection.Count == 0 ? null : selection[1] as Outlook.MailItem;
                return mail != null && MailBelongsToFolder(mail, _folder) ? mail : null;
            }
            catch { return null; }
        }

        internal Outlook.MailItem ResolveMail(string entryId)
        {
            RequireAlive();
            if (string.IsNullOrWhiteSpace(entryId)) return SelectedMail();
            if (_mail != null)
                return string.Equals(_mail.EntryID, entryId, StringComparison.Ordinal) ? _mail : null;
            if (_store != null)
            {
                try
                {
                    var found = _application.Session.GetItemFromID(entryId, _storeId) as Outlook.MailItem;
                    return found != null && MailBelongsToStore(found, _storeId) ? found : null;
                }
                catch { return null; }
            }
            try
            {
                var storeId = _folder.StoreID;
                if (string.IsNullOrEmpty(storeId)) return null;
                var mail = _application.Session.GetItemFromID(entryId, storeId) as Outlook.MailItem;
                return mail != null && MailBelongsToFolder(mail, _folder) ? mail : null;
            }
            catch { return null; }
        }

        private static bool MailBelongsToFolder(Outlook.MailItem mail, Outlook.MAPIFolder folder)
        {
            try
            {
                var parent = mail.Parent as Outlook.MAPIFolder;
                if (parent == null) return false;
                var store = folder.StoreID;
                var entry = folder.EntryID;
                return !string.IsNullOrEmpty(store) && !string.IsNullOrEmpty(entry) &&
                    string.Equals(store, parent.StoreID, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry, parent.EntryID, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool MailBelongsToStore(Outlook.MailItem mail, string storeId)
        {
            try
            {
                var parent = mail.Parent as Outlook.MAPIFolder;
                return parent != null && !string.IsNullOrWhiteSpace(storeId) &&
                    string.Equals(parent.StoreID, storeId, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        internal string Title
        {
            get
            {
                RequireAlive();
                return _store != null ? _store.DisplayName : _mail != null
                    ? SafeString(delegate { return _mail.Subject; })
                    : SafeString(delegate { return _folder.Name; });
            }
        }

        internal string FolderPath
        {
            get
            {
                RequireAlive();
                if (_store != null) return SafeString(delegate { return _store.GetRootFolder().FolderPath; });
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
                if (_store != null)
                {
                    var active = _application.ActiveExplorer();
                    return active == null ? 0 : ReadWindowHwnd(active);
                }
                return _inspector != null
                    ? ReadWindowHwnd(_inspector) : ReadWindowHwnd(_explorer);
            }
        }

        internal void Activate()
        {
            RequireAlive();
            if (_inspector != null) _inspector.Activate();
            else if (_explorer != null) _explorer.Activate();
            else
            {
                var active = _application.ActiveExplorer();
                if (active != null) active.Activate();
            }
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
