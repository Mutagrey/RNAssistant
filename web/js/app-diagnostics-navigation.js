(function () {
  "use strict";
  // Owns routes and presentation identity only. Data and rendering stay with readers.
  var journalKey = "", selected = null, returnFocus = null;
  var routing = false;
  function enter() { routing = true; try { switchTab("diagnostics"); } finally { routing = false; } }
  function value(item, name) { return item && (item[name] !== undefined ? item[name] : item[name[0].toUpperCase() + name.slice(1)]); }
  /** @typedef {{owner: string, chatId: string, workspaceId: string|null, documentId: string|null,
   * available: boolean, mode: 'live'|'retained', requestedRunId: string, stepId: string,
   * requestEventId: string, component: object, snapshot: object, draft: object}} InspectionContext */
  /** @returns {InspectionContext} Identity is a route; ConversationPanel owns data. */
  function context(target) {
    var panels = window.RNAssistantConversationPanel, component = panels && (target ? panels.find({ source: target.owner === 'office' ? 'office' : 'workspace',
      workspaceId: target.workspaceId, sessionId: target.chatId || target.sessionId }) : panels.selected());
    var missing = Object.assign({ owner: 'office', available: false, messages: [], run: {}, live: [], snapshot: {}, draft: {}, reason: 'Выберите открытый чат.' }, target || {});
    if (!component || !component.isCurrent() || !component.target.sessionId) return missing;
    var address = component.target, data = component.snapshot(), office = address.source === 'office';
    if (office ? data.activeChatId !== address.sessionId : value(data, 'sessionId') !== address.sessionId || value(value(data, 'project'), 'workspaceId') !== address.workspaceId)
      return Object.assign(missing, { reason: 'Точный источник чата недоступен.' });
    var snapshot = office ? data : value(data, 'state') || {}, project = office ? null : value(data, 'project');
    var chat = office ? (state.chats || []).find(function (item) { return value(item, 'id') === address.sessionId; }) : snapshot;
    var documentId = office ? value(chat, 'documentId') || value(chat, 'documentAuthorityId') : null;
    if (target && target.documentId && documentId && target.documentId !== documentId) return Object.assign(missing, { reason: 'Документ выбранного чата изменился.' });
    var run = office ? data.activeRunViewState || {} : RNAssistantRunViewState.normalize(value(snapshot, 'runViewState')) || {};
    return Object.freeze({ owner: office ? 'office' : project && value(project, 'pluginId') ? 'plugin' : 'project', available: !component.loadError && !component.loading,
      reason: component.loadError || (component.loading ? 'Чат загружается…' : ''), component: component, snapshot: snapshot, chat: data, project: project,
      chatId: address.sessionId, sessionId: address.sessionId, workspaceId: office ? null : address.workspaceId, documentId: documentId,
      label: value(chat, 'title') || address.sessionId, document: office ? value(chat, 'documentTitle') || 'Документ чата' : value(project, 'name') || address.workspaceId,
      pluginId: value(project, 'pluginId'), runId: run.runId, run: run, messages: data.messages || [], live: office && data.liveRun && data.liveRun.activities || [],
      draft: component.draft() || {}, readOnly: office ? !!data.chatReadOnly : RNAssistantWorkspaceChat.readOnly(data),
      mode: target && target.runId ? 'retained' : 'live', requestedRunId: target && target.runId || '', stepId: target && target.stepId || '', requestEventId: target && target.requestEventId || '' });
  }
  function source() { return context(); }
  function key(target) { return JSON.stringify([target.owner, target.workspaceId || null, target.chatId || target.sessionId || null]); }
  function scope(target) {
    var current = source(), next = target || current, same = key(next) === key(current);
    selected = Object.assign({}, selected && key(selected) === key(next) ? selected : {}, same ? current : {}, next);
    if (same) { selected.label = current.label; selected.document = current.document; selected.pluginId = current.pluginId; }
    var chatId = selected.chatId || selected.sessionId;
    $("diagnosticsScope").textContent = chatId ?
      [selected.document || "Сохранённый источник", selected.label || chatId].filter(Boolean).join(" · ") :
      "Выберите чат, чтобы разобрать его сохранённые запросы и события.";
    $("diagnosticsScope").disabled = !chatId;
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
    RNAssistantConversationPanel.open({ source: target.owner === 'office' ? 'office' : 'workspace', workspaceId: target.workspaceId, sessionId: chatId });
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
  window.RNAssistantDiagnostics = { source: source, context: context, key: key, open: open, openCurrent: openCurrent, show: show,
    showRequest: showRequest, returnToJournal: returnToJournal, forgetRequest: forgetRequest, showJournal: showJournal, onVisible: onVisible, bind: bind,
    onClose: function () { if (window.RNAssistantTrajectory) RNAssistantTrajectory.dispose(); },
    isVisible: function () { return window.RNAssistantDockWorkspace && RNAssistantDockWorkspace.isVisible("diagnostics"); },
    requestVisible: function () { return window.RNAssistantDockWorkspace && RNAssistantDockWorkspace.isVisible("request"); } };
}());
