function appendAgentJsonViewer(parent, label, text, className, open) {
  if (!text) return;

  var details = document.createElement("details");
  details.className = className || "agent-data";
  details.open = !!open;
  var summary = document.createElement("summary");
  summary.textContent = label;
  var host = document.createElement("div");
  host.className = "agent-json-viewer";
  details.appendChild(summary);
  details.appendChild(host);
  var controller = null;

  function mount() {
    if (controller) return;
    if (!window.RNAssistantViewerRegistry || !window.RNAssistantViewerRegistry.has("json")) {
      throw new Error("JSON viewer is unavailable.");
    }
    controller = window.RNAssistantViewerRegistry.mount("json", host, {
      text: String(text),
      completeness: "full",
      mode: "tree",
      onCopy: window.copyTextResult
    });
  }

  function unmount() {
    if (!controller) return;
    window.RNAssistantViewerRegistry.unmount(host);
    controller = null;
  }

  details.addEventListener("toggle", function () {
    if (details.open) mount();
    else unmount();
  });
  parent.appendChild(details);
  if (details.open) mount();
}

function appendArgumentsData(parent, text) {
  appendAgentJsonViewer(parent, "Аргументы", text, "agent-data agent-arguments", false);
}

function appendActivityData(parent, label, text) {
  appendAgentJsonViewer(parent, label, text, "agent-data", false);
}
