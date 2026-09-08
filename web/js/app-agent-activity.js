var activityPresentationCache = new WeakMap();

function renderActivityNode(activity, nested, current, context) {
  var node = document.createElement("div");
  var status = activityStatus(activity);
  var kind = activityKind(activity) || "activity";
  var operation = activityOperation(activity);
  node.className = "agent-activity kind-" + kind + " operation-" + operation +
    (nested ? " nested" : "") + (current ? " current" : "") + " status-" + status;

  var expandable = activityHasDetails(activity, context);
  if (expandable) {
    var details = document.createElement("details");
    details.className = "agent-activity-toggle";
    details.setAttribute("data-disclosure-key", "activity:" + activityTimelineKey(activity));
    details.open = false;
    details.appendChild(renderActivityRow(activity, current, true, context));
    appendActivityDetailsContent(details, activity, context);
    node.appendChild(details);
  } else {
    node.appendChild(renderActivityRow(activity, current, false, context));
  }

  if (!context || context.renderInlineArtifacts !== false) {
    appendActivityArtifacts(node, activity, context);
  }
  appendQuestionCards(node, activity, context);
  return node;
}

function appendQuestionCards(node, activity, context) {
  if (String(activityToolId(activity) || "") !== "common.questions_ask" || activityStatus(activity) !== "waiting") return;
  if (context && Number.isInteger(context.index) && (state.messages || []).slice(context.index + 1).some(function (message) {
    return String(messageRole(message) || "").toLowerCase() === "user";
  })) return;
  var data;
  try { data = JSON.parse(activityDataJson(activity) || "{}"); } catch (error) { return; }
  if (!data || data.type !== "rnassistant.questions" || !Array.isArray(data.questions)) return;
  var form = document.createElement("form");
  form.className = "plan-question-cards";
  data.questions.forEach(function (question) {
    var fieldset = document.createElement("fieldset");
    var legend = document.createElement("legend");
    legend.textContent = question.header || question.prompt || "Вопрос";
    fieldset.appendChild(legend);
    var prompt = document.createElement("p");
    prompt.textContent = question.prompt || "";
    fieldset.appendChild(prompt);
    (question.options || []).forEach(function (option) {
      var label = document.createElement("label");
      var input = document.createElement("input");
      input.type = question.selection === "multiple" ? "checkbox" : "radio";
      input.name = "q_" + question.id;
      input.value = option.id;
      var copy = document.createElement("span");
      copy.innerHTML = "<strong></strong><small></small>";
      copy.querySelector("strong").textContent = option.label + (option.recommended ? " · рекомендуется" : "");
      copy.querySelector("small").textContent = option.description || "";
      label.appendChild(input);
      label.appendChild(copy);
      fieldset.appendChild(label);
    });
    if (question.allowFreeText !== false) {
      var free = document.createElement("textarea");
      free.name = "free_" + question.id;
      free.rows = 2;
      free.placeholder = "Дополнение или свой вариант";
      fieldset.appendChild(free);
    }
    form.appendChild(fieldset);
  });
  var submit = document.createElement("button");
  submit.type = "submit";
  submit.className = "agent-action-button primary";
  submit.textContent = "Ответить";
  form.appendChild(submit);
  form.addEventListener("submit", function (event) {
    event.preventDefault();
    var answers = data.questions.map(function (question) {
      var selectedIds = Array.prototype.slice.call(form.querySelectorAll("[name='q_" + question.id + "']:checked")).map(function (input) { return input.value; });
      var selected = (question.options || []).filter(function (option) {
        return selectedIds.indexOf(option.id) >= 0;
      }).map(function (option) { return option.label; });
      var free = form.querySelector("[name='free_" + question.id + "']");
      return { question: question.prompt || question.header || "Вопрос", selections: selected, freeText: free ? free.value.trim() : "" };
    });
    if (answers.some(function (answer) { return !answer.selections.length && !answer.freeText; })) {
      window.alert("Ответьте на каждый вопрос.");
      return;
    }
    var input = $("chatInput");
    var chatForm = $("chatForm");
    if (!input || !chatForm) return;
    input.value = "PLAN_ANSWERS:\n" + JSON.stringify({ answers: answers });
    updateComposerInputState();
    if (chatForm.requestSubmit) chatForm.requestSubmit();
    else chatForm.dispatchEvent(new Event("submit", { bubbles: true, cancelable: true }));
  });
  node.appendChild(form);
}

