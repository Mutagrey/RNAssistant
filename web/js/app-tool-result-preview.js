// Typed, disposable UI blocks. Content/effect projection belongs to runtime services.
(function () {
  "use strict";
  var watching = new Map();
  new MutationObserver(function () {
    watching.forEach(function (entry, slot) {
      if (!slot.isConnected || state.activeChatId !== entry.chatId) entry.cancel();
    });
  }).observe(document.documentElement, { childList: true, subtree: true });
  window.addEventListener("pagehide", function () { watching.forEach(function (entry) { entry.cancel(); }); });
  function node(tag, cls, text) {
    var result = document.createElement(tag); result.className = cls || "";
    if (text != null) result.textContent = text;
    return result;
  }
  function rows(body, block) {
    var table = block.kind === "table", items = table ? block.rows : block.items;
    var target = body, shown = 0;
    if (table) {
      var grid = node("table"), head = node("thead"), header = node("tr");
      block.columns.forEach(function (label) { header.appendChild(node("th", "", label)); });
      head.appendChild(header); grid.appendChild(head); target = node("tbody"); grid.appendChild(target); body.appendChild(grid);
    }
    var more = node("button", "agent-action-button secondary"); more.type = "button";
    function page() {
      var end = Math.min(shown + 20, items.length);
      for (; shown < end; shown++) {
        var item = items[shown];
        if (table) {
          var tr = node("tr"); item.forEach(function (value) { tr.appendChild(node("td", "", value)); }); target.appendChild(tr);
        } else {
          var entry = node(item.detail ? "details" : "div", "tool-result-item");
          entry.appendChild(node(item.detail ? "summary" : "div", "", item.title));
          if (item.detail) entry.appendChild(node("pre", "", item.detail));
          target.appendChild(entry);
        }
      }
      more.textContent = "Показать ещё · " + Math.min(20, items.length - shown);
      more.hidden = shown >= items.length;
    }
    more.addEventListener("click", page); page(); body.appendChild(more);
    if (!items.length) body.appendChild(node("p", "tool-result-note", "Нет записей."));
  }
  // Add a renderer for a new block kind here; no tool names or payload guessing.
  var renderers = {
    text: function (body, block) { body.appendChild(node("pre", "", block.text)); },
    list: rows,
    table: rows,
    text_changes: function (body, block) {
      if (!block.changes.items.length && block.changes.complete && block.changes.evidenceFound)
        body.appendChild(node("p", "tool-result-note", "Изменений нет."));
      else window.RNAssistantRunChanges.render(body, block.changes);
    }
  };
  function render(parent, response) {
    parent.textContent = "";
    (response.blocks || []).forEach(function (block) {
      var renderer = renderers[block.kind];
      if (typeof renderer !== "function" || !Object.prototype.hasOwnProperty.call(renderers, block.kind)) {
        parent.appendChild(node("p", "tool-result-note", "Этот вид представления недоступен. Данные — в JSON ниже.")); return;
      }
      var card = node("details", "tool-result-preview"); card.open = true;
      card.appendChild(node("summary", "", block.title));
      if (!block.complete && block.kind !== "text_changes")
        card.appendChild(node("p", "tool-result-note", "Показана доступная часть результата. Подробности — в JSON ниже."));
      var body = node("div", "tool-result-preview-body"); card.appendChild(body);
      renderer(body, block); parent.appendChild(card);
    });
  }
  window.RNAssistantToolResultPresentation = { render: render };
  window.appendToolResultPreview = function (parent, activity, context, disclosure) {
    var chatId = state.activeChatId;
    var runId = activityValue(activity, "RunId", "runId", "") || (context && context.message ? messageRunId(context.message) : "");
    var toolCallId = activityToolCallId(activity);
    if (!chatId || !runId || !toolCallId || !disclosure) return;
    var slot = node("div", "tool-result-slot"); parent.appendChild(slot);
    var generation = 0, requestId = null, revision = null;
    function currentRevision() { return (state.chatProjectionRevisions || {})[chatId] || 0; }
    function cancel() {
      generation++;
      watching.delete(slot);
      if (requestId && typeof cancelBridgeRequest === "function") cancelBridgeRequest(requestId).catch(function () {});
      requestId = null;
    }
    function active() { return slot.isConnected && disclosure.open && state.activeChatId === chatId; }
    function load() {
      if (!active()) return;
      cancel(); var ticket = generation; revision = currentRevision();
      watching.set(slot, { chatId: chatId, cancel: cancel });
      slot.textContent = "Загрузка представления…";
      var request = send("getToolResultPresentation", { chatId: chatId, runId: runId, toolCallId: toolCallId });
      requestId = request.requestId;
      request.then(function (response) {
        if (ticket !== generation || !active()) return;
        requestId = null;
        if (revision !== currentRevision()) { load(); return; }
        if (!response || response.chatId !== chatId || response.runId !== runId || response.toolCallId !== toolCallId)
          throw new Error("Mismatched presentation source.");
        render(slot, response);
      }).catch(function () {
        if (ticket !== generation || !active()) return;
        requestId = null; slot.textContent = "";
        var retry = node("button", "agent-action-button secondary", "Не удалось загрузить представление · Повторить");
        retry.type = "button"; retry.addEventListener("click", load); slot.appendChild(retry);
      });
    }
    disclosure.addEventListener("toggle", function () {
      if (disclosure.open) load(); else { cancel(); slot.textContent = ""; }
    });
    // Start only once attached: creating a collapsed card must perform no read.
    setTimeout(function () {
      if (!slot.isConnected) return;
      if (disclosure.open && revision === null) load();
    }, 0);
  };
}());
