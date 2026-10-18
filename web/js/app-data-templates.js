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
    const navProject = container.querySelector("#dtNavProjectName");
    if (navProject) navProject.textContent = project ? project.name : "Проект не выбран";
    renderNavigation(container);
  }
  function setContext(state, pending) {
    context = state;
    window.RNAssistantWorkspaceSources.setContext(state, pending);
    window.RNAssistantWorkspaceDrafts.setContext(state, pending);
    if (window.RNAssistantWorkspaceCatalog) window.RNAssistantWorkspaceCatalog.setContext(state, pending);
    renderHeader();
  }
  let selectedView = "sources";
  let projectExpanded = true;
  const expandedGroups = { templates: true, dictionary: true };
  const viewNodes = [
    ["sources", "Источники", "files", "dtTabSources"],
    ["templates", "Шаблоны", "file", "dtTabTemplates"],
    ["parameters", "Параметры", "braces", "dtTabParameters"],
    ["mapping", "Соответствия", "link", "dtTabMapping"],
    ["results", "Результаты", "chart", "dtTabResults"],
    ["format", "Форматы и ограничения", "file", "dtTabFormat"]
  ];

  function projectTreeNodes() {
    const tree = window.RNAssistantNavigationTree;
    function leaf(view) {
      const item = viewNodes.find(value => value[0] === view);
      return {
        key: "dt:" + view, kind: "leaf", title: item[1], view: view,
        labelId: item[3], iconHtml: tree.icon(item[2]), selected: selectedView === view,
        className: "dt-nav-leaf"
      };
    }
    const projectName = context && context.project ? context.project.name : "Проект не выбран";
    return [{
      key: "dt:project", kind: "parent", title: projectName, labelId: "dtNavProjectName",
      toggleId: "dtNavProjectToggle", iconHtml: tree.icon("folder"), expanded: projectExpanded,
      className: "dt-nav-project",
      children: [
        leaf("sources"),
        { key: "dt:templates-group", kind: "group", title: "Шаблоны", expanded: expandedGroups.templates,
          className: "dt-nav-group", children: [leaf("templates"), {
            key: "dt:drafts", kind: "leaf", title: "Снимки черновиков", iconHtml: tree.icon("clock"),
            view: "templates", target: "dtNativeProjectTitle", className: "dt-nav-leaf"
          }] },
        { key: "dt:dictionary-group", kind: "group", title: "Словарь", expanded: expandedGroups.dictionary,
          className: "dt-nav-group", children: [leaf("parameters"), leaf("mapping")] },
        leaf("results"), leaf("format")
      ]
    }];
  }

  function filterTree(nodes, query) {
    if (!query) return nodes;
    return nodes.map(node => {
      if (node.title.toLocaleLowerCase("ru").includes(query)) return node;
      if (!node.children) return null;
      const children = filterTree(node.children, query);
      return children.length ? Object.assign({}, node, { children, expanded: true }) : null;
    }).filter(Boolean);
  }

  function renderNavigation(container) {
    const root = container.querySelector("#dtNavTree");
    if (!root) return;
    const search = container.querySelector("#dtNavSearch");
    const query = search ? search.value.trim().toLocaleLowerCase("ru") : "";
    window.RNAssistantNavigationTree.render(root, filterTree(projectTreeNodes(), query), {
      label: "Разделы проекта",
      emptyText: "Разделы не найдены.",
      onSelect: node => showView(container, node.view, !!node.target, node.target),
      onToggle: (node, expanded) => {
        if (node.key === "dt:project") projectExpanded = expanded;
        else if (node.key === "dt:templates-group") expandedGroups.templates = expanded;
        else if (node.key === "dt:dictionary-group") expandedGroups.dictionary = expanded;
      }
    });
  }

  function showView(container, name, focusPanel, targetId) {
    if (!viewNodes.some(view => view[0] === name)) return;
    selectedView = name;
    viewNodes.forEach(view => {
      const panel = container.querySelector("#dtView" + view[0][0].toUpperCase() + view[0].slice(1));
      if (panel) panel.hidden = view[0] !== name;
    });
    container.querySelector("#dtViewSelect").value = name;
    projectExpanded = true;
    if (name === "templates") expandedGroups.templates = true;
    if (name === "parameters" || name === "mapping") expandedGroups.dictionary = true;
    renderNavigation(container);
    const panel = container.querySelector("#dtView" + name[0].toUpperCase() + name.slice(1));
    if (focusPanel && panel) panel.focus({ preventScroll: true });
    if (targetId) container.querySelector("#" + targetId)?.scrollIntoView({ block: "start" });
  }

  function mountProductLayout(container) {
    if (container.querySelector(":scope > .product-dt-main")) return;
    const navigation = container.querySelector(":scope > .dt-navigation");
    if (!navigation) return;
    navigation.classList.add("global-nav");
    navigation.setAttribute("aria-label", "Разделы проекта");
    const oldTabs = navigation.querySelector(".dt-tabs");
    const mobile = navigation.querySelector(".dt-mobile-nav");
    const content = document.createElement("div"); content.className = "navigation-content dt-nav-content";
    const heading = document.createElement("div"); heading.className = "workspace-heading dt-nav-heading";
    heading.innerHTML = '<strong class="nav-label">Данные и шаблоны</strong><div class="tree-toolbar"><button type="button" class="product-nav-search-toggle" id="dtNavSearchToggle" aria-label="Поиск в разделе" aria-expanded="false">' + window.RNAssistantNavigationTree.icon("search") + '</button><input type="search" id="dtNavSearch" class="tree-search-input" aria-label="Поиск по разделам проекта" placeholder="Найти…" hidden><button type="button" class="product-nav-search-toggle" id="dtNavOpenProject" aria-label="Открыть проект" title="Открыть проект">' + window.RNAssistantNavigationTree.icon("folder") + '</button></div>';
    content.append(heading);
    const treeRoot = document.createElement("div");
    treeRoot.className = "dt-tabs navigation-tree";
    treeRoot.id = "dtNavTree";
    oldTabs.remove();
    content.append(treeRoot, mobile);
    navigation.append(content);
    const main = document.createElement("div"); main.className = "product-dt-main";
    Array.from(container.children).forEach(child => { if (child !== navigation) main.append(child); });
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
    const nav = container.querySelector(".dt-navigation");
    const searchToggle = nav.querySelector("#dtNavSearchToggle");
    const search = nav.querySelector("#dtNavSearch");
    const heading = nav.querySelector(".dt-nav-heading");
    function closeSearch(clear) {
      if (clear) { search.value = ""; renderNavigation(container); }
      search.hidden = true;
      heading.classList.remove("searching");
      searchToggle.setAttribute("aria-expanded", "false");
    }
    searchToggle.addEventListener("pointerdown", event => {
      if (document.activeElement === search) event.preventDefault();
    }, { signal });
    searchToggle.addEventListener("click", () => {
      if (!search.hidden) { closeSearch(true); searchToggle.focus(); return; }
      search.hidden = false; heading.classList.add("searching");
      searchToggle.setAttribute("aria-expanded", "true"); search.focus();
    }, { signal });
    search.addEventListener("input", () => {
      renderNavigation(container);
      const query = search.value.trim();
      searchToggle.classList.toggle("has-search-query", !!query);
      searchToggle.title = query ? "Фильтр: " + query + " · изменить поиск" : "Поиск в разделе";
      searchToggle.setAttribute("aria-description", query ? "Активен фильтр: " + query : "");
    }, { signal });
    search.addEventListener("blur", () => closeSearch(false), { signal });
    search.addEventListener("keydown", event => {
      if (event.key !== "Escape") return;
      event.preventDefault(); closeSearch(true); searchToggle.focus();
    }, { signal });
    nav.querySelector("#dtNavOpenProject").addEventListener("click", () => container.querySelector("#dtProjectHeaderButton").click(), { signal });
    container.querySelector("#dtViewSelect").addEventListener("change", event => showView(container, event.target.value, false), { signal });
    container.addEventListener("click", event => {
      const action = event.target.closest("[data-dt-go]");
      if (action && container.contains(action)) showView(container, action.dataset.dtGo, true, action.dataset.dtTarget);
    }, { signal });
    showView(container, selectedView, false);
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
