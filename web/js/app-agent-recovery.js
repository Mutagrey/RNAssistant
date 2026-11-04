(function () {
  "use strict";
  function create(component) {
    var target = component.target, closed = false, generation = 0, menu = null;
    var page = [], next = null, selected = new Map(), receipts = new Map(), busy = false, error = "";
    function label(item) { return item.title || "Чат"; }
    function status(receipt) {
      if (receipt.status === "Requested") return "Продолжение принято";
      if (receipt.status === "Changed") return "Состояние изменилось · обновите список";
      if (receipt.status === "Unconfirmed") return "Продолжение не подтверждено: " + (receipt.detail || "обновите список");
      return "Недоступно: " + (receipt.detail || "обновите список");
    }
    function items() {
      var shown = Array.from(selected.values()).concat(page.filter(function (item) { return !selected.has(item.sessionId); }));
      var result = [{ key: "heading", label: target.source === "office" ? "Ожидания в этом документе" : "Ожидания в этом проекте", disabled: true, icon: false }];
      shown.forEach(function (item) {
        var receipt = receipts.get(item.sessionId), selectable = !!item.target && !receipt;
        result.push({ key: item.sessionId, label: label(item), icon: false,
          checked: selectable ? selected.has(item.sessionId) : undefined,
          status: receipt ? status(receipt) : item.error || item.agentId,
          disabled: busy || !selectable || !selected.has(item.sessionId) && selected.size >= 32,
          onSelect: function () { if (selected.has(item.sessionId)) selected.delete(item.sessionId); else selected.set(item.sessionId, item); render(); },
          secondary: { key: item.sessionId + ":open", label: "Открыть: " + label(item), icon: "chat",
            onSelect: function () { RNAssistantConversationPanel.open(Object.assign({}, target, { sessionId: item.sessionId })); } } });
      });
      if (!shown.length && !busy) result.push({ label: next ? "На этой странице сохранённых ожиданий нет" : "Сохранённых ожиданий нет", disabled: true, icon: false });
      if (busy || error) result.push({ key: "status", label: busy ? "Подождите…" : error, disabled: true, icon: error ? "warning" : false });
      result.push({ separator: true });
      result.push({ key: "select", label: selected.size ? "Снять выбор" : "Выбрать показанные", disabled: busy || !shown.some(function (item) { return item.target && !receipts.has(item.sessionId); }),
        keepOpen: true, icon: "check", onSelect: function () {
          if (selected.size) selected.clear();
          else shown.filter(function (item) { return item.target && !receipts.has(item.sessionId); }).slice(0, 32)
            .forEach(function (item) { selected.set(item.sessionId, item); });
          render();
        } });
      result.push({ key: "resume", label: "Продолжить выбранных (" + selected.size + ")", icon: "play", disabled: busy || !selected.size, keepOpen: true, onSelect: resume });
      if (next) result.push({ key: "next", label: "Следующие чаты", icon: "right", disabled: busy, keepOpen: true, onSelect: function () { load(next); } });
      result.push({ key: "refresh", label: "Обновить список", icon: "refresh", disabled: busy, keepOpen: true, onSelect: function () { selected.clear(); receipts.clear(); load(null); } });
      return result;
    }
    function render() { if (!closed && menu && menu.isOpen()) menu.update(items()); }
    async function load(after) {
      var version = ++generation; busy = true; error = ""; render();
      try {
        var response = await send("getRecoverableAgentWaits", Object.assign({}, target, { afterSessionId: after }));
        if (closed || version !== generation) return;
        if (!response || !Array.isArray(response.participants)) throw new Error("Список ожиданий недоступен");
        page = response.participants; next = response.nextAfterSessionId;
      } catch (failure) { if (!closed && version === generation) error = failure.detail || failure.message; }
      finally { if (!closed && version === generation) { busy = false; render(); } }
    }
    async function resume() {
      if (busy || !selected.size) return;
      var version = ++generation, targets = Array.from(selected.values()).map(function (item) { return item.target; });
      busy = true; error = ""; render();
      try {
        var response = await send("resumeAgentWaits", Object.assign({}, target, { targets: targets }));
        if (closed || version !== generation) return;
        if (!response || !Array.isArray(response.receipts) || response.receipts.length !== targets.length ||
          !targets.every(function (item) { return response.receipts.filter(function (receipt) {
            return receipt.sessionId === item.sessionId && receipt.runId === item.runId &&
              ["Requested", "Changed", "Unavailable", "Unconfirmed"].includes(receipt.status);
          }).length === 1; })) throw new Error("Результат продолжения не подтверждён. Обновите список.");
        response.receipts.forEach(function (receipt) { receipts.set(receipt.sessionId, receipt); });
        page = Array.from(selected.values()).concat(page.filter(function (item) { return !selected.has(item.sessionId); }));
        selected.clear();
        response.receipts.forEach(function (receipt) {
          var panel = RNAssistantConversationPanel.find(Object.assign({}, target, { sessionId: receipt.sessionId }));
          if (panel) panel.refresh().catch(function (failure) { panel.notice(failure); });
        });
      } catch (failure) {
        if (!closed && version === generation) {
          error = "Продолжение не подтверждено: " + (failure.detail || failure.message) + ". Обновите список.";
          selected.forEach(function (item) { receipts.set(item.sessionId, { status: "Unconfirmed" }); });
          page = Array.from(selected.values()); selected.clear();
        }
      } finally { if (!closed && version === generation) { busy = false; render(); } }
    }
    function reset() { ++generation; if (menu) menu.close(); menu = null; page = []; selected.clear(); receipts.clear(); busy = false; error = ""; next = null; }
    return { open: function (trigger) {
      if (closed || !target.sessionId || state.bridgeUnavailable || state.initializePromise || component.loading || component.loadError) return;
      reset(); busy = true;
      menu = RNAssistantActionMenu.open(trigger, items(), { label: "Продолжить совместную работу", placement: "top", align: "start" });
      if (menu) load(null);
    }, reset: reset, dispose: function () { reset(); closed = true; } };
  }
  window.RNAssistantAgentRecovery = Object.freeze({ create: create });
}());
