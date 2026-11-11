using System;
using System.Linq;
using Newtonsoft.Json;
using RNAssistant.Office.Contracts;

namespace RNAssistant.Office.WebView
{
    public sealed partial class AssistantWebBridge
    {
        private IAssistantWindowHost _windowHost;
        public IAssistantWindowHost WindowHost
        {
            get { return _windowHost; }
            set
            {
                if (_windowHost != null) { _windowHost.StateChanged -= ReportWindowState; _windowHost.ShutdownRequested -= RequestWindowShutdown; }
                _windowHost = value;
                if (value != null) { value.StateChanged += ReportWindowState; value.ShutdownRequested += RequestWindowShutdown; }
            }
        }
        public HostWindowActivity WindowActivity
        {
            get
            {
                var runs = _controller.ActiveChatRuns().Concat(Plugins == null ?
                    new RNAssistant.Application.Conversations.ConversationActiveSnapshot[0] : Plugins.Conversations.ActiveChatRuns()).ToArray();
                return new HostWindowActivity { Running = runs.Length,
                    Waiting = runs.Count(run => run.Phase == "awaiting_confirmation" || run.Phase == "awaiting_user" ||
                        run.Phase == "waiting_agents" || run.Phase == "paused") };
            }
        }
        public bool CanShutdownWindow
        {
            get { lock (_hostSwitchSync) return !_disposed && !_hostSwitchPending && _requestsInFlight == 0 && WindowActivity.Running == 0; }
        }
        private void ReportWindowState(HostWindowStateDto state)
        { _postMessageJson?.Invoke(JsonConvert.SerializeObject(new HostWindowStateMessage { Payload = state })); }
        private void RequestWindowShutdown()
        { _postMessageJson?.Invoke(JsonConvert.SerializeObject(new HostWindowShutdownMessage())); }
    }
}
