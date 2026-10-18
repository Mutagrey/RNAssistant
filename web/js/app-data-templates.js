(function () {
  "use strict";
  // RNAssistant adapter for the shipped application. No project JS is evaluated here.
  let context = null;
  function renderHeader() {
    const container = document.querySelector("#tab-workspace .workspace-projects");
    if (!container || !container.querySelector("#dtPageTitle")) return;
    const workspace = context && context.workspace, project = context && context.project;
    container.querySelector("#dtPageTitle").textContent = project ? project.name : "Данные и шаблоны";
    container.querySelector("#dtPageContext").textContent = workspace
      ? workspace.rootPath + (workspace.readOnly ? " · Только чтение" : "")
      : "Откройте рабочую папку и проект";
    container.querySelector(".dt-header .dt-phase").textContent = project ? "Проект открыт" : "Проект не выбран";
  }
  function setContext(state, pending) {
    context = state;
    window.RNAssistantWorkspaceSources.setContext(state, pending);
    window.RNAssistantWorkspaceDrafts.setContext(state, pending);
    renderHeader();
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
    window.DataTemplatesShell.mount(container, "data-templates/core/index.html", { nativeSources: true });
    container.querySelector("#dtSourceHeading").textContent = "Источники";
    container.querySelector("#dtSourceIntro").textContent = "Выберите файлы в рабочей папке. Сохранение подборки и подготовка запускаются отдельно.";
    for (const name of ["Workspace", "Activity", "Sources"]) {
      const slot = container.querySelector('[data-dt-slot="' + name.toLowerCase() + '"]');
      if (!slot.childNodes.length) slot.append(document.getElementById("dtHost" + name).content.cloneNode(true));
    }
    mountProductLayout(container);
    const projectButton = container.querySelector("#dtProjectHeaderButton");
    if (!projectButton.dataset.bound) {
      const settings = container.querySelector("#workspaceSettings");
      projectButton.dataset.bound = "true";
      projectButton.setAttribute("aria-controls", "workspaceSettings");
      projectButton.addEventListener("click", () => {
        settings.open = !settings.open;
        projectButton.setAttribute("aria-expanded", String(settings.open));
        if (settings.open) settings.querySelector("summary").focus({ preventScroll: true });
      });
      settings.addEventListener("toggle", () => projectButton.setAttribute("aria-expanded", String(settings.open)));
      projectButton.setAttribute("aria-expanded", String(settings.open));
    }
    window.RNAssistantWorkspaceSources.bind(container);
    window.RNAssistantWorkspaceDrafts.bind(container);
    renderHeader();
  }
  function unbind() { window.RNAssistantWorkspaceSources.unbind(); window.RNAssistantWorkspaceDrafts.unbind(); }
  window.addEventListener("pagehide", () => window.RNAssistantWorkspaceDrafts.dispose());
  window.RNAssistantDataTemplates = Object.freeze({ bind: bind, unbind: unbind, setContext: setContext });
}());
