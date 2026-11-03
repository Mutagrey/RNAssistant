(function () {
  "use strict";
  // Owns routes and presentation identity only. Data and rendering stay with readers.
  var journalKey = "", selected = null, returnFocus = null;
  var routing = false;
  function enter() { routing = true; try { switchTab("diagnostics"); } finally { routing = false; } }
  function value(item, name) { return item && (item[name] !== undefined ? item[name] : item[name[0].toUpperCase() + name.slice(1)]); }
  function source() {
    var workspace = window.RNAssistantWorkspaceChat, owner = workspace ? workspace.owner() : "office";
    if (owner !== "office") {
      var snapshot = workspace.snapshot(), project = value(snapshot, "project"), run = value(snapshot, "state");
      return { owner: owner, workspaceId: value(project, "workspaceId"), chatId: value(snapshot, "sessionId"),
        label: value(run, "title") || "Проектный чат", document: value(project, "name") || value(project, "rootPath") || "Проект",
        pluginId: owner === "plugin" && window.RNAssistantPlugins ? RNAssistantPlugins.selectedId() : null,
        runId: value(run, "runId") };
    }
    var chat = typeof activeChatSummary === "function" ? activeChatSummary() : null;
    return { owner: "office", workspaceId: null, chatId: state.activeChatId,
      label: value(chat, "title") || "Чат", document: value(chat, "documentName") || state.title || "Документ Office",
      runId: value(state.activeRunViewState, "runId") };
  }
  function key(target) { return JSON.stringify([target.owner, target.workspaceId || null, target.chatId || target.sessionId || null]); }
  function scope(target) {
    var current = source(), next = target || current, same = key(next) === key(current);
    selected = Object.assign({}, selected && key(selected) === key(next) ? selected : {}, same ? current : {}, next);
    if (same) { selected.label = current.label; selected.document = current.document; selected.pluginId = current.pluginId; }
    var chatId = selected.chatId || selected.sessionId;
    $("diagnosticsScope").textContent = chatId ?
      [selected.document || "Сохранённый источник", selected.label || chatId].filter(Boolean).join(" · ") :
      "Выберите чат, чтобы разобрать его сохранённые запросы и события.";
    $("diagnosticsScope").disabled = !chatId || selected.owner === "plugin" && !selected.pluginId;
    $("diagnosticsScope").title = chatId ? "Открыть чат «" + (selected.label || chatId) + "»\n" +
      [selected.owner, selected.workspaceId, chatId].filter(Boolean).join(" · ") : "Выберите чат в дереве";
    $("diagnosticsScope").setAttribute("aria-label", chatId ? "Открыть чат «" + (selected.label || chatId) + "»" : "Чат не выбран");
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
    $("diagnosticsScopeBar").classList.toggle("hidden", tab !== "runs");
  }
  function show(tab, initialize) {
    var next = ["runs", "model-lab", "application"].indexOf(tab) >= 0 ? tab : "runs";
    if (next !== "runs" && window.RNAssistantTrajectory) RNAssistantTrajectory.pause();
    state.diagnosticsTab = next; display();
    if (next === "runs") {
      if (initialize !== false && (!journalKey || !selected || !(selected.chatId || selected.sessionId))) {
        journalKey = key(source()); scope(source()); setTrajectoryDiagnosticsMode("runs", true);
      } else if (window.RNAssistantTrajectory) RNAssistantTrajectory.resume();
    } else if (next === "model-lab") openModelDiagnosticsLab(false);
    else { performanceDiagnostics.render(); requestAnimationFrame(updateLogScrollButton); }
  }
  function open(tab) {
    enter(); show(tab || state.diagnosticsTab || "runs");
  }
  function openCurrent() {
    var current = source();
    if (current.chatId) return window.openRunJournal(Object.assign({}, current, { sessionId: current.chatId, runId: null }));
    showJournal(current); setTrajectoryDiagnosticsMode("runs", true);
  }
  function openSourceChat() {
    if (!selected) return;
    var target = Object.assign({}, selected), chatId = target.chatId || target.sessionId;
    if (!chatId) return;
    if (target.owner === "office") {
      RNAssistantProjectChats.setScope("office"); switchTab("chat", { preserveChatOwner: true }); selectChat(chatId);
    } else if (target.owner === "plugin") {
      if (target.pluginId) RNAssistantPlugins.select(target.pluginId, chatId);
    } else RNAssistantProjectChats.open(target.workspaceId, chatId);
  }
  function showRequest(target) {
    returnFocus = document.activeElement;
    if (window.RNAssistantTrajectory) RNAssistantTrajectory.pause();
    switchTab("request"); display();
  }
  function returnToJournal(target) {
    if (journalKey === key(target) && window.RNAssistantTrajectory && RNAssistantTrajectory.hasRun(target.runId)) {
      state.diagnosticsTab = "runs"; enter(); display();
      if (window.RNAssistantTrajectory) RNAssistantTrajectory.resume();
    } else window.openRunJournal(target);
    if (returnFocus && returnFocus.isConnected && $("diagnosticsRunJournal").contains(returnFocus) && returnFocus.getClientRects().length)
      returnFocus.focus({ preventScroll: true });
    else $("trajectoryViewInput").focus({ preventScroll: true });
  }
  function forgetRequest() { returnFocus = null; }
  function showJournal(target) {
    journalKey = key(target); scope(target); state.diagnosticsTab = "runs";
    enter(); display();
  }
  function onVisible(shown) {
    if (routing) return;
    if (!shown) {
      if (window.RNAssistantActionMenu) RNAssistantActionMenu.closeWithin($("tab-diagnostics"));
      if (window.RNAssistantTrajectory) RNAssistantTrajectory.pause();
      return;
    }
    show(state.diagnosticsTab || "runs");
  }
  function bind() {
    document.querySelectorAll(".diagnostics-tab-button").forEach(function (button) {
      button.addEventListener("click", function () { show(button.dataset.diagnosticsTab); });
    });
    $("diagnosticsAppView").addEventListener("change", function () { show("application", false); });
    $("diagnosticsScope").addEventListener("click", openSourceChat);
    state.diagnosticsTab = "runs"; scope(source()); display();
  }
  window.RNAssistantDiagnostics = { source: source, key: key, open: open, openCurrent: openCurrent, show: show,
    showRequest: showRequest, returnToJournal: returnToJournal, forgetRequest: forgetRequest, showJournal: showJournal, onVisible: onVisible, bind: bind,
    onClose: function () { if (window.RNAssistantTrajectory) RNAssistantTrajectory.dispose(); },
    isVisible: function () { return window.RNAssistantDockWorkspace && RNAssistantDockWorkspace.isVisible("diagnostics"); },
    requestVisible: function () { return window.RNAssistantDockWorkspace && RNAssistantDockWorkspace.isVisible("request"); } };
}());
