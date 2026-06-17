using System;
using System.IO;
using RNAssistant.Office.WebView;

namespace RNAssistant.Office
{
    public sealed class AssistantRuntime
    {
        private readonly IOfficeApplicationAdapter _adapter;

        public AssistantRuntime(IOfficeApplicationAdapter adapter)
        {
            _adapter = adapter;
            Controller = new AssistantController(adapter);
        }

        public AssistantController Controller { get; private set; }

        public AssistantPaneControl CreatePaneControl()
        {
            return new AssistantPaneControl(Controller, ResolveWebRoot());
        }

        public void RunQuickAction(string action)
        {
            Controller.QueueQuickAction(action);
        }

        private static string ResolveWebRoot()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var webRoot = Path.Combine(baseDirectory, "web");
            return Directory.Exists(webRoot) ? webRoot : Path.Combine(baseDirectory, "..", "..", "web");
        }
    }
}

