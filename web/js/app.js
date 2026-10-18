function setMobileNavigationOpen(open) {
  var shell = document.querySelector(".shell");
  var button = $("productMobileMenuButton");
  if (!shell || !button) return;
  shell.classList.toggle("navigation-open", !!open);
  button.setAttribute("aria-expanded", open ? "true" : "false");
  button.setAttribute("aria-label", open ? "Закрыть навигацию" : "Открыть навигацию");
  button.setAttribute("title", open ? "Закрыть навигацию" : "Открыть навигацию");
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
    var selected = tab.dataset.section === section;
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
  } else if (name === "workspace" && window.RNAssistantWorkspace) {
    window.RNAssistantWorkspace.show();
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
    var treeControl = event.target.closest(".chat-document-toggle, .chat-document-delete, .chat-edit, .chat-delete");
    if (window.innerWidth <= 620 && document.querySelector(".shell").classList.contains("navigation-open") &&
        (event.target.closest(".global-nav .settings-nav-button, .global-nav .dt-tabs button") ||
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
  if (window.RNAssistantDataTemplates) window.RNAssistantDataTemplates.bind();
  if (window.RNAssistantWorkspace) window.RNAssistantWorkspace.bind();
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
  if (typeof initializeSplitPanes === "function") {
    initializeSplitPanes();
  }
  window.addEventListener("load", function () {
    if (window.hljs) {
      highlightAllCode();
    }
  });

  initialize();
  state.syncTimer = window.setInterval(synchronizeChatState, 60000);
});
