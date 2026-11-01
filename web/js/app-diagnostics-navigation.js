(function () {
  "use strict";
  // Owns routes and presentation identity only. Data and rendering stay with readers.
  var request = false, suspendedRequest = false, requestScope = null, journalKey = "", selected = null, returnFocus = null;
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
      panel.classList.toggle("active", panel.dataset.diagnosticsPanel === tab && !(tab === "runs" && request));
    });
    document.querySelectorAll("[data-diagnostics-application]").forEach(function (panel) {
      panel.classList.toggle("active", tab === "application" && panel.dataset.diagnosticsApplication === $("diagnosticsAppView").value);
    });
    $("diagnosticsAppControls").classList.toggle("hidden", tab !== "application");
    $("diagnosticsScope").classList.toggle("hidden", tab !== "runs");
    $("modelContextDialog").classList.toggle("hidden", tab !== "runs" || !request);
    $("diagnosticsReturnRequest").classList.toggle("hidden", !suspendedRequest || tab !== "runs");
  }
  function show(tab, initialize) {
    var next = ["runs", "model-lab", "application"].indexOf(tab) >= 0 ? tab : "runs";
    if (state.diagnosticsTab === "runs" && next !== "runs" && window.RNAssistantModelContext) RNAssistantModelContext.pause();
    if (next !== "runs" && window.RNAssistantTrajectory) RNAssistantTrajectory.pause();
    state.diagnosticsTab = next; display();
    if (next === "runs") {
      if (request) { if (window.RNAssistantModelContext) RNAssistantModelContext.resume(); }
      else if (initialize !== false && journalKey !== key(source())) {
        journalKey = key(source()); scope(source()); setTrajectoryDiagnosticsMode("runs", true);
      } else if (window.RNAssistantTrajectory) RNAssistantTrajectory.resume();
    } else if (next === "model-lab") openModelDiagnosticsLab(false);
    else { performanceDiagnostics.render(); requestAnimationFrame(updateLogScrollButton); }
  }
  function open(tab) {
    enter(); show(tab || state.diagnosticsTab || "runs");
  }
  function showRequest(target) {
    returnFocus = document.activeElement; request = true; suspendedRequest = false; requestScope = target; scope(target);
    if (window.RNAssistantTrajectory) RNAssistantTrajectory.pause();
    state.diagnosticsTab = "runs"; enter(); display();
  }
  function hideRequest(restore) {
    request = false; suspendedRequest = false; display();
    if (restore !== false) {
      if (journalKey !== key(selected || source())) {
        var target = selected || source();
        window.openRunJournal(Object.assign({}, target, { sessionId: target.chatId }));
      } else if (window.RNAssistantTrajectory) RNAssistantTrajectory.resume();
      if (returnFocus && returnFocus.isConnected && returnFocus.getClientRects().length) returnFocus.focus({ preventScroll: true });
      else $("diagnosticsReturnChat").focus();
    }
  }
  function showJournal(target) {
    if (request && window.RNAssistantModelContext) { RNAssistantModelContext.pause(); suspendedRequest = true; }
    request = false; journalKey = key(target); scope(target); state.diagnosticsTab = "runs";
    enter(); display();
  }
  function onTabChange(name) {
    if (routing) return;
    if (name !== "diagnostics") {
      if (window.RNAssistantModelContext) RNAssistantModelContext.pause();
      if (window.RNAssistantTrajectory) RNAssistantTrajectory.pause();
      return;
    }
    if (selected && key(selected) !== key(source())) {
      if (window.RNAssistantModelContext) RNAssistantModelContext.close(false);
      request = false; suspendedRequest = false; journalKey = ""; scope(source());
    }
    show(state.diagnosticsTab || "runs");
  }
  function bind() {
    document.querySelectorAll(".diagnostics-tab-button").forEach(function (button) {
      button.addEventListener("click", function () { show(button.dataset.diagnosticsTab); });
    });
    $("diagnosticsAppView").addEventListener("change", function () { show("application", false); });
    $("diagnosticsReturnRequest").addEventListener("click", function () {
      request = true; suspendedRequest = false; scope(requestScope); show("runs");
    });
    $("diagnosticsReturnChat").addEventListener("click", function () { switchTab("chat", { preserveChatOwner: true });
      if (returnFocus && returnFocus.isConnected && returnFocus.getClientRects().length) returnFocus.focus({ preventScroll: true });
    });
    state.diagnosticsTab = "runs"; scope(source()); display();
  }
  window.RNAssistantDiagnostics = { source: source, key: key, open: open, show: show,
    showRequest: showRequest, hideRequest: hideRequest, showJournal: showJournal, onTabChange: onTabChange, bind: bind,
    requestVisible: function () { return request && state.diagnosticsTab === "runs" && $("tab-diagnostics").classList.contains("active"); } };
}());
