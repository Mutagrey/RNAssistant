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
            : this(adapter, null)
        {
        }

        public AssistantRuntime(IOfficeApplicationAdapter adapter, string rootPath)
        {
            _adapter = adapter;
            RootPath = rootPath;
            Controller = new AssistantController(adapter);
        }

        public AssistantController Controller { get; private set; }
        public string RootPath { get; private set; }

        public AssistantPaneControl CreatePaneControl()
        {
            _paneControl = new AssistantPaneControl(Controller, ResolveWebRoot(RootPath));
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

        public void ReleaseKeyboardFocusToHost()
        {
            if (_paneControl != null)
            {
                _paneControl.ReleaseKeyboardFocusToHost(_adapter.PrepareForContextCapture);
                return;
            }

            _adapter.PrepareForContextCapture();
        }

        public void AddSelectionContext(string mode)
        {
            Controller.AddSelectionContext(mode);
            if (_paneControl != null)
            {
                _paneControl.RefreshContext();
            }
        }

        public void RefreshState()
        {
            if (_paneControl != null)
            {
                _paneControl.RefreshState();
            }
        }

        private static string ResolveWebRoot(string rootPath)
        {
            if (!string.IsNullOrWhiteSpace(rootPath))
            {
                return Path.Combine(Path.GetFullPath(rootPath), "web");
            }

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var webRoot = Path.Combine(baseDirectory, "web");
            return Directory.Exists(webRoot) ? webRoot : Path.Combine(baseDirectory, "..", "..", "web");
        }
    }
}
