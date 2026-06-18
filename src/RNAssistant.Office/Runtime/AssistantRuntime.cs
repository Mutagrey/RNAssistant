using System;
using System.IO;
using RNAssistant.Office.WebView;

namespace RNAssistant.Office
{
    public sealed class AssistantRuntime
    {
        private readonly IOfficeApplicationAdapter _adapter;
        private AssistantPaneControl _paneControl;

        public AssistantRuntime(IOfficeApplicationAdapter adapter)
        {
            _adapter = adapter;
            Controller = new AssistantController(adapter);
        }

        public AssistantController Controller { get; private set; }

        public AssistantPaneControl CreatePaneControl()
        {
            _paneControl = new AssistantPaneControl(Controller, ResolveWebRoot());
            return _paneControl;
        }

        public void RunQuickAction(string action)
        {
            Controller.QueueQuickAction(action);
            if (_paneControl != null)
            {
                _paneControl.RunQuickAction(action);
            }
        }

        public void BlurComposer()
        {
            if (_paneControl != null)
            {
                _paneControl.BlurComposer();
            }
        }

        public void AddSelectionContext(string mode)
        {
            Controller.AddSelectionContext(mode);
            if (_paneControl != null)
            {
                _paneControl.RefreshContext();
            }
        }

        private static string ResolveWebRoot()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var webRoot = Path.Combine(baseDirectory, "web");
            return Directory.Exists(webRoot) ? webRoot : Path.Combine(baseDirectory, "..", "..", "web");
        }
    }
}