function renderActivityRow(activity, current, expandable, context) {
  var row = document.createElement(expandable ? "summary" : "div");
  var status = activityStatus(activity);
  var title = activityPrimaryText(activity);
  var comment = activityCommentText(activity);
  var time = activityTimeText(context);
  var hideIcon = (context && context.hideIcon) ||
    ["notice", "reasoning", "step", "compaction"].indexOf(activityKind(activity)) >= 0;
  row.className = "agent-activity-row" + (comment ? " has-comment" : " has-no-comment") +
    (hideIcon ? " is-status-label" : "") +
    (context && context.liveFeed && activity === context.currentActivity && status === "running" ? " is-live-current" : "");
  row.title = [title, comment, agentStatusLabel(status), time].filter(Boolean).join(" · ");

  if (!hideIcon) {
    var mark = document.createElement("span");
    mark.className = "agent-activity-mark operation-" + activityOperation(activity);
    mark.setAttribute("aria-hidden", "true");
    mark.innerHTML = activityOperationIcon(activity);
    row.appendChild(mark);
  }

  var copy = document.createElement("span");
  copy.className = "agent-activity-copy";

  var heading = document.createElement("span");
  heading.className = "agent-activity-heading";
  var name = document.createElement("span");
  name.className = "agent-activity-name";
  if (current && context && context.hideIcon) name.setAttribute("aria-live", "polite");
  name.textContent = title;
  heading.appendChild(name);
  copy.appendChild(heading);

  if (comment) {
    var target = document.createElement("span");
    target.className = "agent-activity-target";
    var targetValue = document.createElement("code");
    targetValue.textContent = comment;
    target.appendChild(targetValue);
    copy.appendChild(target);
  }

  var resultText = activityDisplayResult(activity);
  if (resultText && resultText !== title && resultText !== comment) {
    var resultLine = document.createElement("span");
    resultLine.className = "agent-activity-caption status-" + activityPresentationState(activity);
    var symbol = document.createElement("span");
    symbol.className = "agent-activity-status-symbol";
    symbol.setAttribute("aria-hidden", "true");
    symbol.textContent = { failed: "×", unknown: "!", waiting: "…", partial: "…", cancelled: "−", completed: "✓" }[activityPresentationState(activity)] || "";
    resultLine.appendChild(symbol);
    var resultCopy = document.createElement("span");
    resultCopy.textContent = resultText;
    resultLine.appendChild(resultCopy);
    copy.appendChild(resultLine);
  }
  row.appendChild(copy);

  if (expandable) {
    var caret = document.createElement("span");
    caret.className = "agent-activity-caret";
    caret.setAttribute("aria-hidden", "true");
    copy.appendChild(caret);
  }
  return row;
}

function activityOperation(activity) {
  var kind = activityKind(activity);
  if (kind === "reasoning") return "reasoning";
  if (kind === "diagnostic") return "diagnostic";
  if (kind === "step" || kind === "notice" || kind === "compaction") return "status";

  var display = activityValue(activity, "Display", "display", null);
  var operation = String(activityValue(display, "Operation", "operation", "command")).toLowerCase();
  return ["command", "read", "search", "write", "delete", "export", "learn", "question", "plan", "chart", "package", "check"].indexOf(operation) >= 0 ? operation : "command";
}

