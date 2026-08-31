using System;
using System.Collections.Generic;
using Microsoft.Office.Core;
using RNAssistant.Office;
using RNAssistant.OfficeHosts;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.ExcelAddIn
{
    public sealed partial class ThisAddIn
    {
        private OfficeUiDispatcher _officeDispatcher;
        private readonly Dictionary<int, PaneEntry> _panes = new Dictionary<int, PaneEntry>();
        private readonly HashSet<int> _creatingPanes = new HashSet<int>();
        private int _activeWindowHwnd;
        private bool _assistantVisible;
        private readonly List<CommandBarButton> _contextButtons = new List<CommandBarButton>();

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            _officeDispatcher = new OfficeUiDispatcher();
            Application.SheetSelectionChange += Application_SheetSelectionChange;
            Application.WorkbookBeforeClose += Application_WorkbookBeforeClose;
            Application.WindowActivate += Application_WindowActivate;
            InstallContextMenus();
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
            Application.SheetSelectionChange -= Application_SheetSelectionChange;
            Application.WorkbookBeforeClose -= Application_WorkbookBeforeClose;
            Application.WindowActivate -= Application_WindowActivate;
            foreach (var entry in new List<PaneEntry>(_panes.Values))
            {
                try { CustomTaskPanes.Remove(entry.Pane); } catch { }
                entry.Runtime.Dispose();
            }
            _panes.Clear();
            RemoveContextMenus();
            if (_officeDispatcher != null) _officeDispatcher.Dispose();
        }

        public void ShowAssistant(string quickAction = null)
        {
            _assistantVisible = true;
            var entry = EnsurePane(SafeActiveWindow(), SafeActiveWorkbook());
            if (entry == null)
            {
                return;
            }

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

        private void Application_SheetSelectionChange(object sheet, Excel.Range target)
        {
            var entry = ActivePane();
            if (entry != null)
            {
                entry.Runtime.BlurComposer();
            }
        }

        private void Application_WindowActivate(Excel.Workbook workbook, Excel.Window window)
        {
            var hwnd = window == null ? 0 : window.Hwnd;
            var windowChanged = hwnd != 0 && hwnd != _activeWindowHwnd;
            var paneAlreadyExists = hwnd != 0 && _panes.ContainsKey(hwnd);
            if (hwnd != 0) _activeWindowHwnd = hwnd;
            var entry = _assistantVisible ? EnsurePane(window, workbook) : null;
            if (entry != null)
            {
                entry.Pane.Visible = true;
                if (windowChanged && paneAlreadyExists)
                {
                    entry.Runtime.RefreshState();
                }
            }
        }

        private void Application_WorkbookBeforeClose(Excel.Workbook workbook, ref bool cancel)
        {
            if (!cancel)
            {
                var timer = new System.Windows.Forms.Timer { Interval = 150 };
                timer.Tick += delegate
                {
                    timer.Stop();
                    timer.Dispose();
                    RemoveClosedWindowPanes();
                    RefreshActivePane();
                };
                timer.Start();
            }
        }

        private PaneEntry EnsurePane(Excel.Window window, Excel.Workbook workbook)
        {
            if (window == null || workbook == null)
            {
                return null;
            }

            var key = window.Hwnd;
            PaneEntry entry;
            if (_panes.TryGetValue(key, out entry))
            {
                return entry;
            }
            if (!_creatingPanes.Add(key))
            {
                return null;
            }

            try
            {
                var runtime = new AssistantRuntime(new UiThreadOfficeApplicationAdapter(
                    new ExcelAdapter(Application, workbook, _officeDispatcher, "vsto-owner"),
                    _officeDispatcher));
                var pane = CustomTaskPanes.Add(runtime.CreatePaneControl(), "RN Assistant", window);
                pane.Width = 1200;
                entry = new PaneEntry { Window = window, Pane = pane, Runtime = runtime };
                _panes[key] = entry;
                return entry;
            }
            finally
            {
                _creatingPanes.Remove(key);
            }
        }

        private void RefreshActivePane()
        {
            var entry = ActivePane();
            if (entry != null)
            {
                entry.Runtime.RefreshState();
            }
        }

        private void RemoveClosedWindowPanes()
        {
            var closed = new List<int>();
            foreach (var pair in _panes)
            {
                try
                {
                    var ignored = pair.Value.Window.Hwnd;
                }
                catch
                {
                    closed.Add(pair.Key);
                }
            }

            foreach (var key in closed)
            {
                var entry = _panes[key];
                try { CustomTaskPanes.Remove(entry.Pane); } catch { }
                entry.Runtime.Dispose();
                _panes.Remove(key);
            }
        }

        private Excel.Workbook SafeActiveWorkbook()
        {
            try { return Application.ActiveWorkbook; }
            catch { return null; }
        }

        private Excel.Window SafeActiveWindow()
        {
            try { return Application.ActiveWindow; }
            catch { return null; }
        }

        private AssistantRuntime ActiveRuntime()
        {
            var entry = ActivePane() ?? EnsurePane(SafeActiveWindow(), SafeActiveWorkbook());
            return entry == null ? null : entry.Runtime;
        }

        private PaneEntry ActivePane()
        {
            Excel.Window window;
            try { window = Application.ActiveWindow; }
            catch { return null; }
            if (window == null) return null;
            PaneEntry entry;
            return _panes.TryGetValue(window.Hwnd, out entry) ? entry : null;
        }

        private sealed class PaneEntry
        {
            public Excel.Window Window { get; set; }
            public Microsoft.Office.Tools.CustomTaskPane Pane { get; set; }
            public AssistantRuntime Runtime { get; set; }
        }

        private void InstallContextMenus()
        {
            AddContextMenu("Cell");
            AddContextMenu("Row");
            AddContextMenu("Column");
            AddContextMenu("List Range Popup");
        }

        private void AddContextMenu(string name)
        {
            try
            {
                var commandBar = Application.CommandBars[name];
                AddContextButtons(commandBar);
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
                {
                    throw new InvalidOperationException("No active Excel workbook.");
                }
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
            RemoveContextMenu("Cell");
            RemoveContextMenu("Row");
            RemoveContextMenu("Column");
            RemoveContextMenu("List Range Popup");
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
