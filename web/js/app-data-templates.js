(function () {
  "use strict";
  // RNAssistant adapter for the shipped application. No project JS is evaluated here.
  const loader = window.DataTemplatesLoader.create();
  let context = null, busy = false;
  function refresh() {
    const workspace = context && context.workspace, project = context && context.project;
    const key = workspace ? JSON.stringify([workspace.bindingId, project ? project.manifestPath : null]) : "";
    loader.setContext(key, busy, window.RNAssistantWorkspaceSources.hasSources());
  }
  function setContext(state, pending) {
    context = state; busy = !!pending;
    window.RNAssistantWorkspaceSources.setContext(state, pending);
    refresh();
  }
  function bind(container) {
    container = container || document.querySelector("#tab-workspace .workspace-projects");
    if (!container) return;
    window.DataTemplatesShell.mount(container, "data-templates/core/index.html");
    for (const name of ["Workspace", "Activity", "Sources"]) {
      const slot = container.querySelector('[data-dt-slot="' + name.toLowerCase() + '"]');
      if (!slot.childNodes.length) slot.append(document.getElementById("dtHost" + name).content.cloneNode(true));
    }
    loader.bind(container);
    window.RNAssistantWorkspaceSources.bind(container);
    refresh();
  }
  function unbind() { loader.unbind(); window.RNAssistantWorkspaceSources.unbind(); }
  window.addEventListener("pagehide", () => loader.dispose());
  window.RNAssistantDataTemplates = Object.freeze({ bind: bind, unbind: unbind,
    setContext: setContext, workspaceSourcesChanged: refresh });
}());
