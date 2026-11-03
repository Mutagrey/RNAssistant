(function () {
  "use strict";

  var MAX_RENDERED_ROWS = 1000;
  var MAX_BULK_EXPANDED_ROWS = 50;
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
    if (filter === "attempts") return ["cancelled", "rejected"].indexOf(rowStatus(row)) >= 0;
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
      "model.response.accepted": "Ответ модели принят",
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

  // Presentation only. A missing correlation can be resolved only through a unique,
  // recorded call/attempt -> step link inside this exact run, never by adjacency.
  function buildTree(rows) {
    var runs = new Map();
    rows.forEach(function (row) {
      var id = correlation(row, "RunId");
      if (!runs.has(id)) runs.set(id, group("run", ["run", id], []));
      runs.get(id).rows.push(row);
    });
    runs.forEach(function (run, runId) {
      run.runId = runId;
      var links = new Map(), steps = new Map(), boundaries = [];
      run.rows.forEach(function (row) {
        var step = correlation(row, "StepId");
        if (!step || isRunBoundary(row)) return;
        ["ToolCallId", "ModelAttemptId"].forEach(function (field) {
          var id = correlation(row, field);
          if (!id) return;
          var key = JSON.stringify([field, id]);
          if (!links.has(key)) links.set(key, new Set());
          links.get(key).add(step);
        });
      });
      run.rows.forEach(function (row) {
        if (isRunBoundary(row)) { boundaries.push(row); return; }
        var step = correlation(row, "StepId");
        if (!step && runId) {
          var candidates = new Set();
          ["ToolCallId", "ModelAttemptId"].forEach(function (field) {
            var known = links.get(JSON.stringify([field, correlation(row, field)]));
            if (known) known.forEach(function (id) { candidates.add(id); });
          });
          if (candidates.size === 1) step = Array.from(candidates)[0];
        }
        if (!steps.has(step)) {
          var branch = group("step", ["step", runId, step], [], step ? "Шаг " + (Array.from(steps.keys()).filter(Boolean).length + 1) : "Без привязки к шагу");
          branch.stepId = step;
          steps.set(step, branch);
        }
        steps.get(step).rows.push(row);
      });
      steps.forEach(function (step) {
        var actions = new Map(), models = new Map(), secondary = [];
        step.rows.forEach(function (row) {
          var call = correlation(row, "ToolCallId"), attempt = correlation(row, "ModelAttemptId");
          if (isTool(row)) {
            var key = call && runId ? call : rowId(row);
            if (!actions.has(key)) {
              var action = group("action", ["action", runId, step.stepId, key], []);
              action.toolCallId = call;
              actions.set(key, action);
            }
            actions.get(key).rows.push(row);
          } else if (isModel(row) && !/^step\./.test(rowKind(row))) {
            // Uncorrelated model events share a clearly labelled bucket, not an invented attempt.
            var modelKey = runId ? attempt : rowId(row);
            if (!models.has(modelKey)) models.set(modelKey, group("model", ["model", runId, step.stepId, modelKey], []));
            models.get(modelKey).rows.push(row);
          } else secondary.push(row);
        });
        actions.forEach(function (action) {
          action.title = actionTitle(action.rows);
          action.children = action.rows.map(eventNode);
          step.children.push(action);
        });
        if (models.size) {
          var modelRows = [];
          models.forEach(function (model) { modelRows = modelRows.concat(model.rows); });
          modelRows.sort(function (a, b) { return firstSequence(a) - firstSequence(b); });
          var modelBranch = group("models", ["models", runId, step.stepId], modelRows, "Обмен с моделью");
          models.forEach(function (model, id) {
            model.title = id ? "Попытка " + (modelBranch.children.length + 1) : "Без привязки к попытке";
            model.children = model.rows.map(eventNode);
            modelBranch.children.push(model);
          });
          // A single attempt does not need another disclosure level.
          if (models.size === 1) modelBranch.children = modelRows.map(eventNode);
          step.children.push(modelBranch);
        }
        if (secondary.length) {
          var service = group("service", ["service", runId, step.stepId], secondary, "Служебные события");
          service.children = secondary.map(eventNode);
          step.children.push(service);
        }
        step.children.sort(function (a, b) {
          if (a.type === "service" || b.type === "service") return a.type === b.type ? 0 : a.type === "service" ? 1 : -1;
          return firstSequence(a.rows[0]) - firstSequence(b.rows[0]);
        });
        run.children.push(step);
      });
      if (boundaries.length) {
        var lifecycle = group("service", ["lifecycle", runId], boundaries, "События запуска");
        lifecycle.children = boundaries.map(eventNode);
        run.children.push(lifecycle);
      }
    });
    return Array.from(runs.values());
  }

  function representative(node) {
    var issue = node.rows.filter(isProblem).sort(function (a, b) { return issuePriority(b) - issuePriority(a); })[0];
    if (node.type === "run") return terminalRow(node.rows, ["turn.ended", "run.ended"]);
    if (node.type === "step") return terminalRow(node.rows, ["step.ended"]);
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
    if (node.type === "run" || node.type === "step") {
      if (row) return statusLabel(rowStatus(row)) + (node.rows.some(hasUnknownEffect) && !hasUnknownEffect(row) ? " · эффект не подтверждён" : !isProblem(row) && problem ? " · были проблемы" : "");
      return node.rows.some(hasUnknownEffect) ? "Эффект не подтверждён" : problem ? "Есть проблемы" : (node.type === "run" ? "Итог не найден в выборке" : "");
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
    var stepIds = [], allIds = [], problemPaths = new Set();
    var limitedProblems = rows.filter(isProblem).slice(0, MAX_BULK_EXPANDED_ROWS).map(rowId);
    visitTree(tree, function (node, path) {
      if (node.type !== "event") allIds.push(node.id);
      if (node.type === "run" || node.type === "step") stepIds.push(node.id);
      if (limitedProblems.indexOf(node.id) >= 0) path.concat(node.id).forEach(function (id) { problemPaths.add(id); });
    });
    command("Развернуть шаги", function () {
      if (typeof options.onExpandedSet === "function") options.onExpandedSet(stepIds, true);
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
    return node.type === "models" ? "Модель" : node.title;
  }

  function navigationNode(node, filter, options, latestRun, selectedId, selectedPath) {
    var children = (node.children || []).filter(function (child) { return visibleNode(child, filter); });
    var targeted = !!((options.activeStepId && node.rows.some(function (item) { return correlation(item, "StepId") === options.activeStepId; })) ||
      (options.activeToolCallId && node.rows.some(function (item) { return correlation(item, "ToolCallId") === options.activeToolCallId; })));
    var defaultOpen = filter !== "all" || node.type === "run" && (node.runId === options.activeRunId || node === latestRun) ||
      targeted && (node.type === "run" || node.type === "step") || selectedPath.indexOf(node.id) >= 0;
    var open = options.expanded && Object.prototype.hasOwnProperty.call(options.expanded, node.id) ? !!options.expanded[node.id] : defaultOpen;
    var uncertain = node.rows.some(hasUnknownEffect), problem = node.rows.some(isProblem);
    var title = node.type === "event" ? titleLabel(node.row) : navigatorTitle(node);
    var status = node.type === "event" ? statusLabel(rowStatus(node.row)) : groupStatus(node);
    return { key: node.id, kind: children.length ? "parent" : "item", selectable: true,
      title: title, tooltip: [title, status].filter(Boolean).join(" · "), ariaLabel: [title, status].filter(Boolean).join(". "),
      iconHtml: window.RNAssistantIcons.render(node.type === "run" ? "clock" : node.type === "step" ? "layers" : node.type === "action" ? "tool" : node.type === "event" ? "file" : "code"),
      badge: uncertain ? "?" : problem ? "!" : "", badgeKind: uncertain ? "warning" : problem ? "critical" : "",
      badgeTitle: status, selected: node.id === selectedId, expanded: open,
      children: children.map(function (child) { return navigationNode(child, filter, options, latestRun, selectedId, selectedPath); }) };
  }

  function renderGroupDetail(host, node, options) {
    var row = representative(node);
    detailHeading(host, navigatorTitle(node), groupStatus(node));
    var steps = node.type === "run" ? node.children.filter(function (child) { return child.type === "step" && child.stepId; }).length : 0;
    var tools = node.type === "run" || node.type === "step" ? uniqueToolCallCount(node.rows) : 0;
    var counts = [steps ? countLabel(steps, ["шаг", "шага", "шагов"]) : "",
      tools ? countLabel(tools, ["действие", "действия", "действий"]) : ""].filter(Boolean).join(" · ");
    if (counts) appendText(host, "p", "rn-run-journal-detail-meta", counts);
    var cause = row && (displayCause(row) || (node.type === "action" ? effectExplanation(row) || rowNote(row) : ""));
    if (!cause && node.type === "step") {
      var explanation = terminalRow(node.rows.filter(function (item) { return !!displayCause(item); }), ["model.response.accepted", "tool.call.recorded"]);
      cause = explanation && displayCause(explanation);
    }
    var notes = new Set(cause ? [cause] : []);
    if (node.type === "run" || node.type === "step") node.rows.filter(isProblem).forEach(function (item) {
      var problem = displayCause(item) || effectExplanation(item);
      if (problem && notes.size < 3) notes.add(problem);
    });
    notes.forEach(function (text) { appendText(host, "p", "rn-run-journal-result-summary", text); });
    var target = node.type === "action" && node.rows.map(function (item) { return correlation(item, "Target"); }).filter(Boolean)[0];
    if (target) appendText(host, "p", "rn-run-journal-detail-meta", target);
    if (node.type === "action") { actionContent(host, node, options); return; }
    if ((node.type === "step" || node.type === "models" || node.type === "model") && typeof options.onOpenModelContext === "function") {
      var requests = node.rows.filter(function (item) { return rowKind(item) === "llm.request" && sourceIds(item).length === 1; });
      var request = requests[requests.length - 1];
      if (request) host.appendChild(actionButton(requests.length > 1 ? "Открыть последний запрос" : "Открыть запрос", function () {
        options.onOpenModelContext({ runId: correlation(request, "RunId"), stepId: correlation(request, "StepId"), requestEventId: sourceIds(request)[0] });
      }));
    }
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
          options.activeStepId && Object.keys(nodes).find(function (id) { return nodes[id].stepId === options.activeStepId && nodes[id].type === "step" && visibleNode(nodes[id], filter); }) ||
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
        var viewerOptions = Object.assign({}, options, { payloadCleanups: root.rnaPayloadCleanups, payloadPauses: root.rnaPayloadPauses,
          isAlive: function () { return options.isAlive() && root.rnaJournalSelection === selectionToken; } });
        detail.dataset.selectionId = id;
        if (node.type === "event") renderEventDetail(detail, node.row, viewerOptions);
        else renderGroupDetail(detail, node, viewerOptions);
      }
      function renderTree() {
        var selectedPath = [], parent = parents[selectedId];
        while (parent) { selectedPath.push(parent); parent = parents[parent]; }
        RNAssistantNavigationTree.render(list, tree.filter(function (node) { return visibleNode(node, filter); }).map(function (node) {
          return navigationNode(node, filter, options, latestRun, selectedId, selectedPath);
        }), { label: "Запуски, шаги и действия", emptyText: "Для выбранного фильтра строк нет.",
          onSelect: function (item) { select(item.key); },
          onToggle: function (item, expanded) { if (typeof options.onExpandedChange === "function") options.onExpandedChange(item.key, expanded); } });
      }
      renderTree();
      if (selectedId) select(selectedId);
      else appendText(detail, "p", "rn-run-journal-empty", "Выберите запуск или шаг слева.");
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