function activityOperationIcon(activity) {
  var icons = {
    search: "<svg viewBox=\"0 0 24 24\"><circle cx=\"11\" cy=\"11\" r=\"6.5\"/><path d=\"m16 16 4 4\"/></svg>",
    read: "<svg viewBox=\"0 0 24 24\"><path d=\"M3.5 5.5A7.5 7.5 0 0 1 12 7v13a7.5 7.5 0 0 0-8.5-1.5Z\"/><path d=\"M20.5 5.5A7.5 7.5 0 0 0 12 7v13a7.5 7.5 0 0 1 8.5-1.5Z\"/></svg>",
    write: "<svg viewBox=\"0 0 24 24\"><path d=\"M4 20h4l11-11a2.1 2.1 0 0 0-4-4L4 16Z\"/><path d=\"m13.5 6.5 4 4\"/></svg>",
    command: "<svg viewBox=\"0 0 24 24\"><rect x=\"3\" y=\"4\" width=\"18\" height=\"16\" rx=\"2\"/><path d=\"m7 9 3 3-3 3\"/><path d=\"M13 15h4\"/></svg>",
    reasoning: "<svg viewBox=\"0 0 24 24\"><path d=\"M9 18h6M10 22h4\"/><path d=\"M8.5 15.5A7 7 0 1 1 15.5 15.5c-.8.6-1 1.1-1 2h-5c0-.9-.2-1.4-1-2Z\"/></svg>",
    diagnostic: "<svg viewBox=\"0 0 24 24\"><path d=\"M12 3 2.8 20h18.4Z\"/><path d=\"M12 9v5M12 17.5v.1\"/></svg>",
    status: "<svg viewBox=\"0 0 24 24\"><circle cx=\"12\" cy=\"12\" r=\"8\"/><path d=\"m8.5 12 2.3 2.3 4.8-5\"/></svg>"
  };
  icons.learn = '<svg viewBox="0 0 24 24"><path d="M4 4h10l4 4v12H4Z"/><path d="M14 4v5h5M8 12h6M8 16h4"/><path d="m17 14 2 2 3-4"/></svg>';
  icons.question = '<svg viewBox="0 0 24 24"><path d="M4 4h16v13H9l-5 4Z"/><path d="M9 8a3 3 0 0 1 6 0c0 2-3 2-3 4M12 14v.1"/></svg>';
  icons.delete = '<svg viewBox="0 0 24 24"><path d="M4 6h16M9 6V3h6v3M6 6l1 15h10l1-15M10 10v7M14 10v7"/></svg>';
  icons.export = '<svg viewBox="0 0 24 24"><path d="M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5"/></svg>';
  icons.plan = '<svg viewBox="0 0 24 24"><path d="m3 6 2 2 3-4M11 6h10m-18 7 2 2 3-4M11 13h10M4 20h3M11 20h10"/></svg>';
  icons.chart = '<svg viewBox="0 0 24 24"><path d="M4 3v18h17M8 17v-5M13 17V6M18 17V9"/></svg>';
  icons.package = '<svg viewBox="0 0 24 24"><path d="m3 7 9-4 9 4v10l-9 4-9-4Zm0 0 9 4 9-4M12 11v10M8 5l9 4"/></svg>';
  icons.check = icons.status;
  return icons[activityOperation(activity)] || icons.status;
}

function activityPrimaryText(activity) {
  if (activityKind(activity) === "diagnostic") return "Выполнение";
  if (activityToolId(activity)) {
    var display = activityValue(activity, "Display", "display", null);
    var action = activityStatus(activity) === "running"
      ? activityValue(display, "RunningAction", "runningAction", "") : activityValue(display, "Action", "action", "");
    if (action) return String(action).replace(/^(Поиск|Чтение) ресурс(?:а|ов)?$/, "$1").replace(/^Ищу ресурсы$/, "Ищу").replace(/^Читаю ресурс$/, "Читаю");
    var title = activityTitle(activity);
    if (title !== activityToolId(activity) && /[А-Яа-яЁё]/.test(title)) return title;
    return activityStatus(activity) === "running" ? "Вызываю инструмент" : "Вызов инструмента";
  }
  var progressTitle = typeof activityProgressTitle === "function" ? activityProgressTitle(activity) : "";
  if (progressTitle && !activityToolId(activity)) {
    return progressTitle;
  }

  var title = activityTitle(activity);
  var toolId = activityToolId(activity);
  if (title && title !== toolId && title !== "Tool step" && title !== "Agent step" &&
      title.toLowerCase().indexOf("deterministic") !== 0) {
    return title.charAt(0).toUpperCase() + title.slice(1);
  }
  var resultMessage = activityResultMessage(activity);
  var status = activityStatus(activity);
  if (resultMessage && (status === "completed" || status === "failed" || status === "cancelled")) {
    return resultMessage;
  }
  var stepMessage = typeof activityStepMessage === "function" ? activityStepMessage(activity) : "";
  if (stepMessage) {
    return stepMessage;
  }
  if (toolId) {
    return toolId;
  }

  var labels = {
    reasoning: "Анализирую задачу",
    tool: "Выполняю действие",
    control: "Выполняю действие",
    diagnostic: "Ошибка ответа агента"
  };
  return labels[activityKind(activity)] || toolId || title || "Выполняю шаг";
}

