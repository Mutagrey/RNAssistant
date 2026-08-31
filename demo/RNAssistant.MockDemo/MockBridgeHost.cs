using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RNAssistant.Office;
using RNAssistant.Office.WebView;

namespace RNAssistant.MockDemo
{
    internal sealed class BridgePacket
    {
        [JsonProperty("events")]
        public List<string> Events { get; set; }

        [JsonProperty("response")]
        public string Response { get; set; }
    }

    internal sealed class MockBridgeHost
    {
        private readonly AssistantWebBridge _bridge;
        private readonly AsyncLocal<List<string>> _currentEvents;
        private readonly Action<string> _eventObserver;

        public MockBridgeHost(AssistantController controller, Action<string> eventObserver = null)
        {
            _currentEvents = new AsyncLocal<List<string>>();
            _eventObserver = eventObserver;
            _bridge = new AssistantWebBridge(controller, PostMessage);
        }

        public async Task<BridgePacket> HandleAsync(string requestJson)
        {
            var events = new List<string>();
            _currentEvents.Value = events;
            try
            {
                var response = await _bridge.HandleMessageAsync(requestJson).ConfigureAwait(false);
                return new BridgePacket
                {
                    Events = events,
                    Response = response
                };
            }
            finally
            {
                _currentEvents.Value = null;
            }
        }

        private void PostMessage(string json)
        {
            if (_eventObserver != null) _eventObserver(json);
            var events = _currentEvents.Value;
            if (events != null)
            {
                events.Add(json);
            }
        }
    }
}
