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
    window.RNAssistantWorkspaceDrafts.setContext(state, pending);
    refresh();
  }
  function mountProductLayout(container) {
    if (container.querySelector(":scope > .product-dt-main")) return;
    const navigation = container.querySelector(":scope > .dt-navigation");
    if (!navigation) return;
    const heading = document.createElement("strong");
    heading.className = "product-nav-heading";
    heading.textContent = "Данные и шаблоны";
    navigation.classList.add("global-nav");
    navigation.setAttribute("aria-label", "Разделы проекта");
    navigation.prepend(heading);
    const main = document.createElement("div");
    main.className = "product-dt-main";
    Array.from(container.children).forEach(child => {
      if (child !== navigation) main.append(child);
    });
    container.append(main);
    container.prepend(navigation);
  }
  function bind(container) {
    container = container || document.querySelector("#tab-workspace .workspace-projects");
    if (!container) return;
    window.DataTemplatesShell.mount(container, "data-templates/core/index.html");
    container.querySelector(".dt-header .dt-phase").textContent = "Проект данных";
    for (const name of ["Workspace", "Activity", "Sources"]) {
      const slot = container.querySelector('[data-dt-slot="' + name.toLowerCase() + '"]');
      if (!slot.childNodes.length) slot.append(document.getElementById("dtHost" + name).content.cloneNode(true));
    }
    mountProductLayout(container);
    loader.bind(container);
    window.RNAssistantWorkspaceSources.bind(container);
    window.RNAssistantWorkspaceDrafts.bind(container);
    refresh();
  }
  function unbind() { loader.unbind(); window.RNAssistantWorkspaceSources.unbind(); window.RNAssistantWorkspaceDrafts.unbind(); }
  window.addEventListener("pagehide", () => { loader.dispose(); window.RNAssistantWorkspaceDrafts.dispose(); });
  window.RNAssistantDataTemplates = Object.freeze({ bind: bind, unbind: unbind,
    setContext: setContext, workspaceSourcesChanged: refresh });
}());