function activityResultCaption(activity) {
  var toolId = activityToolId(activity);
  var source = activityDataJson(activity);
  if (!source || source.length > 32768) return "";
  var cached = activityPresentationCache.get(activity);
  if (cached && cached.source === source && cached.toolId === toolId) return cached.caption;
  var caption = "";
  var partial = false;
  try {
    // Generic JSON needs no tool-specific renderer. Reserved resource/capability
    // claims require their source owner: arbitrary custom JSON cannot assert media
    // hydration, capability admission or search coverage. Neither establishes effects.
    var data = JSON.parse(source);
    if (toolId === "common.capabilities_read" && data && (data.kind === "tool-schema" || data.kind === "skill" || data.kind === "reference") && typeof data.complete === "boolean") {
      partial = !data.complete;
      caption = partial ? "Загружена часть описания" : "Загружено";
    } else if (data && typeof data === "object" && (data.truncated || data.externalized)) {
      caption = data.externalized ? "Результат сохранён отдельно" : "В журнале показана часть результата";
    } else if (toolId === "common.resources_read" && data && data.kind === "resource-read") {
      if (data.table && Array.isArray(data.table.rows)) caption = "Получено строк: " + data.table.rows.length;
      else if (data.representation === "media") caption = data.hydratedForNextModelStep === true
        ? "Медиа подготовлено для следующего запроса модели" : "Получены сведения о медиа";
      else if (data.representation === "metadata") caption = "Получены сведения · содержимое не загружено";
      else {
        caption = { text: "Получен текст", source: "Получен исходный код", structure: "Получена структура" }[data.representation] || "Прочитано";
        if (Number.isSafeInteger(data.returnedCharacters) && data.returnedCharacters >= 0)
          caption += " · символов: " + data.returnedCharacters;
      }
      if (data.complete === false) { caption += " · часть данных"; partial = true; }
    } else if ((toolId === "common.resources_find" || toolId === "common.capabilities_search") && data && Array.isArray(data.items) && typeof data.complete === "boolean") {
      var count = data.items.length;
      partial = !data.complete || data.partial === true;
      // An empty arbitrary collection is not evidence that a search found nothing.
      caption = "Получено элементов: " + count + (partial ? " · неполный список" : "");
    } else if (Array.isArray(data)) caption = "Получен список · элементов: " + data.length;
    else if (typeof data === "string") caption = "Получен текстовый ответ";
    else caption = "Получены данные JSON";
  } catch (ignore) { /* Invalid/large data remains available in the existing details. */ }
  activityPresentationCache.set(activity, { toolId: toolId, source: source, caption: caption, partial: partial });
  return caption;
}

function activityPresentationState(activity) {
  var evidence = activityValue(activity, "ExecutionEvidence", "executionEvidence", null);
  if (activityValue(evidence, "Effect", "effect", "") === "Unknown") return "unknown";
  var status = activityStatus(activity);
  if (status === "completed") {
    activityResultCaption(activity);
    var display = activityPresentationCache.get(activity);
    if (display && display.source === activityDataJson(activity) && display.partial) return "partial";
  }
  return status === "completed_with_errors" ? "failed" : status;
}

