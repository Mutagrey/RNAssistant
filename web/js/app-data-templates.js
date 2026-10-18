(function () {
  "use strict";
  // RNAssistant adapter for the shipped application. No project JS is evaluated here.
  let context = null, navigationEvents = null;
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
    if (window.RNAssistantWorkspaceCatalog) window.RNAssistantWorkspaceCatalog.setContext(state, pending);
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
  function bindNavigation(container) {
    if (navigationEvents) return;
    navigationEvents = new AbortController();
    const signal = navigationEvents.signal;
    const tabs = Array.from(container.querySelectorAll("[data-dt-view]"));
    const select = container.querySelector("#dtViewSelect");
    function show(name, focusPanel) {
      const active = tabs.find(tab => tab.dataset.dtView === name);
      if (!active) return;
      tabs.forEach(tab => {
        const selected = tab === active;
        tab.setAttribute("aria-selected", String(selected));
        tab.tabIndex = selected ? 0 : -1;
        container.querySelector("#" + tab.getAttribute("aria-controls")).hidden = !selected;
      });
      select.value = name;
      if (focusPanel) container.querySelector("#" + active.getAttribute("aria-controls")).focus({ preventScroll: true });
    }
    tabs.forEach((tab, index) => {
      tab.addEventListener("click", () => show(tab.dataset.dtView, false), { signal });
      tab.addEventListener("keydown", event => {
        const next = event.key === "Home" ? 0 : event.key === "End" ? tabs.length - 1
          : ["ArrowRight", "ArrowDown"].includes(event.key) ? (index + 1) % tabs.length
            : ["ArrowLeft", "ArrowUp"].includes(event.key) ? (index + tabs.length - 1) % tabs.length : -1;
        if (next < 0) return;
        event.preventDefault(); tabs[next].focus(); show(tabs[next].dataset.dtView, false);
      }, { signal });
    });
    select.addEventListener("change", () => show(select.value, false), { signal });
    container.addEventListener("click", event => {
      const action = event.target.closest("[data-dt-go]");
      if (action && container.contains(action)) show(action.dataset.dtGo, true);
    }, { signal });
    show(tabs.find(tab => tab.getAttribute("aria-selected") === "true")?.dataset.dtView || "sources", false);
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
    bindNavigation(container);
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
    if (window.RNAssistantWorkspaceCatalog) window.RNAssistantWorkspaceCatalog.bind(container);
    renderHeader();
  }
  function unbind() {
    window.RNAssistantWorkspaceSources.unbind();
    window.RNAssistantWorkspaceDrafts.unbind();
    if (window.RNAssistantWorkspaceCatalog) window.RNAssistantWorkspaceCatalog.unbind();
    if (navigationEvents) { navigationEvents.abort(); navigationEvents = null; }
  }
  window.addEventListener("pagehide", () => window.RNAssistantWorkspaceDrafts.dispose());
  window.RNAssistantDataTemplates = Object.freeze({ bind: bind, unbind: unbind, setContext: setContext });
}());
