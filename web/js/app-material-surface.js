(function () {
  "use strict";

  // One shell for Material and Browser objects. Providers supply presentation
  // controls, search/status adapters and an optional navigation surface.
  var sequence = 0;
  var materialNavigationWidthKey = "rnassistant.ui.material.navigation.width";

  function node(tag, className, text) {
    var result = document.createElement(tag);
    if (className) result.className = className;
    if (text !== undefined) result.textContent = text;
    return result;
  }

  function iconButton(label, icon) {
    var button = node("button", "icon-btn compact");
    button.type = "button";
    button.title = label;
    button.setAttribute("aria-label", label);
    if (window.RNAssistantIcons) button.innerHTML = window.RNAssistantIcons.render(icon);
    return button;
  }

  function create(options) {
    options = options || {};
    var id = "material-surface-" + (++sequence);
    var root = node("section", "material-surface product-viewer");
    var layout = node("div", "material-surface-layout");
    var sidebar = node("aside", "material-surface-sidebar");
    var sidebarBody = node("div", "material-surface-sidebar-body");
    var navigationSplitter = node("div", "splitter material-surface-splitter");
    var workspace = node("section", "material-surface-workspace");
    var header = node("header", "material-surface-header resource-editor-head");
    var navigationToggle = node("button", "icon-btn panel-toggle material-surface-navigation-toggle");
    var titleBox = node("div", "resource-editor-titlebox material-surface-title");
    var title = node("strong", "resource-editor-title", options.title || "Материал");
    var meta = node("small", "resource-editor-meta", options.meta || "");
    var provider = node("div", "material-surface-provider");
    var modes = node("div", "material-surface-modes");
    var search = node("div", "material-surface-search product-search-heading");
    var searchToggle = node("button", "product-nav-search-toggle icon-btn");
    var searchInput = node("input", "material-surface-search-input product-nav-search-input rna-form-control compact");
    var searchPrevious = iconButton("Предыдущее совпадение", "left");
    var searchNext = iconButton("Следующее совпадение", "right");
    var searchClose = iconButton("Закрыть поиск", "close");
    var searchStatus = node("span", "material-surface-search-status");
    var actions = node("div", "resource-editor-actions material-surface-actions");
    var providerActions = node("div", "material-surface-provider-actions");
    var commonActions = node("div", "resource-editor-actions material-surface-common-actions");
    var content = node("div", "material-surface-content product-viewer");
    var statusbar = node("footer", "material-surface-statusbar");
    var viewerStatus = node("div", "material-surface-viewer-status");
    viewerStatus.setAttribute("role", "status"); viewerStatus.setAttribute("aria-live", "polite");
    var statusMeta = node("div", "material-surface-status-meta", options.statusMeta || "");

    sidebar.id = id + "-navigation";
    sidebar.hidden = true;
    navigationSplitter.setAttribute("role", "separator");
    navigationSplitter.setAttribute("aria-orientation", "vertical");
    navigationSplitter.setAttribute("aria-label", "Изменить ширину боковой панели материала");
    navigationSplitter.hidden = true;
    sidebar.appendChild(sidebarBody);
    searchToggle.type = "button";
    searchToggle.setAttribute("aria-label", "Найти в материале");
    searchToggle.setAttribute("aria-controls", id + "-search");
    searchToggle.setAttribute("aria-expanded", "false");
    searchToggle.title = "Найти в материале";
    searchToggle.innerHTML = window.RNAssistantIcons.render("search");
    searchInput.type = "search";
    searchInput.id = id + "-search";
    searchInput.maxLength = 160;
    searchInput.placeholder = "Найти в материале";
    searchInput.setAttribute("aria-label", "Найти в материале");
    searchInput.hidden = true;
    searchStatus.setAttribute("role", "status");
    searchStatus.setAttribute("aria-live", "polite");
    searchPrevious.hidden = searchNext.hidden = searchClose.hidden = true;
    search.append(searchToggle, searchInput, searchPrevious, searchNext, searchClose, searchStatus);
    titleBox.append(title, meta);
    if (options.hideTitle) titleBox.hidden = true;
    titleBox.title = (options.title || "") + (options.meta ? "\n" + options.meta : "");
    modes.hidden = true;
    actions.append(providerActions, commonActions);
    header.append(navigationToggle, titleBox, provider, modes, search, actions);
    workspace.append(header, content, statusbar);
    statusbar.append(viewerStatus, statusMeta);
    layout.append(sidebar, navigationSplitter, workspace);
    root.append(layout);
    root.dataset.materialSurfaceKind = options.kind || "material";

    var searchAdapter = null, searchGeneration = 0, searchTimer = 0, searchResult = null;
    var statusListener = null, unsubscribeStatus = null;
    var navigationAvailable = false, navigationOpen = true, compactLayout = null, compactAutoCollapsed = false;
    var panelBinding = null, searchBinding = null, resizeObserver = null;

    function setTitle(nextTitle, nextMeta) {
      title.textContent = nextTitle || "Материал";
      meta.textContent = nextMeta || "";
      meta.hidden = !nextMeta;
      titleBox.title = title.textContent + (nextMeta ? "\n" + nextMeta : "");
    }

    function syncPanelToggle() {
      if (!panelBinding) return;
      panelBinding.sync();
      navigationToggle.hidden = !navigationAvailable;
      navigationToggle.disabled = !navigationAvailable;
    }

    function setNavigationOpen(open) {
      navigationOpen = !!open && navigationAvailable;
      sidebar.hidden = !navigationOpen;
      root.classList.toggle("has-material-sidebar", navigationOpen);
      syncMaterialNavigationSplitter();
      if (navigationAdapter && typeof navigationAdapter.setCollapsed === "function")
        navigationAdapter.setCollapsed(!navigationOpen);
      syncPanelToggle();
    }

    var navigationAdapter = null;
    function setNavigation(adapter) {
      navigationAdapter = adapter || null;
      sidebarBody.replaceChildren();
      if (navigationAdapter && navigationAdapter.element) sidebarBody.appendChild(navigationAdapter.element);
      navigationAvailable = !!(navigationAdapter && navigationAdapter.element &&
        (typeof navigationAdapter.available === "function" ? navigationAdapter.available() : navigationAdapter.available !== false));
      if (navigationAdapter && typeof navigationAdapter.attach === "function") navigationAdapter.attach(sidebarBody);
      setNavigationOpen(navigationAvailable && navigationAdapter.initiallyOpen !== false && !(compactLayout && compactAutoCollapsed));
    }

    function setNavigationAvailability(available) {
      var wasAvailable = navigationAvailable;
      navigationAvailable = !!available && !!(navigationAdapter && navigationAdapter.element);
      if (!navigationAvailable) navigationOpen = false;
      else if (!wasAvailable && compactLayout !== true && navigationAdapter.initiallyOpen !== false) navigationOpen = true;
      sidebar.hidden = !navigationOpen;
      root.classList.toggle("has-material-sidebar", navigationOpen);
      syncMaterialNavigationSplitter();
      if (navigationAdapter && typeof navigationAdapter.setCollapsed === "function")
        navigationAdapter.setCollapsed(!navigationOpen);
      syncPanelToggle();
    }

    function token(name, fallback) {
      var value = Number.parseFloat(getComputedStyle(root).getPropertyValue(name));
      return Number.isFinite(value) && value > 0 ? value : fallback;
    }
    function materialNavigationWidth() {
      return Number.parseFloat(getComputedStyle(layout).getPropertyValue("--material-navigation-width")) || token("--navigation-width-default", 224);
    }
    function materialNavigationBounds() {
      var width = layout.getBoundingClientRect().width || root.clientWidth || 900;
      var minimum = Math.min(token("--navigation-width-min", 180), Math.max(140, width - 280));
      var maximum = Math.max(minimum, Math.min(token("--navigation-width-max", 340), width - 280));
      return { min: minimum, max: maximum };
    }
    function setMaterialNavigationWidth(value, persist) {
      var bounds = materialNavigationBounds();
      var next = Math.round(Math.max(bounds.min, Math.min(bounds.max, Number(value) || materialNavigationWidth())));
      layout.style.setProperty("--material-navigation-width", next + "px");
      if (persist) {
        try { window.localStorage.setItem(materialNavigationWidthKey, String(next)); } catch (error) { /* Storage may be disabled. */ }
      }
      if (window.RNAssistantSplitter) window.RNAssistantSplitter.update(navigationSplitter, next, bounds);
    }
    function syncMaterialNavigationSplitter() {
      navigationSplitter.hidden = !navigationAvailable || !navigationOpen || compactLayout === true;
    }
    function bindMaterialNavigationSplitter() {
      if (!window.RNAssistantSplitter || navigationSplitter.dataset.bound === "true") return;
      window.RNAssistantSplitter.bind(navigationSplitter, {
        getValue: materialNavigationWidth,
        setValue: setMaterialNavigationWidth,
        bounds: materialNavigationBounds,
        defaultValue: function () { return token("--navigation-width-default", 224); },
        step: token("--navigation-resize-step", 16),
        collapse: function () { compactAutoCollapsed = false; setNavigationOpen(false); }
      });
    }

    function syncCompactLayout() {
      var compact = root.clientWidth > 0 && root.clientWidth <= 680;
      if (compactLayout === null && compact && navigationOpen) {
        compactAutoCollapsed = true; setNavigationOpen(false);
      } else if (compactLayout === false && compact && navigationOpen) {
        compactAutoCollapsed = true; setNavigationOpen(false);
      } else if (compactLayout === true && !compact && compactAutoCollapsed) {
        setNavigationOpen(true); compactAutoCollapsed = false;
      }
      compactLayout = compact;
      if (!compact && layout.getBoundingClientRect().width > 0) setMaterialNavigationWidth(materialNavigationWidth(), false);
      syncMaterialNavigationSplitter();
    }

    if (window.RNAssistantPanelToggle && navigationToggle) {
      panelBinding = window.RNAssistantPanelToggle.bind(navigationToggle, {
        side: "left", controls: function () { return sidebar.id; },
        isOpen: function () { return navigationOpen && navigationAvailable; },
        setOpen: function (open) { compactAutoCollapsed = false; setNavigationOpen(open); },
        label: function (open) { return open ? "Скрыть дерево материала" : "Показать дерево материала"; }
      });
    } else {
      navigationToggle.innerHTML = window.RNAssistantIcons.render("panelLeft");
      navigationToggle.addEventListener("click", function () { compactAutoCollapsed = false; setNavigationOpen(!navigationOpen); });
    }

    if (window.RNAssistantNavigationPane && window.RNAssistantNavigationPane.bind) {
      searchBinding = window.RNAssistantNavigationPane.bind(searchToggle, searchInput, search);
    }

    function clearSearchResult() {
      searchResult = null;
      searchStatus.textContent = "";
      searchPrevious.hidden = searchNext.hidden = searchClose.hidden = true;
      searchPrevious.disabled = searchNext.disabled = true;
    }

    function runSearch(direction) {
      if (!searchAdapter) { clearSearchResult(); return; }
      var query = String(searchInput.value || "").trim(), generation = ++searchGeneration;
      if (!query) {
        clearSearchResult();
        Promise.resolve(searchAdapter.search("", 0)).catch(function () {});
        return;
      }
      Promise.resolve(searchAdapter.search(query, direction || 0)).then(function (result) {
        if (generation !== searchGeneration) return;
        searchResult = result || { count: 0, index: 0 };
        var count = Number(searchResult.count || 0), index = Number(searchResult.index || 0);
        searchStatus.textContent = count ? Math.max(0, index + 1) + " из " + count : "Нет совпадений";
        searchPrevious.hidden = searchNext.hidden = searchClose.hidden = false;
        searchPrevious.disabled = searchNext.disabled = count < 2;
      }).catch(function (error) {
        if (generation !== searchGeneration) return;
        clearSearchResult(); searchClose.hidden = false;
        searchStatus.textContent = error && (error.detail || error.message) || "Поиск недоступен";
      });
    }

    searchInput.addEventListener("input", function () {
      window.clearTimeout(searchTimer);
      searchTimer = window.setTimeout(function () { runSearch(0); }, 120);
    });
    searchPrevious.addEventListener("click", function () { runSearch(-1); });
    searchNext.addEventListener("click", function () { runSearch(1); });
    searchClose.addEventListener("click", function () {
      searchInput.value = ""; ++searchGeneration; clearSearchResult();
      if (searchAdapter) Promise.resolve(searchAdapter.search("", 0)).catch(function () {});
      if (searchBinding) searchBinding.close(false);
      searchToggle.focus();
    });
    searchToggle.addEventListener("click", function (event) {
      if (searchAdapter && typeof searchAdapter.open === "function") {
        event.preventDefault(); event.stopImmediatePropagation(); searchAdapter.open();
      }
    }, true);
    searchToggle.disabled = true;

    function setSearch(adapter) {
      searchAdapter = adapter || null;
      search.hidden = !searchAdapter;
      searchToggle.disabled = !searchAdapter;
      searchToggle.title = searchAdapter ? "Найти в материале" : "Поиск недоступен для этого представления";
      if (!searchAdapter) {
        clearSearchResult();
        if (searchBinding) searchBinding.close(true);
      }
    }

    function setViewerStatus(value, error) {
      viewerStatus.replaceChildren();
      if (value && value.nodeType === 1) viewerStatus.appendChild(value);
      else viewerStatus.textContent = value || "";
      viewerStatus.title = typeof value === "string" ? value : "";
      viewerStatus.dataset.error = error ? "true" : "false";
      viewerStatus.setAttribute("role", error ? "alert" : "status");
      statusbar.hidden = !viewerStatus.textContent && !statusMeta.textContent;
    }

    function setStatusMeta(value) {
      statusMeta.textContent = value || "";
      statusbar.hidden = !viewerStatus.textContent && !statusMeta.textContent;
    }

    function setCommonActions(value) {
      commonActions.replaceChildren();
      if (!value) return;
      if (value.nodeType === 1) commonActions.appendChild(value);
      else if (Array.isArray(value)) value.forEach(function (item) { if (item) commonActions.appendChild(item); });
    }

    function setProviderToolbar(value) {
      provider.replaceChildren();
      if (value) provider.appendChild(value);
      provider.hidden = !value;
    }

    function hoist(source, destination) {
      if (!source || !destination || source === destination) return false;
      destination.appendChild(source);
      source.classList.add("material-surface-hoisted");
      return true;
    }

    function hideEmptyToolbars(viewerRoot) {
      [".rn-markdown-viewer-toolbar", ".rn-text-viewer-toolbar", ".rn-pdf-viewer-toolbar",
        ".rn-resource-viewer-toolbar", ".rn-json-toolbar", ".rn-task-list-toolbar", ".material-viewer-toolbar"].forEach(function (selector) {
        viewerRoot.querySelectorAll(selector).forEach(function (toolbar) {
          if (!toolbar.querySelector("button:not([hidden]), input:not([hidden]), select:not([hidden]), [role=status]:not(:empty)"))
            toolbar.hidden = true;
        });
      });
    }

    function attachViewer(target, kind, viewerOptions, controller) {
      target.__rnMaterialSurface = api;
      modes.replaceChildren();
      providerActions.replaceChildren();
      viewerStatus.replaceChildren();
      if (searchBinding) searchBinding.close(true);
      clearSearchResult();
      if (unsubscribeStatus) unsubscribeStatus();
      unsubscribeStatus = null;
      var viewerRoot = controller && controller.element || target.firstElementChild || target;
      var chrome = controller && controller.chrome || {};
      var modeNode = chrome.modes || null;
      if (!modeNode) {
        var selector = kind === "markdown" ? ".rn-markdown-viewer-tabs" : kind === "pdf" ? ".rn-pdf-viewer-tabs" :
          kind === "json" ? ".rn-json-modes" : kind === "sequence" ? ".material-viewer-toolbar .view-switch" : null;
        if (selector) modeNode = viewerRoot.querySelector(selector);
      }
      hoist(modeNode, modes);
      modes.hidden = !modeNode;

      var actionNodes = [];
      if (chrome.actions) actionNodes = Array.isArray(chrome.actions) ? chrome.actions : [chrome.actions];
      else {
        var actionSelector = kind === "markdown" ? ".rn-markdown-viewer-actions > *" : kind === "text" ? ".rn-text-viewer-actions > *" :
          kind === "pdf" ? ".rn-pdf-viewer-actions > *" : kind === "json" ? ".rn-json-actions > *" :
          kind === "task_list" ? ".rn-task-list-toolbar > *" : kind === "image" ? ".rn-resource-viewer-toolbar > button[data-viewer-action=copy], .rn-resource-viewer-toolbar > [role=status]" : null;
        if (actionSelector) actionNodes = Array.prototype.slice.call(viewerRoot.querySelectorAll(actionSelector));
      }
      actionNodes = actionNodes.filter(function (item) {
        var label = item.getAttribute("aria-label") || item.title || item.textContent || "";
        return item.getAttribute("data-viewer-action") !== "download" && !/^\s*Скачать/.test(label);
      });
      actionNodes.forEach(function (item) { hoist(item, providerActions); });

      var statusNode = chrome.status || null;
      if (!statusNode) {
        var statusSelector = kind === "markdown" ? ".rn-markdown-viewer-note" : kind === "text" ? ".rn-text-viewer-status" :
          kind === "pdf" ? ".rn-pdf-viewer-warning" : kind === "json" ? ".rn-json-status" :
          kind === "image" ? ".rn-resource-viewer-status" : null;
        if (statusSelector) statusNode = viewerRoot.querySelector(statusSelector);
      }
      if (statusNode && (statusNode.textContent || statusNode.hidden === false)) setViewerStatus(statusNode);
      else setViewerStatus("");

      if (kind === "sequence" && controller && typeof controller.setNavigationOrientation === "function")
        controller.setNavigationOrientation("vertical");
      var navigation = chrome.navigation || null;
      if (!navigation) {
        var navigationSelector = kind === "markdown" ? ".rn-markdown-viewer-navigation" : kind === "pdf" ? ".rn-pdf-thumbnail-rail" :
          kind === "image" ? ".rn-sequence-rail" : kind === "sequence" ? ".collection-rail" : null;
        var navigationElement = navigationSelector ? viewerRoot.querySelector(navigationSelector) : null;
        navigation = navigationElement && { element: navigationElement, available: true } || null;
      }
      setNavigation(navigation);

      viewerRoot.querySelectorAll(".rn-text-viewer-find, .rn-markdown-viewer-navigation-toggle").forEach(function (item) { item.hidden = true; });
      viewerRoot.querySelectorAll(".rn-text-viewer-search-result, .rn-text-viewer-action-status").forEach(function (item) {
        if (!item.closest(".material-surface-actions")) item.hidden = true;
      });
      hideEmptyToolbars(viewerRoot);

      if (controller && typeof controller.search === "function") setSearch({ search: controller.search });
      else if (chrome.search) setSearch(chrome.search);
      else setSearch(null);

      statusListener = function (text, error) { setViewerStatus(text || "", error); };
      if (chrome.subscribeStatus) unsubscribeStatus = chrome.subscribeStatus(statusListener);
      else if (controller && typeof controller.subscribeStatus === "function") unsubscribeStatus = controller.subscribeStatus(statusListener);
      if (navigation && typeof navigation.available === "function") {
        setNavigationAvailability(navigation.available());
        if (typeof navigation.subscribeAvailability === "function") navigation.subscribeAvailability(setNavigationAvailability);
      }
      return api;
    }

    resizeObserver = typeof window.ResizeObserver === "function" ? new window.ResizeObserver(syncCompactLayout) : null;
    if (resizeObserver) resizeObserver.observe(root); else window.addEventListener("resize", syncCompactLayout);
    try {
      var savedNavigationWidth = Number(window.localStorage.getItem(materialNavigationWidthKey));
      if (savedNavigationWidth > 0) layout.style.setProperty("--material-navigation-width", savedNavigationWidth + "px");
    } catch (error) { /* Storage may be disabled. */ }
    syncCompactLayout();
    bindMaterialNavigationSplitter();
    syncPanelToggle();
    if (options.statusMeta) setStatusMeta(options.statusMeta); else statusbar.hidden = true;

    var api = {
      element: root, content: content, header: header, workspace: workspace, titleBox: titleBox, actions: commonActions, provider: provider,
      modesHost: modes, providerActions: providerActions, sidebarBody: sidebarBody,
      setTitle: setTitle, setStatus: setViewerStatus, setStatusMeta: setStatusMeta, setProviderToolbar: setProviderToolbar,
      setCommonActions: setCommonActions, setSearch: setSearch, setNavigation: setNavigation,
      setNavigationAvailability: setNavigationAvailability, attachViewer: attachViewer,
      destroy: function () {
        ++searchGeneration; window.clearTimeout(searchTimer);
        if (unsubscribeStatus) unsubscribeStatus();
        if (resizeObserver) resizeObserver.disconnect(); else window.removeEventListener("resize", syncCompactLayout);
        if (searchBinding) searchBinding.close(true);
        root.replaceChildren();
      }
    };
    if (options.actions) setCommonActions(options.actions);
    if (options.modes) hoist(options.modes, modes);
    return api;
  }

  window.RNAssistantMaterialSurface = { create: create };
}());
