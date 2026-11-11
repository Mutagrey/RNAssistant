function setMobileNavigationOpen(open) {
  if (window.RNAssistantShellLayout) window.RNAssistantShellLayout.setNavigationOpen(open);
}

function switchTab(name, options) {
  if (window.RNAssistantWindow && RNAssistantWindow.inFull(function () { switchTab(name, options); })) return;
  var workspaceView = RNAssistantDockWorkspace.sectionView(name) || (["settings", "request", "diagnostics"].indexOf(name) >= 0 ? name : null);
  if (["settings", "request", "diagnostics"].indexOf(name) >= 0) {
    // These are workspace views. Keep existing callers, with one placement path.
    name = "chat";
    options = { preserveChatOwner: true };
  }
  if (window.RNAssistantShellLayout && window.RNAssistantShellLayout.mode() !== "wide") setMobileNavigationOpen(false);
  if ($("tab-instructions") && $("tab-instructions").classList.contains("active") && name !== "instructions" && typeof syncSelectedLibraryItem === "function") {
    syncSelectedLibraryItem();
  }
  if (name === "tools") name = "instructions";
  if (name === "html") name = "artifacts";
  var section = name === "instructions" ? "library" : name;
  var active = document.querySelector(".panel.active");
  var changed = !active || active.id !== "tab-" + name;
  Array.prototype.slice.call(document.querySelectorAll(".shell [data-section]")).forEach(function (tab) {
    var selected = tab.dataset.section === section && (!tab.dataset.pluginId || window.RNAssistantPlugins.selectedId() === tab.dataset.pluginId);
    tab.classList.toggle("active", selected);
    if (selected) tab.setAttribute("aria-current", "page");
    else tab.removeAttribute("aria-current");
  });
  Array.prototype.slice.call(document.querySelectorAll(".panel")).forEach(function (panel) {
    panel.classList.toggle("active", panel.id === "tab-" + name);
  });
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
  if (window.RNAssistantDockWorkspace) window.RNAssistantDockWorkspace.onTabChange(name);
  if (!changed) {
    if (workspaceView) RNAssistantDockWorkspace.show(workspaceView);
    return;
  }
  if (name === "chat") {
    if (window.RNAssistantProjectChats) window.RNAssistantProjectChats.show(options);
    renderChatSessions();
    renderMessages();
    renderContext();
    renderContextMeter();
    renderModelControls();
  } else if (name === "instructions" && typeof renderInstructions === "function") {
    renderInstructions();
  } else if (name === "plugins" && window.RNAssistantPlugins) {
    window.RNAssistantPlugins.show();
  } else if (name === "vba" && typeof renderVbaProject === "function") {
    renderVbaProject();
    if (typeof updateVbaMacroRunState === "function") updateVbaMacroRunState();
  }
  if (workspaceView) RNAssistantDockWorkspace.show(workspaceView);
}

document.addEventListener("DOMContentLoaded", function () {
  document.querySelector(".content").addEventListener("click", function (event) {
    var creationMenu = event.target.closest(".product-create-menu");
    if (creationMenu && event.target.closest("button")) {
      creationMenu.removeAttribute("open");
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

  Array.prototype.slice.call(document.querySelectorAll(".shell [data-section]")).forEach(function (tab) {
    tab.addEventListener("click", function () {
      if (tab.dataset.tab === "diagnostics") { RNAssistantDiagnostics.openCurrent(); return; }
      if (tab.dataset.tab === "settings") { switchTab("settings"); return; }
      RNAssistantShellLayout.selectSection(function () { switchTab(tab.dataset.defaultTab || tab.dataset.tab); }, tab.getAttribute("aria-current") === "page");
    });
  });

  bindChatActions();
  if (window.RNAssistantRailPopovers) window.RNAssistantRailPopovers.bind();
  if (window.RNAssistantPlugins) window.RNAssistantPlugins.bind();
  bindVbaActions();
  bindHtmlWorkspaceActions();
  bindModelActions();
  bindSettingsActions();
  bindLogActions();
  bindDiagnosticsActions();
  bindTrajectoryActions();
  bindToolActions();
  bindSkillActions();
  RNAssistantNavigationPane.bindSearches(document);
  if (typeof initializeSplitPanes === "function") initializeSplitPanes();
  if (window.RNAssistantChatInspector) window.RNAssistantChatInspector.bind();
  window.RNAssistantMaterialView.register(window.RNAssistantDockWorkspace);
  window.RNAssistantHtmlWorkspaceSurface.register(window.RNAssistantDockWorkspace);
  window.RNAssistantLibraryView.register(window.RNAssistantDockWorkspace);
  window.RNAssistantVbaView.register(window.RNAssistantDockWorkspace);
  window.RNAssistantConversationPanel.register(window.RNAssistantDockWorkspace);
  window.RNAssistantExternalBrowser.register(window.RNAssistantDockWorkspace);
  window.RNAssistantDockWorkspace.bind();
  window.RNAssistantShellLayout.refresh();
  window.addEventListener("load", function () {
    if (window.hljs) {
      highlightAllCode();
    }
  });

  initialize().then(function () { RNAssistantActiveRuns.start(); if (window.RNAssistantPlugins) window.RNAssistantPlugins.refresh().catch(function (error) { document.getElementById("pluginStatus").textContent = error.message; }); });
  state.syncTimer = window.setInterval(synchronizeChatState, 60000);
});
