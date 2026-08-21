function switchTab(name) {
  var libraryNames = ["instructions", "tools"];
  var codeNames = ["vba", "html"];
  var section = libraryNames.indexOf(name) >= 0 ? "library" : (codeNames.indexOf(name) >= 0 ? "code" : name);
  Array.prototype.slice.call(document.querySelectorAll(".tab")).forEach(function (tab) {
    tab.classList.toggle("active", tab.dataset.section === section);
  });
  Array.prototype.slice.call(document.querySelectorAll(".section-tab")).forEach(function (tab) {
    tab.classList.toggle("active", tab.dataset.tab === name);
  });
  Array.prototype.slice.call(document.querySelectorAll(".section-tabs")).forEach(function (tabs) {
    tabs.classList.toggle("hidden", tabs.dataset.sectionTabs !== section);
  });
  var sectionRoot = section === "library" ? $("libraryRootTab") : (section === "code" ? $("codeRootTab") : null);
  if (sectionRoot) sectionRoot.dataset.activeTab = name;
  Array.prototype.slice.call(document.querySelectorAll(".panel")).forEach(function (panel) {
    panel.classList.toggle("active", panel.id === "tab-" + name);
  });
  if (typeof refreshCodeEditors === "function") {
    refreshCodeEditors();
  }
  if (typeof refreshSplitPanes === "function") {
    refreshSplitPanes();
  }
  if (name === "logs" && typeof runtimeLogVisible === "function" && runtimeLogVisible()) {
    refreshRuntimeLog();
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
  scheduleFocusStateReport();

  Array.prototype.slice.call(document.querySelectorAll(".tab")).forEach(function (tab) {
    tab.addEventListener("click", function () {
      switchTab(tab.dataset.section === "library" || tab.dataset.section === "code"
        ? (tab.dataset.activeTab || tab.dataset.defaultTab || "instructions")
        : tab.dataset.tab);
    });
  });
  Array.prototype.slice.call(document.querySelectorAll(".section-tab")).forEach(function (tab) {
    tab.addEventListener("click", function () { switchTab(tab.dataset.tab); });
  });

  bindChatActions();
  bindContextActions();
  bindVbaActions();
  bindHtmlWorkspaceActions();
  bindModelActions();
  bindSettingsActions();
  bindLogActions();
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
  state.syncTimer = window.setInterval(synchronizeChatState, 5000);
});
