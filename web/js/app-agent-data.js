function mountAgentJsonContent(host, text) {
  if (!text) return function () {};
  var viewer = document.createElement("div");
  viewer.className = "agent-json-viewer";
  host.appendChild(viewer);
  try {
    if (!window.RNAssistantViewerRegistry || !window.RNAssistantViewerRegistry.has("json")) {
      throw new Error("JSON viewer is unavailable.");
    }
    var controller = window.RNAssistantViewerRegistry.mount("json", viewer, {
      text: String(text),
      completeness: "full",
      mode: "tree",
      compact: true,
      limits: { maxRawRenderChars: 750000, maxPrettyChars: 4000000 },
      onCopy: window.copyTextResult,
      onStatus: function (message, kind) {
        viewer.classList.toggle("is-json-clean", kind === "ok");
      }
    });
    var toolbar = viewer.querySelector(".rn-json-toolbar");
    var expand = document.createElement("button");
    expand.type = "button";
    expand.className = "agent-json-expand";
    expand.textContent = "Весь текст";
    expand.setAttribute("aria-expanded", "false");
    expand.addEventListener("click", function () {
      var open = viewer.classList.toggle("is-expanded");
      host.classList.toggle("is-expanded-json", open);
      controller.setMode(open ? "pretty" : "tree");
      expand.textContent = open ? "Свернуть" : "Весь текст";
      expand.setAttribute("aria-expanded", String(open));
    });
    toolbar.appendChild(expand);
  } catch (error) {
    host.removeChild(viewer);
    throw error;
  }
  return function () {
    host.classList.remove("is-expanded-json");
    window.RNAssistantViewerRegistry.unmount(viewer);
    host.removeChild(viewer);
  };
}
