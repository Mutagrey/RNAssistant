namespace RNAssistant.MockDemo
{
    internal static class MockBridgeScript
    {
        public const string Script =
@"(function () {
  var listeners = [];
  function dispatch(data) {
    listeners.slice().forEach(function (listener) {
      listener({ data: data });
    });
  }
  function parseJson(text) {
    return typeof text === ""string"" ? JSON.parse(text) : text;
  }
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    addEventListener: function (type, listener) {
      if (type === ""message"" && typeof listener === ""function"") {
        listeners.push(listener);
      }
    },
    postMessage: function (message) {
      if (message && message.type === ""focusState"") {
        return;
      }
      fetch(""/bridge"", {
        method: ""POST"",
        headers: { ""Content-Type"": ""application/json"" },
        body: JSON.stringify(message || {})
      }).then(function (response) {
        if (!response.ok) {
          throw new Error(""Mock bridge HTTP "" + response.status);
        }
        return response.json();
      }).then(function (packet) {
        (packet.events || []).forEach(function (eventJson) {
          dispatch(parseJson(eventJson));
        });
        if (packet.response) {
          dispatch(parseJson(packet.response));
        }
      }).catch(function (error) {
        dispatch({ id: message && message.id, ok: false, error: error.message || String(error) });
      });
    }
  };
}());";
    }
}
