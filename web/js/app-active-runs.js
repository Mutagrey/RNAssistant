(function () {
  "use strict";
  var trigger = document.getElementById("activeChatRunsButton"), count = document.getElementById("activeChatRunsCount");
  var runs = [], stopping = new Set(), listError = "", stopError = "", menu = null, pending = null, generation = 0, timer = null, started = false;
  function key(run) { return JSON.stringify([run.source, run.workspaceId || "", run.sessionId, run.runId]); }
  function target(run) { return { source: run.source, workspaceId: run.workspaceId, sessionId: run.sessionId }; }
  function phase(run) {
    return stopping.has(key(run)) || run.phase === "cancelling" ? "Остановка…" :
      run.phase === "executing" ? "Выполняет действие" : run.phase === "compacting" ? "Сжимает контекст" : "Работает";
  }
  function items() {
    var error = stopError || listError;
    var result = runs.map(function (run) {
      var title = run.title || "Чат", detail = [run.scopeTitle || (run.source === "office" ? "Office" : "Проект"), phase(run)].join(" · ");
      return { key: key(run), label: title, icon: false, status: detail,
        onSelect: function () { RNAssistantConversationPanel.open(target(run)); },
        secondary: { key: key(run) + ":stop", label: "Остановить: " + title, icon: "stop",
          disabled: stopping.has(key(run)) || run.phase === "cancelling", onSelect: function () { stop(run); } } };
    });
    if (!result.length) result.push({ label: error || "Активных запросов нет", disabled: true, icon: error ? "warning" : false });
    else if (error) result.push({ label: error, disabled: true, icon: "warning" });
    if (error) result.push({ label: "Обновить список", icon: "refresh", onSelect: function () { stopError = ""; refresh(); } });
    return result;
  }
  function render() {
    count.textContent = listError ? "?" : String(runs.length);
    trigger.title = stopError || listError || "Активные запросы: " + runs.length;
    trigger.setAttribute("aria-label", trigger.title);
    trigger.classList.toggle("has-active-runs", !!runs.length);
    if (menu && menu.isOpen()) menu.update(items());
  }
  function schedule() {
    clearTimeout(timer);
    if (started && !document.hidden) timer = setTimeout(refresh, 2000);
  }
  function refresh() {
    if (!started || document.hidden || state.initializePromise || state.bridgeUnavailable) { schedule(); return Promise.resolve(); }
    if (pending && pending.generation === generation) return pending.promise;
    var request = { generation: generation };
    pending = request;
    request.promise = send("getActiveChatRuns", {}).then(function (value) {
      if (!started || request.generation !== generation) return;
      if (!value || !Array.isArray(value.runs)) throw new Error("Список запросов недоступен");
      runs = value.runs.filter(function (run) { return run && run.sessionId && run.runId &&
        (run.source === "office" || run.source === "workspace" && run.workspaceId); });
      listError = ""; render();
    }).catch(function (failure) {
      if (started && request.generation === generation) { listError = failure.detail || failure.message || "Список запросов недоступен"; render(); }
    }).finally(function () { if (pending === request) pending = null; schedule(); });
    return request.promise;
  }
  async function stop(run) {
    var identity = key(run); if (stopping.has(identity)) return;
    stopping.add(identity); ++generation; stopError = ""; render();
    try {
      var reply = await send("stopActiveChatRun", Object.assign(target(run), { runId: run.runId }));
      if (!reply || typeof reply.requested !== "boolean") throw new Error("Остановка не подтверждена. Обновите список.");
      var panel = RNAssistantConversationPanel.find(target(run));
      if (panel) panel.refresh();
      await refresh();
    } catch (failure) { stopError = failure.detail || failure.message || "Не удалось остановить запрос"; }
    finally { stopping.delete(identity); render(); }
  }
  trigger.addEventListener("click", function () {
    menu = RNAssistantActionMenu.open(trigger, items(), { label: "Активные запросы чатов", className: "active-chat-runs-menu", placement: "top" });
    if (menu) refresh();
  });
  document.addEventListener("visibilitychange", function () { if (document.hidden) clearTimeout(timer); else refresh(); });
  window.addEventListener("pagehide", function () { started = false; ++generation; clearTimeout(timer); });
  window.RNAssistantActiveRuns = Object.freeze({ start: function () { if (!started) { started = true; refresh(); } }, refresh: refresh });
}());
