(function () {
  "use strict";

  var MAX_RENDERED_ROWS = 1000;
  var MAX_BULK_EXPANDED_ROWS = 50;
  var OVERVIEW_PAGE_SIZE = 30;
  var FILTERS = [
    { id: "all", label: "Все" },
    { id: "problems", label: "Проблемы" },
    { id: "attempts", label: "Попытки и отмены" },
    { id: "api", label: "Запросы и ответы" },
    { id: "model", label: "Модель" },
    { id: "tools", label: "Действия" },
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
    if (hasUnknownEffect(row)) return true;
    if (status === "cancelled" || status === "rejected") return false;
    return Number(value(row, "FailureCount", "failureCount", 0) || 0) > 0 ||
      ["failed", "error", "unknown", "rejected", "cancelled", "missing", "blocked", "refused",
        "partial", "partial_failure", "interrupted", "interrupted_unknown", "runtime_error",
        "invalid_model_response", "compaction_failed", "completed_with_errors"]
        .indexOf(status) >= 0 || rowKind(row) === "diagnostic.evidence.missing";
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
    if (filter === "attempts") return !!requestCause(row) || ["cancelled", "rejected"].indexOf(rowStatus(row)) >= 0;
    if (filter === "api") {
      var payload = payloadAction(row);
      return !!(payload && payload.api);
    }
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
      missing: "Нет подтверждения",
      failed: "Ошибка",
      error: "Ошибка",
      unknown: "Эффект неизвестен",
      cancelled: "Отменено",
      partial: "Частичный результат",
      partial_failure: "Частичная ошибка",
      interrupted: "Прервано",
      interrupted_unknown: "Прервано, эффект неизвестен",
      runtime_error: "Ошибка выполнения",
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
      "assistant.chunk": "Получена часть ответа",
      "llm.failure": "Запрос модели завершился ошибкой",
      "agent.response.rejected": "Ответ модели отклонён",
      "model.attempt.rejected": "Попытка модели отклонена",
      "model.response.accepted": "Формат ответа модели принят",
      "tool.call.recorded": "Действие принято",
      "tool.execution.started": "Действие начато",
      "tool.execution.completed": "Получен результат действия",
      "tool.execution.finished": "Действие завершено",
      "tool.result.recorded": "Результат действия сохранён",
      "domain.effect.prepared": "Изменение подготовлено",
      "domain.effect.dispatched": "Изменение отправлено",
      "domain.effect.verified": "Проверка результата действия",
      "run.summary.created": "Сводка запуска создана",
      "ui.projected": "Ответ подготовлен для интерфейса",
      "diagnostic.evidence.missing": "Не хватает сведений о выполнении",
      "user.message.appended": "Запрос пользователя сохранён",
      "assistant.message.appended": "Ответ ассистента сохранён",
      "artifact.revision.created": "Создана версия артефакта",
      "artifact.remove": "Артефакт удалён"
    };
    return labels[rowKind(row)] || boundedText(value(row, "Title", "title", rowKind(row) || "Этап"), 512);
  }

  function timeLabel(row) {
    var input = value(row, "CreatedUtc", "createdUtc", "");
    if (!input) return "";
    var date = new Date(input);
    if (isNaN(date.getTime())) return "";
    return date.toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
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
      return "Ответ подготовлен. Подтверждения его отображения в окне нет.";
    }
    if (rowStatus(row) === "rejected") {
      return "Эта попытка отклонена. Повторные попытки показаны отдельно.";
    }
    if (kind === "domain.effect.verified") {
      return "Сохранены сведения о проверке результата.";
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

  function mountJson(host, text, completeness, expandedPaths) {
    var registry = window.RNAssistantViewerRegistry;
    if (!registry || !registry.has("json")) {
      host.textContent = "JSON viewer недоступен.";
      return;
    }
    registry.mount("json", host, {
      text: text === null || text === undefined ? "" : String(text),
      completeness: completeness || "full",
      mode: "tree", compact: true,
      expandedPaths: expandedPaths,
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

  function payloadAction(row) {
    var kind = rowKind(row);
    var ids = sourceIds(row);
    if (ids.length !== 1) return null;
    if (kind === "model.request.prepared" || kind === "llm.request") {
      return {
        eventId: ids[0],
        label: "Показать запрос к модели",
        title: "Запрос к модели",
        api: true
      };
    }
    if (kind === "llm.response") {
      return {
        eventId: ids[0],
        label: "Показать ответ модели",
        title: "Ответ модели",
        api: true
      };
    }
    if (kind === "model.attempt.rejected" || kind === "agent.response.rejected") {
      return {
        eventId: ids[0],
        label: "Показать отклонённый ответ",
        title: "Отклонённый ответ модели",
        rejected: true,
        api: false
      };
    }
    return null;
  }

  function createPayloadView(row, options) {
    var definition = payloadAction(row);
    if (!definition || typeof options.onLoadPayload !== "function") return null;
    var section = document.createElement("section"); section.className = "rn-run-journal-payload hidden";
    var host = appendText(section, "div", "rn-run-journal-payload-host", "");
    var retry = actionButton("Повторить загрузку", function () { load(true); }); retry.hidden = true; section.appendChild(retry);
    var pending = null, loaded = false;
    function cancel() {
      if (pending) pending.abort();
      pending = null; loaded = false; unmountJson(host); section.classList.add("hidden");
    }
    function pause() {
      if (!pending) return;
      pending.abort(); pending = null; loaded = false;
      host.textContent = "Загрузка приостановлена."; retry.hidden = false;
    }
    function load(force) {
      section.classList.remove("hidden");
      if (pending || loaded && !force) return;
      var operation = new AbortController(); pending = operation;
      retry.hidden = true; unmountJson(host); host.textContent = "Загружаю содержимое…";
      Promise.resolve().then(function () {
        if (pending === operation) return options.onLoadPayload(definition.eventId, operation.signal);
      }).then(function (response) {
        if (pending !== operation || !options.isAlive()) return;
        var text = value(response, "Text", "text", "");
        var truncated = !!value(response, "TextTruncated", "textTruncated", false);
        if (/json/i.test(String(value(response, "ContentType", "contentType", "")))) {
          mountJson(host, text, truncated ? "preview" : "full", definition.rejected ? [
            '$["response"]', '$["tool_calls"]', '$["tool_calls"][0]', '$["response"]["tool_calls"]', '$["response"]["tool_calls"][0]'
          ] : undefined);
        } else host.textContent = text + (truncated ? "\n\n[Показан фрагмент.]" : "");
        loaded = true;
      }).catch(function (error) {
        if (pending !== operation || !options.isAlive()) return;
        host.textContent = "Не удалось загрузить содержимое: " + (error && error.message ? error.message : String(error));
        retry.hidden = false;
      }).then(function () { if (pending === operation) pending = null; });
    }
    options.payloadCleanups.push(cancel); options.payloadPauses.push(pause);
    return { section: section, load: load, cancel: cancel };
  }

  function actionButton(label, onClick) {
    var button = document.createElement("button");
    button.type = "button";
    button.className = "button compact ghost rn-run-journal-action";
    button.textContent = label;
    button.addEventListener("click", function (event) {
      event.preventDefault();
      event.stopPropagation();
      onClick();
    });
    return button;
  }

  function actionMenu(root, label) {
    var trigger = RNAssistantActionMenu.createTrigger(label);
    var body = document.createElement("div"); body.hidden = true;
    trigger.addEventListener("click", function () { window.RNAssistantActionMenu.openButtons(trigger, body); });
    root.appendChild(trigger); root.appendChild(body); return body;
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
      root.appendChild(actionButton("Только этот вызов", function () {
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

  function displayCause(row) {
    var evidence = value(row, "ExecutionEvidence", "executionEvidence", null);
    if (value(evidence, "Dispatch", "dispatch", "") === "NotDispatched" &&
        value(row, "ErrorCode", "errorCode", "") === "excel_sheet_already_exists") {
      return "Лист уже существует. Создание не выполнено.";
    }
    return boundedText(value(row, "Summary", "summary", ""), 2000);
  }

  function requestCause(row) { return value(row, "RequestCause", "requestCause", null); }
  function requestCauseLabel(cause) {
    return {
      ProviderRetry: "Повтор после сбоя API", FormatRepair: "Исправление формата ответа",
      CompletionRejected: "Завершение отклонено", PartialFinishRecheck: "Перепроверка частичного результата",
      ReflectionLimit: "Обратная связь после рефлексии", InputReceived: "Продолжение после входящего сообщения"
    }[value(cause, "Kind", "kind", "")] || "Повторный запрос";
  }

  function renderRequestCause(host, row, options, includeTitle) {
    var cause = requestCause(row);
    if (!cause) return;
    var section = appendText(host, "section", "rn-run-journal-request-cause", "");
    if (includeTitle !== false) appendText(section, "h4", "", requestCauseLabel(cause));
    appendText(section, "p", "rn-run-journal-note", boundedText(value(cause, "Feedback", "feedback", ""), 4000));
    var status = value(cause, "StatusCode", "statusCode", null);
    var delay = value(cause, "DelayMilliseconds", "delayMilliseconds", null);
    var details = [value(cause, "FailureKind", "failureKind", ""), status === null ? "" : "HTTP " + status,
      delay === null ? "" : "Пауза перед повтором: " + delay + " мс"].filter(Boolean).join(" · ");
    if (details) appendText(section, "p", "rn-run-journal-detail-meta", details);
    var attempt = value(cause, "SourceModelAttemptId", "sourceModelAttemptId", "");
    var run = correlation(row, "RunId");
    var source = attempt && run ? (options.sourceRows || []).filter(function (item) {
      return correlation(item, "RunId") === run && correlation(item, "ModelAttemptId") === attempt;
    }) : [];
    var rejected = source.filter(function (item) { return /^(model.attempt.rejected|agent.response.rejected)$/.test(rowKind(item)); });
    var replies = rejected.length ? rejected : source.filter(function (item) { return rowKind(item) === "llm.response"; });
    var payload = replies.length === 1 ? createPayloadView(replies[0], options) : null;
    if (payload) {
      section.appendChild(actionButton(value(cause, "Kind", "kind", "") === "InputReceived"
        ? "Ответ модели до входящего сообщения" : "Ответ модели, вызвавший повтор", function () { payload.load(); }));
      section.appendChild(payload.section);
    } else appendText(section, "p", "rn-run-journal-detail-meta",
      source.some(function (item) { return rowKind(item) === "llm.failure"; })
        ? "Попытка завершилась ошибкой API. Ответ модели для неё не записан."
        : "Исходный ответ не найден в загруженной выборке. Связь по соседству событий не подставляется.");
    var requests = source.filter(isRequest);
    if (requests.length === 1 && options.onInspect)
      section.appendChild(actionButton("Исходный запрос", function () { options.onInspect(rowId(requests[0])); }));
  }

  function hasUnknownEffect(row) {
    var evidence = value(row, "ExecutionEvidence", "executionEvidence", null);
    return value(evidence, "Effect", "effect", "") === "Unknown" ||
      ["unknown", "interrupted_unknown"].indexOf(rowStatus(row)) >= 0;
  }

  function issuePriority(row) {
    return (hasUnknownEffect(row) ? 4 : 0) + (displayCause(row) ? 1 : 0);
  }

  function effectExplanation(row) {
    var evidence = value(row, "ExecutionEvidence", "executionEvidence", null);
    var effect = value(evidence, "Effect", "effect", "");
    if (hasUnknownEffect(row))
      return "Результат не подтверждён. Проверьте фактическое состояние перед повторной записью.";
    if (value(evidence, "Dispatch", "dispatch", "") === "NotDispatched") return "Действие не отправлено на выполнение.";
    if (effect === "VerifiedNoChange") return "Проверка подтвердила отсутствие изменений.";
    if (effect === "VerifiedChange") return "Изменение подтверждено проверкой результата.";
    return "";
  }

  function correlation(row, name) {
    return String(value(row, name, name.charAt(0).toLowerCase() + name.slice(1), "") || "");
  }

  function treeKey(parts) { return "group:" + JSON.stringify(parts); }

  function group(type, key, rows, title) {
    return { type: type, id: treeKey(key), rows: rows, title: title || "", children: [] };
  }

  function eventNode(row) { return { type: "event", id: rowId(row), rows: [row], row: row }; }

  function isRunBoundary(row) {
    return /^(run\.(started|ended|summary.created)|turn\.(started|ended)|user.message.appended|assistant.message.appended|ui.projected)$/.test(rowKind(row));
  }

  function terminalRow(rows, kinds) {
    return rows.slice().reverse().filter(function (row) { return kinds.indexOf(rowKind(row)) >= 0; })[0];
  }

  function actionTitle(rows) {
    var tool = rows.map(function (row) { return correlation(row, "ToolId"); }).filter(Boolean)[0];
    var labels = {
      "core.find": "Поиск источников", "core.read": "Чтение источника",
      "core.tools.search": "Поиск возможностей", "core.tools.read": "Описание возможности",
      "excel.read_cells": "Чтение ячеек", "excel.write_cells": "Запись ячеек", "excel.create_sheet": "Создание листа"
    };
    return labels[tool] || tool || "Действие";
  }

  function isRequest(row) { return /^(model.request.prepared|llm.request)$/.test(rowKind(row)); }
  function isReply(row) { return /^(llm.response|llm.failure|model.response.accepted|model.attempt.rejected|agent.response.rejected)$/.test(rowKind(row)); }

  // Only recorded request/attempt/call identities link an exchange. A unique
  // logical step may attach metadata, but lifecycle rows never create exchanges.
  function buildTree(rows) {
    var runs = new Map(), trace = new Map();
    function identity(row) {
      if (trace.has(row)) return trace.get(row);
      var fields = { attempt: correlation(row, "ModelAttemptId"), request: "", step: correlation(row, "StepId") };
      if ((isRequest(row) || isReply(row) || /^step\./.test(rowKind(row))) && !value(row, "DataTruncated", "dataTruncated", false) && window.RNAssistantJsonViewer) {
        var parsed = RNAssistantJsonViewer.parse(value(row, "DataJson", "dataJson", ""));
        if (parsed.ok && parsed.root.type === "object") {
          var matches = parsed.root.entries.filter(function (entry) { return entry.key.value === "requestId" || entry.key.value === "RequestId"; });
          if (matches.length === 1 && matches[0].value.type === "string") fields.request = matches[0].value.value;
        }
      }
      trace.set(row, fields); return fields;
    }
    rows.forEach(function (row) {
      var id = correlation(row, "RunId");
      if (!runs.has(id)) runs.set(id, group("run", ["run", id], []));
      runs.get(id).rows.push(row);
    });
    runs.forEach(function (run, runId) {
      run.runId = runId;
      var exchanges = new Map(), seeds = new Map(), links = { request: new Map(), attempt: new Map(), step: new Map() }, calls = new Map();
      function register(node, fields) {
        ["request", "attempt", "step"].forEach(function (field) {
          var id = fields[field]; if (!id || !runId) return;
          node.identities[field].add(id);
          if (!links[field].has(id)) links[field].set(id, new Set());
          links[field].get(id).add(node);
        });
      }
      function resolve(fields, allowStep) {
        if (!runId) return null;
        var candidates = null;
        ["request", "attempt"].forEach(function (field) {
          var known = fields[field] && links[field].get(fields[field]);
          if (known) candidates = candidates === null ? Array.from(known) : candidates.filter(function (node) { return known.has(node); });
        });
        if (candidates !== null) {
          candidates = candidates.filter(function (node) {
            return ["request", "attempt"].every(function (field) {
              return !fields[field] || !node.identities[field].size || node.identities[field].has(fields[field]);
            });
          });
          return candidates.length === 1 ? candidates[0] : null;
        }
        if (fields.request || fields.attempt || !allowStep) return null;
        var steps = links.step.get(fields.step);
        return steps && steps.size === 1 ? Array.from(steps)[0] : null;
      }
      function seed(row, request) {
        var fields = identity(row), node = resolve(fields, !request);
        if (!node) {
          var id = runId && (fields.request || fields.attempt) || rowId(row);
          var key = JSON.stringify([request ? "request" : "unbound-response", fields.request ? "requestId" : fields.attempt ? "attemptId" : "event", id]);
          if (!exchanges.has(key)) {
            node = group("exchange", ["exchange", runId, key], []);
            node.identities = { request: new Set(), attempt: new Set(), step: new Set() };
            exchanges.set(key, node);
          }
          node = exchanges.get(key);
        }
        if (request && !node.request) node.request = row;
        register(node, fields); seeds.set(row, node);
      }
      run.rows.filter(isRequest).forEach(function (row) { seed(row, true); });
      run.rows.filter(isReply).forEach(function (row) { seed(row, false); });
      run.rows.forEach(function (row) {
        var call = correlation(row, "ToolCallId"); if (!call || !runId) return;
        var fields = identity(row), owner = resolve(fields, true);
        if (!calls.has(call)) calls.set(call, new Set());
        if (owner || fields.attempt || fields.request || fields.step) calls.get(call).add(owner);
      });
      var bookkeeping = [], unbound = [];
      run.rows.forEach(function (row) {
        if (isRunBoundary(row)) { bookkeeping.push(row); return; }
        var fields = identity(row), owner = seeds.get(row) || resolve(fields, true);
        if (!owner && !fields.attempt && !fields.request) {
          var known = calls.get(correlation(row, "ToolCallId"));
          if (known && known.size === 1) owner = Array.from(known)[0];
        }
        if (owner) owner.rows.push(row);
        else if (isTool(row)) unbound.push(row);
        else bookkeeping.push(row);
      });
      function children(parent) {
        var actions = new Map(), secondary = [];
        parent.rows.forEach(function (row) {
          if (isTool(row)) {
            var call = correlation(row, "ToolCallId"), id = call && runId ? call : rowId(row);
            if (!actions.has(id)) {
              var action = group("action", ["action", runId, parent.id, id], []);
              action.toolCallId = call; actions.set(id, action);
            }
            actions.get(id).rows.push(row);
          } else if (isRequest(row) || /^(llm.response|llm.failure)$/.test(rowKind(row))) parent.children.push(eventNode(row));
          else secondary.push(row);
        });
        actions.forEach(function (action) {
          action.title = actionTitle(action.rows); action.children = action.rows.map(eventNode); parent.children.push(action);
        });
        parent.children.sort(function (a, b) { return firstSequence(a.rows[0]) - firstSequence(b.rows[0]); });
        if (secondary.length) {
          var service = group("service", ["service", runId, parent.id], secondary, "Служебные события");
          service.children = secondary.map(eventNode); parent.children.push(service);
        }
      }
      var number = 0;
      Array.from(exchanges.values()).sort(function (a, b) {
        return firstSequence(a.request || a.rows[0]) - firstSequence(b.request || b.rows[0]);
      }).forEach(function (exchange) {
        exchange.title = exchange.request ? "Запрос " + (++number) : "Ответ без привязки к запросу";
        if (requestCause(exchange.request)) exchange.title += " · " + requestCauseLabel(requestCause(exchange.request));
        exchange.stepIds = Array.from(exchange.identities.step);
        delete exchange.identities;
        children(exchange); run.children.push(exchange);
      });
      if (unbound.length) {
        var unlinked = group("unlinked", ["unlinked", runId], unbound, "Действия без привязки к запросу");
        children(unlinked); run.children.push(unlinked);
      }
      if (bookkeeping.length) {
        var service = group("service", ["lifecycle", runId], bookkeeping, "Служебные события");
        service.children = bookkeeping.map(eventNode); run.children.push(service);
      }
    });
    return Array.from(runs.values());
  }

  function representative(node) {
    var issue = node.rows.filter(isProblem).sort(function (a, b) { return issuePriority(b) - issuePriority(a); })[0];
    if (node.type === "run") return terminalRow(node.rows, ["turn.ended", "run.ended"]);
    if (node.type === "exchange") return terminalRow(node.rows, ["model.response.accepted", "model.attempt.rejected", "agent.response.rejected", "llm.failure"]) ||
      terminalRow(node.rows, ["llm.response"]) || node.request;
    if (node.type === "action") return issue || terminalRow(node.rows,
      ["tool.execution.completed", "tool.execution.finished", "tool.result.recorded", "domain.effect.verified"]) || node.rows[node.rows.length - 1];
    return node.rows.slice().sort(function (a, b) { return issuePriority(b) - issuePriority(a); })[0];
  }

  function countLabel(count, forms) {
    var lastTwo = count % 100, last = count % 10;
    return count + " " + forms[lastTwo >= 11 && lastTwo <= 14 ? 2 : last === 1 ? 0 : last >= 2 && last <= 4 ? 1 : 2];
  }

  function groupStatus(node) {
    var row = representative(node), problem = node.rows.some(isProblem);
    if (node.type === "service") return node.rows.some(hasUnknownEffect) ? "Не подтверждено" : problem ? "Есть проблемы" : countLabel(node.rows.length, ["событие", "события", "событий"]);
    if (node.type === "exchange") {
      if (!node.request) return "Запрос не найден в выборке";
      if (!row || isRequest(row)) return "Ответ не найден в выборке";
      if (rowKind(row) === "model.response.accepted") return "Ответ принят";
      if (rowStatus(row) === "rejected") return "Ответ отклонён";
      return rowKind(row) === "llm.response" ? "Ответ получен" : statusLabel(rowStatus(row));
    }
    if (node.type === "run") {
      if (row) return statusLabel(rowStatus(row)) + (node.rows.some(hasUnknownEffect) && !hasUnknownEffect(row) ? " · эффект не подтверждён" : !isProblem(row) && problem ? " · были проблемы" : "");
      return node.rows.some(hasUnknownEffect) ? "Эффект не подтверждён" : problem ? "Есть проблемы" : "Итог не найден в выборке";
    }
    if (node.rows.some(hasUnknownEffect)) return "Не подтверждено";
    if (problem) return "Есть проблемы";
    if (node.type === "action") return row ? statusLabel(rowStatus(row)) : "";
    return node.rows.some(function (item) { return rowStatus(item) === "rejected"; }) ? "Были отклонённые ответы" : countLabel(node.rows.length, ["событие", "события", "событий"]);
  }

  function visibleNode(node, filter) {
    return node.rows.some(function (row) { return matchesFilter(row, filter); });
  }

  function visitTree(nodes, callback, path) {
    nodes.forEach(function (node) {
      callback(node, path || []);
      if (node.children) visitTree(node.children, callback, (path || []).concat(node.id));
    });
  }

  // Read a named field from the canonical projection with the shared lossless parser.
  // A duplicate/truncated/invalid wrapper is never silently treated as a complete result.
  function actionPart(rows, part) {
    var reader = window.RNAssistantJsonViewer;
    if (!reader) return { notice: "Просмотр структуры недоступен. Откройте события действия." };
    var candidates = rows.filter(function (row) {
      return part === "arguments" ? /^tool\./.test(rowKind(row)) :
        ["tool.execution.completed", "tool.execution.finished", "tool.result.recorded"].indexOf(rowKind(row)) >= 0;
    });
    if (part !== "arguments") candidates.reverse();
    for (var i = 0; i < candidates.length; i++) {
      var row = candidates[i];
      if (value(row, "DataTruncated", "dataTruncated", false)) return { notice: "Данные события усечены. Доступный фрагмент — в событиях действия." };
      var parsed = reader.parse(value(row, "DataJson", "dataJson", ""));
      if (!parsed.ok || parsed.root.type !== "object") return { notice: "Не удалось прочитать структуру. Исходные данные — в событиях действия." };
      var names = part === "arguments" ? ["arguments"] : ["resultData", "toolResult"];
      for (var j = 0; j < names.length; j++) {
        var entries = parsed.root.entries.filter(function (entry) { return entry.key.value === names[j]; });
        if (entries.length > 1) return { notice: "Поле повторяется. Откройте исходное событие, чтобы увидеть все значения." };
        if (entries.length && entries[0].value.type !== "null") return {
          text: reader.raw(parsed, entries[0].value), row: row
        };
      }
    }
    return { notice: part === "arguments" ? "Аргументов нет в загруженной выборке. Исходные записи — в событиях действия." :
      "Структурированного результата нет в загруженной выборке. Краткий итог показан выше; исходные записи — в событиях действия." };
  }

  function actionContent(body, node, options) {
    var controls = appendText(body, "div", "rn-run-journal-part-controls", "");
    var select = appendText(controls, "select", "rna-form-control compact rn-run-journal-data-kind", "");
    select.setAttribute("aria-label", "Содержимое действия");
    [["result", "Результат"], ["arguments", "Аргументы"]].forEach(function (item) {
      var option = appendText(select, "option", "", item[1]); option.value = item[0];
    });
    select.value = options.parts && options.parts[node.id] === "arguments" ? "arguments" : "result";
    var host = appendText(body, "div", "rn-run-journal-json rn-run-journal-part", "");
    function sync() {
      if (controls.parentNode !== body) body.insertBefore(controls, host);
      unmountJson(host);
      var content = actionPart(node.rows, select.value);
      if (content.notice) appendText(host, "p", "rn-run-journal-note", content.notice);
      else {
        mountJson(host, content.text, "full");
        var toolbar = host.querySelector(".rn-json-toolbar");
        if (toolbar) toolbar.insertBefore(controls, toolbar.firstElementChild);
      }
    }
    select.addEventListener("change", function () {
      if (options.onPartChange) options.onPartChange(node.id, select.value);
      sync();
    });
    sync();
  }

  function detailHeading(host, title, status) {
    var heading = appendText(host, "div", "rn-run-journal-detail-heading", "");
    appendText(heading, "h3", "", title);
    if (status) appendText(heading, "span", "rn-run-journal-status", status);
    return heading;
  }

  function renderEventDetail(host, row, options) {
    var heading = detailHeading(host, titleLabel(row), hasUnknownEffect(row) ? "Не подтверждено" : statusLabel(rowStatus(row)));
    var actions = actionMenu(heading, "Переходы события");
    appendNavigationActions(actions, row, options);
    var duration = value(row, "DurationMs", "durationMs", null);
    appendText(host, "p", "rn-run-journal-detail-meta", ["#" + firstSequence(row), timeLabel(row),
      duration === null ? "" : Number(duration) + " мс", identityText(row), correlation(row, "Target")].filter(Boolean).join(" · "));
    var note = displayCause(row) || effectExplanation(row) || rowNote(row);
    if (note) appendText(host, "p", "rn-run-journal-note", note);
    renderRequestCause(host, row, options);
    var controls = appendText(host, "div", "rn-run-journal-part-controls", "");
    var choice = appendText(controls, "select", "rna-form-control compact rn-run-journal-data-kind", "");
    choice.setAttribute("aria-label", "Содержимое события");
    var payload = createPayloadView(row, options);
    [["data", "Данные"], ["evidence", "Связи"]].concat(payload ? [["payload", "Содержимое"]] : []).forEach(function (item) {
      var option = appendText(choice, "option", "", item[1]); option.value = item[0];
    });
    choice.value = options.parts && ["data", "evidence"].indexOf(options.parts[rowId(row)]) >= 0 ? options.parts[rowId(row)] : "data";
    var dataHost = appendText(host, "div", "rn-run-journal-json rn-run-journal-data", "");
    if (payload) host.appendChild(payload.section);
    function sync() {
      if (controls.parentNode !== host) host.insertBefore(controls, dataHost);
      unmountJson(dataHost);
      dataHost.hidden = choice.value === "payload";
      if (payload && choice.value !== "payload") payload.cancel();
      if (choice.value === "payload") { payload.load(); return; }
      var evidence = choice.value === "evidence";
      mountJson(dataHost, evidence ? evidenceJson(row) : value(row, "DataJson", "dataJson", "{}"),
        !evidence && value(row, "DataTruncated", "dataTruncated", false) ? "preview" : "full");
      var toolbar = dataHost.querySelector(".rn-json-toolbar");
      if (toolbar) toolbar.insertBefore(controls, toolbar.firstElementChild);
    }
    choice.addEventListener("change", function () { if (options.onPartChange) options.onPartChange(rowId(row), choice.value); sync(); });
    sync();
  }

  function uniqueToolCallCount(rows) {
    var ids = new Set();
    var uncorrelatedCalls = 0;
    rows.forEach(function (row) {
      var id = value(row, "ToolCallId", "toolCallId", "");
      if (id) ids.add(JSON.stringify([correlation(row, "RunId"), String(id)]));
      else if (rowKind(row) === "tool.call.recorded") uncorrelatedCalls += 1;
    });
    return ids.size + uncorrelatedCalls;
  }

  function renderHeader(root, rows, filter, options, tree) {
    var problems = rows.filter(isProblem).length;
    var toolbar = options.controls || root;
    root.rnaJournalControls = options.controls;
    var filters = document.createElement("select");
    filters.className = "rna-form-control compact rn-run-journal-filters";
    filters.setAttribute("aria-label", "Фильтр истории");
    FILTERS.forEach(function (definition) {
      var option = appendText(filters, "option", "", definition.label);
      option.value = definition.id;
    });
    filters.value = filter;
    filters.addEventListener("change", function () {
      if (typeof options.onFilterChange === "function") options.onFilterChange(filters.value);
    });
    toolbar.appendChild(filters);
    var expand = options.commands || toolbar;
    root.rnaJournalCommands = [];
    function command(label, action) {
      var button = actionButton(label, action);
      root.rnaJournalCommands.push(button); expand.appendChild(button);
    }
    var exchangeIds = [], allIds = [], problemPaths = new Set();
    var limitedProblems = rows.filter(isProblem).slice(0, MAX_BULK_EXPANDED_ROWS).map(rowId);
    visitTree(tree, function (node, path) {
      if (node.type !== "event") allIds.push(node.id);
      if (node.type === "run" || node.type === "exchange") exchangeIds.push(node.id);
      if (limitedProblems.indexOf(node.id) >= 0) path.concat(node.id).forEach(function (id) { problemPaths.add(id); });
    });
    command("Развернуть запросы", function () {
      if (typeof options.onExpandedSet === "function") options.onExpandedSet(exchangeIds, true);
    });
    var problemIds = Array.from(problemPaths);
    var expandLabel = problems > MAX_BULK_EXPANDED_ROWS
      ? "Развернуть первые " + MAX_BULK_EXPANDED_ROWS + " проблем"
      : "Развернуть проблемы";
    command(expandLabel, function () {
      if (typeof options.onExpandedSet === "function") {
        options.onExpandedSet(problemIds, true);
      }
    });
    command("Свернуть всё", function () {
      if (typeof options.onExpandedSet === "function") options.onExpandedSet(allIds, false);
    });
  }

  function navigatorTitle(node) {
    if (node.type === "run") {
      var date = new Date(value(node.rows[0], "CreatedUtc", "createdUtc", ""));
      return node.runId ? "Запуск" + (isNaN(date.getTime()) ? "" : " · " + date.toLocaleString("ru-RU", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" })) : "Без привязки к запуску";
    }
    return node.title;
  }

  function navigationNode(node, filter, options, latestRun, selectedId, selectedPath) {
    var children = (node.children || []).filter(function (child) { return visibleNode(child, filter); });
    var targeted = !!((options.activeStepId && node.rows.some(function (item) { return correlation(item, "StepId") === options.activeStepId; })) ||
      (options.activeToolCallId && node.rows.some(function (item) { return correlation(item, "ToolCallId") === options.activeToolCallId; })));
    var defaultOpen = filter !== "all" || node.type === "run" && (node.runId === options.activeRunId || node === latestRun) ||
      targeted && (node.type === "run" || node.type === "exchange" || node.type === "unlinked") || selectedPath.indexOf(node.id) >= 0;
    var open = options.expanded && Object.prototype.hasOwnProperty.call(options.expanded, node.id) ? !!options.expanded[node.id] : defaultOpen;
    var uncertain = node.rows.some(hasUnknownEffect), problem = node.rows.some(isProblem);
    var title = node.type === "event" ? titleLabel(node.row) : navigatorTitle(node);
    var status = node.type === "event" ? statusLabel(rowStatus(node.row)) : groupStatus(node);
    return { key: node.id, kind: children.length ? "parent" : "item", selectable: true,
      title: title, tooltip: [title, status].filter(Boolean).join(" · "), ariaLabel: [title, status].filter(Boolean).join(". "),
      iconHtml: window.RNAssistantIcons.render(node.type === "run" ? "clock" : node.type === "exchange" ? "code" : node.type === "action" ? "tool" : node.type === "event" ? "file" : "layers"),
      badge: uncertain ? "?" : problem ? "!" : "", badgeKind: uncertain ? "warning" : problem ? "critical" : "",
      badgeTitle: status, selected: node.id === selectedId, expanded: open,
      children: children.map(function (child) { return navigationNode(child, filter, options, latestRun, selectedId, selectedPath); }) };
  }

  function groupSummary(node) {
    var cause = node.type === "exchange" && requestCause(node.request);
    if (cause) return requestCauseLabel(cause) + ": " + boundedText(value(cause, "Feedback", "feedback", ""), 2000);
    var row = representative(node), summary = row && displayCause(row);
    if (summary) return summary;
    if (node.type === "run" || node.type === "exchange") return "";
    var problem = node.rows.find(function (item) { return isProblem(item) && (displayCause(item) || effectExplanation(item)); });
    if (problem) return displayCause(problem) || effectExplanation(problem);
    if (node.type === "action") return row && (effectExplanation(row) || rowNote(row)) || "";
    var explanation = terminalRow(node.rows.filter(function (item) { return !!displayCause(item); }), ["model.response.accepted", "tool.call.recorded"]);
    return explanation ? displayCause(explanation) : "";
  }

  function structureEntries(node) {
    function item(child) {
      var row = child.type === "event" ? child.row : representative(child);
      return { id: child.id, title: child.type === "event" ? titleLabel(row) : navigatorTitle(child),
        meta: child.type === "exchange" ? timeLabel(child.request || row) : child.type === "action" ?
          child.rows.map(function (entry) { return correlation(entry, "Target"); }).filter(Boolean)[0] : row && timeLabel(row),
        status: child.type === "event" ? statusLabel(rowStatus(row)) : groupStatus(child),
        summary: child.type === "event" ? displayCause(row) || effectExplanation(row) : groupSummary(child),
        actions: child.type === "exchange" ? child.children.filter(function (entry) { return entry.type === "action"; }) : [] };
    }
    if (node.type === "run") return node.children.filter(function (child) { return child.type !== "service"; }).map(item);
    if (node.type === "exchange") {
      var response = terminalRow(node.rows, ["llm.response", "llm.failure", "model.attempt.rejected", "agent.response.rejected"]) ||
        terminalRow(node.rows, ["model.response.accepted"]);
      var entries = [[node.request, "Запрос"], [response, "Ответ"]].map(function (pair) {
        var row = pair[0];
        return { id: row && rowId(row), title: pair[1], meta: row && timeLabel(row),
          status: row ? pair[1] === "Ответ" && node.request ? groupStatus(node) : statusLabel(rowStatus(row)) : "Нет в загруженной выборке",
          summary: row && (displayCause(row) || (pair[1] === "Ответ" ? groupSummary(node) : "") || effectExplanation(row)), actions: [] };
      });
      return entries.concat(node.children.filter(function (child) { return child.type === "action"; }).map(item));
    }
    return node.children.map(item);
  }

  function renderStructure(host, node, options) {
    var entries = structureEntries(node);
    if (!entries.length) return;
    var wrap = appendText(host, "div", "rna-table-wrap rn-run-journal-overview", "");
    var table = appendText(wrap, "table", "rna-table", "");
    table.setAttribute("aria-label", node.type === "run" ? "Запросы и результаты" : "Состав и результаты");
    var header = appendText(appendText(table, "thead", "", ""), "tr", "", "");
    [node.type === "run" ? "Запрос" : "Состав", "Результат"].forEach(function (label) { appendText(header, "th", "", label).setAttribute("scope", "col"); });
    var body = appendText(table, "tbody", "", ""), count = 0;
    var more = actionButton("Ещё", page);
    function page() {
      var end = Math.min(entries.length, count + OVERVIEW_PAGE_SIZE);
      for (; count < end; count++) {
        var entry = entries[count], row = appendText(body, "tr", "", "");
        var label = appendText(row, "td", "", ""), result = appendText(row, "td", "", "");
        if (entry.id) {
          (function (target) {
            var link = actionButton(target.title, function () { options.onInspect(target.id); });
            link.className = "rna-table-link"; label.appendChild(link);
          })(entry);
        } else appendText(label, "span", "", entry.title);
        if (entry.meta) appendText(label, "small", "", entry.meta);
        appendText(result, "span", "", entry.status);
        if (entry.summary) appendText(result, "div", "rn-run-journal-overview-summary", entry.summary);
        (entry.actions || []).slice(0, 3).forEach(function (action) {
          appendText(result, "small", "", [action.title, groupStatus(action), groupSummary(action)].filter(Boolean).join(" · "));
        });
        if (entry.actions && entry.actions.length > 3) appendText(result, "small", "", "Ещё действий: " + (entry.actions.length - 3));
      }
      more.hidden = count >= entries.length;
    }
    page(); wrap.appendChild(more);
  }

  function renderGroupDetail(host, node, options) {
    var heading = detailHeading(host, navigatorTitle(node), node.type === "exchange" ? "" : groupStatus(node));
    var requests = node.type === "run" ? node.children.filter(function (child) { return child.type === "exchange" && child.request; }).length : 0;
    var tools = node.type === "run" || node.type === "exchange" || node.type === "unlinked" ? uniqueToolCallCount(node.rows) : 0;
    var counts = [requests ? countLabel(requests, ["запрос", "запроса", "запросов"]) : "",
      tools ? countLabel(tools, ["действие", "действия", "действий"]) : ""].filter(Boolean).join(" · ");
    if (counts) appendText(host, "p", "rn-run-journal-detail-meta", counts);
    if (node.type === "run" || node.type === "action") {
      var summary = groupSummary(node);
      if (summary) appendText(host, "p", "rn-run-journal-result-summary", summary);
    }
    var target = node.type === "action" && node.rows.map(function (row) { return correlation(row, "Target"); }).filter(Boolean)[0];
    if (target) appendText(host, "p", "rn-run-journal-detail-meta", target);
    if (node.type === "action") { actionContent(host, node, options); return; }
    if (node.type === "exchange" && node.request && sourceIds(node.request).length === 1 && typeof options.onOpenModelContext === "function") {
      heading.appendChild(actionButton("Открыть запрос", function () {
        options.onOpenModelContext({ runId: correlation(node.request, "RunId"), stepId: correlation(node.request, "StepId"), requestEventId: sourceIds(node.request)[0] });
      }));
    }
    if (node.type === "exchange") renderRequestCause(host, node.request, options, false);
    renderStructure(host, node, options);
  }

  function renderError(root, error) {
    unmount(root);
    var message = document.createElement("div");
    message.className = "rn-run-journal-empty is-error";
    message.textContent = "Журнал не отображён: " + error.message;
    root.appendChild(message);
    return { displayed: 0, problems: 0, truncated: false, error: error.message };
  }

  function unmount(root) {
    if (!root) return;
    if (window.RNAssistantActionMenu) RNAssistantActionMenu.closeWithin(root);
    if (root.rnaJournalControls) root.rnaJournalControls.replaceChildren();
    (root.rnaJournalCommands || []).forEach(function (command) { command.remove(); });
    root.rnaJournalCommands = [];
    if (root.rnaNavigationTree && window.RNAssistantNavigationTree) RNAssistantNavigationTree.unmount(root.rnaNavigationTree);
    root.rnaNavigationTree = null;
    (root.rnaPayloadCleanups || []).forEach(function (cancel) { cancel(); });
    root.rnaJournalRender = null;
    root.rnaPayloadCleanups = [];
    root.rnaPayloadPauses = [];
    Array.prototype.slice.call(root.querySelectorAll(".rn-run-journal-json")).forEach(unmountJson);
    Array.prototype.slice.call(root.querySelectorAll(".rn-run-journal-payload-host")).forEach(unmountJson);
    root.replaceChildren();
  }

  function render(root, input, options) {
    options = options || {};
    if (!root) throw new Error("Run journal root is required.");
    unmount(root);
    var renderToken = {};
    root.rnaJournalRender = renderToken;
    options = Object.assign({}, options, { payloadCleanups: root.rnaPayloadCleanups,
      isAlive: function () { return root.rnaJournalRender === renderToken; } });
    try {
      var normalized = normalizeRows(input === undefined ? [] : input);
      var rows = normalized.rows;
      var filter = FILTERS.some(function (item) { return item.id === options.filter; }) ? options.filter : "all";
      var tree = buildTree(rows);
      renderHeader(root, rows, filter, options, tree);
      if (normalized.truncated) {
        appendText(root, "div", "rn-run-journal-limit",
          "Показаны первые " + MAX_RENDERED_ROWS + " событий. Уточните поиск или скачайте историю.");
      }
      var visible = rows.filter(function (row) { return matchesFilter(row, filter); });
      var layout = appendText(root, "div", "rn-run-journal-layout", "");
      var list = appendText(layout, "div", "rn-run-journal-tree rn-run-journal-nav", "");
      root.rnaNavigationTree = list;
      var detail = appendText(layout, "div", "rn-run-journal-detail", "");
      var validExpansionIds = [];
      var nodes = {}, parents = {};
      visitTree(tree, function (node) {
        validExpansionIds.push(node.id);
      });
      function indexNode(node, parent) {
        nodes[node.id] = node; if (parent) parents[node.id] = parent;
        (node.children || []).forEach(function (child) { indexNode(child, node.id); });
      }
      tree.forEach(function (node) { indexNode(node, ""); });
      var latestRun = tree[tree.length - 1], selectedId = options.selectedId && nodes[options.selectedId] && visibleNode(nodes[options.selectedId], filter)
        ? options.selectedId : options.activeToolCallId && Object.keys(nodes).find(function (id) { return nodes[id].toolCallId === options.activeToolCallId && visibleNode(nodes[id], filter); }) ||
          options.activeStepId && Object.keys(nodes).reverse().find(function (id) { return nodes[id].type === "exchange" && nodes[id].stepIds.indexOf(options.activeStepId) >= 0 && visibleNode(nodes[id], filter); }) ||
          (tree.filter(function (node) { return visibleNode(node, filter); }).slice(-1)[0] || {}).id;
      function select(id) {
        if (!nodes[id] || !options.isAlive()) return;
        selectedId = id;
        if (typeof options.onSelect === "function") options.onSelect(id);
        (root.rnaPayloadCleanups || []).forEach(function (cancel) { cancel(); });
        root.rnaPayloadCleanups = [];
        Array.prototype.slice.call(detail.querySelectorAll(".rn-run-journal-json, .rn-run-journal-payload-host")).forEach(unmountJson);
        detail.replaceChildren();
        var node = nodes[id];
        root.rnaPayloadPauses = [];
        var selectionToken = {}; root.rnaJournalSelection = selectionToken;
        var viewerOptions = Object.assign({}, options, { sourceRows: rows, payloadCleanups: root.rnaPayloadCleanups, payloadPauses: root.rnaPayloadPauses,
          onInspect: function (targetId) {
            if (!nodes[targetId]) return;
            if (!visibleNode(nodes[targetId], filter)) {
              if (options.onSelect) options.onSelect(targetId);
              if (options.onFilterChange) { options.onFilterChange("all"); return; }
              filter = "all";
            }
            var parent = parents[targetId];
            while (parent) {
              if (!options.expanded) options.expanded = {};
              options.expanded[parent] = true;
              if (options.onExpandedChange) options.onExpandedChange(parent, true);
              parent = parents[parent];
            }
            select(targetId); renderTree(true);
          },
          isAlive: function () { return options.isAlive() && root.rnaJournalSelection === selectionToken; } });
        detail.dataset.selectionId = id;
        if (node.type === "event") renderEventDetail(detail, node.row, viewerOptions);
        else renderGroupDetail(detail, node, viewerOptions);
      }
      function renderTree(revealSelection) {
        var selectedPath = [], parent = parents[selectedId];
        while (parent) { selectedPath.push(parent); parent = parents[parent]; }
        RNAssistantNavigationTree.render(list, tree.filter(function (node) { return visibleNode(node, filter); }).map(function (node) {
          return navigationNode(node, filter, options, latestRun, selectedId, selectedPath);
        }), { label: "Запуски, запросы и действия", emptyText: "Для выбранного фильтра строк нет.", toggleOnSelect: true, reserveToggleSpace: true, lazyChildren: true,
          revealSelection: revealSelection,
          onSelect: function (item) { select(item.key); },
          onToggle: function (item, expanded) { if (typeof options.onExpandedChange === "function") options.onExpandedChange(item.key, expanded); } });
      }
      renderTree(options.revealSelection);
      if (selectedId) select(selectedId);
      else appendText(detail, "p", "rn-run-journal-empty", "Выберите запуск или запрос слева.");
      return {
        displayed: visible.length,
        loaded: rows.length,
        problems: rows.filter(isProblem).length,
        truncated: normalized.truncated,
        totalInput: normalized.totalInput,
        validExpansionIds: validExpansionIds, selectedId: selectedId
      };
    } catch (error) {
      return renderError(root, error);
    }
  }

  window.RNAssistantRunJournal = {
    render: render,
    unmount: unmount,
    pause: function (root) { (root.rnaPayloadPauses || []).forEach(function (pause) { pause(); }); },
    isProblem: isProblem,
    statusLabel: statusLabel,
    matchesFilter: matchesFilter,
    maxRenderedRows: MAX_RENDERED_ROWS,
    maxBulkExpandedRows: MAX_BULK_EXPANDED_ROWS
  };
}());
