using System;
using System.Collections.Generic;
using Microsoft.Office.Core;
using RNAssistant.Office;
using RNAssistant.OfficeHosts;
using PowerPoint = Microsoft.Office.Interop.PowerPoint;

namespace RNAssistant.PowerPointAddIn
{
    public sealed partial class ThisAddIn
    {
        private OfficeUiDispatcher _officeDispatcher;
        private readonly Dictionary<int, PaneEntry> _panes =
            new Dictionary<int, PaneEntry>();
        private readonly HashSet<int> _creatingPanes = new HashSet<int>();
        private int _activeWindowHwnd;
        private bool _assistantVisible;
        private readonly List<CommandBarButton> _contextButtons =
            new List<CommandBarButton>();

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            _officeDispatcher = new OfficeUiDispatcher();
            Application.WindowActivate += Application_WindowActivate;
            Application.WindowSelectionChange += Application_WindowSelectionChange;
            Application.PresentationBeforeClose += Application_PresentationBeforeClose;
            InstallContextMenus();
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
            RemoveContextMenus();
            Application.WindowActivate -= Application_WindowActivate;
            Application.WindowSelectionChange -= Application_WindowSelectionChange;
            Application.PresentationBeforeClose -= Application_PresentationBeforeClose;
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
            _assistantVisible = true;
            var entry = EnsurePane(
                SafeActiveWindow(), SafeActivePresentation());
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

        private void Application_WindowActivate(
            PowerPoint.Presentation presentation,
            PowerPoint.DocumentWindow window)
        {
            var hwnd = WindowHwnd(window);
            var windowChanged = hwnd != 0 && hwnd != _activeWindowHwnd;
            var paneAlreadyExists = hwnd != 0 && _panes.ContainsKey(hwnd);
            if (hwnd != 0) _activeWindowHwnd = hwnd;
            var entry = _assistantVisible
                ? EnsurePane(window, presentation) : null;
            if (entry != null)
            {
                entry.Pane.Visible = true;
                if (windowChanged && paneAlreadyExists)
                    entry.Runtime.RefreshState();
            }
        }

        private void Application_WindowSelectionChange(
            PowerPoint.Selection selection)
        {
            var entry = ActivePane();
            if (entry != null) entry.Runtime.BlurComposer();
        }

        private void Application_PresentationBeforeClose(
            PowerPoint.Presentation presentation, ref bool cancel)
        {
            if (cancel) return;
            var timer = new System.Windows.Forms.Timer { Interval = 150 };
            timer.Tick += delegate
            {
                timer.Stop();
                timer.Dispose();
                RemoveClosedWindowPanes();
                var entry = ActivePane();
                if (entry != null) entry.Runtime.RefreshState();
            };
            timer.Start();
        }

        private PaneEntry EnsurePane(
            PowerPoint.DocumentWindow window,
            PowerPoint.Presentation presentation)
        {
            if (window == null || presentation == null) return null;
            var key = WindowHwnd(window);
            if (key == 0) return null;
            PaneEntry existing;
            if (_panes.TryGetValue(key, out existing)) return existing;
            if (!_creatingPanes.Add(key)) return null;
            try
            {
                var runtime = new AssistantRuntime(
                    new UiThreadOfficeApplicationAdapter(
                        new PowerPointAdapter(
                            Application, presentation, window,
                            _officeDispatcher),
                        _officeDispatcher));
                var pane = CustomTaskPanes.Add(
                    runtime.CreatePaneControl(), "RN Assistant", window);
                pane.Width = 520;
                var entry = new PaneEntry
                {
                    Window = window,
                    Pane = pane,
                    Runtime = runtime
                };
                _panes[key] = entry;
                return entry;
            }
            finally
            {
                _creatingPanes.Remove(key);
            }
        }

        private PaneEntry ActivePane()
        {
            var window = SafeActiveWindow();
            if (window == null) return null;
            PaneEntry entry;
            return _panes.TryGetValue(WindowHwnd(window), out entry)
                ? entry : null;
        }

        private AssistantRuntime ActiveRuntime()
        {
            var entry = ActivePane() ?? EnsurePane(
                SafeActiveWindow(), SafeActivePresentation());
            return entry == null ? null : entry.Runtime;
        }

        private void RemoveClosedWindowPanes()
        {
            var closed = new List<int>();
            foreach (var pair in _panes)
            {
                try
                {
                    if (WindowHwnd(pair.Value.Window) == 0)
                        closed.Add(pair.Key);
                }
                catch { closed.Add(pair.Key); }
            }
            foreach (var key in closed)
            {
                var entry = _panes[key];
                try { CustomTaskPanes.Remove(entry.Pane); } catch { }
                entry.Runtime.Dispose();
                _panes.Remove(key);
            }
        }

        private PowerPoint.Presentation SafeActivePresentation()
        {
            try { return Application.ActivePresentation; }
            catch { return null; }
        }

        private PowerPoint.DocumentWindow SafeActiveWindow()
        {
            try { return Application.ActiveWindow; }
            catch { return null; }
        }

        private static int WindowHwnd(PowerPoint.DocumentWindow window)
        {
            try { return window == null ? 0 : window.HWND; }
            catch { return 0; }
        }

        private sealed class PaneEntry
        {
            public PowerPoint.DocumentWindow Window { get; set; }
            public Microsoft.Office.Tools.CustomTaskPane Pane { get; set; }
            public AssistantRuntime Runtime { get; set; }
        }

        private void InstallContextMenus()
        {
            AddContextMenu("Shapes");
            AddContextMenu("Slide View");
            AddContextMenu("Slide Sorter");
            AddContextMenu("Thumbnails");
        }

        private void AddContextMenu(string name)
        {
            try { AddContextButtons(Application.CommandBars[name]); }
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
                        "Open a PowerPoint presentation first.");
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
            RemoveContextMenu("Shapes");
            RemoveContextMenu("Slide View");
            RemoveContextMenu("Slide Sorter");
            RemoveContextMenu("Thumbnails");
            _contextButtons.Clear();
        }

        private void RemoveContextMenu(string name)
        {
            try { DeleteTaggedControls(Application.CommandBars[name]); }
            catch { }
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

        private void InternalStartup()
        {
            Startup += ThisAddIn_Startup;
            Shutdown += ThisAddIn_Shutdown;
        }
    }
}
