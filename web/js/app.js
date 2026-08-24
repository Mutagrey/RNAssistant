function switchTab(name) {
  if ($("tab-instructions") && $("tab-instructions").classList.contains("active") && name !== "instructions" && typeof syncSelectedLibraryItem === "function") {
    syncSelectedLibraryItem();
  }
  if (name === "tools") name = "instructions";
  if (name === "html") name = "artifacts";
  var section = name === "instructions" ? "library" : name;
  Array.prototype.slice.call(document.querySelectorAll(".tab")).forEach(function (tab) {
    tab.classList.toggle("active", tab.dataset.section === section);
  });
  Array.prototype.slice.call(document.querySelectorAll(".panel")).forEach(function (panel) {
    panel.classList.toggle("active", panel.id === "tab-" + name);
  });
  if (typeof refreshCodeEditors === "function") {
    refreshCodeEditors();
  }
  if (typeof refreshSplitPanes === "function") {
    refreshSplitPanes();
  }
}

document.addEventListener("DOMContentLoaded", function () {
  try {
    state.chatSidebarHidden = window.localStorage.getItem("rnassistant.chat.sidebar.hidden") === "1";
  } catch (error) {
    state.chatSidebarHidden = false;
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

  Array.prototype.slice.call(document.querySelectorAll(".tab")).forEach(function (tab) {
    tab.addEventListener("click", function () {
      switchTab(tab.dataset.defaultTab || tab.dataset.tab);
    });
  });

  bindChatActions();
  bindContextActions();
  bindVbaActions();
  bindHtmlWorkspaceActions();
  bindModelActions();
  bindSettingsActions();
  bindLogActions();
  bindDiagnosticsActions();
  bindToolActions();
  bindSkillActions();
  if (typeof initializeSplitPanes === "function") {
    initializeSplitPanes();
  }
  if (typeof initializeCodeEditors === "function") {
    initializeCodeEditors();
  }

  window.addEventListener("load", function () {
    if (window.hljs) {
      highlightAllCode();
    }
  });

  initialize();
  state.syncTimer = window.setInterval(synchronizeChatState, 15000);
});
