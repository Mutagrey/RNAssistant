var activityPresentationCache = new WeakMap();

function renderActivityNode(activity, nested, current, context) {
  var node = document.createElement("div");
  var status = activityStatus(activity);
  var kind = activityKind(activity) || "activity";
  var operation = activityOperation(activity);
  node.className = "agent-activity kind-" + kind + " operation-" + operation +
    (nested ? " nested" : "") + (current ? " current" : "") + " status-" + status + " presentation-" + activityPresentationState(activity);

  var expandable = activityHasDetails(activity, context);
  if (expandable) {
    var details = document.createElement("details");
    details.className = "agent-activity-toggle";
    details.setAttribute("data-disclosure-key", "activity:" + activityTimelineKey(activity) + (status === "failed" ? ":failed" : ""));
    details.open = false;
    details.appendChild(renderActivityRow(activity, current, true, context));
    if (activityToolId(activity)) {
      var built = false;
      details.addEventListener("toggle", function () {
        if (!details.open || built) return;
        built = true;
        appendActivityDetailsContent(details, activity, context);
      });
    } else appendActivityDetailsContent(details, activity, context);
    node.appendChild(details);
  } else {
    node.appendChild(renderActivityRow(activity, current, false, context));
  }

  if (!context || context.renderInlineArtifacts !== false) {
    appendActivityArtifacts(node, activity, context);
  }
  if (!context || !context.terminal && !context.workspace) appendQuestionCards(node, activity, context);
  return node;
}

function appendQuestionCards(node, activity, context) {
  if (String(activityToolId(activity) || "") !== "core.ask" || activityStatus(activity) !== "waiting") return;
  if (context && Number.isInteger(context.index) && (state.messages || []).slice(context.index + 1).some(function (message) {
    return !messageProtocolMessage(message) && String(messageRole(message) || "").toLowerCase() === "user";
  })) return;
  var slot = document.createElement("div");
  slot.className = "agent-question-slot";
  slot.setAttribute("aria-live", "polite");
  node.appendChild(slot);
  slot.textContent = "Загрузка вопросов…";
  if (typeof appendToolResultPreview !== "function") {
    slot.textContent = "Не удалось загрузить вопросы. Обновите чат.";
    return;
  }
  slot.textContent = "";
  var loading = appendToolResultPreview(slot, activity, context, null, function (target, response) {
    target.textContent = "";
    if (!response.questions) {
      target.textContent = response.questionsUnavailableReason || "Сохранённые вопросы недоступны.";
      var retry = document.createElement("button");
      retry.type = "button";
      retry.className = "agent-action-button secondary";
      retry.textContent = "Повторить загрузку";
      retry.addEventListener("click", function () {
        slot.remove();
        if (!context || !context.terminal) appendQuestionCards(node, activity, context);
      });
      target.appendChild(retry);
      return;
    }
    renderQuestionCards(target, response.questions, response);
  });
  if (!loading) slot.textContent = "Не удалось определить источник вопросов. Обновите чат.";
}

function renderQuestionCards(node, data, source) {
  window.RNAssistantQuestions.render(node, data, {
    scope: "office:" + source.chatId, runId: source.runId,
    submit: function (answers) { return submitInboxMessage(null, [], source.chatId, "Steer", answers); }
  });
}

