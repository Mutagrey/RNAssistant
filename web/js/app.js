function setMobileNavigationOpen(open) {
  if (window.RNAssistantShellLayout) window.RNAssistantShellLayout.setNavigationOpen(open);
}

function bindProductNavSearches() {
  document.querySelectorAll("[data-nav-search-toggle]").forEach(function (button) {
    var input = document.getElementById(button.dataset.navSearchToggle);
    var heading = button.closest(".product-search-heading");
    if (!input || !heading) return;
    var defaultTitle = button.title || button.getAttribute("aria-label") || "Поиск";
    function updateQueryState() {
      var query = input.value.trim();
      button.classList.toggle("has-search-query", !!query);
      button.title = query ? "Фильтр: " + input.value + " · изменить поиск" : defaultTitle;
      button.setAttribute("aria-description", query ? "Активен фильтр: " + input.value : "");
    }
    function close(clear) {
      if (clear && input.value) {
        input.value = "";
        input.dispatchEvent(new Event("input", { bubbles: true }));
      }
      input.hidden = true;
      heading.classList.remove("searching");
      button.setAttribute("aria-expanded", "false");
      updateQueryState();
    }
    button.addEventListener("pointerdown", function (event) {
      if (document.activeElement === input) event.preventDefault();
    });
    button.addEventListener("click", function () {
      if (input.disabled) { close(true); return; }
      if (!input.hidden) { close(true); button.focus(); return; }
      input.hidden = false;
      heading.classList.add("searching");
      button.setAttribute("aria-expanded", "true");
      input.focus();
    });
    input.addEventListener("input", updateQueryState);
    input.addEventListener("blur", function () { close(false); });
    input.addEventListener("keydown", function (event) {
      if (event.key !== "Escape") return;
      event.preventDefault(); close(true); button.focus();
    });
  });
}

function switchTab(name) {
  if (window.RNAssistantShellLayout && window.RNAssistantShellLayout.mode() !== "wide") setMobileNavigationOpen(false);
  if ($("tab-instructions") && $("tab-instructions").classList.contains("active") && name !== "instructions" && typeof syncSelectedLibraryItem === "function") {
    syncSelectedLibraryItem();
  }
  if (name === "tools") name = "instructions";
  if (name === "html") name = "artifacts";
  if (window.RNAssistantChatBrowser) window.RNAssistantChatBrowser.onTabChange(name);
  var section = name === "instructions" ? "library" : name;
  var active = document.querySelector(".panel.active");
  var changed = !active || active.id !== "tab-" + name;
  if (changed && active && active.id === "tab-artifacts" && typeof deactivateHtmlWorkspaceInspector === "function") {
    deactivateHtmlWorkspaceInspector();
  }
  if (changed && active && active.id === "tab-settings" && typeof discardModelCatalogPreview === "function") {
    discardModelCatalogPreview();
  }
  Array.prototype.slice.call(document.querySelectorAll(".section-rail [data-section]")).forEach(function (tab) {
    var selected = tab.dataset.section === section && (!tab.dataset.pluginId || window.RNAssistantPlugins.selectedId() === tab.dataset.pluginId);
    tab.classList.toggle("active", selected);
    if (selected) tab.setAttribute("aria-current", "page");
    else tab.removeAttribute("aria-current");
  });
  Array.prototype.slice.call(document.querySelectorAll(".panel")).forEach(function (panel) {
    panel.classList.toggle("active", panel.id === "tab-" + name);
  });
  if (changed && active && active.id === "tab-plugins" && name !== "plugins" && window.RNAssistantShellLayout) {
    window.RNAssistantShellLayout.restoreAfterPlugin();
  }
  var selectedPanel = document.getElementById("tab-" + name);
  if (changed && selectedPanel && document.querySelector(".shell.layout-ready") &&
      !window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
    selectedPanel.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 140, easing: "cubic-bezier(.2, .8, .2, 1)" });
  }
  if (typeof activateCodeEditorsForTab === "function") {
    activateCodeEditorsForTab(name);
  }
  if (typeof refreshSplitPanes === "function") {
    refreshSplitPanes();
  }
  if (!changed) return;
  if (name === "chat") {
    if (window.RNAssistantProjectChats) window.RNAssistantProjectChats.show();
    renderChatSessions();
    renderMessages();
    renderContext();
    renderContextMeter();
    renderModelControls();
  } else if (name === "instructions" && typeof renderInstructions === "function") {
    renderInstructions();
  } else if (name === "settings") {
    renderSettings();
    loadSettingsModelCatalogIfNeeded();
  } else if (name === "plugins" && window.RNAssistantPlugins) {
    window.RNAssistantPlugins.show();
  } else if (name === "artifacts" && typeof renderHtmlWorkspace === "function") {
    renderHtmlWorkspace();
    if (window.RNAssistantArtifactCatalog) window.RNAssistantArtifactCatalog.show();
  } else if (name === "vba" && typeof renderVbaProject === "function") {
    renderVbaProject();
    if (typeof updateVbaMacroRunState === "function") updateVbaMacroRunState();
  }
}

