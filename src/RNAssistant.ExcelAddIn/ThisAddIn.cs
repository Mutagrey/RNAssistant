using System;
using Microsoft.Office.Core;
using RNAssistant.Office;
using Excel = Microsoft.Office.Interop.Excel;

namespace RNAssistant.ExcelAddIn
{
    public sealed partial class ThisAddIn
    {
        private AssistantRuntime _runtime;
        private Microsoft.Office.Tools.CustomTaskPane _pane;

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            _runtime = new AssistantRuntime(new ExcelAdapter(Application));
            Application.SheetSelectionChange += Application_SheetSelectionChange;
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
            Application.SheetSelectionChange -= Application_SheetSelectionChange;
        }

        public void ShowAssistant(string quickAction = null)
        {
            if (_pane == null)
            {
                _pane = CustomTaskPanes.Add(_runtime.CreatePaneControl(), "RN Assistant");
                _pane.Width = 520;
            }

            if (!string.IsNullOrWhiteSpace(quickAction))
            {
                _runtime.RunQuickAction(quickAction);
            }

            _pane.Visible = true;
        }

        protected override IRibbonExtensibility CreateRibbonExtensibilityObject()
        {
            return new AssistantRibbon(this);
        }

        private void Application_SheetSelectionChange(object sheet, Excel.Range target)
        {
            if (_runtime != null)
            {
                _runtime.BlurComposer();
            }
        }

        private void InternalStartup()
        {
            Startup += ThisAddIn_Startup;
            Shutdown += ThisAddIn_Shutdown;
        }
    }
}
