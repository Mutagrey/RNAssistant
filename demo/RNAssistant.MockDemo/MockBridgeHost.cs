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

        public MockBridgeHost(AssistantController controller)
        {
            _currentEvents = new AsyncLocal<List<string>>();
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
            var events = _currentEvents.Value;
            if (events != null)
            {
                events.Add(json);
            }
        }
    }
}
