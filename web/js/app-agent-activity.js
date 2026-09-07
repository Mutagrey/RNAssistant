function renderActivityNode(activity, nested, current, context) {
  var node = document.createElement("div");
  var status = activityStatus(activity);
  var kind = activityKind(activity) || "activity";
  var operation = activityOperation(activity);
  node.className = "agent-activity kind-" + kind + " operation-" + operation +
    (nested ? " nested" : "") + (current ? " current" : "") + " status-" + status;

  var expandable = activityHasDetails(activity);
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
    (hideIcon ? " is-status-label" : "");
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
    target.textContent = comment;
    copy.appendChild(target);
  }

  var resultText = activityDisplayResult(activity);
  if (resultText && resultText !== title && resultText !== comment) {
    var resultLine = document.createElement("span");
    resultLine.className = "agent-activity-caption";
    resultLine.textContent = resultText.length > 240 ? resultText.slice(0, 239) + "…" : resultText;
    copy.appendChild(resultLine);
  }

  if (status === "failed" || status === "completed_with_errors" || status === "cancelled") {
    var state = document.createElement("span");
    state.className = "agent-activity-state status-" + status;
    var evidence = activity.ExecutionEvidence || activity.executionEvidence || {};
    state.textContent = (evidence.Effect || evidence.effect) === "Unknown" ? "Не подтверждено" :
      status === "cancelled" ? "Отменено" : "Не выполнено";
    copy.appendChild(state);
  }
  row.appendChild(copy);

  if (expandable) {
    var caret = document.createElement("span");
    caret.className = "agent-activity-caret";
    caret.setAttribute("aria-hidden", "true");
    heading.appendChild(caret);
  }
  return row;
}

function activityOperation(activity) {
  var kind = activityKind(activity);
  if (kind === "reasoning") return "reasoning";
  if (kind === "diagnostic") return "diagnostic";
  if (kind === "step" || kind === "notice" || kind === "compaction") return "status";

  var toolId = String(activityToolId(activity) || "").toLowerCase();
  var operationId = toolId.replace(/[.\-]/g, "_");
  if (/(^|_)search($|_)/.test(operationId)) return "search";
  if (/(^|_)(read|inspect|list|resolve|get|find)($|_)/.test(operationId)) return "read";
  if (/(^|_)(write|upsert|update|patch|format|create|add|set|delete|remove|rename|restore|install|bind|refresh|freeze)($|_)/.test(operationId)) return "write";
  if (/(^|_)(run|execute|command|macro)($|_)/.test(operationId)) return "command";
  return kind === "tool" || kind === "control" ? "command" : "status";
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
  return icons[activityOperation(activity)] || icons.status;
}

