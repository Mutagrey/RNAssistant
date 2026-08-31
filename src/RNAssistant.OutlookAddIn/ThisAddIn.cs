using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Office.Core;
using RNAssistant.Office;
using RNAssistant.OfficeHosts;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace RNAssistant.OutlookAddIn
{
    public sealed partial class ThisAddIn
    {
        private OfficeUiDispatcher _officeDispatcher;
        private readonly Dictionary<long, PaneEntry> _panes =
            new Dictionary<long, PaneEntry>();
        private readonly HashSet<long> _creatingPanes = new HashSet<long>();
        private readonly List<CommandBarButton> _contextButtons =
            new List<CommandBarButton>();

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            _officeDispatcher = new OfficeUiDispatcher();
            Application.ItemContextMenuDisplay += Application_ItemContextMenuDisplay;
            InstallContextMenus();
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
            Application.ItemContextMenuDisplay -= Application_ItemContextMenuDisplay;
            RemoveContextMenus();
            foreach (var entry in new List<PaneEntry>(_panes.Values))
            {
                try { CustomTaskPanes.Remove(entry.Pane); } catch { }
                entry.Runtime.Dispose();
            }
            _panes.Clear();
            if (_officeDispatcher != null) _officeDispatcher.Dispose();
        }

        public void ShowAssistant(string quickAction = null)
        {
            var entry = EnsureActivePane();
            if (entry == null) return;
            if (!string.IsNullOrWhiteSpace(quickAction))
                entry.Runtime.RunQuickAction(quickAction);
            entry.Pane.Visible = true;
            entry.Runtime.ReleaseKeyboardFocusToHost();
        }

        protected override IRibbonExtensibility CreateRibbonExtensibilityObject()
        {
            return new AssistantRibbon(this);
        }

        private PaneEntry EnsureActivePane()
        {
            RemoveClosedPanes();
            var binding = ActiveBinding();
            if (binding == null || binding.Hwnd == 0) return null;
            PaneEntry existing;
            if (_panes.TryGetValue(binding.Hwnd, out existing))
            {
                if (string.Equals(
                    existing.TargetId, binding.TargetId,
                    StringComparison.OrdinalIgnoreCase)) return existing;
                RemovePane(binding.Hwnd);
            }
            if (!_creatingPanes.Add(binding.Hwnd)) return null;
            try
            {
                var runtime = new AssistantRuntime(
                    new UiThreadOfficeApplicationAdapter(
                        new OutlookAdapter(
                            Application, binding.Mail, binding.Folder,
                            binding.Inspector, binding.Explorer,
                            _officeDispatcher),
                        _officeDispatcher));
                var pane = CustomTaskPanes.Add(
                    runtime.CreatePaneControl(), "RN Assistant", binding.Window);
                pane.Width = 520;
                var entry = new PaneEntry
                {
                    Window = binding.Window,
                    TargetId = binding.TargetId,
                    Pane = pane,
                    Runtime = runtime
                };
                _panes[binding.Hwnd] = entry;
                return entry;
            }
            finally
            {
                _creatingPanes.Remove(binding.Hwnd);
            }
        }

        private AssistantRuntime ActiveRuntime()
        {
            var entry = EnsureActivePane();
            return entry == null ? null : entry.Runtime;
        }

        private WindowBinding ActiveBinding()
        {
            object window;
            try
            {
                window = Application.ActiveWindow();
            }
            catch { return null; }
            try
            {
                var inspector = window as Outlook.Inspector;
                var mail = inspector == null
                    ? null : inspector.CurrentItem as Outlook.MailItem;
                if (mail != null)
                {
                    var hwnd = WindowHwnd(inspector);
                    var entryId = SafeString(delegate { return mail.EntryID; });
                    return new WindowBinding
                    {
                        Window = inspector,
                        Inspector = inspector,
                        Mail = mail,
                        Hwnd = hwnd,
                        TargetId = string.IsNullOrWhiteSpace(entryId)
                            ? "mail-window:" + hwnd : entryId
                    };
                }
            }
            catch { }
            try
            {
                var explorer = window as Outlook.Explorer;
                var folder = explorer == null
                    ? null : explorer.CurrentFolder as Outlook.MAPIFolder;
                if (folder == null) return null;
                var hwnd = WindowHwnd(explorer);
                var path = SafeString(delegate { return folder.FolderPath; });
                var storeId = SafeString(delegate { return folder.StoreID; });
                return new WindowBinding
                {
                    Window = explorer,
                    Explorer = explorer,
                    Folder = folder,
                    Hwnd = hwnd,
                    TargetId = string.IsNullOrWhiteSpace(path)
                        ? "folder-window:" + hwnd : storeId + "\n" + path
                };
            }
            catch { return null; }
        }

        private void RemoveClosedPanes()
        {
            var closed = new List<long>();
            foreach (var pair in _panes)
                if (WindowHwnd(pair.Value.Window) == 0) closed.Add(pair.Key);
            foreach (var key in closed) RemovePane(key);
        }

        private void RemovePane(long key)
        {
            PaneEntry entry;
            if (!_panes.TryGetValue(key, out entry)) return;
            try { CustomTaskPanes.Remove(entry.Pane); } catch { }
            entry.Runtime.Dispose();
            _panes.Remove(key);
        }

        private void Application_ItemContextMenuDisplay(
            CommandBar commandBar, Outlook.Selection selection)
        {
            AddContextButtons(commandBar);
        }

        private void InstallContextMenus()
        {
            try
            {
                var explorer = Application.ActiveExplorer();
                if (explorer != null)
                {
                    AddContextMenu(explorer.CommandBars, "Context Menu");
                    AddContextMenu(explorer.CommandBars, "Mail Item");
                }
            }
            catch { }
        }

        private void AddContextMenu(CommandBars commandBars, string name)
        {
            try { AddContextButtons(commandBars[name]); }
            catch { }
        }

        private void AddContextButtons(CommandBar commandBar)
        {
            if (commandBar == null) return;
            DeleteTaggedControls(commandBar);
            AddContextButton(commandBar, "Add to RN context", "full", "add", true, 487);
            AddContextButton(commandBar, "Add RN reference only", "reference", "reference", false, 1088);
            AddContextButton(commandBar, "Ask RN Assistant about this", "full", "ask", false, 162);
        }

        private void AddContextButton(
            CommandBar commandBar, string caption, string mode,
            string action, bool beginGroup, int faceId)
        {
            var button = (CommandBarButton)commandBar.Controls.Add(
                MsoControlType.msoControlButton,
                Type.Missing, Type.Missing, Type.Missing, true);
            button.Caption = caption;
            button.Tag = "RNAssistant." + action;
            button.BeginGroup = beginGroup;
            button.FaceId = faceId;
            button.Click += (CommandBarButton control, ref bool cancelDefault) =>
            {
                cancelDefault = true;
                HandleContextAction(mode, action);
            };
            _contextButtons.Add(button);
        }

        private void HandleContextAction(string mode, string action)
        {
            try
            {
                var runtime = ActiveRuntime();
                if (runtime == null)
                    throw new InvalidOperationException(
                        "Open an Outlook mail or folder window first.");
                runtime.AddSelectionContext(mode);
                ShowAssistant(action == "ask" ? null : "context");
                if (action == "ask") runtime.RunQuickAction("ask-context");
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.Message, "RN Assistant");
            }
        }

        private void RemoveContextMenus()
        {
            foreach (var button in _contextButtons)
            {
                try { button.Delete(true); }
                catch { }
            }
            _contextButtons.Clear();
        }

        private static void DeleteTaggedControls(CommandBar commandBar)
        {
            if (commandBar == null) return;
            for (var index = commandBar.Controls.Count; index >= 1; index--)
            {
                var control = commandBar.Controls[index];
                if ((control.Tag ?? string.Empty).StartsWith(
                    "RNAssistant.", StringComparison.OrdinalIgnoreCase))
                    control.Delete(true);
            }
        }

        private static long WindowHwnd(object window)
        {
            if (window == null) return 0;
            try
            {
                var property = window.GetType().GetProperty(
                    "HWND", BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.IgnoreCase);
                if (property == null) return 0;
                return Convert.ToInt64(property.GetValue(window, null));
            }
            catch { return 0; }
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private sealed class WindowBinding
        {
            public object Window { get; set; }
            public Outlook.MailItem Mail { get; set; }
            public Outlook.MAPIFolder Folder { get; set; }
            public Outlook.Inspector Inspector { get; set; }
            public Outlook.Explorer Explorer { get; set; }
            public long Hwnd { get; set; }
            public string TargetId { get; set; }
        }

        private sealed class PaneEntry
        {
            public object Window { get; set; }
            public string TargetId { get; set; }
            public Microsoft.Office.Tools.CustomTaskPane Pane { get; set; }
            public AssistantRuntime Runtime { get; set; }
        }

        private void InternalStartup()
        {
            Startup += ThisAddIn_Startup;
            Shutdown += ThisAddIn_Shutdown;
        }
    }
}
