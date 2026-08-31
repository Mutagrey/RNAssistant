using System;
using System.Collections.Generic;
using Microsoft.Office.Core;
using RNAssistant.Office;
using RNAssistant.OfficeHosts;
using Word = Microsoft.Office.Interop.Word;

namespace RNAssistant.WordAddIn
{
    public sealed partial class ThisAddIn
    {
        private OfficeUiDispatcher _officeDispatcher;
        private readonly Dictionary<int, PaneEntry> _panes =
            new Dictionary<int, PaneEntry>();
        private readonly HashSet<int> _creatingPanes = new HashSet<int>();
        private int _activeWindowHwnd;
        private bool _assistantVisible;
        private readonly List<CommandBarButton> _contextButtons = new List<CommandBarButton>();

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            _officeDispatcher = new OfficeUiDispatcher();
            Application.WindowActivate += Application_WindowActivate;
            Application.WindowSelectionChange += Application_WindowSelectionChange;
            Application.DocumentBeforeClose += Application_DocumentBeforeClose;
            InstallContextMenus();
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
            RemoveContextMenus();
            Application.WindowActivate -= Application_WindowActivate;
            Application.WindowSelectionChange -= Application_WindowSelectionChange;
            Application.DocumentBeforeClose -= Application_DocumentBeforeClose;
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
            var entry = EnsurePane(SafeActiveWindow(), SafeActiveDocument());
            if (entry == null) return;

            if (!string.IsNullOrWhiteSpace(quickAction))
            {
                entry.Runtime.RunQuickAction(quickAction);
            }

            entry.Pane.Visible = true;
            entry.Runtime.ReleaseKeyboardFocusToHost();
        }

        protected override IRibbonExtensibility CreateRibbonExtensibilityObject()
        {
            return new AssistantRibbon(this);
        }

        private void Application_WindowActivate(
            Word.Document document, Word.Window window)
        {
            var hwnd = WindowHwnd(window);
            var windowChanged = hwnd != 0 && hwnd != _activeWindowHwnd;
            var paneAlreadyExists = hwnd != 0 && _panes.ContainsKey(hwnd);
            if (hwnd != 0) _activeWindowHwnd = hwnd;
            var entry = _assistantVisible ? EnsurePane(window, document) : null;
            if (entry != null)
            {
                entry.Pane.Visible = true;
                if (windowChanged && paneAlreadyExists)
                    entry.Runtime.RefreshState();
            }
        }

        private void Application_WindowSelectionChange(Word.Selection selection)
        {
            var entry = ActivePane();
            if (entry != null) entry.Runtime.BlurComposer();
        }

        private void Application_DocumentBeforeClose(
            Word.Document document, ref bool cancel)
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
            Word.Window window, Word.Document document)
        {
            if (window == null || document == null) return null;
            var key = WindowHwnd(window);
            if (key == 0) return null;
            PaneEntry existing;
            if (_panes.TryGetValue(key, out existing)) return existing;
            if (!_creatingPanes.Add(key)) return null;
            try
            {
                var runtime = new AssistantRuntime(
                    new UiThreadOfficeApplicationAdapter(
                        new WordAdapter(
                            Application, document, _officeDispatcher),
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
            var entry = ActivePane() ??
                EnsurePane(SafeActiveWindow(), SafeActiveDocument());
            return entry == null ? null : entry.Runtime;
        }

        private void RemoveClosedWindowPanes()
        {
            var closed = new List<int>();
            foreach (var pair in _panes)
            {
                try
                {
                    var hwnd = WindowHwnd(pair.Value.Window);
                    if (hwnd == 0) closed.Add(pair.Key);
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

        private Word.Document SafeActiveDocument()
        {
            try { return Application.ActiveDocument; }
            catch { return null; }
        }

        private Word.Window SafeActiveWindow()
        {
            try { return Application.ActiveWindow; }
            catch { return null; }
        }

        private static int WindowHwnd(Word.Window window)
        {
            try { return window == null ? 0 : window.Hwnd; }
            catch { return 0; }
        }

        private sealed class PaneEntry
        {
            public Word.Window Window { get; set; }
            public Microsoft.Office.Tools.CustomTaskPane Pane { get; set; }
            public AssistantRuntime Runtime { get; set; }
        }

        private void InstallContextMenus()
        {
            AddContextMenu("Text");
            AddContextMenu("Table Text");
            AddContextMenu("Lists");
        }

        private void AddContextMenu(string name)
        {
            try
            {
                AddContextButtons(Application.CommandBars[name]);
            }
            catch
            {
            }
        }

        private void AddContextButtons(CommandBar commandBar)
        {
            if (commandBar == null)
            {
                return;
            }

            DeleteTaggedControls(commandBar);
            AddContextButton(commandBar, "Add to RN context", "full", "add", true, 487);
            AddContextButton(commandBar, "Add RN reference only", "reference", "reference", false, 1088);
            AddContextButton(commandBar, "Ask RN Assistant about this", "full", "ask", false, 162);
        }

        private void AddContextButton(CommandBar commandBar, string caption, string mode, string action, bool beginGroup, int faceId)
        {
            var button = (CommandBarButton)commandBar.Controls.Add(MsoControlType.msoControlButton, Type.Missing, Type.Missing, Type.Missing, true);
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
                        "Open a Word document first.");
                runtime.AddSelectionContext(mode);
                ShowAssistant(action == "ask" ? null : "context");
                if (action == "ask")
                {
                    runtime.RunQuickAction("ask-context");
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.Message, "RN Assistant");
            }
        }

        private void RemoveContextMenus()
        {
            RemoveContextMenu("Text");
            RemoveContextMenu("Table Text");
            RemoveContextMenu("Lists");
            _contextButtons.Clear();
        }

        private void RemoveContextMenu(string name)
        {
            try
            {
                DeleteTaggedControls(Application.CommandBars[name]);
            }
            catch
            {
            }
        }

        private static void DeleteTaggedControls(CommandBar commandBar)
        {
            if (commandBar == null)
            {
                return;
            }

            for (var i = commandBar.Controls.Count; i >= 1; i--)
            {
                var control = commandBar.Controls[i];
                if ((control.Tag ?? string.Empty).StartsWith("RNAssistant.", StringComparison.OrdinalIgnoreCase))
                {
                    control.Delete(true);
                }
            }
        }

        private void InternalStartup()
        {
            Startup += ThisAddIn_Startup;
            Shutdown += ThisAddIn_Shutdown;
        }
    }
}
