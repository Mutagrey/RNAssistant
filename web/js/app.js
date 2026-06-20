function switchTab(name) {
  Array.prototype.slice.call(document.querySelectorAll(".tab")).forEach(function (tab) {
    tab.classList.toggle("active", tab.dataset.tab === name);
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
  ["focusin", "focusout", "selectionchange", "mouseup", "keyup"].forEach(function (name) {
    document.addEventListener(name, scheduleFocusStateReport);
  });
  window.addEventListener("focus", scheduleFocusStateReport);
  window.addEventListener("blur", scheduleFocusStateReport);
  scheduleFocusStateReport();

  Array.prototype.slice.call(document.querySelectorAll(".tab")).forEach(function (tab) {
    tab.addEventListener("click", function () { switchTab(tab.dataset.tab); });
  });

  $("helpButton").addEventListener("click", showHelp);
  $("closeHelpButton").addEventListener("click", hideHelp);
  $("helpModal").addEventListener("click", function (event) {
    if (event.target === $("helpModal")) {
      hideHelp();
    }
  });
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape") {
      hideHelp();
    }
  });

  bindChatActions();
  bindContextActions();
  bindVbaActions();
  bindModelActions();
  bindSettingsActions();
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
});
