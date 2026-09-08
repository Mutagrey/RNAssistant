(function () {
  "use strict";
  var cache = new Map(), pending = new Map(), observed = new Map();
  if (typeof MutationObserver === "function") {
    new MutationObserver(function () {
      observed.forEach(function (observer, slot) {
        if (!slot.isConnected) { observer.disconnect(); observed.delete(slot); }
      });
    }).observe(document.documentElement, { childList: true, subtree: true });
  }
  function element(tag, cls, text) {
    var node = document.createElement(tag); node.className = cls;
    if (text != null) node.textContent = text;
    return node;
  }
  function counts(parent, added, removed) {
    var count = element("span", "run-change-counts");
    count.appendChild(element("span", "run-change-added", "+" + added));
    count.appendChild(element("span", "run-change-removed", "−" + removed));
    parent.appendChild(count);
  }
  function availability(item) {
    return item.availability === "unverified" ? "Результат не подтверждён" :
      item.availability === "too_large" ? "Исходник превышает лимит сравнения" : "Исходник для сравнения недоступен";
  }
  function render(parent, result) {
    parent.textContent = "";
    var items = result.items || [];
    if (!items.length && result.complete) return;
    var card = element("details", "run-changes"); card.open = true;
    var summary = element("summary", "run-changes-summary");
    summary.appendChild(element("span", "", "Изменения: " + items.length));
    var added = 0, removed = 0, compared = 0, uncertain = !result.complete;
    var rows = items.map(function (item) {
      var diff = item.availability === "available" ? window.RNAssistantTextDiff.format(item.before, item.after) : null;
      if (diff && diff.complete) { added += diff.added; removed += diff.removed; compared++; } else uncertain = true;
      return { item: item, diff: diff };
    });
    if (compared) counts(summary, added, removed);
    card.appendChild(summary);
    var caption = element("div", "run-changes-caption", uncertain ?
      "Счётчики только для доступных подтверждённых сравнений." : "Сохранённые изменения этого запуска");
    card.appendChild(caption);
    rows.forEach(function (entry, index) {
      var item = entry.item, diff = entry.diff;
      var row = element("details", "run-change-file" + (index >= 3 ? " run-change-extra" : ""));
      var head = element("summary", "run-change-file-summary");
      var labels = element("span", "run-change-labels");
      var renamed = item.beforeTitle && item.beforeTitle !== item.title;
      var title = renamed ? item.beforeTitle + " → " + item.title : item.title;
      labels.appendChild(element("span", "run-change-title", title || "Исходник"));
      var action = item.availability !== "available" ? availability(item) :
        !item.beforeExists ? "Создан" : !item.afterExists ? "Удалён" : renamed ? "Переименован" : "Изменён";
      labels.appendChild(element("span", "run-change-scope", item.scope + " · " + action));
      head.appendChild(labels);
      if (diff && diff.complete) counts(head, diff.added, diff.removed);
      row.appendChild(head);
      var body = element("div", "run-change-diff"); body.tabIndex = 0;
      body.setAttribute("aria-label", "Сравнение: " + (title || "исходник"));
      row.appendChild(body);
      var rendered = false;
      row.addEventListener("toggle", function () {
        if (!row.open || rendered) return;
        rendered = true;
        if (diff) window.RNAssistantTextDiff.render(body, diff);
        else body.appendChild(element("p", "run-changes-caption", availability(item) + ". Счётчик строк не вычисляется."));
      });
      card.appendChild(row);
    });
    if (rows.length > 3) {
      var more = element("button", "run-changes-more", "Показать ещё: " + (rows.length - 3)); more.type = "button";
      more.addEventListener("click", function () {
        var expanded = card.classList.toggle("show-all");
        more.textContent = expanded ? "Свернуть список" : "Показать ещё: " + (rows.length - 3);
        more.setAttribute("aria-expanded", String(expanded));
      });
      more.setAttribute("aria-expanded", "false"); card.appendChild(more);
    }
    if (!result.complete) card.appendChild(element("p", "run-changes-caption", "Список неполный: достигнут лимит или часть журнала недоступна."));
    parent.appendChild(card);
  }
  window.appendRunChanges = function (parent, chatId, runId) {
    if (!chatId || !runId) return;
    // Session revision invalidates cached unknown/missing data after reconciliation.
    function currentKey() { return chatId + ":" + runId + ":" + String((state.chatProjectionRevisions || {})[chatId] || 0); }
    var slot = element("div", "run-changes-slot"); parent.appendChild(slot);
    function load() {
      if (!slot.isConnected || state.activeChatId !== chatId) return;
      var key = currentKey();
      if (cache.has(key)) { render(slot, cache.get(key)); return; }
      if (!pending.has(key)) {
        var request = send("getRunChanges", { chatId: chatId, runId: runId }).then(function (response) {
          if (!response || response.chatId !== chatId || response.runId !== runId) throw new Error("Mismatched source.");
          cache.set(key, response);
          while (cache.size > 12) cache.delete(cache.keys().next().value);
          return response;
        });
        pending.set(key, request);
        request.then(function () { pending.delete(key); }, function () { pending.delete(key); });
      }
      pending.get(key).then(function (response) {
        if (!slot.isConnected || state.activeChatId !== chatId) return;
        if (key !== currentKey()) { load(); return; }
        render(slot, response);
      }, function () {
        if (!slot.isConnected || state.activeChatId !== chatId) return;
        slot.textContent = "";
        var retry = element("button", "run-changes-more", "Не удалось загрузить изменения · Повторить");
        retry.type = "button"; retry.addEventListener("click", load); slot.appendChild(retry);
      });
    }
    if (typeof IntersectionObserver === "function") {
      var observer = new IntersectionObserver(function (entries) {
        if (!slot.isConnected || state.activeChatId !== chatId) { observer.disconnect(); observed.delete(slot); return; }
        if (entries.some(function (entry) { return entry.isIntersecting; })) { observer.disconnect(); observed.delete(slot); load(); }
      }, { rootMargin: "120px" });
      observed.set(slot, observer); observer.observe(slot);
    } else setTimeout(load, 0);
  };
}());
