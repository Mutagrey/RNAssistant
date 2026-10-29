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
  function targetStatus(item) {
    return { Prepared: "Предложено", Changed: "", Unchanged: "Изменений не потребовалось",
      Rejected: "Отказ без эффекта", Unknown: "Эффект неизвестен", NotStarted: "Выполнение не начиналось" }[item.targetStatus] || "";
  }
  function availability(item) {
    return targetStatus(item) || (item.availability === "unverified" ? "Точный текст результата недоступен" :
      item.availability === "hunks" ? "Проверенные фрагменты" :
      item.availability === "copied" ? "Создана точная копия" :
      item.availability === "too_large" ? "Исходник превышает лимит сравнения" : "Исходник для сравнения недоступен");
  }
  function render(parent, result) {
    parent.textContent = "";
    var items = result.items || [];
    if (!items.length && result.complete) return;
    var card = element("details", "run-changes");
    var summary = element("summary", "run-changes-summary");
    summary.appendChild(element("span", "", "Сравнения: " + items.length));
    var added = 0, removed = 0, compared = 0, uncountedActual = 0, plannedAdded = 0, plannedRemoved = 0, planned = 0, unverified = 0, uncertain = !result.complete;
    var rows = items.map(function (item) {
      if (item.availability === "unverified") unverified++;
      var preview = item.availability === "unverified" && typeof item.before === "string" &&
        typeof item.intendedAfter === "string" && typeof item.intendedAfterExists === "boolean";
      var diff = item.availability === "available" ? window.RNAssistantTextDiff.format(item.before, item.after) :
        preview ? window.RNAssistantTextDiff.format(item.before, item.intendedAfter) : null;
      if (item.availability === "hunks" || item.availability === "copied") uncountedActual++;
      else if (diff && diff.complete && !preview) { added += diff.added; removed += diff.removed; compared++; }
      else if (diff && diff.complete && preview) { plannedAdded += diff.added; plannedRemoved += diff.removed; planned++; }
      else uncertain = true;
      return { item: item, diff: diff, preview: preview };
    });
    // Ordinary changes start as the demo's compact row. Missing evidence stays visible.
    card.open = uncertain || unverified > 0;
    if (compared) counts(summary, added, removed);
    if (compared && planned) summary.appendChild(element("span", "run-changes-caption", "фактическое сравнение"));
    card.appendChild(summary);
    if (unverified) {
      var plannedSummary = element("div", "run-changes-caption run-changes-planned", "Предварительное сравнение");
      if (planned) { plannedSummary.appendChild(document.createTextNode(": ")); counts(plannedSummary, plannedAdded, plannedRemoved); }
      card.appendChild(plannedSummary);
    }
    if (uncertain) card.appendChild(element("p", "run-changes-caption run-changes-unavailable", "Часть сравнений недоступна; её строки не учтены."));
    if (uncountedActual) card.appendChild(element("p", "run-changes-caption", "Полный счётчик строк не вычислялся для объектов: " + uncountedActual + "."));
    rows.forEach(function (entry, index) {
      var item = entry.item, diff = entry.diff;
      var row = element("details", "run-change-file" + (index >= 3 ? " run-change-extra" : ""));
      var head = element("summary", "run-change-file-summary");
      var labels = element("span", "run-change-labels");
      var renamed = item.beforeTitle && item.beforeTitle !== item.title;
      var title = renamed ? item.beforeTitle + " → " + item.title : item.title;
      labels.appendChild(element("span", "run-change-title", title || "Исходник"));
      var action = targetStatus(item) || (item.availability !== "available" ? availability(item) :
        !item.beforeExists ? "Создан" : !item.afterExists ? "Удалён" : renamed ? "Переименован" : "Изменён");
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
        if (item.availability === "copied") {
          body.appendChild(element("p", "run-changes-caption", "Новый файл сохранён из точного исходного ресурса. Скачивание доступно в Library."));
        } else if (item.availability === "hunks") {
          body.appendChild(element("p", "run-changes-caption", "Полный файл проверен после записи. Здесь показаны изменённые фрагменты; длинные фрагменты сокращены."));
          (item.hunks || []).forEach(function (hunk) {
            var block = element("div", "run-change-preview-source");
            block.appendChild(element("p", "run-changes-caption", (hunk.version ? "Версия " + hunk.version + " · " : "") + (hunk.offset >= 0 ? "символ " + hunk.offset : "Фрагмент") + " · −" + hunk.removed + " / +" + hunk.inserted));
            var fragment = window.RNAssistantTextDiff.format(hunk.before || "", hunk.after || "");
            if (fragment) window.RNAssistantTextDiff.render(block, fragment);
            body.appendChild(block);
          });
        } else if (diff) {
          var output = body;
          if (entry.preview) {
            body.appendChild(element("p", "run-changes-caption run-change-preview-notice",
              "План изменений: исходный → запланированный код. Точный текст результата недоступен. Статус действия указан в истории."));
            output = element("div", "run-change-preview-source"); body.appendChild(output);
          }
          window.RNAssistantTextDiff.render(output, diff);
        } else body.appendChild(element("p", "run-changes-caption", item.availability === "unverified" ?
          "Точный текст результата недоступен. Исходник плана недоступен или превышает лимит сравнения." :
          availability(item) + ". Счётчик строк не вычисляется."));
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
  function renderInline(parent, result) {
    (result.items || []).forEach(function (item) {
      var block = element("div", "inline-change");
      var title = item.beforeTitle && item.beforeTitle !== item.title ? item.beforeTitle + " → " + item.title : item.title;
      var header = element("div", "inline-change-header");
      header.appendChild(element("span", "run-change-title", title || "Исходник"));
      block.appendChild(header);
      if (item.targetStatus && item.targetStatus !== "Changed" && item.targetStatus !== "Unchanged")
        block.appendChild(element("p", "run-changes-caption", targetStatus(item)));
      var preview = item.availability === "unverified" && typeof item.before === "string" && typeof item.intendedAfter === "string";
      if (preview) block.appendChild(element("p", "run-changes-caption", "Предварительный diff · результат не подтверждён"));
      if (item.availability === "available" || preview) {
        var diff = window.RNAssistantTextDiff.format(item.before, preview ? item.intendedAfter : item.after);
        header.appendChild(element("span", "vba-diff-summary", diff.summary || ""));
        window.RNAssistantTextDiff.renderInline(block, diff, { hideSummary: true });
      } else if (item.availability === "hunks") {
        block.appendChild(element("p", "run-changes-caption", "Проверенные фрагменты · длинные фрагменты сокращены"));
        (item.hunks || []).forEach(function (hunk, index) {
          block.appendChild(element("p", "run-changes-caption", "@@ " + (hunk.offset >= 0 ? "символ " + hunk.offset : "фрагмент " + (index + 1)) + " · −" + hunk.removed + " / +" + hunk.inserted + " @@"));
          window.RNAssistantTextDiff.renderInline(block, window.RNAssistantTextDiff.format(hunk.before || "", hunk.after || ""));
        });
      } else if (item.targetStatus === "Changed" || item.targetStatus === "Unchanged" || !targetStatus(item))
        block.appendChild(element("p", "run-changes-caption", availability(item)));
      parent.appendChild(block);
    });
    if (!result.complete) parent.appendChild(element("p", "run-changes-caption", "Часть сравнений недоступна."));
  }
  function renderPrepared(parent, preview) {
    (preview.targets || []).forEach(function (target) {
      var details = element("details", "inline-change");
      details.appendChild(element("summary", "run-change-title", target.target + " · Предложенные изменения"));
      (target.preview || []).forEach(function (part) {
        window.RNAssistantTextDiff.renderInline(details, window.RNAssistantTextDiff.format(part.before || "", part.after || ""));
        if (part.truncated) details.appendChild(element("p", "run-changes-caption", "Показана часть подготовленного изменения."));
      });
      parent.appendChild(details);
    });
  }
  window.RNAssistantRunChanges = { render: render, renderInline: renderInline, renderPrepared: renderPrepared };
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