function activityDisplayResult(activity) {
  var status = activityStatus(activity);
  var evidence = activityValue(activity, "ExecutionEvidence", "executionEvidence", null);
  var effect = activityValue(evidence, "Effect", "effect", "");
  var dispatch = activityValue(evidence, "Dispatch", "dispatch", "");
  if (effect === "Unknown") return "Результат не подтверждён — нужна проверка";
  if (status === "running") return "";
  if (status === "pending") return "В очереди";
  if (status === "waiting") return activityValue(activity, "ExecutionStatus", "executionStatus", "") === "awaiting_user"
    ? "Жду ответа" : "Жду подтверждения";
  if (status === "cancelled") return "Отменено";
  if (activityKind(activity) === "diagnostic" && status === "failed") return window.RNAssistantRunViewState.failureReasonLabel(
    activityValue(activity, "ExecutionStatus", "executionStatus", ""));
  var code = String(activityValue(activity, "ErrorCode", "errorCode", "") || "").toLowerCase();
  if (dispatch === "NotDispatched" && code === "excel_sheet_already_exists") return "Лист уже существует. Создание не выполнено.";
  if (status === "failed" || status === "completed_with_errors") {
    var errors = {
      resource_access_denied: "Нет доступа к ресурсу",
      resource_not_found: "Ресурс не найден",
      resource_target_not_found: "Ресурс не найден",
      resource_target_ambiguous: "Нужно уточнить ресурс",
      resource_scope_incomplete: "Часть источников недоступна — нужно уточнить поиск",
      resource_snapshot_unavailable: "Эта версия ресурса недоступна",
      resource_revision_unavailable: "Эта версия ресурса недоступна",
      resource_revision_changed: "Ресурс изменился — нужно перечитать",
      resource_head_unknown: "Состояние ресурса требует проверки",
      resource_batch_too_large: "Слишком большой объём данных",
      resource_backpressure: "Ресурс занят — попробуйте позже",
      resource_authority_not_ready: "Ресурс пока недоступен",
      resource_cursor_invalid: "Данные изменились — повторите поиск",
      resource_view_invalid: "Этот формат чтения не поддерживается",
      capability_not_found: "Инструмент или навык не найден",
      invalid_arguments: "Нужно уточнить параметры действия",
      parameters_schema: "Некорректная схема параметров",
      tool_display_invalid: "Поля цели не соответствуют параметрам инструмента",
      resource_target_required: "Нужно указать ресурс",
      resource_target_runtime_owned: "Нужно указать понятное имя ресурса",
      resource_view_unsupported: "Этот формат чтения не поддерживается",
      resource_whole_read_incomplete: "Не удалось прочитать ресурс целиком",
      resource_whole_read_invalid: "Не удалось получить согласованную версию ресурса",
      tool_not_found: "Инструмент не найден",
      tool_arguments_invalid: "Нужно уточнить параметры действия",
      tool_mutation_busy: "Документ занят другим действием",
      manual_tool_chat_busy: "Дождитесь завершения текущего действия",
      active_document_changed: "Активный документ изменился",
      document_session_unavailable: "Документ недоступен",
      tool_effect_uncertain: "Результат не подтверждён — нужна проверка"
    };
    var failure = errors[code] || (dispatch === "NotDispatched" ? "Не удалось начать действие" : "Действие завершилось с ошибкой");
    return code ? failure + " · " + code : failure;
  }
  if (!activityToolId(activity)) return activityResultMessage(activity);
  if (effect === "VerifiedNoChange") return "Без изменений";
  if (effect === "VerifiedChange") return "Изменения подтверждены";
  if (status !== "completed") return "Статус пока неизвестен";
  var resultCaption = activityResultCaption(activity);
  if (resultCaption) return resultCaption;
  return { learn: "Загружено", read: "Прочитано", search: "Поиск завершён", check: "Проверка завершена" }[activityOperation(activity)] ||
    (activityResultMessage(activity) ? "Получен текстовый ответ" : "Завершено");
}

function activityCommentText(activity) {
  if (["notice", "diagnostic"].indexOf(activityKind(activity)) >= 0 && !activityToolId(activity)) return "";
  var toolId = activityToolId(activity);
  var subtitle = activityValue(activity, "Subtitle", "subtitle", "");
  if (!subtitle || subtitle === toolId) {
    // Keep unknown/custom calls identifiable even when no semantic target was supplied.
    return toolId || "";
  }
  subtitle = String(subtitle).replace(/[\r\n\t]+/g, " ").trim();
  return subtitle;
}

function activityTimeText(context) {
  var value = context && context.message ? messageCreatedUtc(context.message) : "";
  if (!value) {
    return "";
  }
  var date = new Date(value);
  if (isNaN(date.getTime())) {
    return "";
  }
  var hours = date.getHours();
  var minutes = date.getMinutes();
  return (hours < 10 ? "0" : "") + hours + ":" + (minutes < 10 ? "0" : "") + minutes;
}

function activityHasDetails(activity, context) {
  return !!((!(context && context.liveFeed) && activityChildren(activity).length) ||
    activityArgumentsJson(activity) ||
    activityDataJson(activity) ||
    activityDetailTexts(activity, context).length ||
    activityStatus(activity) === "failed" ||
    (activityPendingId(activity) && activityStatus(activity) === "waiting"));
}

function createAgentTextButton(label, className, onClick) {
  var button = document.createElement("button");
  button.type = "button";
  button.className = "agent-action-button " + (className || "secondary");
  button.textContent = label;
  button.addEventListener("click", onClick);
  return button;
}