document.addEventListener("DOMContentLoaded", function () {
  var chatScopeIcon = document.getElementById("chatScopeIcon");
  if (chatScopeIcon) chatScopeIcon.innerHTML = window.RNAssistantNavigationTree.icon("office");
  document.querySelector(".content").addEventListener("click", function (event) {
    var creationMenu = event.target.closest(".product-create-menu, .product-material-menu");
    if (creationMenu && event.target.closest("button")) {
      creationMenu.removeAttribute("open");
      if (creationMenu.classList.contains("product-create-menu") && window.innerWidth <= 620) setMobileNavigationOpen(false);
    }
    var officeMenu = document.querySelector(".chat-office-launch-list[open]");
    if (officeMenu && !event.target.closest(".chat-office-launch-list") && !officeMenu.contains(event.target)) {
      officeMenu.open = false;
      state.officeHostLaunchOpen = false;
    }
    var treeItem = event.target.closest(".global-nav [role='treeitem']");
    var treeControl = event.target.closest(".navigation-tree-expander, .tree-action");
    if (window.innerWidth <= 820 && document.querySelector(".shell").classList.contains("navigation-open") &&
        (event.target.closest(".global-nav .dt-tabs button") ||
          (treeItem && !treeControl))) {
      setMobileNavigationOpen(false);
    }
  });
  ["focusin", "focusout", "selectionchange", "mouseup", "keyup"].forEach(function (name) {
    document.addEventListener(name, scheduleFocusStateReport);
  });
  window.addEventListener("focus", scheduleFocusStateReport);
  window.addEventListener("blur", scheduleFocusStateReport);
  window.addEventListener("focus", synchronizeChatState);
  document.addEventListener("visibilitychange", function () {
    if (!document.hidden) synchronizeChatState();
  });
  scheduleFocusStateReport();

  Array.prototype.slice.call(document.querySelectorAll(".section-rail [data-section]")).forEach(function (tab) {
    tab.addEventListener("click", function () {
      switchTab(tab.dataset.defaultTab || tab.dataset.tab);
    });
  });

  bindChatActions();
  if (window.RNAssistantRailPopovers) window.RNAssistantRailPopovers.bind();
  if (window.RNAssistantPlugins) window.RNAssistantPlugins.bind();
  bindContextActions();
  bindVbaActions();
  bindHtmlWorkspaceActions();
  bindModelActions();
  bindSettingsActions();
  bindLogActions();
  bindDiagnosticsActions();
  bindTrajectoryActions();
  bindToolActions();
  bindSkillActions();
  bindProductNavSearches();
  if (window.RNAssistantChatInspector) window.RNAssistantChatInspector.bind();
  if (window.RNAssistantChatBrowser) window.RNAssistantChatBrowser.bind();
  if (typeof initializeSplitPanes === "function") {
    initializeSplitPanes();
  }
  window.addEventListener("load", function () {
    if (window.hljs) {
      highlightAllCode();
    }
  });

  initialize().then(function () { if (window.RNAssistantPlugins) window.RNAssistantPlugins.refresh().catch(function (error) { document.getElementById("pluginStatus").textContent = error.message; }); });
  state.syncTimer = window.setInterval(synchronizeChatState, 60000);
});
