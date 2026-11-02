(function () {
  "use strict";
  // Owns routes and presentation identity only. Data and rendering stay with readers.
  var requestScope = null, journalKey = "", selected = null, returnFocus = null;
  var routing = false;
  function enter() { routing = true; try { switchTab("diagnostics"); } finally { routing = false; } }
  function value(item, name) { return item && (item[name] !== undefined ? item[name] : item[name[0].toUpperCase() + name.slice(1)]); }
  function source() {
    var workspace = window.RNAssistantWorkspaceChat, owner = workspace ? workspace.owner() : "office";
    if (owner !== "office") {
      var snapshot = workspace.snapshot(), project = value(snapshot, "project"), run = value(snapshot, "state");
      return { owner: owner, workspaceId: value(project, "workspaceId"), chatId: value(snapshot, "sessionId"),
        label: value(run, "title") || "Проектный чат", document: value(project, "name") || value(project, "rootPath") || "Проект",
        runId: value(run, "runId") };
    }
    var chat = typeof activeChatSummary === "function" ? activeChatSummary() : null;
    return { owner: "office", workspaceId: null, chatId: state.activeChatId,
      label: value(chat, "title") || "Чат", document: value(chat, "documentName") || state.title || "Документ Office",
      runId: value(state.activeRunViewState, "runId") };
  }
  function key(target) { return JSON.stringify([target.owner, target.workspaceId || null, target.chatId || target.sessionId || null]); }
  function scope(target) {
    selected = Object.assign({}, target || source());
    var current = source(), same = key(selected) === key(current);
    $("diagnosticsScope").textContent = selected.chatId || selected.sessionId ?
      [(same ? current.document : selected.document) || "Сохранённый источник", (same ? current.label : selected.label) || selected.chatId,
        selected.runId ? "Запуск " + selected.runId : "История чата"].filter(Boolean).join(" · ") :
      "Выберите чат, чтобы разобрать его сохранённые запросы и события.";
  }
  function display() {
    var tab = state.diagnosticsTab || "runs";
    document.querySelectorAll(".diagnostics-tab-button").forEach(function (button) {
      var active = button.dataset.diagnosticsTab === tab;
      button.classList.toggle("active", active);
      if (active) button.setAttribute("aria-current", "page"); else button.removeAttribute("aria-current");
    });
    document.querySelectorAll("[data-diagnostics-panel]").forEach(function (panel) {
      panel.classList.toggle("active", panel.dataset.diagnosticsPanel === tab);
    });
    document.querySelectorAll("[data-diagnostics-application]").forEach(function (panel) {
      panel.classList.toggle("active", tab === "application" && panel.dataset.diagnosticsApplication === $("diagnosticsAppView").value);
    });
    $("diagnosticsAppControls").classList.toggle("hidden", tab !== "application");
    $("diagnosticsScope").classList.toggle("hidden", tab !== "runs");
    $("diagnosticsReturnRequest").classList.toggle("hidden", !requestScope || tab !== "runs");
  }
  function show(tab, initialize) {
    var next = ["runs", "model-lab", "application"].indexOf(tab) >= 0 ? tab : "runs";
    if (next !== "runs" && window.RNAssistantTrajectory) RNAssistantTrajectory.pause();
    state.diagnosticsTab = next; display();
    if (next === "runs") {
      if (initialize !== false && journalKey !== key(source())) {
        journalKey = key(source()); scope(source()); setTrajectoryDiagnosticsMode("runs", true);
      } else if (window.RNAssistantTrajectory) RNAssistantTrajectory.resume();
    } else if (next === "model-lab") openModelDiagnosticsLab(false);
    else { performanceDiagnostics.render(); requestAnimationFrame(updateLogScrollButton); }
  }
  function open(tab) {
    enter(); show(tab || state.diagnosticsTab || "runs");
  }
  function showRequest(target) {
    returnFocus = document.activeElement; requestScope = Object.assign({}, target);
    if (window.RNAssistantTrajectory) RNAssistantTrajectory.pause();
    switchTab("request"); display();
  }
  function returnToJournal(target) {
    if (journalKey === key(target) && selected && selected.runId === target.runId) {
      state.diagnosticsTab = "runs"; enter(); display();
      if (window.RNAssistantTrajectory) RNAssistantTrajectory.resume();
    } else window.openRunJournal(target);
    if (returnFocus && returnFocus.isConnected && $("diagnosticsRunJournal").contains(returnFocus) && returnFocus.getClientRects().length)
      returnFocus.focus({ preventScroll: true });
    else $("diagnosticsReturnRequest").focus({ preventScroll: true });
  }
  function forgetRequest() { requestScope = null; display(); }
  function showJournal(target) {
    journalKey = key(target); scope(target); state.diagnosticsTab = "runs";
    enter(); display();
  }
  function onTabChange(name) {
    if (routing) return;
    if (name !== "diagnostics") {
      if (window.RNAssistantTrajectory) RNAssistantTrajectory.pause();
      return;
    }
    if (selected && key(selected) !== key(source())) {
      journalKey = ""; scope(source());
    }
    show(state.diagnosticsTab || "runs");
  }
  function bind() {
    document.querySelectorAll(".diagnostics-tab-button").forEach(function (button) {
      button.addEventListener("click", function () { show(button.dataset.diagnosticsTab); });
    });
    $("diagnosticsAppView").addEventListener("change", function () { show("application", false); });
    $("diagnosticsReturnRequest").addEventListener("click", function () {
      if (requestScope) switchTab("request");
    });
    $("diagnosticsReturnChat").addEventListener("click", function () { switchTab("chat", { preserveChatOwner: true });
      if (returnFocus && returnFocus.isConnected && returnFocus.getClientRects().length) returnFocus.focus({ preventScroll: true });
    });
    state.diagnosticsTab = "runs"; scope(source()); display();
  }
  window.RNAssistantDiagnostics = { source: source, key: key, open: open, show: show,
    showRequest: showRequest, returnToJournal: returnToJournal, forgetRequest: forgetRequest, showJournal: showJournal, onTabChange: onTabChange, bind: bind,
    requestVisible: function () { return window.RNAssistantDockWorkspace && RNAssistantDockWorkspace.isVisible("request"); } };
}());
