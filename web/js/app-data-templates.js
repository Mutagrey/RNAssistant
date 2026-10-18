(function () {
  "use strict";
  // RNAssistant adapter for the shipped application. No project JS is evaluated here.
  let context = null, navigationEvents = null;
  const navPaths = {
    search: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 4 4"/>',
    folder: '<path d="M3 7V5h6l2 2h10v12H3Z"/>',
    down: '<path d="m6 9 6 6 6-6"/>',
    right: '<path d="m9 6 6 6-6 6"/>',
    files: '<path d="M7 3h9l4 4v13H7Z"/><path d="M16 3v5h4M3 7v14h12"/>',
    file: '<path d="M5 3h10l4 4v14H5Z"/><path d="M14 3v5h5M9 12h6M9 16h6"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 6v6l4 2"/>',
    braces: '<path d="M8 3H6v7l-3 2 3 2v7h2M16 3h2v7l3 2-3 2v7h-2"/>',
    link: '<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.72-1.71"/>',
    chart: '<path d="M3 3v18h18M7 16v-5M12 16V7M17 16V4"/>'
  };
  function navIcon(name) {
    return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.55" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + navPaths[name] + '</svg>';
  }
  function renderHeader() {
    const container = document.querySelector("#tab-workspace .workspace-projects");
    if (!container || !container.querySelector("#dtPageTitle")) return;
    const workspace = context && context.workspace, project = context && context.project;
    container.querySelector("#dtPageTitle").textContent = project ? project.name : "Данные и шаблоны";
    container.querySelector("#dtPageContext").textContent = workspace
      ? workspace.rootPath + (workspace.readOnly ? " · Только чтение" : "")
      : "Откройте рабочую папку и проект";
    container.querySelector(".dt-header .dt-phase").textContent = project ? "Проект открыт" : "Проект не выбран";
    const navProject = container.querySelector("#dtNavProjectName");
    if (navProject) navProject.textContent = project ? project.name : "Проект не выбран";
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
    navigation.classList.add("global-nav");
    navigation.setAttribute("aria-label", "Разделы проекта");
    const tabs = navigation.querySelector(".dt-tabs");
    const mobile = navigation.querySelector(".dt-mobile-nav");
    const content = document.createElement("div"); content.className = "navigation-content dt-nav-content";
    const heading = document.createElement("div"); heading.className = "workspace-heading dt-nav-heading";
    heading.innerHTML = '<strong class="nav-label">Данные и шаблоны</strong><div class="tree-toolbar"><button type="button" class="dt-nav-icon-button" id="dtNavSearchToggle" aria-label="Поиск в разделе" aria-expanded="false">' + navIcon("search") + '</button><input type="search" id="dtNavSearch" class="tree-search-input" aria-label="Поиск по разделам проекта" placeholder="Найти…" hidden><button type="button" class="dt-nav-icon-button" id="dtNavOpenProject" aria-label="Открыть проект" title="Открыть проект">' + navIcon("folder") + '</button></div>';
    content.append(heading);
    navigation.append(content);
    const project = document.createElement("div"); project.className = "dt-nav-project navigation-tree-row is-group";
    project.style.setProperty("--tree-depth", "0");
    const projectToggle = document.createElement("button"); projectToggle.type = "button";
    projectToggle.className = "navigation-tree-expander"; projectToggle.id = "dtNavProjectToggle";
    projectToggle.setAttribute("aria-label", "Свернуть разделы проекта"); projectToggle.setAttribute("aria-expanded", "true");
    projectToggle.innerHTML = navIcon("down"); project.append(projectToggle);
    const projectName = document.createElement("span"); projectName.id = "dtNavProjectName";
    projectName.className = "navigation-tree-label";
    projectName.textContent = "Проект не выбран"; project.append(projectName);
    content.append(project);
    tabs.classList.add("navigation-tree");
    content.append(tabs, mobile);
    const icons = { dtTabSources: "files", dtTabTemplates: "file", dtTabParameters: "braces",
      dtTabMapping: "link", dtTabResults: "chart", dtTabFormat: "file" };
    function leaf(button, icon, depth) {
      const row = document.createElement("div"); row.className = "dt-nav-leaf navigation-tree-row";
      row.style.setProperty("--tree-depth", String(depth));
      const spacer = document.createElement("span"); spacer.className = "navigation-tree-expander";
      spacer.setAttribute("aria-hidden", "true");
      const image = document.createElement("span"); image.className = "navigation-tree-icon";
      image.setAttribute("aria-hidden", "true"); image.innerHTML = navIcon(icon);
      button.classList.add("navigation-tree-label");
      row.append(spacer, image, button);
      return row;
    }
    function group(label, ids) {
      const block = document.createElement("div"); block.className = "dt-nav-group";
      if (label) {
        const row = document.createElement("div"); row.className = "dt-nav-group-label navigation-tree-row is-group";
        row.style.setProperty("--tree-depth", "1");
        const toggle = document.createElement("button"); toggle.type = "button";
        toggle.className = "navigation-tree-expander"; toggle.setAttribute("aria-expanded", "true");
        toggle.setAttribute("aria-label", "Свернуть " + label); toggle.innerHTML = navIcon("down");
        const title = document.createElement("span"); title.className = "navigation-tree-label"; title.textContent = label;
        row.append(toggle, title); block.append(row);
      }
      const children = document.createElement("div"); children.className = "dt-nav-group-children";
      ids.forEach(id => children.append(leaf(navigation.querySelector("#" + id), icons[id], label ? 2 : 1)));
      block.append(children);
      tabs.append(block);
      return children;
    }
    group("", ["dtTabSources"]);
    const templates = group("Шаблоны", ["dtTabTemplates"]);
    const drafts = document.createElement("button"); drafts.type = "button"; drafts.className = "dt-nav-extra";
    drafts.dataset.dtGo = "templates"; drafts.dataset.dtTarget = "dtNativeProjectTitle"; drafts.textContent = "Снимки черновиков";
    templates.append(leaf(drafts, "clock", 2));
    group("Словарь", ["dtTabParameters", "dtTabMapping"]);
    group("", ["dtTabResults", "dtTabFormat"]);
    navigation.querySelector("#dtTabMapping").textContent = "Соответствия";
    navigation.querySelector("#dtTabFormat").textContent = "Форматы и ограничения";
    const main = document.createElement("div");
    main.className = "product-dt-main";
    Array.from(container.children).forEach(child => {
      if (child !== navigation) main.append(child);
    });
    const divider = document.createElement("div");
    divider.className = "splitter";
    divider.setAttribute("role", "separator");
    divider.setAttribute("aria-orientation", "vertical");
    divider.setAttribute("aria-label", "Изменить ширину разделов проекта");
    container.classList.add("split-layout");
    container.setAttribute("data-default-left", "310");
    container.setAttribute("data-min-left", "240");
    container.append(navigation, divider, main);
    if (typeof window.initializeSplitPanes === "function") window.initializeSplitPanes();
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
        tab.closest(".dt-nav-leaf")?.classList.toggle("selected", selected);
        container.querySelector("#" + tab.getAttribute("aria-controls")).hidden = !selected;
      });
      select.value = name;
      const nav = container.querySelector(".dt-navigation");
      const projectToggle = nav.querySelector("#dtNavProjectToggle");
      if (nav.querySelector(".dt-tabs").hidden) toggle(projectToggle, nav.querySelector(".dt-tabs"), true, "разделы проекта");
      const group = active.closest(".dt-nav-group");
      const groupButton = group?.querySelector(".dt-nav-group-label button");
      if (groupButton && group.querySelector(".dt-nav-group-children").hidden) {
        toggle(groupButton, group.querySelector(".dt-nav-group-children"), true, groupButton.nextElementSibling.textContent);
      }
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
    const nav = container.querySelector(".dt-navigation");
    const projectToggle = nav.querySelector("#dtNavProjectToggle");
    function toggle(button, children, expanded, name) {
      children.hidden = !expanded;
      button.setAttribute("aria-expanded", String(expanded));
      button.setAttribute("aria-label", (expanded ? "Свернуть " : "Развернуть ") + name);
      button.innerHTML = navIcon(expanded ? "down" : "right");
    }
    projectToggle.addEventListener("click", () => toggle(projectToggle, nav.querySelector(".dt-tabs"),
      projectToggle.getAttribute("aria-expanded") !== "true", "разделы проекта"), { signal });
    nav.querySelectorAll(".dt-nav-group-label button").forEach(button => button.addEventListener("click", () => {
      const children = button.closest(".dt-nav-group").querySelector(".dt-nav-group-children");
      toggle(button, children, button.getAttribute("aria-expanded") !== "true", button.nextElementSibling.textContent);
    }, { signal }));
    const searchToggle = nav.querySelector("#dtNavSearchToggle"), search = nav.querySelector("#dtNavSearch");
    function closeSearch(clear) {
      if (clear && search.value) { search.value = ""; search.dispatchEvent(new Event("input")); }
      search.hidden = true;
      nav.querySelector(".dt-nav-heading").classList.remove("searching");
      searchToggle.setAttribute("aria-expanded", "false");
    }
    searchToggle.addEventListener("pointerdown", event => {
      if (document.activeElement === search) event.preventDefault();
    }, { signal });
    searchToggle.addEventListener("click", () => {
      if (!search.hidden) { closeSearch(true); searchToggle.focus(); return; }
      search.hidden = false; nav.querySelector(".dt-nav-heading").classList.add("searching");
      searchToggle.setAttribute("aria-expanded", "true"); search.focus();
    }, { signal });
    search.addEventListener("input", () => {
      const query = search.value.trim().toLocaleLowerCase("ru");
      const projectMatches = !!query && nav.querySelector("#dtNavProjectName").textContent.toLocaleLowerCase("ru").includes(query);
      nav.querySelectorAll(".dt-nav-leaf").forEach(row => {
        row.hidden = !!query && !projectMatches && !row.textContent.toLocaleLowerCase("ru").includes(query);
      });
      nav.querySelectorAll(".dt-nav-group").forEach(group => {
        const label = group.querySelector(".dt-nav-group-label .navigation-tree-label")?.textContent || "";
        if (query && label.toLocaleLowerCase("ru").includes(query)) {
          group.querySelectorAll(".dt-nav-leaf").forEach(row => { row.hidden = false; });
        }
        group.hidden = !!query && !Array.from(group.querySelectorAll(".dt-nav-leaf")).some(row => !row.hidden);
        if (query && !group.hidden) {
          const button = group.querySelector(".dt-nav-group-label button");
          if (button) toggle(button, group.querySelector(".dt-nav-group-children"), true, button.nextElementSibling.textContent);
        }
      });
      if (query) toggle(projectToggle, nav.querySelector(".dt-tabs"), true, "разделы проекта");
      searchToggle.classList.toggle("has-search-query", !!query);
      searchToggle.title = query ? "Фильтр: " + search.value + " · изменить поиск" : "Поиск в разделе";
      searchToggle.setAttribute("aria-description", query ? "Активен фильтр: " + search.value : "");
    }, { signal });
    search.addEventListener("blur", () => closeSearch(false), { signal });
    search.addEventListener("keydown", event => {
      if (event.key !== "Escape") return;
      event.preventDefault(); closeSearch(true); searchToggle.focus();
    }, { signal });
    nav.querySelector("#dtNavOpenProject").addEventListener("click", () => container.querySelector("#dtProjectHeaderButton").click(), { signal });
    select.addEventListener("change", () => show(select.value, false), { signal });
    container.addEventListener("click", event => {
      const action = event.target.closest("[data-dt-go]");
      if (action && container.contains(action)) {
        show(action.dataset.dtGo, true);
        if (action.dataset.dtTarget) container.querySelector("#" + action.dataset.dtTarget)?.scrollIntoView({ block: "start" });
      }
    }, { signal });
    show(tabs.find(tab => tab.getAttribute("aria-selected") === "true")?.dataset.dtView || "sources", false);
  }
  function bind(container) {
    container = container || document.querySelector("#tab-workspace .workspace-projects");
    if (!container) return;
    window.DataTemplatesShell.mount(container, "data-templates/core/index.html", { nativeSources: true });
    container.querySelector("#dtViewSources > .dt-section-heading")?.remove();
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
