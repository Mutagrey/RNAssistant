function setMobileNavigationOpen(open) {
  var shell = document.querySelector(".shell");
  var button = $("productMobileMenuButton");
  if (!shell || !button) return;
  shell.classList.toggle("navigation-open", !!open);
  button.setAttribute("aria-expanded", open ? "true" : "false");
  button.setAttribute("aria-label", open ? "Закрыть навигацию" : "Открыть навигацию");
  button.setAttribute("title", open ? "Закрыть навигацию" : "Открыть навигацию");
}

function bindProductNavSearches() {
  document.querySelectorAll("[data-nav-search-toggle]").forEach(function (button) {
    var input = document.getElementById(button.dataset.navSearchToggle);
    var heading = button.closest(".product-search-heading");
    if (!input || !heading) return;
    var mirror = input.dataset.navSearchMirror && document.getElementById(input.dataset.navSearchMirror);
    function close(clear) {
      if (clear && input.value) {
        input.value = "";
        input.dispatchEvent(new Event("input", { bubbles: true }));
      }
      input.hidden = true;
      heading.classList.remove("searching");
      button.setAttribute("aria-expanded", "false");
      button.classList.toggle("has-search-query", !!input.value.trim());
    }
    button.addEventListener("pointerdown", function (event) {
      if (document.activeElement === input) event.preventDefault();
    });
    button.addEventListener("click", function () {
      if (!input.hidden) { close(true); button.focus(); return; }
      input.hidden = false;
      heading.classList.add("searching");
      button.setAttribute("aria-expanded", "true");
      input.focus();
    });
    input.addEventListener("input", function () {
      button.classList.toggle("has-search-query", !!input.value.trim());
      if (mirror && mirror.value !== input.value) {
        mirror.value = input.value;
        mirror.dispatchEvent(new Event("input", { bubbles: true }));
      }
    });
    if (mirror) mirror.addEventListener("input", function () {
      input.value = mirror.value;
      button.classList.toggle("has-search-query", !!input.value.trim());
    });
    input.addEventListener("blur", function () { close(false); });
    input.addEventListener("keydown", function (event) {
      if (event.key !== "Escape") return;
      event.preventDefault(); close(true); button.focus();
    });
  });
}

function switchTab(name) {
  setMobileNavigationOpen(false);
  if ($("tab-instructions") && $("tab-instructions").classList.contains("active") && name !== "instructions" && typeof syncSelectedLibraryItem === "function") {
    syncSelectedLibraryItem();
  }
  if (name === "tools") name = "instructions";
  if (name === "html") name = "artifacts";
  var section = name === "instructions" ? "library" : name;
  var active = document.querySelector(".panel.active");
  var changed = !active || active.id !== "tab-" + name;
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
  if (typeof activateCodeEditorsForTab === "function") {
    activateCodeEditorsForTab(name);
  }
  if (typeof refreshSplitPanes === "function") {
    refreshSplitPanes();
  }
  if (!changed) return;
  if (name === "chat") {
    renderChatSessions();
    renderMessages();
    renderContext();
    renderContextMeter();
    renderModelControls();
  } else if (name === "instructions" && typeof renderInstructions === "function") {
    renderInstructions();
  } else if (name === "settings") {
    renderSettings();
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
  var mobileMenuButton = document.getElementById("productMobileMenuButton");
  if (mobileMenuButton) mobileMenuButton.addEventListener("click", function () {
    setMobileNavigationOpen(!document.querySelector(".shell").classList.contains("navigation-open"));
  });
  window.addEventListener("resize", function () {
    if (window.innerWidth > 620) setMobileNavigationOpen(false);
  });
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
    if (window.innerWidth <= 620 && document.querySelector(".shell").classList.contains("navigation-open") &&
        (event.target.closest(".global-nav .dt-tabs button") ||
          (treeItem && !treeControl))) {
      setMobileNavigationOpen(false);
    }
  });
  try {
    state.chatSidebarHidden = window.localStorage.getItem("rnassistant.chat.sidebar.hidden") === "1";
    state.htmlWorkspaceSidebarHidden = window.localStorage.getItem("rnassistant.artifacts.sidebar.hidden") === "1";
    state.vbaSidebarHidden = window.localStorage.getItem("rnassistant.vba.sidebar.hidden") === "1";
  } catch (error) {
    state.chatSidebarHidden = false;
    state.htmlWorkspaceSidebarHidden = false;
    state.vbaSidebarHidden = false;
  }
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
