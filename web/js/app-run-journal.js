(function () {
  "use strict";

  var MAX_RENDERED_ROWS = 1000;
  var MAX_BULK_EXPANDED_ROWS = 50;
  var FILTERS = [
    { id: "all", label: "Все" },
    { id: "problems", label: "Проблемы" },
    { id: "model", label: "Модель" },
    { id: "tools", label: "Tools" },
    { id: "effects", label: "Эффекты" }
  ];

  function value(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] :
      (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function boundedText(input, limit) {
    var text = String(input === null || input === undefined ? "" : input);
    return text.length <= limit ? text : text.substring(0, limit - 1) + "…";
  }

  function rowId(row) {
    return boundedText(value(row, "Id", "id", ""), 512);
  }

  function rowKind(row) {
    return String(value(row, "Kind", "kind", "") || "").toLowerCase();
  }

  function rowStatus(row) {
    return String(value(row, "Status", "status", "") || "").toLowerCase();
  }

  function firstSequence(row) {
    return Number(value(row, "FirstSequence", "firstSequence", 0) || 0);
  }

  function lastSequence(row) {
    return Number(value(row, "LastSequence", "lastSequence", firstSequence(row)) || firstSequence(row));
  }

  function sourceSequences(row) {
    return value(row, "SourceEventSeqs", "sourceEventSeqs", []) || [];
  }

  function sourceIds(row) {
    return value(row, "SourceEventIds", "sourceEventIds", []) || [];
  }

  function isProblem(row) {
    var status = rowStatus(row);
    return Number(value(row, "FailureCount", "failureCount", 0) || 0) > 0 ||
      ["failed", "error", "unknown", "rejected", "cancelled", "missing", "blocked", "refused",
        "partial", "partial_failure", "interrupted", "interrupted_unknown", "runtime_error",
        "invalid_model_response", "compaction_failed", "completed_with_errors"]
        .indexOf(status) >= 0 || rowKind(row) === "diagnostic.evidence.missing";
  }

  function isWaiting(row) {
    return ["waiting", "waiting_confirmation", "awaiting_confirmation", "awaiting_user", "running"]
      .indexOf(rowStatus(row)) >= 0;
  }

  function isModel(row) {
    var kind = rowKind(row);
    return !!value(row, "ModelAttemptId", "modelAttemptId", "") ||
      /^(model\.|llm\.|agent\.|step\.|assistant\.)/.test(kind);
  }

  function isTool(row) {
    return !!(value(row, "ToolCallId", "toolCallId", "") || value(row, "ToolId", "toolId", "")) ||
      /^tool\./.test(rowKind(row));
  }

  function isEffect(row) {
    return !!(value(row, "MutationId", "mutationId", "") || value(row, "JournalRunId", "journalRunId", "")) ||
      /^(domain\.effect\.|artifact\.)/.test(rowKind(row));
  }

  function matchesFilter(row, filter) {
    if (filter === "problems") return isProblem(row);
    if (filter === "model") return isModel(row);
    if (filter === "tools") return isTool(row);
    if (filter === "effects") return isEffect(row);
    return true;
  }

  function normalizeRows(input) {
    if (!Array.isArray(input)) throw new Error("Run journal rows must be an array.");
    var seen = {};
    var previous = -1;
    var rows = input.slice(0, MAX_RENDERED_ROWS);
    rows.forEach(function (row) {
      if (!row || typeof row !== "object" || Array.isArray(row)) {
        throw new Error("Run journal row must be an object.");
      }
      var id = rowId(row);
      if (!id || seen[id]) throw new Error("Run journal row IDs must be non-empty and unique.");
      seen[id] = true;
      if (!rowKind(row) || !rowStatus(row)) {
        throw new Error("Run journal row kind and status must be non-empty.");
      }
      var sequence = firstSequence(row);
      var last = lastSequence(row);
      if (!isFinite(sequence) || !isFinite(last) || sequence < 0 || last < sequence || sequence < previous) {
        throw new Error("Run journal rows are not chronological.");
      }
      var sequences = sourceSequences(row);
      var ids = sourceIds(row);
      if (!Array.isArray(sequences) || !Array.isArray(ids) || sequences.length !== ids.length ||
          sequences.some(function (item) { return typeof item !== "number" || !Number.isSafeInteger(item) || item < 0; }) ||
          ids.some(function (item) { return typeof item !== "string" || !item.trim(); })) {
        throw new Error("Run journal source evidence must contain correlated sequence and ID arrays.");
      }
      previous = sequence;
    });
    return { rows: rows, truncated: input.length > rows.length, totalInput: input.length };
  }

  function statusLabel(status) {
    var labels = {
      running: "Выполняется",
      streaming: "Поток данных",
      prepared: "Подготовлено",
      received: "Получено",
      accepted: "Принято",
      rejected: "Отклонено",
      waiting: "Ожидает",
      waiting_confirmation: "Ждёт подтверждения",
      awaiting_confirmation: "Ждёт подтверждения",
      awaiting_user: "Ждёт пользователя",
      dispatched: "Отправлено на выполнение",
      ok: "Выполнено",
      completed: "Завершено",
      completed_with_errors: "Завершено с ошибками",
      committed: "Эффект подтверждён",
      persisted: "Сохранено",
      recorded: "Записано",
      projected: "Спроецировано",
      missing: "Нет evidence",
      failed: "Ошибка",
      error: "Ошибка",
      unknown: "Эффект неизвестен",
      cancelled: "Отменено",
      partial: "Частичный результат",
      partial_failure: "Частичная ошибка",
      interrupted: "Прервано",
      interrupted_unknown: "Прервано, эффект неизвестен",
      runtime_error: "Ошибка runtime",
      invalid_model_response: "Некорректный ответ модели",
      compaction_failed: "Ошибка сжатия контекста",
      blocked: "Заблокировано",
      refused: "Отказ модели",
      removed: "Удалено"
    };
    return labels[status] || boundedText(status || "recorded", 80);
  }

  function titleLabel(row) {
    var labels = {
      "run.started": "Запуск начат",
      "turn.started": "Пользовательский запрос принят",
      "turn.ended": "Запуск завершён",
      "step.started": "Шаг модели начат",
      "step.ended": "Шаг модели завершён",
      "model.request.prepared": "Запрос модели подготовлен",
      "llm.request": "Запрос отправлен модели",
      "llm.response": "Получен исходный ответ модели",
      "assistant.chunk": "Получена часть stream",
      "llm.failure": "Запрос модели завершился ошибкой",
      "agent.response.rejected": "Ответ модели отклонён",
      "model.attempt.rejected": "Попытка модели отклонена",
      "model.response.accepted": "Ответ модели принят",
      "tool.call.recorded": "Вызов tool принят",
      "tool.execution.started": "Выполнение tool начато",
      "tool.execution.completed": "Tool вернул результат",
      "tool.execution.finished": "Выполнение tool завершено",
      "tool.result.recorded": "Результат tool сохранён",
      "domain.effect.prepared": "Изменение подготовлено",
      "domain.effect.dispatched": "Изменение отправлено",
      "domain.effect.verified": "Фактический эффект проверен",
      "run.summary.created": "Сводка запуска создана",
      "ui.projected": "Ответ подготовлен для интерфейса",
      "diagnostic.evidence.missing": "В журнале отсутствует обязательное evidence",
      "user.message.appended": "Запрос пользователя сохранён",
      "assistant.message.appended": "Ответ ассистента сохранён",
      "artifact.revision.created": "Создана версия артефакта",
      "artifact.remove": "Артефакт удалён"
    };
    return labels[rowKind(row)] || boundedText(value(row, "Title", "title", rowKind(row) || "Этап"), 512);
  }

  function layer(row) {
    var kind = rowKind(row);
    if (isModel(row)) return "Модель";
    if (isTool(row)) return "Tool runtime";
    if (/^domain\.effect\./.test(kind)) return "Фактический эффект";
    if (/^artifact\./.test(kind)) return "Артефакт";
    if (/^ui\./.test(kind)) return "UI projection";
    if (/^user\./.test(kind)) return "Пользователь";
    return "Run lifecycle";
  }

  function tone(row) {
    if (isProblem(row)) return "problem";
    if (isWaiting(row)) return "waiting";
    if (rowKind(row) === "domain.effect.verified") return "verified";
    return "neutral";
  }

  function timeLabel(row) {
    var input = value(row, "CreatedUtc", "createdUtc", "");
    if (!input) return "";
    var date = new Date(input);
    if (isNaN(date.getTime())) return "";
    return date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  }

  function identityText(row) {
    var tool = value(row, "ToolId", "toolId", "");
    return tool ? boundedText(tool, 64) : "";
  }

  function rowNote(row) {
    var kind = rowKind(row);
    if (kind === "diagnostic.evidence.missing") {
      return "Нужная граница не записана. Это не доказывает ни успех, ни ошибку выполнения.";
    }
    if (kind === "ui.projected") {
      return "Это подтверждает построение UI projection, но не фактическую доставку или отрисовку WebView.";
    }
    if (isProblem(row) && rowStatus(row) === "rejected") {
      return "Эта попытка отклонена. Следующая попытка или repair показаны отдельными строками и не заменяют исходный ответ.";
    }
    if (kind === "domain.effect.verified") {
      return "Статус взят из сохранённого domain/read-back evidence, а не из текста ответа модели.";
    }
    return "";
  }

  function appendText(parent, tag, className, text) {
    var node = document.createElement(tag);
    node.className = className || "";
    node.textContent = text || "";
    parent.appendChild(node);
    return node;
  }

  function unmountJson(host) {
    if (!host) return;
    if (window.RNAssistantViewerRegistry) window.RNAssistantViewerRegistry.unmount(host);
    else host.replaceChildren();
  }

  function mountJson(host, text, completeness) {
    var registry = window.RNAssistantViewerRegistry;
    if (!registry || !registry.has("json")) {
      host.textContent = "JSON viewer недоступен.";
      return;
    }
    registry.mount("json", host, {
      text: text === null || text === undefined ? "" : String(text),
      completeness: completeness || "full",
      mode: "tree",
      onCopy: window.copyTextResult
    });
  }

  function evidenceJson(row) {
    return JSON.stringify({
      sourceEventSeqs: sourceSequences(row),
      sourceEventIds: sourceIds(row),
      runId: value(row, "RunId", "runId", null),
      turnId: value(row, "TurnId", "turnId", null),
      stepId: value(row, "StepId", "stepId", null),
      modelAttemptId: value(row, "ModelAttemptId", "modelAttemptId", null),
      toolCallId: value(row, "ToolCallId", "toolCallId", null),
      toolId: value(row, "ToolId", "toolId", null),
      mutationId: value(row, "MutationId", "mutationId", null),
      journalRunId: value(row, "JournalRunId", "journalRunId", null),
      resourceRefs: value(row, "ResourceRefs", "resourceRefs", []) || []
    });
  }

  function setDetailsMounted(details, row, mounted) {
    var hosts = details.querySelectorAll(".rn-run-journal-json");
    if (!mounted) {
      Array.prototype.slice.call(hosts).forEach(unmountJson);
      return;
    }
    if (details.getAttribute("data-mounted") === "true") return;
    var dataHost = details.querySelector(".rn-run-journal-data");
    var evidenceHost = details.querySelector(".rn-run-journal-evidence");
    mountJson(dataHost, value(row, "DataJson", "dataJson", "{}"),
      value(row, "DataTruncated", "dataTruncated", false) ? "preview" : "full");
    mountJson(evidenceHost, evidenceJson(row), "full");
    details.setAttribute("data-mounted", "true");
  }

  function payloadAction(row) {
    var kind = rowKind(row);
    var ids = sourceIds(row);
    if (ids.length !== 1) return null;
    if (kind === "model.request.prepared" || kind === "llm.request") {
      return { eventId: ids[0], label: "Показать запрос модели", title: "Фактический запрос модели" };
    }
    if (kind === "llm.response") {
      return { eventId: ids[0], label: "Показать ответ модели", title: "Фактический ответ модели" };
    }
    if (kind === "model.attempt.rejected" || kind === "agent.response.rejected") {
      return { eventId: ids[0], label: "Показать отклонённый ответ", title: "Отклонённый ответ модели" };
    }
    return null;
  }

  function appendPayloadAction(actions, row, options) {
    var definition = payloadAction(row);
    if (!definition || typeof options.onLoadPayload !== "function") return null;
    var section = document.createElement("section");
    section.className = "rn-run-journal-payload hidden";
    appendText(section, "h4", "", definition.title);
    var host = document.createElement("div");
    host.className = "rn-run-journal-payload-host";
    section.appendChild(host);

    var button = actionButton(definition.label, function () {
      button.disabled = true;
      button.textContent = "Загружаю…";
      Promise.resolve(options.onLoadPayload(definition.eventId)).then(function (response) {
        var text = value(response, "Text", "text", "");
        var truncated = !!value(response, "TextTruncated", "textTruncated", false);
        var contentType = String(value(response, "ContentType", "contentType", "") || "");
        section.classList.remove("hidden");
        if (/json/i.test(contentType)) {
          mountJson(host, text, truncated ? "preview" : "full");
        } else {
          host.textContent = text + (truncated ? "\n\n[Показан только bounded preview.]" : "");
        }
        button.textContent = "Payload загружен";
      }).catch(function (error) {
        section.classList.remove("hidden");
        host.textContent = "Не удалось загрузить payload: " + (error && error.message ? error.message : String(error));
        button.textContent = "Повторить";
      }).then(function () {
        button.disabled = false;
      });
    });
    actions.appendChild(button);
    return section;
  }

  function actionButton(label, onClick) {
    var button = document.createElement("button");
    button.type = "button";
    button.className = "secondary rn-run-journal-action";
    button.textContent = label;
    button.addEventListener("click", function (event) {
      event.preventDefault();
      event.stopPropagation();
      onClick();
    });
    return button;
  }

  function appendNavigationActions(root, row, options) {
    if (typeof options.onNavigate !== "function") return;
    var sequences = sourceSequences(row).map(Number).filter(function (item) { return isFinite(item); });
    if (sequences.length) {
      var minimum = Math.min.apply(Math, sequences);
      var maximum = Math.max.apply(Math, sequences);
      root.appendChild(actionButton("Диапазон событий #" + minimum + (maximum === minimum ? "" : "…#" + maximum), function () {
        options.onNavigate("sourceRange", { min: minimum, max: maximum }, "raw");
      }));
    }
    var callId = value(row, "ToolCallId", "toolCallId", "");
    if (callId) {
      root.appendChild(actionButton("Только этот tool call", function () {
        options.onNavigate("toolCallId", callId, "run-causal");
      }));
    }
    var runId = value(row, "RunId", "runId", "");
    if (runId && options.activeRunId !== runId) {
      root.appendChild(actionButton("Только этот запуск", function () {
        options.onNavigate("runId", runId, "run-causal");
      }));
    }
  }

  function renderRow(row, options) {
    var id = rowId(row);
    var details = document.createElement("details");
    details.className = "rn-run-journal-row tone-" + tone(row);
    details.setAttribute("data-row-id", id);
    details.open = !!(options.expanded && options.expanded[id]);

    var summary = document.createElement("summary");
    summary.className = "rn-run-journal-row-summary";
    var marker = appendText(summary, "span", "rn-run-journal-marker", "");
    marker.setAttribute("aria-hidden", "true");
    var main = document.createElement("span");
    main.className = "rn-run-journal-row-main";
    var head = document.createElement("span");
    head.className = "rn-run-journal-row-head";
    appendText(head, "span", "rn-run-journal-layer", layer(row));
    appendText(head, "span", "rn-run-journal-title", titleLabel(row));
    var status = appendText(head, "span", "rn-run-journal-status", statusLabel(rowStatus(row)));
    status.setAttribute("data-status", rowStatus(row));
    main.appendChild(head);
    var meta = document.createElement("span");
    meta.className = "rn-run-journal-row-meta";
    var sequence = firstSequence(row);
    var last = lastSequence(row);
    var duration = value(row, "DurationMs", "durationMs", null);
    meta.textContent = [
      "#" + sequence + (last !== sequence ? "…#" + last : ""),
      timeLabel(row),
      duration === null ? "" : Number(duration) + " мс",
      identityText(row)
    ].filter(Boolean).join(" · ");
    main.appendChild(meta);
    summary.appendChild(main);
    details.appendChild(summary);

    var body = document.createElement("div");
    body.className = "rn-run-journal-row-body";
    var note = rowNote(row);
    if (note) appendText(body, "p", "rn-run-journal-note", note);
    var actions = document.createElement("div");
    actions.className = "rn-run-journal-actions";
    appendNavigationActions(actions, row, options);
    var payloadSection = appendPayloadAction(actions, row, options);
    if (actions.childElementCount) body.appendChild(actions);
    if (payloadSection) body.appendChild(payloadSection);

    var dataSection = document.createElement("section");
    dataSection.className = "rn-run-journal-json-section";
    appendText(dataSection, "h4", "", "Содержимое этапа");
    var dataHost = document.createElement("div");
    dataHost.className = "rn-run-journal-json rn-run-journal-data";
    dataSection.appendChild(dataHost);
    body.appendChild(dataSection);

    var technical = document.createElement("details");
    technical.className = "rn-run-journal-technical";
    appendText(technical, "summary", "", "Технические связи и ID");
    var evidenceSection = document.createElement("section");
    evidenceSection.className = "rn-run-journal-json-section";
    var evidenceHost = document.createElement("div");
    evidenceHost.className = "rn-run-journal-json rn-run-journal-evidence";
    evidenceSection.appendChild(evidenceHost);
    technical.appendChild(evidenceSection);
    body.appendChild(technical);
    details.appendChild(body);

    details.addEventListener("toggle", function () {
      if (typeof options.onExpandedChange === "function") options.onExpandedChange(id, details.open);
      if (details.open) setDetailsMounted(details, row, true);
      else {
        setDetailsMounted(details, row, false);
        details.setAttribute("data-mounted", "false");
      }
    });
    if (details.open) setDetailsMounted(details, row, true);
    return details;
  }

  function metric(root, label, number, toneName) {
    var item = document.createElement("div");
    item.className = "rn-run-journal-metric" + (toneName ? " tone-" + toneName : "");
    appendText(item, "strong", "", String(number));
    appendText(item, "span", "", label);
    root.appendChild(item);
  }

  function uniqueToolCallCount(rows) {
    var ids = {};
    var uncorrelatedCalls = 0;
    rows.forEach(function (row) {
      var id = value(row, "ToolCallId", "toolCallId", "");
      if (id) ids[String(id).toLowerCase()] = true;
      else if (rowKind(row) === "tool.call.recorded") uncorrelatedCalls += 1;
    });
    return Object.keys(ids).length + uncorrelatedCalls;
  }

  function renderHeader(root, rows, filter, options) {
    var problems = rows.filter(isProblem).length;
    var tools = uniqueToolCallCount(rows);
    var effects = rows.filter(function (row) { return rowKind(row) === "domain.effect.verified"; }).length;
    var missing = rows.filter(function (row) { return rowKind(row) === "diagnostic.evidence.missing"; }).length;
    var terminal = rows.slice().reverse().filter(function (row) {
      return rowKind(row) === "turn.ended" || rowKind(row) === "run.ended";
    })[0];

    var summary = document.createElement("section");
    summary.className = "rn-run-journal-summary";
    var metrics = document.createElement("div");
    metrics.className = "rn-run-journal-metrics";
    metric(metrics, "Загружено строк", rows.length, "neutral");
    metric(metrics, "Проблемы", problems, problems ? "problem" : "neutral");
    metric(metrics, "Уникальные tool calls", tools, "neutral");
    metric(metrics, "Effect evidence", effects, effects ? "verified" : "neutral");
    metric(metrics, "Пробелы evidence", missing, missing ? "problem" : "neutral");
    metric(metrics, "Итог", terminal ? statusLabel(rowStatus(terminal)) : "Не найден в выборке", terminal && isProblem(terminal) ? "problem" : "neutral");
    summary.appendChild(metrics);

    var toolbar = document.createElement("div");
    toolbar.className = "rn-run-journal-toolbar";
    var filters = document.createElement("div");
    filters.className = "rn-run-journal-filters";
    filters.setAttribute("role", "toolbar");
    filters.setAttribute("aria-label", "Фильтр журнала запуска");
    FILTERS.forEach(function (definition) {
      var count = rows.filter(function (row) { return matchesFilter(row, definition.id); }).length;
      var button = actionButton(definition.label + " " + count, function () {
        if (typeof options.onFilterChange === "function") options.onFilterChange(definition.id);
      });
      button.className = "rn-run-journal-filter" + (filter === definition.id ? " active" : "");
      button.setAttribute("aria-pressed", filter === definition.id ? "true" : "false");
      filters.appendChild(button);
    });
    toolbar.appendChild(filters);
    var expand = document.createElement("div");
    expand.className = "rn-run-journal-expand-actions";
    var problemIds = rows.filter(isProblem).slice(0, MAX_BULK_EXPANDED_ROWS).map(rowId);
    var expandLabel = problems > MAX_BULK_EXPANDED_ROWS
      ? "Развернуть первые " + MAX_BULK_EXPANDED_ROWS + " проблем"
      : "Развернуть проблемы";
    expand.appendChild(actionButton(expandLabel, function () {
      if (typeof options.onExpandedSet === "function") {
        options.onExpandedSet(problemIds, true);
      }
    }));
    expand.appendChild(actionButton("Свернуть всё", function () {
      if (typeof options.onExpandedSet === "function") options.onExpandedSet(rows.map(rowId), false);
    }));
    toolbar.appendChild(expand);
    summary.appendChild(toolbar);
    root.appendChild(summary);
  }

  function renderError(root, error) {
    root.replaceChildren();
    var message = document.createElement("div");
    message.className = "rn-run-journal-empty is-error";
    message.textContent = "Журнал не отображён: " + error.message;
    root.appendChild(message);
    return { displayed: 0, problems: 0, truncated: false, error: error.message };
  }

  function unmount(root) {
    if (!root) return;
    Array.prototype.slice.call(root.querySelectorAll(".rn-run-journal-json")).forEach(unmountJson);
    Array.prototype.slice.call(root.querySelectorAll(".rn-run-journal-payload-host")).forEach(unmountJson);
    root.replaceChildren();
  }

  function render(root, input, options) {
    options = options || {};
    if (!root) throw new Error("Run journal root is required.");
    unmount(root);
    try {
      var normalized = normalizeRows(input === undefined ? [] : input);
      var rows = normalized.rows;
      var filter = FILTERS.some(function (item) { return item.id === options.filter; }) ? options.filter : "all";
      renderHeader(root, rows, filter, options);
      if (normalized.truncated) {
        appendText(root, "div", "rn-run-journal-limit",
          "Достигнут UI-лимит " + MAX_RENDERED_ROWS + " строк. Используйте run/search filter или bounded export.");
      }
      var visible = rows.filter(function (row) { return matchesFilter(row, filter); });
      var list = document.createElement("div");
      list.className = "rn-run-journal-list";
      list.setAttribute("role", "list");
      list.setAttribute("aria-label", "Хронологический журнал запуска");
      visible.forEach(function (row) {
        var item = renderRow(row, options);
        item.setAttribute("role", "listitem");
        list.appendChild(item);
      });
      if (!visible.length) appendText(list, "div", "rn-run-journal-empty", "Для выбранного фильтра строк нет.");
      root.appendChild(list);
      return {
        displayed: visible.length,
        loaded: rows.length,
        problems: rows.filter(isProblem).length,
        truncated: normalized.truncated,
        totalInput: normalized.totalInput
      };
    } catch (error) {
      return renderError(root, error);
    }
  }

  window.RNAssistantRunJournal = {
    render: render,
    unmount: unmount,
    isProblem: isProblem,
    matchesFilter: matchesFilter,
    maxRenderedRows: MAX_RENDERED_ROWS,
    maxBulkExpandedRows: MAX_BULK_EXPANDED_ROWS
  };
}());
