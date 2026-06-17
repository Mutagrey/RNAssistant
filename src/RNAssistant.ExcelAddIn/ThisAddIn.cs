using System;
using Microsoft.Office.Core;
using RNAssistant.Office;

namespace RNAssistant.ExcelAddIn
{
    public sealed partial class ThisAddIn
    {
        private AssistantRuntime _runtime;
        private Microsoft.Office.Tools.CustomTaskPane _pane;

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            _runtime = new AssistantRuntime(new ExcelAdapter(Application));
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
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

        private void InternalStartup()
        {
            Startup += ThisAddIn_Startup;
            Shutdown += ThisAddIn_Shutdown;
        }
    }
}