function renderActivityRow(activity, current, expandable, context) {
  var row = document.createElement(expandable ? "summary" : "div");
  var status = activityStatus(activity);
  var title = activityPrimaryText(activity);
  var comment = activityCommentText(activity);
  var time = activityTimeText(context);
  var presentation = activityPresentationState(activity);
  var resultText = context && context.terminal && isActiveTimelineStatus(status) ? "Остановлено" : activityDisplayResult(activity);
  if (activityToolId(activity) && status === "completed" && presentation !== "unknown") {
    var completed = activityCompletedRowCopy(activity, resultText);
    if (completed) { title = completed.title; resultText = completed.result; }
  }
  if (context && context.terminal && isActiveTimelineStatus(status)) presentation = "cancelled";
  var hideIcon = (context && context.hideIcon) ||
    ["reasoning", "step", "compaction"].indexOf(activityKind(activity)) >= 0 ||
    (activityKind(activity) === "notice" && isActiveTimelineStatus(status));
  row.className = "agent-activity-row" + (comment ? " has-comment" : " has-no-comment") +
    (hideIcon ? " is-status-label" : "") +
    (context && context.liveFeed && activity === context.currentActivity && status === "running" ? " is-live-current" : "");
  row.title = [title, comment, resultText, agentStatusLabel(presentation), time].filter(Boolean).join(" · ");

  if (!hideIcon) {
    var mark = document.createElement("span");
    mark.className = "agent-activity-mark operation-" + activityOperation(activity) + " presentation-" + presentation;
    mark.setAttribute("aria-hidden", "true");
    mark.innerHTML = presentation === "running" && current ? '<span class="agent-work-spinner"></span>' : activityOperationIcon(activity);
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
  if (comment && comment !== title) {
    var target = document.createElement("span");
    target.className = "agent-activity-object";
    target.textContent = comment;
    copy.appendChild(target);
  }
  if (resultText && resultText !== title && resultText !== comment) {
    var resultLine = document.createElement("span");
    resultLine.className = "agent-activity-caption status-" + presentation;
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

function activityCompletedRowCopy(activity, result) {
  var evidence = activityValue(activity, "ExecutionEvidence", "executionEvidence", null);
  var effect = activityValue(evidence, "Effect", "effect", "");
  if (effect === "VerifiedChange" || effect === "VerifiedNoChange") return { title: result, result: "" };
  var operation = activityOperation(activity);
  if (operation === "read") {
    var read = activityReadSummary(activity);
    if (read) {
      var readCopy = activityReadPresentation(read);
      return { title: readCopy.rowTitle, result: readCopy.rowDetail };
    }
    if (result === "Прочитано") return { title: result, result: "" };
  }
  if (operation === "search") {
    var summary = activityTypedResultSummary(activity);
    var count = activityValue(summary, "ReturnedItems", "returnedItems", null);
    if (Number.isSafeInteger(count) && count >= 0) return { title: "Найдено", result: "элементов: " + count +
      (activityValue(summary, "Complete", "complete", true) === false ||
        activityValue(summary, "Partial", "partial", false) === true ? " · неполный список" : "") };
    if (result === "Поиск завершён") return { title: result, result: "" };
  }
  if (operation === "learn" && (result === "Загружено" || result === "Загружена часть описания"))
    return { title: result, result: "" };
  if (operation === "check" && result === "Проверка завершена") return { title: "Проверено", result: "" };
  return null;
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
  var names = { search: "search", read: "book", write: "edit", command: "terminal",
    reasoning: "bulb", diagnostic: "warning", status: "status", learn: "learn",
    question: "question", delete: "trash", export: "download", plan: "plan",
    chart: "chart", package: "package", check: "check" };
  var name = names[activityOperation(activity)] || "status";
  return window.RNAssistantIcons ? window.RNAssistantIcons.render(name) : '<svg data-rna-icon="' + name + '" aria-hidden="true"></svg>';
}

function activityPrimaryText(activity) {
  if (activityKind(activity) === "diagnostic") return "Выполнение";
  if (activityToolId(activity)) {
    var summary = activityTypedResultSummary(activity);
    if (activityToolId(activity) === "core.tools.read" && summary) {
      var representation = activityValue(summary, "Representation", "representation", "");
      var capabilityTitle = { skill: "Чтение навыка", reference: "Чтение справки навыка", "tool-schema": "Чтение описания инструмента" }[representation];
      if (capabilityTitle) return capabilityTitle;
    }
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

function activityTypedResultSummary(activity) {
  var kinds = {
    "core.find": "resource-find",
    "core.search": "resource-search",
    "core.read": "resource-read",
    "core.tools.search": "capability-search",
    "core.tools.read": "capability-read"
  };
  var kind = kinds[activityToolId(activity)];
  var summary = activityValue(activity, "ResultSummary", "resultSummary", null);
  return kind && summary && activityValue(summary, "Kind", "kind", "") === kind ? summary : null;
}

function activityReadSummary(activity) {
  return activityToolId(activity) === "core.read"
    ? activityTypedResultSummary(activity) || activityValue(activity, "ReadSummary", "readSummary", null) : null;
}

function activityReadCaption(summary) {
  return activityReadPresentation(summary).caption;
}

function activityReadPresentation(summary) {
  var representation = activityValue(summary, "Representation", "representation", "");
  var rows = activityValue(summary, "ReturnedRows", "returnedRows", null);
  var characters = activityValue(summary, "ReturnedCharacters", "returnedCharacters", null);
  var caption = "", rowTitle = "Прочитано", rowDetail = "";
  if (Number.isSafeInteger(rows) && rows >= 0) {
    caption = "Получено строк: " + rows; rowDetail = "строк: " + rows;
  } else if (representation === "media") {
    caption = activityValue(summary, "HydratedForNextModelStep", "hydratedForNextModelStep", false)
      ? "Медиа подготовлено для следующего запроса модели" : "Получены сведения о медиа";
    rowTitle = caption;
  } else if (representation === "metadata") {
    caption = "Получены сведения · содержимое не загружено";
    rowTitle = "Получены сведения"; rowDetail = "содержимое не загружено";
  } else {
    caption = { text: "Получен текст", source: "Получен исходный код", structure: "Получена структура" }[representation] || "Прочитано";
    rowDetail = { text: "текст", source: "исходный код", structure: "структура" }[representation] || "";
    if (Number.isSafeInteger(characters) && characters >= 0) {
      caption += " · символов: " + characters;
      rowDetail += (rowDetail ? " · " : "") + "символов: " + characters;
    }
  }
  if (activityValue(summary, "Complete", "complete", true) === false) {
    caption += " · часть данных";
    rowDetail += (rowDetail ? " · " : "") + "часть данных";
  }
  return { caption: caption, rowTitle: rowTitle, rowDetail: rowDetail };
}

function activityResultCaption(activity) {
  var toolId = activityToolId(activity);
  var summary = activityTypedResultSummary(activity);
  if (summary) {
    if (toolId === "core.read") return activityReadCaption(summary);
    if (toolId === "core.tools.read") return activityValue(summary, "Complete", "complete", true)
      ? "Загружено" : "Загружена часть описания";
    var count = activityValue(summary, "ReturnedItems", "returnedItems", null);
    if (Number.isSafeInteger(count) && count >= 0) return "Получено элементов: " + count +
      (activityValue(summary, "Complete", "complete", true) === false ||
        activityValue(summary, "Partial", "partial", false) === true ? " · неполный список" : "");
  }
  var read = activityReadSummary(activity);
  if (read) return activityReadCaption(read); // Historical read activity.
  if (["core.find", "core.read", "core.tools.search", "core.tools.read"].indexOf(toolId) >= 0) return "";
  var source = activityDataJson(activity);
  if (!source || source.length > 32768) return "";
  var cached = activityPresentationCache.get(activity);
  if (cached && cached.source === source && cached.toolId === toolId) return cached.caption;
  var caption = "";
  try {
    var data = JSON.parse(source);
    if (data && typeof data === "object" && (data.truncated || data.externalized)) {
      caption = data.externalized ? "Результат сохранён отдельно" : "В журнале показана часть результата";
    } else if (Array.isArray(data)) caption = "Получен список · элементов: " + data.length;
    else if (typeof data === "string") caption = "Получен текстовый ответ";
    else caption = "Получены данные JSON";
  } catch (ignore) { /* Invalid/large data remains available in the existing details. */ }
  activityPresentationCache.set(activity, { toolId: toolId, source: source, caption: caption });
  return caption;
}

var activityNoEffectWarningCodes = {
  excel_sheet_already_exists: true,
  vba_patch_stale_source: true,
  vba_patch_ambiguous: true,
  vba_patch_invalid: true,
  vba_snapshot_refresh_required: true,
  stale_vba_module: true,
  vba_backup_target_ambiguous: true,
  text_patch_invalid: true,
  text_patch_ambiguous: true,
  html_source_observation_required: true,
  html_workspace_target_ambiguous: true,
  invalid_task_list: true,
  task_list_blocker_required: true,
  invalid_plan_document: true,
  resource_not_found: true,
  resource_target_not_found: true,
  resource_target_ambiguous: true,
  resource_scope_incomplete: true,
  resource_revision_changed: true,
  resource_head_unknown: true,
  resource_batch_too_large: true,
  resource_snapshot_too_large: true,
  resource_backpressure: true,
  resource_authority_not_ready: true,
  resource_cursor_invalid: true,
  resource_view_invalid: true,
  resource_target_required: true,
  resource_target_runtime_owned: true,
  resource_view_unsupported: true,
  resource_request_invalid: true,
  resource_whole_read_incomplete: true,
  resource_whole_read_invalid: true,
  capability_not_found: true,
  invalid_arguments: true,
  tool_not_found: true,
  tool_arguments_invalid: true,
  tool_not_dispatched: true,
  repeated_unknown_tool_call: true,
  repeated_tool_failure: true,
  repeated_tool_no_progress: true,
  excel_clear_table_header: true,
  excel_delete_out_of_range: true,
  excel_delete_all_table_columns: true,
  excel_delete_table_not_found: true,
  excel_delete_target_changed: true,
  excel_delete_too_large: true,
  tool_mutation_busy: true,
  manual_tool_chat_busy: true,
  active_document_changed: true,
  document_session_unavailable: true
};

function activityPresentationState(activity) {
  var evidence = activityValue(activity, "ExecutionEvidence", "executionEvidence", null);
  var effect = activityValue(evidence, "Effect", "effect", "");
  if (effect === "Unknown") return "unknown";
  var status = activityStatus(activity);
  if (status === "completed") {
    var summary = activityTypedResultSummary(activity) || activityReadSummary(activity);
    if (summary && (activityValue(summary, "Complete", "complete", true) === false ||
      activityValue(summary, "Partial", "partial", false) === true)) return "partial";
  }
  if (status === "failed" || status === "completed_with_errors") {
    var code = String(activityValue(activity, "ErrorCode", "errorCode", "") || "").toLowerCase();
    var dispatch = activityValue(evidence, "Dispatch", "dispatch", "");
    var noEffect = dispatch === "NotDispatched" || effect === "None" || effect === "VerifiedNoChange";
    return activityToolId(activity) && noEffect && Object.prototype.hasOwnProperty.call(activityNoEffectWarningCodes, code)
      ? "warning" : "failed";
  }
  return status;
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
      resource_snapshot_too_large: "Выберите меньший объём данных",
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
      tool_not_dispatched: "Действие не выполнено",
      repeated_unknown_tool_call: "Повтор не выполнен: результат предыдущей записи не подтверждён",
      repeated_tool_failure: "Повтор не выполнен: нужно устранить причину предыдущей ошибки",
      repeated_tool_no_progress: "Повтор не выполнен: новых данных нет",
      excel_clear_table_header: "Очистка не удаляет столбец таблицы — нужно изменить её размер или удалить столбец",
      excel_delete_out_of_range: "Строки или столбцы выходят за границы таблицы",
      excel_delete_all_table_columns: "В таблице должен остаться хотя бы один столбец",
      excel_delete_table_not_found: "Таблица не найдена на выбранном листе",
      excel_delete_target_changed: "Данные изменились — нужно уточнить удаление",
      excel_delete_too_large: "Область удаления слишком велика для проверки",
      tool_mutation_busy: "Документ занят другим действием",
      manual_tool_chat_busy: "Дождитесь завершения текущего действия",
      active_document_changed: "Активный документ изменился",
      document_session_unavailable: "Документ недоступен",
      vba_patch_stale_source: "Фрагмент патча не совпал с кодом",
      vba_patch_ambiguous: "Нужно уточнить место правки",
      vba_patch_invalid: "Нужно исправить патч",
      vba_snapshot_refresh_required: "Код изменился — перечитайте модуль",
      stale_vba_module: "Модуль изменился — перечитайте код",
      html_source_observation_required: "Нужно прочитать текущий HTML",
      tool_effect_uncertain: "Результат не подтверждён — нужна проверка"
    };
    if (Object.prototype.hasOwnProperty.call(errors, code)) return errors[code];
    var message = String(activityResultMessage(activity) || "").replace(/\s+/g, " ").trim();
    if (message) return message.length > 160 ? message.slice(0, 157) + "…" : message;
    return code ? "Ошибка: " + code : dispatch === "NotDispatched" ? "Ошибка запуска" : "Ошибка выполнения";
  }
  if (!activityToolId(activity)) return activityResultMessage(activity);
  if (effect === "VerifiedNoChange") return "Без изменений";
  if (effect === "VerifiedChange") {
    var toolId = activityToolId(activity);
    if (activityOperation(activity) === "delete") return "Удалено";
    if (activityOperation(activity) === "export") return "Экспортировано";
    return toolId === "core.write" || /\.write$/.test(toolId) ? "Записано" : "Изменено";
  }
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
    activityCommentText(activity) ||
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
  if (!(context && context.workspace)) actions.appendChild(createAgentTextButton("Причина и детали", "secondary", function () {
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

function appendToolActivityCard(parent, activity, context, disclosure, outcome) {
  var card = document.createElement("div");
  card.className = "agent-tool-card";
  var header = document.createElement("div");
  header.className = "agent-tool-card-header";
  var message = activityResultMessage(activity);
  if (message && message.trim() && message.trim() !== outcome) {
    var messageWrap = document.createElement("div");
    messageWrap.className = "agent-tool-message";
    var messageText = document.createElement("span");
    messageText.textContent = message.trim();
    messageWrap.appendChild(messageText);
    if (message.length > 180 || message.indexOf("\n") >= 0) {
      var more = document.createElement("button");
      more.type = "button";
      more.className = "agent-tool-message-more";
      more.textContent = "Показать всё";
      more.setAttribute("aria-expanded", "false");
      more.addEventListener("click", function () {
        var expanded = messageWrap.classList.toggle("is-expanded");
        more.textContent = expanded ? "Свернуть" : "Показать всё";
        more.setAttribute("aria-expanded", String(expanded));
      });
      messageWrap.appendChild(more);
    }
    header.appendChild(messageWrap);
    header.classList.add("has-message");
  }
  var tabs = document.createElement("div");
  tabs.className = "agent-tool-tabs";
  tabs.setAttribute("role", "tablist");
  tabs.setAttribute("aria-label", "Данные вызова");
  header.appendChild(tabs);
  card.appendChild(header);
  var panels = document.createElement("div");
  panels.className = "agent-tool-panels";
  card.appendChild(panels);
  parent.appendChild(card);
  var entries = {}, active = "";
  function addTab(key, label, render, disposable) {
    if (entries[key]) return entries[key];
    var button = document.createElement("button");
    button.type = "button";
    button.className = "agent-tool-tab";
    button.textContent = label;
    button.setAttribute("role", "tab");
    button.setAttribute("aria-selected", "false");
    button.tabIndex = -1;
    button.addEventListener("click", function () { selectTab(key); });
    tabs.appendChild(button);
    var panel = document.createElement("div");
    panel.className = "agent-tool-panel";
    panel.setAttribute("role", "tabpanel");
    panel.setAttribute("aria-label", label);
    panel.hidden = true;
    panels.appendChild(panel);
    entries[key] = { button: button, panel: panel, render: render, disposable: !!disposable, mounted: false, cleanup: null };
    return entries[key];
  }
  function release(entry) {
    if (!entry || !entry.mounted) return;
    if (entry.cleanup) entry.cleanup();
    entry.cleanup = null;
    entry.mounted = false;
  }
  function mount(entry) {
    if (!entry || entry.mounted || typeof entry.render !== "function") return;
    entry.cleanup = entry.render(entry.panel) || null;
    entry.mounted = true;
  }
  function selectTab(key) {
    if (!entries[key] || entries[key].button.hidden) return;
    if (active && active !== key && entries[active].disposable) release(entries[active]);
    active = key;
    Object.keys(entries).forEach(function (name) {
      var selected = name === key;
      entries[name].button.classList.toggle("active", selected);
      entries[name].button.setAttribute("aria-selected", String(selected));
      entries[name].button.tabIndex = selected ? 0 : -1;
      entries[name].panel.hidden = !selected;
    });
    mount(entries[key]);
  }
  tabs.addEventListener("keydown", function (event) {
    if (event.key !== "ArrowRight" && event.key !== "ArrowLeft") return;
    var names = Object.keys(entries).filter(function (key) { return !entries[key].button.hidden; });
    var index = names.indexOf(active);
    var next = names[(index + (event.key === "ArrowRight" ? 1 : names.length - 1)) % names.length];
    if (!next) return;
    event.preventDefault();
    selectTab(next);
    entries[next].button.focus();
  });

  var result = addTab("result", "Результат", null, false);
  var argumentsJson = activityArgumentsJson(activity);
  if (argumentsJson) addTab("arguments", "Аргументы", function (panel) {
    return mountAgentJsonContent(panel, argumentsJson);
  }, true);
  var resultSlot = document.createElement("div");
  resultSlot.className = "agent-tool-result-content";
  result.panel.appendChild(resultSlot);
  function showResponse(target, response) {
    var blocks = response.blocks || [];
    var normal = blocks.filter(function (block) { return block.kind !== "text_changes"; });
    var changes = blocks.filter(function (block) { return block.kind === "text_changes"; });
    var raw = response.rawJson || "";
    release(result);
    result.button.hidden = false;
    if (normal.length) result.render = function () {
      window.RNAssistantToolResultPresentation.renderCompact(target, normal);
    };
    else if (raw) result.render = function () {
      target.textContent = "";
      return mountAgentJsonContent(target, raw);
    };
    else result.render = function () { target.textContent = "Сохранённое содержимое результата недоступно."; };
    result.disposable = !normal.length && !!raw;
    if (changes.length) {
      var changeTab = addTab("changes", "Изменения", null, false);
      release(changeTab);
      changeTab.panel.textContent = "";
      changeTab.button.hidden = false;
      changeTab.render = function (panel) {
        changes.forEach(function (block) {
          if (!block.changes) return;
          if (!(block.changes.items || []).length && block.changes.complete && block.changes.evidenceFound)
            panel.textContent = "Изменений нет.";
          else window.RNAssistantRunChanges.renderInline(panel, block.changes);
        });
      };
      if (active === "changes") mount(changeTab);
    } else if (entries.changes) entries.changes.button.hidden = true;
    if (raw && normal.length) {
      var jsonTab = addTab("json", "JSON", null, true);
      release(jsonTab);
      jsonTab.button.hidden = false;
      jsonTab.render = function (panel) { return mountAgentJsonContent(panel, raw); };
      if (active === "json") mount(jsonTab);
    } else if (entries.json) entries.json.button.hidden = true;
    if (changes.length && !normal.length && !raw) {
      result.button.hidden = true;
      selectTab("changes");
    } else if (active === "result") mount(result);
    else if (entries[active] && entries[active].button.hidden) selectTab("result");
  }
  var loading = typeof appendToolResultPreview === "function" &&
    appendToolResultPreview(resultSlot, activity, context, disclosure, showResponse);
  if (!loading) {
    var local = activityDataJson(activity);
    result.render = local ? function () { return mountAgentJsonContent(resultSlot, local); }
      : function () { resultSlot.textContent = activityStatus(activity) === "running" ? "Ожидаю результат…" : "Содержимое результата недоступно."; };
    result.disposable = !!local;
  }
  selectTab("result");
  disclosure.addEventListener("toggle", function () {
    if (disclosure.open) {
      if (active === "arguments" || !loading) mount(entries[active]);
      return;
    }
    Object.keys(entries).forEach(function (key) {
      if (entries[key].disposable) release(entries[key]);
    });
  });
}

function appendActivityDetailsContent(node, activity, context) {
  var children = activityChildren(activity);

  var body = document.createElement("div");
  body.className = "agent-activity-detail-body";
  var toolId = activityToolId(activity);
  var outcome = activityDisplayResult(activity);
  if (!toolId && outcome && !activityDetailTexts(activity, context).length) {
    var summary = document.createElement("p"); summary.className = "agent-activity-detail-summary";
    summary.textContent = outcome; body.appendChild(summary);
  }
  var target = activityCommentText(activity);
  if (target && !toolId) {
    var source = document.createElement("code");
    source.className = "agent-activity-target";
    source.textContent = target;
    body.appendChild(source);
  }

  if (children.length && !(context && context.liveFeed)) {
    var childList = document.createElement("div");
    childList.className = "agent-activity-children";
    children.forEach(function (child) {
      childList.appendChild(renderActivityNode(child, true, context && activityContains(child, context.currentActivity), context));
    });
    body.appendChild(childList);
  }

  if (toolId) {
    appendToolActivityCard(body, activity, context, node, outcome);
    appendActivityErrorActions(body, activity, context);
    node.appendChild(body);
    return;
  }

  activityDetailTexts(activity, context).forEach(function (text) {
    var result = document.createElement("div");
    result.className = "agent-activity-result";
    result.textContent = (activityToolId(activity) ? "" : "Диагностика:\n") + text;
    body.appendChild(result);
  });
  appendActivityErrorActions(body, activity, context);
  node.appendChild(body);
}

function appendActivityArtifacts(node, activity, context) {
  if (context && context.workspace) return false;
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
    warning: "Требует внимания",
    unknown: "Результат требует проверки",
    partial: "Получена часть данных",
    failed: "Ошибка",
    cancelled: "Отменено",
    pending: "Ожидает"
  };
  return labels[status] || status || "Статус";
}

function appendAgentRunArtifacts(parent, timeline, workspace) {
  var artifacts = document.createElement("div");
  artifacts.className = "agent-run-artifacts";
  (timeline || []).forEach(function (item) {
    appendActivityTreeArtifacts(artifacts, item.activity, {
      messageId: messageId(item.message),
      index: item.index,
      message: item.message,
      workspace: !!workspace
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