function activityPrimaryText(activity) {
  var toolTitle = activityToolLabel(activityToolId(activity), activityStatus(activity) === "running");
  if (toolTitle) return toolTitle;
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

function activityToolLabel(toolId, running) {
  var labels = {
    "common.resources_find": ["Поиск ресурсов", "Ищу ресурсы"],
    "common.resources_read": ["Чтение ресурса", "Читаю ресурс"],
    "common.capabilities_search": ["Поиск инструментов и навыков", "Ищу подходящие инструменты"],
    "common.capabilities_read": ["Изучение инструмента или навыка", "Изучаю инструмент или навык"],
    "common.questions_ask": ["Уточнение задачи", "Готовлю уточнение"],
    "common.task_list_set": ["Обновление шагов задачи", "Обновляю шаги задачи"],
    "common.plan_doc_save": ["Сохранение плана", "Сохраняю план"],
    "common.plan_doc_restore": ["Восстановление плана", "Восстанавливаю план"],
    "common.plan_doc_delete": ["Удаление плана", "Удаляю план"],
    "common.vba_write": ["Запись VBA-модуля", "Записываю VBA-модуль"],
    "common.vba_patch": ["Изменение VBA-модуля", "Изменяю VBA-модуль"],
    "common.vba_rename": ["Переименование VBA-модуля", "Переименовываю VBA-модуль"],
    "common.vba_delete": ["Удаление VBA-модуля", "Удаляю VBA-модуль"],
    "common.vba_restore": ["Восстановление VBA-модуля", "Восстанавливаю VBA-модуль"],
    "common.office_run_macro": ["Выполнение макроса", "Выполняю макрос"],
    "common.html_workspace_write_file": ["Запись файла страницы", "Записываю файл страницы"],
    "common.html_workspace_apply_patch": ["Изменение файла страницы", "Изменяю файл страницы"],
    "common.html_workspace_delete": ["Удаление файла или данных страницы", "Удаляю файл или данные страницы"],
    "common.html_data_write": ["Запись данных страницы", "Записываю данные страницы"],
    "common.html_data_bind": ["Подключение данных к странице", "Подключаю данные к странице"],
    "common.html_data_refresh": ["Обновление данных страницы", "Обновляю данные страницы"],
    "common.html_data_freeze": ["Сохранение снимка данных", "Сохраняю снимок данных"],
    "excel.inspect": ["Проверка структуры книги", "Проверяю структуру книги"],
    "excel.add_sheet": ["Создание листа", "Создаю лист"],
    "excel.rename_sheet": ["Переименование листа", "Переименовываю лист"],
    "excel.write_range": ["Запись диапазона", "Записываю диапазон"],
    "excel.format_range": ["Форматирование диапазона", "Оформляю диапазон"],
    "excel.clear_range": ["Очистка диапазона", "Очищаю диапазон"],
    "excel.sort_range": ["Сортировка диапазона", "Сортирую диапазон"],
    "excel.filter_range": ["Фильтрация диапазона", "Фильтрую диапазон"],
    "excel.add_table": ["Создание таблицы", "Создаю таблицу"],
    "excel.upsert_chart": ["Обновление диаграммы", "Обновляю диаграмму"],
    "excel.delete_chart": ["Удаление диаграммы", "Удаляю диаграмму"],
    "excel.create_chat_chart": ["Создание диаграммы в чате", "Создаю диаграмму в чате"],
    "excel.find_cells": ["Поиск ячеек", "Ищу ячейки"],
    "excel.replace_cells": ["Замена содержимого ячеек", "Заменяю содержимое ячеек"]
  };
  var label = labels[toolId];
  return label ? label[running ? 1 : 0] : "";
}

function activityDisplayResult(activity) {
  var status = activityStatus(activity);
  if (status === "running" || status === "pending") return "";
  var evidence = activityValue(activity, "ExecutionEvidence", "executionEvidence", null);
  var effect = activityValue(evidence, "Effect", "effect", "");
  var dispatch = activityValue(evidence, "Dispatch", "dispatch", "");
  if (effect === "Unknown") return "Результат действия не подтверждён. Нужна проверка.";
  var code = activityValue(activity, "ErrorCode", "errorCode", "");
  if (dispatch === "NotDispatched" && code === "excel_sheet_already_exists") return "Лист уже существует. Создание не выполнено.";
  if (effect === "VerifiedNoChange") return "Проверка подтвердила отсутствие изменений.";
  return activityResultMessage(activity);
}

function activityCommentText(activity) {
  var toolId = activityToolId(activity);
  var subtitle = activityValue(activity, "Subtitle", "subtitle", "");
  return subtitle && subtitle !== toolId ? subtitle : "";
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

function activityHasDetails(activity) {
  return !!(activityChildren(activity).length ||
    activityArgumentsJson(activity) ||
    activityDataJson(activity) ||
    activityResultMessage(activity) ||
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

function appendActivityErrorPanel(node, activity, context) {
  if (activityStatus(activity) !== "failed") {
    return;
  }

  var result = activityDisplayResult(activity);
  var toolId = activityToolId(activity);
  var panel = document.createElement("div");
  panel.className = "agent-error-panel";

  var reason = document.createElement("div");
  reason.className = "agent-error-reason";
  reason.textContent = result || "Шаг завершился ошибкой.";
  panel.appendChild(reason);

  var meta = document.createElement("div");
  meta.className = "agent-error-meta";
  meta.textContent = toolId ? ("Инструмент: " + toolId) : "Шаг инструмента";
  panel.appendChild(meta);

  var actions = document.createElement("div");
  actions.className = "agent-inline-actions";
  actions.appendChild(createAgentCopyButton("Копировать диагностику", [
    "Title: " + activityTitle(activity),
    "Tool: " + toolId,
    "Status: " + activityStatus(activity),
    "Reason: " + activityResultMessage(activity)
  ].join("\n")));
  actions.appendChild(createAgentTextButton("Причина и детали", "secondary", function () {
    if (typeof window.openRunJournal !== "function") return;
    var message = context && context.message ? context.message : null;
    window.openRunJournal({
      chatId: state.activeChatId,
      runId: activityValue(activity, "RunId", "runId", "") || (message ? messageRunId(message) : ""),
      stepId: activityStepId(activity),
      toolCallId: activityToolCallId(activity),
      filter: "problems"
    });
  }));
  panel.appendChild(actions);
  node.appendChild(panel);
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
  }

  if (children.length) {
    var childList = document.createElement("div");
    childList.className = "agent-activity-children";
    children.forEach(function (child) {
      childList.appendChild(renderActivityNode(child, true, context && activityContains(child, context.currentActivity), context));
    });
    body.appendChild(childList);
  }

  appendActivityErrorPanel(body, activity, context);

  if (activityStatus(activity) !== "failed" && activityResultMessage(activity)) {
    var result = document.createElement("div");
    result.className = "agent-activity-result";
    result.textContent = activityResultMessage(activity);
    body.appendChild(result);
  }
  if (typeof appendArgumentsData === "function") {
    appendArgumentsData(body, activityArgumentsJson(activity));
  }
  if (typeof appendActivityData === "function") {
    appendActivityData(body, "Данные результата", activityDataJson(activity));
  }

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

function agentDiagnosticText(item) {
  if (!item) return "";
  return item.message ? messageContent(item.message).trim() : "";
}

function appendAgentDiagnosticMessage(parent, text) {
  text = String(text || "").trim();
  if (!text) return;
  var message = document.createElement("div");
  message.className = "agent-diagnostic-message markdown";
  message.innerHTML = markdown(text);
  parent.appendChild(message);
  enhanceMarkdown(message, { enableJsonViewer: true, sourceText: text });
}
