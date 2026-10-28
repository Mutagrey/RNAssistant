// Typed, disposable UI blocks. Content/effect projection belongs to runtime services.
(function () {
  "use strict";
  var watching = new Map();
  new MutationObserver(function () {
    watching.forEach(function (entry, slot) {
      if (!slot.isConnected || !entry.active()) entry.cancel();
    });
  }).observe(document.documentElement, { childList: true, subtree: true });
  window.addEventListener("pagehide", function () { watching.forEach(function (entry) { entry.cancel(); }); });
  function node(tag, cls, text) {
    var result = document.createElement(tag); result.className = cls || "";
    if (text != null) result.textContent = text;
    return result;
  }
  function sourceLabel(source) {
    var coverage = source.coverage || {}, location = "";
    if (source.line != null) location = "строка " + source.line + (source.column != null ? ", столбец " + source.column : "");
    else if (coverage.kind === "line-range" && coverage.start != null && coverage.end > coverage.start)
      location = coverage.end === coverage.start + 1 ? "строка " + coverage.start : "строки " + coverage.start + "–" + (coverage.end - 1);
    else if (coverage.kind === "character-range" && coverage.start != null && coverage.end != null)
      location = (Number.isInteger(source.pageIndex) && source.pageIndex >= 0 ? "страница " + (source.pageIndex + 1) + ", " :
        coverage.address ? coverage.address + ", " : "") + "символы [" + coverage.start + ", " + coverage.end + ")";
    else if (coverage.kind === "page-range" && coverage.start != null && coverage.end != null)
      location = coverage.end === coverage.start + 1 ? "страница " + coverage.end : "страницы " + (coverage.start + 1) + "–" + coverage.end;
    else if (coverage.address) location = coverage.address;
    else if (coverage.path != null) location = "путь " + JSON.stringify(coverage.path);
    else if (coverage.kind === "record-range") location = "записи [" + coverage.start + ", " + coverage.end + ")";
    var views = { text: "текст", source: "исходник", metadata: "метаданные", structure: "структура",
      table: "таблица", tree: "дерево", media: "медиа", formulas: "формулы" };
    var text = (source.reference ? source.reference + " — " : "") + (source.target || "Источник");
    if (location) text += ", " + location;
    else if (source.view) text += " · " + (views[source.view] || source.view);
    if (source.excerpt != null && source.excerpt !== "") text += ": «" + source.excerpt + (source.excerptTruncated ? "…" : "") + "»";
    return text;
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
          entry.appendChild(node(item.detail ? "summary" : "div", item.source ? "tool-source-caption" : "", item.source ? sourceLabel(item.source) : item.title));
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
      else window.RNAssistantRunChanges.renderInline(body, block.changes);
    }
  };
  function render(parent, response) {
    parent.textContent = "";
    if (response.executionReference || response.action) parent.appendChild(node("p", "tool-result-note tool-result-receipt",
      [response.executionReference, response.action || "Результат инструмента", response.target].filter(Boolean).join(" · ")));
    (response.blocks || []).forEach(function (block) {
      var renderer = renderers[block.kind];
      if (typeof renderer !== "function" || !Object.prototype.hasOwnProperty.call(renderers, block.kind)) {
        parent.appendChild(node("p", "tool-result-note", "Этот вид представления недоступен. Данные — в JSON ниже.")); return;
      }
      var card = node("details", "tool-result-preview"); card.open = !block.source;
      var heading = node("summary", block.source ? "tool-source-caption" : "", block.source ? sourceLabel(block.source) : block.title);
      if (block.source) heading.title = "Развернуть сохранённый фрагмент результата";
      card.appendChild(heading);
      if (block.note) card.appendChild(node("p", "tool-result-note", block.note));
      if (!block.complete && block.kind !== "text_changes")
        card.appendChild(node("p", "tool-result-note", "Показана доступная часть результата. Подробности — в JSON ниже."));
      var body = node("div", "tool-result-preview-body"); card.appendChild(body);
      renderer(body, block); parent.appendChild(card);
    });
    if (response.rawJson) appendActivityData(parent, "Результат JSON", response.rawJson);
  }
  window.RNAssistantToolResultPresentation = { render: render, sourceLabel: sourceLabel };
  window.appendToolResultPreview = function (parent, activity, context, disclosure, renderResponse) {
    var workspace = !!(context && context.workspace);
    var owner = workspace && window.RNAssistantChatInspector ? window.RNAssistantChatInspector.target() : null;
    var scopeOwner = owner && owner.owner;
    var chatId = workspace ? owner && owner.sessionId : state.activeChatId;
    var workspaceId = workspace ? owner && owner.workspaceId : null;
    var runId = activityValue(activity, "RunId", "runId", "") || (context && context.message ? messageRunId(context.message) : "");
    var toolCallId = activityToolCallId(activity);
    if (!chatId || workspace && !workspaceId || !runId || !toolCallId) return false;
    var slot = node("div", "tool-result-slot"); parent.appendChild(slot);
    var generation = 0, requestId = null, revision = null;
    function currentRevision() { return workspace ? 0 : (state.chatProjectionRevisions || {})[chatId] || 0; }
    function cancel() {
      generation++;
      watching.delete(slot);
      if (requestId && typeof cancelBridgeRequest === "function") cancelBridgeRequest(requestId).catch(function () {});
      requestId = null;
    }
    function active() {
      if (!slot.isConnected || disclosure && !disclosure.open) return false;
      if (!workspace) return state.activeChatId === chatId;
      var current = window.RNAssistantChatInspector && window.RNAssistantChatInspector.target();
      return !!current && current.owner === scopeOwner && current.workspaceId === workspaceId && current.sessionId === chatId;
    }
    function load() {
      if (!active()) return;
      cancel(); var ticket = generation; revision = currentRevision();
      watching.set(slot, { active: active, cancel: cancel });
      slot.textContent = "Загрузка представления…";
      var request = workspace
        ? send("getProjectToolResultPresentation", { workspaceId: workspaceId, sessionId: chatId,
          runId: runId, toolCallId: toolCallId })
        : send("getToolResultPresentation", { chatId: chatId, runId: runId, toolCallId: toolCallId });
      requestId = request.requestId;
      request.then(function (response) {
        if (ticket !== generation || !active()) return;
        requestId = null;
        if (revision !== currentRevision()) { load(); return; }
        if (!response || response.chatId !== chatId || response.runId !== runId || response.toolCallId !== toolCallId ||
            workspace && response.workspaceId !== workspaceId)
          throw new Error("Mismatched presentation source.");
        (renderResponse || render)(slot, response);
      }).catch(function () {
        if (ticket !== generation || !active()) return;
        requestId = null; slot.textContent = "";
        var retry = node("button", "agent-action-button secondary", "Не удалось загрузить представление · Повторить");
        retry.type = "button"; retry.addEventListener("click", load); slot.appendChild(retry);
      });
    }
    if (disclosure) disclosure.addEventListener("toggle", function () {
      if (disclosure.open) load(); else { cancel(); slot.textContent = ""; }
    });
    // Interactions load on attachment; ordinary closed details remain lazy.
    setTimeout(function () {
      if (!slot.isConnected) return;
      if ((!disclosure || disclosure.open) && revision === null) load();
    }, 0);
    return true;
  };
}());