function appendActivityErrorActions(node, activity, context) {
  if (activityStatus(activity) !== "failed") {
    return;
  }

  var toolId = activityToolId(activity);
  var sourceChatId = state.activeChatId;
  var actions = document.createElement("div");
  actions.className = "agent-inline-actions";
  actions.appendChild(createAgentCopyButton("Копировать диагностику", [
    "Title: " + activityTitle(activity),
    "Tool: " + toolId,
    "Status: " + activityStatus(activity),
    "Reason: " + activityDetailTexts(activity, context).join("\n\n")
  ].join("\n")));
  actions.appendChild(createAgentTextButton("Причина и детали", "secondary", function () {
    if (state.activeChatId !== sourceChatId || typeof window.openRunJournal !== "function") return;
    var message = context && context.message ? context.message : null;
    window.openRunJournal({
      chatId: sourceChatId,
      runId: activityValue(activity, "RunId", "runId", "") || (message ? messageRunId(message) : ""),
      stepId: activityStepId(activity),
      toolCallId: activityToolCallId(activity),
      filter: "problems"
    });
  }));
  node.appendChild(actions);
}

function createAgentCopyButton(label, text) {
  var button = document.createElement("button");
  button.type = "button";
  button.className = "agent-copy-button";
  button.textContent = label;
  button.addEventListener("click", function (event) {
    event.preventDefault();
    event.stopPropagation();
    var value = typeof text === "function" ? text() : text;
    copyText(value || "");
  });
  return button;
}

function appendActivityDetailsContent(node, activity, context) {
  var children = activityChildren(activity);

  var body = document.createElement("div");
  body.className = "agent-activity-detail-body";

  if (activityToolId(activity)) {
    var tool = document.createElement("div");
    tool.className = "agent-activity-tool-id";
    tool.textContent = activityToolId(activity);
    body.appendChild(tool);
    var code = activityValue(activity, "ErrorCode", "errorCode", "");
    if (code) {
      var errorCode = document.createElement("div");
      errorCode.className = "agent-activity-tool-id";
      errorCode.textContent = "Код ошибки: " + code;
      body.appendChild(errorCode);
    }
  }

  if (children.length && !(context && context.liveFeed)) {
    var childList = document.createElement("div");
    childList.className = "agent-activity-children";
    children.forEach(function (child) {
      childList.appendChild(renderActivityNode(child, true, context && activityContains(child, context.currentActivity), context));
    });
    body.appendChild(childList);
  }

  activityDetailTexts(activity, context).forEach(function (text) {
    var result = document.createElement("div");
    result.className = "agent-activity-result";
    result.textContent = (activityToolId(activity) ? "" : "Диагностика:\n") + text;
    body.appendChild(result);
  });
  if (typeof appendToolResultPreview === "function") appendToolResultPreview(body, activity, context, node);
  if (typeof appendArgumentsData === "function") {
    appendArgumentsData(body, activityArgumentsJson(activity));
  }
  if (typeof appendActivityData === "function") {
    appendActivityData(body, "Результат", activityDataJson(activity));
  }

  appendActivityErrorActions(body, activity, context);
  node.appendChild(body);
}

function appendActivityArtifacts(node, activity, context) {
  var appended = false;
  if (typeof tryRenderChartArtifact === "function") {
    var chart = tryRenderChartArtifact(activity, context || {});
    if (chart) {
      node.appendChild(chart);
      appended = true;
    }
  }
  return appended;
}

function agentStatusLabel(status) {
  var labels = {
    completed: "Готово",
    completed_with_errors: "Завершено с ошибками",
    running: "Выполняю",
    waiting: "Нужно подтверждение",
    failed: "Ошибка",
    cancelled: "Отменено",
    pending: "Ожидает"
  };
  return labels[status] || status || "Статус";
}

function appendAgentRunArtifacts(parent, timeline) {
  var artifacts = document.createElement("div");
  artifacts.className = "agent-run-artifacts";
  (timeline || []).forEach(function (item) {
    appendActivityTreeArtifacts(artifacts, item.activity, {
      messageId: messageId(item.message),
      index: item.index,
      message: item.message
    });
  });
  if (artifacts.childNodes.length) {
    parent.appendChild(artifacts);
  }
}

function appendActivityTreeArtifacts(parent, activity, context) {
  appendActivityArtifacts(parent, activity, context);
  activityChildren(activity).forEach(function (child) {
    appendActivityTreeArtifacts(parent, child, context);
  });
}

// Some diagnostic events retain the body only in Content. Keep both sources
// when different, once when equal; do not merge separate calls or model messages.
function activityDetailTexts(activity, context) {
  var texts = [];
  function append(text) {
    if (text && String(text).trim() && !texts.some(function (existing) { return existing.trim() === String(text).trim(); })) texts.push(String(text));
  }
  append(activityResultMessage(activity));
  if (activityKind(activity) === "diagnostic" && context && context.message) append(messageContent(context.message));
  return texts;
}
