using System;
using System.Collections.Generic;
using Microsoft.Office.Core;
using RNAssistant.Office;

namespace RNAssistant.WordAddIn
{
    public sealed partial class ThisAddIn
    {
        private AssistantRuntime _runtime;
        private Microsoft.Office.Tools.CustomTaskPane _pane;
        private readonly List<CommandBarButton> _contextButtons = new List<CommandBarButton>();

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            _runtime = new AssistantRuntime(new WordAdapter(Application));
            InstallContextMenus();
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
            RemoveContextMenus();
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
                _runtime.AddSelectionContext(mode);
                ShowAssistant(action == "ask" ? null : "context");
                if (action == "ask")
                {
                    _runtime.RunQuickAction("ask-context");
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
