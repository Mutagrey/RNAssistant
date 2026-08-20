function renderActivityNode(activity, nested, current, context) {
  var node = document.createElement("div");
  var status = activityStatus(activity);
  var kind = activityKind(activity) || "activity";
  node.className = "agent-activity kind-" + kind + (nested ? " nested" : "") + (current ? " current" : "") + " status-" + status;

  var expandable = activityHasDetails(activity);
  if (expandable) {
    var details = document.createElement("details");
    details.className = "agent-activity-toggle";
    details.open = kind === "tool_batch";
    details.appendChild(renderActivityRow(activity, current, true, context));
    appendActivityDetailsContent(details, activity, context);
    node.appendChild(details);
  } else {
    node.appendChild(renderActivityRow(activity, current, false, context));
  }

  if (!context || context.renderInlineArtifacts !== false) {
    appendActivityArtifacts(node, activity, context);
  }
  return node;
}

function renderActivityRow(activity, current, expandable, context) {
  var row = document.createElement(expandable ? "summary" : "div");
  var status = activityStatus(activity);
  var title = activityPrimaryText(activity);
  var comment = activityCommentText(activity);
  row.className = "agent-activity-row" + (comment ? " has-comment" : " has-no-comment");
  row.title = [title, comment, agentStatusLabel(status)].filter(Boolean).join(" · ");

  var mark = document.createElement("span");
  mark.className = "agent-activity-mark";
  mark.setAttribute("aria-hidden", "true");
  row.appendChild(mark);

  var copy = document.createElement("span");
  copy.className = "agent-activity-copy";

  var operation = toolActionLabel(activity);
  var operationNode = document.createElement("span");
  operationNode.className = "agent-activity-action" + (operation ? "" : " is-empty");
  operationNode.textContent = operation;
  copy.appendChild(operationNode);

  var name = document.createElement("span");
  name.className = "agent-activity-name";
  name.textContent = title;
  copy.appendChild(name);

  var commentNode = document.createElement("span");
  commentNode.className = "agent-activity-comment" + (comment ? "" : " is-empty");
  commentNode.textContent = comment;
  copy.appendChild(commentNode);

  var metaParts = [agentStatusLabel(status)];
  var time = activityTimeText(context);
  if (time) {
    metaParts.push(time);
  }
  var meta = document.createElement("span");
  meta.className = "agent-activity-meta";
  meta.textContent = metaParts.join(" · ");
  copy.appendChild(meta);
  row.appendChild(copy);

  if (expandable) {
    var caret = document.createElement("span");
    caret.className = "agent-activity-caret";
    caret.setAttribute("aria-hidden", "true");
    caret.textContent = "›";
    copy.appendChild(caret);
  }
  return row;
}

function activityPrimaryText(activity) {
  var progressTitle = typeof activityProgressTitle === "function" ? activityProgressTitle(activity) : "";
  if (progressTitle) {
    return progressTitle;
  }

  var title = activityTitle(activity);
  var toolId = activityToolId(activity);
  if (title && title !== toolId && title !== "Tool step" && title !== "Agent step" &&
      title.toLowerCase().indexOf("deterministic") !== 0) {
    return title.charAt(0).toUpperCase() + title.slice(1);
  }
  if (toolId) {
    return friendlyToolActionText(toolId);
  }

  var labels = {
    reasoning: "Анализирую задачу",
    verification: "Проверяю результат",
    retry: "Повторяю шаг",
    tool: "Выполняю действие",
    tool_batch: "Инструменты",
    plan: "Формирую план"
  };
  return labels[activityKind(activity)] || toolId || title || "Выполняю шаг";
}

function toolActionKind(toolId) {
  var id = String(toolId || "").toLowerCase();
  if (id.indexOf("find") >= 0 || id.indexOf("search") >= 0) return "SEARCH";
  if (id.indexOf("delete") >= 0 || id.indexOf("remove") >= 0 || id.indexOf("clear") >= 0) return "DELETE";
  if (id.indexOf("add_") >= 0 || id.indexOf("create") >= 0 || id.indexOf("insert") >= 0) return "CREATE";
  if (id.indexOf("write") >= 0 || id.indexOf("set_formula") >= 0) return "WRITE";
  if (id.indexOf("update") >= 0 || id.indexOf("replace") >= 0 || id.indexOf("rename") >= 0 ||
      id.indexOf("format") >= 0 || id.indexOf("autofit") >= 0 || id.indexOf("sort") >= 0 ||
      id.indexOf("filter") >= 0 || id.indexOf("upsert") >= 0) return "UPDATE";
  if (id.indexOf("run") >= 0 || id.indexOf("execute") >= 0) return "RUN";
  if (id.indexOf("verify") >= 0 || id.indexOf("validate") >= 0 || id.indexOf("check") >= 0) return "CHECK";
  if (id.indexOf("load") >= 0) return "LOAD";
  if (id.indexOf("read") >= 0 || id.indexOf("get_") >= 0 || id.indexOf("list_") >= 0 ||
      id.indexOf("summary") >= 0 || id.indexOf("profile") >= 0 || id.indexOf("inspect") >= 0) return "READ";
  return "ACTION";
}

function toolActionLabel(activity) {
  var kind = activityKind(activity);
  if (["tool", "verification", "control"].indexOf(kind) < 0 || !activityToolId(activity)) return "";
  return toolActionKind(activityToolId(activity));
}

function friendlyToolActionText(toolId) {
  var labels = {
    "excel.get_context": "Читаю контекст книги",
    "excel.get_selection": "Читаю выделенные ячейки",
    "excel.workbook_summary": "Читаю структуру книги",
    "excel.list_sheets": "Получаю список листов",
    "excel.read_range": "Читаю значения диапазона",
    "excel.read_formula_range": "Читаю формулы диапазона",
    "excel.profile_range": "Анализирую структуру диапазона",
    "excel.find_cells": "Ищу ячейки",
    "excel.write_range": "Записываю значение в ячейки",
    "excel.write_table": "Записываю таблицу",
    "excel.set_formula": "Записываю формулу",
    "excel.add_table": "Создаю таблицу Excel",
    "excel.add_chart": "Создаю график",
    "excel.update_chart": "Изменяю график",
    "excel.delete_chart": "Удаляю график",
    "excel.format_range": "Форматирую диапазон",
    "excel.autofit": "Подбираю ширину строк и столбцов",
    "excel.add_sheet": "Создаю новый лист",
    "excel.rename_sheet": "Переименовываю лист",
    "excel.clear_range": "Очищаю диапазон",
    "excel.sort_range": "Сортирую диапазон",
    "excel.filter_range": "Фильтрую диапазон",
    "excel.replace_cells": "Заменяю значения в ячейках",
    "common.skills_load": "Загружаю инструкции навыка"
  };
  var id = String(toolId || "").toLowerCase();
  if (labels[id]) return labels[id];
  var fallbacks = {
    SEARCH: "Выполняю поиск",
    READ: "Читаю данные",
    CREATE: "Создаю объект",
    WRITE: "Записываю данные",
    UPDATE: "Обновляю объект",
    DELETE: "Удаляю объект",
    RUN: "Запускаю действие",
    CHECK: "Проверяю результат",
    LOAD: "Загружаю данные",
    ACTION: "Выполняю действие"
  };
  return fallbacks[toolActionKind(id)] || fallbacks.ACTION;
}

function activityCommentText(activity) {
  var toolId = activityToolId(activity);
  var title = activityTitle(activity);
  var subtitle = activityValue(activity, "Subtitle", "subtitle", "");
  if (toolId && toolId !== title) {
    return toolId;
  }
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

function appendActivityErrorPanel(node, activity) {
  if (activityStatus(activity) !== "failed") {
    return;
  }

  var result = activityResultMessage(activity);
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
    "Reason: " + result
  ].join("\n")));
  panel.appendChild(actions);
  node.appendChild(panel);
}

function appendActivityDetailsContent(node, activity, context) {
  var children = activityChildren(activity);

  var body = document.createElement("div");
  body.className = "agent-activity-detail-body";

  if (children.length) {
    var childList = document.createElement("div");
    childList.className = "agent-activity-children";
    children.forEach(function (child) {
      childList.appendChild(renderActivityNode(child, true, context && activityContains(child, context.currentActivity), context));
    });
    body.appendChild(childList);
  }

  appendActivityErrorPanel(body, activity);

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
    appendActivityData(body, "Данные результата", activityDataJson(activity), "Копировать результат");
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
  if (typeof tryRenderHtmlArtifact === "function") {
    var html = tryRenderHtmlArtifact(activity, context || {});
    if (html) {
      node.appendChild(html);
      appended = true;
    }
  }
  return appended;
}

function agentStatusLabel(status) {
  var labels = {
    completed: "Готово",
    running: "Выполняю",
    waiting: "Нужно подтверждение",
    failed: "Ошибка",
    cancelled: "Отменено",
    planned: "В плане",
    pending: "Ожидает",
    incomplete: "Не завершено"
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

function agentDecisionText(item) {
  if (!item) return "";
  if (item.decisionText) return String(item.decisionText).trim();
  var activity = item.activity || {};
  var explicit = activity.__decisionMessage || activityValue(activity, "DecisionMessage", "decisionMessage", "");
  if (explicit) return String(explicit).trim();
  if (activityKind(activity) === "plan") {
    var latestPlanSummary = activityValue(activity, "Subtitle", "subtitle", "");
    if (latestPlanSummary) return String(latestPlanSummary).trim();
  }
  var persistedSummary = item.message ? messageDecisionSummary(item.message).trim() : "";
  if (persistedSummary) return persistedSummary;
  if ((activityKind(activity) === "plan" || activityKind(activity) === "tool" || activityStatus(activity) === "planned") && item.message) {
    return messageContent(item.message).trim();
  }
  return "";
}

function appendAgentDecisionMessage(parent, text) {
  text = String(text || "").trim();
  if (!text) return;
  var message = document.createElement("div");
  message.className = "agent-decision-message markdown";
  message.innerHTML = markdown(text);
  parent.appendChild(message);
  enhanceMarkdown(message);
}

function appendAgentRunProcess(parent, timeline, stats) {
  if (!timeline.length) {
    var empty = document.createElement("div");
    empty.className = "agent-run-empty";
    empty.textContent = "Шаги пока не получены.";
    parent.appendChild(empty);
    return;
  }

  var process = document.createElement("div");
  process.className = "agent-run-process";
  timeline.forEach(function (item) {
    var entry = document.createElement("div");
    entry.className = "agent-transcript-entry";
    if (typeof appendMessageReasoning === "function") appendMessageReasoning(entry, item.reasoningMessage || item.message);
    var itemKind = activityKind(item.activity);
    if (itemKind === "tool_batch" || itemKind === "diagnostic" || itemKind === "retry") {
      appendAgentDecisionMessage(entry, agentDecisionText(item));
    }
    var isCurrent = stats.current && activityContains(item.activity, stats.current);
    var activityContext = {
      messageId: messageId(item.message),
      index: item.index,
      message: item.message,
      currentActivity: stats.current,
      renderInlineArtifacts: false
    };
    if (itemKind === "tool_batch" && activityChildren(item.activity).length) {
      var batchList = document.createElement("div");
      batchList.className = "agent-tool-batch-list";
      activityChildren(item.activity).forEach(function (child) {
        batchList.appendChild(renderActivityNode(
          child,
          false,
          stats.current && activityContains(child, stats.current),
          activityContext));
      });
      entry.appendChild(batchList);
    } else {
      entry.appendChild(renderActivityNode(item.activity, false, isCurrent, activityContext));
    }
    process.appendChild(entry);
  });
  parent.appendChild(process);
}

function collectAgentRunTimelineItems(items) {
  items = items || [];
  return items.filter(function (item) {
    return item && activityKind(item.activity) !== "plan";
  });
}

function collectVisibleAgentTimelineItems(items, finalMessage) {
  var timeline = collapseAgentTimelineItems(collectAgentRunTimelineItems(items));
  return timeline.filter(function (item, index) {
    return !isRecoveredFailureItem(timeline, index, finalMessage);
  });
}

function collapseAgentTimelineItems(timeline) {
  var result = [];
  var latestByKey = {};
  (timeline || []).forEach(function (item) {
    var nextItem = {
      message: item.message,
      index: item.index,
      activity: item.activity,
      decisionText: agentDecisionText(item),
      reasoningMessage: typeof messageHasReasoning === "function" && messageHasReasoning(item.message) ? item.message : null
    };
    var key = activityTimelineKey(item.activity);
    var existingIndex = latestByKey[key];
    if (existingIndex !== undefined) {
      var existingStatus = activityStatus(result[existingIndex].activity);
      var nextStatus = activityStatus(item.activity);
      if (existingStatus === "planned" || existingStatus === "running" || existingStatus === "waiting" ||
          (existingStatus === "failed" && nextStatus === "completed")) {
        nextItem.decisionText = result[existingIndex].decisionText || nextItem.decisionText;
        nextItem.reasoningMessage = nextItem.reasoningMessage || result[existingIndex].reasoningMessage;
        result[existingIndex] = nextItem;
        return;
      }
    }
    latestByKey[key] = result.length;
    result.push(nextItem);
  });
  return result;
}

function isRecoveredFailureItem(timeline, index, finalMessage) {
  var item = timeline[index];
  var activity = item && item.activity;
  if (activityStatus(activity) !== "failed") {
    return false;
  }

  var toolId = activityToolId(activity);
  for (var i = index + 1; i < timeline.length; i += 1) {
    var later = timeline[i] && timeline[i].activity;
    if (activityStatus(later) === "completed" && (!toolId || activityToolId(later) === toolId)) {
      return true;
    }
  }
  return false;
}

function timelineStatusCounts(timeline) {
  var counts = { total: 0 };
  (timeline || []).forEach(function (item) {
    activityCountStatus(item.activity, counts);
  });
  return counts;
}

function statusFromCounts(counts) {
  return counts.failed ? "failed" :
    (counts.running ? "running" :
      (counts.waiting ? "waiting" :
        (counts.incomplete ? "incomplete" :
          (counts.cancelled ? "cancelled" :
          (counts.planned && counts.planned === counts.total ? "planned" : "completed")))));
}

function agentRunDisplayStats(stats, finalMessage, timeline) {
  var visibleCounts = timelineStatusCounts(timeline);
  if (!stats.counts || !stats.counts.failed || visibleCounts.failed) {
    return stats;
  }
  return {
    text: stats.text,
    current: visibleCounts.running || visibleCounts.waiting ? stats.current : null,
    counts: visibleCounts,
    elapsed: stats.elapsed,
    status: statusFromCounts(visibleCounts)
  };
}

function activityContains(activity, target) {
  if (!activity || !target) {
    return false;
  }
  if (activity === target) {
    return true;
  }
  var children = activityChildren(activity);
  for (var i = 0; i < children.length; i += 1) {
    if (activityContains(children[i], target)) {
      return true;
    }
  }
  return false;
}

function findAgentPlanItem(items) {
  var plan = null;
  (items || []).forEach(function (item) {
    if (item && activityKind(item.activity) === "plan") {
      plan = item;
    }
  });
  return plan;
}

function normalizePlanStepStatus(status) {
  var value = String(status || "pending").toLowerCase();
  if (value === "inprogress" || value === "in_progress") return "running";
  return ["completed", "running", "waiting", "failed", "cancelled"].indexOf(value) >= 0 ? value : "pending";
}

function agentPlanInfo(plan) {
  var steps = activityChildren(plan);
  var completed = 0;
  var current = null;
  steps.forEach(function (step) {
    var status = normalizePlanStepStatus(activityStatus(step));
    if (status === "completed") completed += 1;
    if (!current && (status === "running" || status === "waiting")) current = step;
  });
  if (steps.length && completed === steps.length) {
    current = null;
  }
  if (!current) {
    current = completed === steps.length ? null : (steps.filter(function (step) {
      return normalizePlanStepStatus(activityStatus(step)) === "pending";
    })[0] || steps.filter(function (step) {
      var status = normalizePlanStepStatus(activityStatus(step));
      return status === "failed" || status === "cancelled";
    })[0] || (steps.length ? steps[steps.length - 1] : null));
  }
  return { steps: steps, completed: completed, total: steps.length, current: current };
}

function planStatusMark(status) {
  var marks = { completed: "✓", running: "•", waiting: "!", failed: "×", cancelled: "–", pending: "" };
  return marks[normalizePlanStepStatus(status)] || "";
}

function renderAgentPlanSteps(plan, includeGoal) {
  var list = document.createElement("ol");
  list.className = "agent-plan-list";
  if (includeGoal) {
    list.appendChild(renderAgentPlanGoal(plan, "li"));
  }
  activityChildren(plan).forEach(function (step) {
    var status = normalizePlanStepStatus(activityStatus(step));
    var row = document.createElement("li");
    row.className = "agent-plan-step status-" + status;
    row.setAttribute("aria-label", activityTitle(step) + " · " + agentStatusLabel(status));
    var mark = document.createElement("span");
    mark.className = "agent-plan-step-mark";
    mark.setAttribute("aria-hidden", "true");
    mark.textContent = planStatusMark(status);
    row.appendChild(mark);
    var title = document.createElement("span");
    title.className = "agent-plan-step-title";
    title.textContent = activityTitle(step);
    row.appendChild(title);
    list.appendChild(row);
  });
  return list;
}

function renderAgentPlanGoal(plan, tagName) {
  var goal = document.createElement(tagName || "div");
  goal.className = "agent-plan-goal";
  var label = document.createElement("span");
  label.textContent = "Цель";
  goal.appendChild(label);
  var value = document.createElement("strong");
  value.textContent = activityTitle(plan);
  goal.appendChild(value);
  return goal;
}

function appendAgentRunPlan(parent, plan) {
  if (!plan || !activityChildren(plan).length) return;
  var info = agentPlanInfo(plan);
  var details = document.createElement("details");
  details.className = "agent-run-plan status-" + activityStatus(plan);
  var summary = document.createElement("summary");
  summary.className = "agent-run-plan-summary";
  var label = document.createElement("strong");
  label.textContent = "План · " + info.completed + "/" + info.total;
  summary.appendChild(label);
  var current = document.createElement("span");
  current.textContent = info.current ? activityTitle(info.current) : activityTitle(plan);
  summary.appendChild(current);
  var caret = document.createElement("span");
  caret.className = "agent-run-plan-caret";
  caret.setAttribute("aria-hidden", "true");
  caret.textContent = "›";
  summary.appendChild(caret);
  details.appendChild(summary);
  details.appendChild(renderAgentPlanGoal(plan));
  details.appendChild(renderAgentPlanSteps(plan));
  parent.appendChild(details);
}

function activeAgentPlanActivity() {
  var activeSend = currentActiveSend();
  var approval = pendingAgentApprovalActivity();
  if (!activeSend && !approval) return null;
  var activities = state.liveAgentRun || [];
  for (var i = activities.length - 1; i >= 0; i -= 1) {
    if (activityKind(activities[i]) === "plan") return activities[i];
  }
  if (!approval && (!activeSend || !activeSend.confirming)) return null;
  for (var messageIndex = state.messages.length - 1; messageIndex >= 0; messageIndex -= 1) {
    var activity = messageActivity(state.messages[messageIndex]);
    if (activityKind(activity) === "plan") return activity;
  }
  return null;
}

function pendingConfirmationInActivity(activity) {
  if (!activity) return null;
  if (activityPendingId(activity) && activityStatus(activity) === "waiting") {
    return activity;
  }
  var children = activityChildren(activity);
  for (var index = children.length - 1; index >= 0; index -= 1) {
    var child = pendingConfirmationInActivity(children[index]);
    if (child) return child;
  }
  return null;
}

function pendingAgentApprovalActivity() {
  if (currentActiveSend()) return null;
  var live = state.liveAgentRun || [];
  for (var liveIndex = live.length - 1; liveIndex >= 0; liveIndex -= 1) {
    var liveMatch = pendingConfirmationInActivity(live[liveIndex]);
    if (liveMatch) return liveMatch;
  }
  for (var messageIndex = state.messages.length - 1; messageIndex >= 0; messageIndex -= 1) {
    var match = pendingConfirmationInActivity(messageActivity(state.messages[messageIndex]));
    if (match) return match;
  }
  return null;
}

function renderAgentApprovalDock() {
  var dock = $("agentApprovalDock");
  if (!dock) return;
  var activity = pendingAgentApprovalActivity();
  if (!activity) {
    dock.replaceChildren();
    dock.classList.add("hidden");
    return;
  }

  var pendingId = activityPendingId(activity);
  var panel = document.createElement("section");
  panel.className = "agent-approval-panel";
  panel.setAttribute("aria-label", "Подтверждение действия агента");

  var mark = document.createElement("span");
  mark.className = "agent-approval-mark";
  mark.setAttribute("aria-hidden", "true");
  mark.textContent = "!";
  panel.appendChild(mark);

  var copy = document.createElement("div");
  copy.className = "agent-approval-copy";
  var title = document.createElement("div");
  title.className = "agent-approval-title";
  title.textContent = activityPrimaryText(activity);
  copy.appendChild(title);
  var meta = document.createElement("div");
  meta.className = "agent-approval-meta";
  meta.textContent = ["Нужно подтверждение", activityToolId(activity)].filter(Boolean).join(" · ");
  copy.appendChild(meta);
  var reason = activityResultMessage(activity);
  if (reason) {
    var reasonNode = document.createElement("div");
    reasonNode.className = "agent-approval-reason";
    reasonNode.textContent = reason;
    copy.appendChild(reasonNode);
  }
  panel.appendChild(copy);

  var actions = document.createElement("div");
  actions.className = "agent-approval-actions";
  actions.appendChild(createAgentTextButton("Отменить", "secondary", function () {
    cancelAgentTool(pendingId);
  }));
  actions.appendChild(createAgentTextButton("Подтвердить", "primary", function () {
    confirmAgentTool(pendingId);
  }));
  panel.appendChild(actions);

  dock.replaceChildren(panel);
  dock.classList.remove("hidden");
}

function renderAgentPlanDock() {
  var dock = $("agentPlanDock");
  if (!dock) return;
  var plan = activeAgentPlanActivity();
  if (!plan || !activityChildren(plan).length) {
    dock.replaceChildren();
    dock.classList.add("hidden");
    return;
  }

  var info = agentPlanInfo(plan);
  var runId = activityValue(plan, "RunId", "runId", "") || (state.chatRuns[state.activeChatId] || {}).runId || state.activeChatId;
  var details = document.createElement("details");
  details.className = "agent-plan-card status-" + activityStatus(plan);
  details.open = !!state.agentPlanExpanded[runId];
  details.addEventListener("toggle", function () {
    state.agentPlanExpanded[runId] = details.open;
  });

  var summary = document.createElement("summary");
  summary.className = "agent-plan-card-summary";
  var count = document.createElement("span");
  count.className = "agent-plan-card-count";
  count.textContent = info.completed + " из " + info.total;
  summary.appendChild(count);
  var current = document.createElement("span");
  current.className = "agent-plan-card-current";
  current.textContent = info.current ? activityTitle(info.current) : activityTitle(plan);
  summary.appendChild(current);
  var caret = document.createElement("span");
  caret.className = "agent-plan-card-caret";
  caret.setAttribute("aria-hidden", "true");
  caret.textContent = "›";
  summary.appendChild(caret);
  details.appendChild(summary);
  details.appendChild(renderAgentPlanSteps(plan, true));
  dock.replaceChildren(details);
  dock.classList.remove("hidden");
}

function appendAgentFinalAnswer(parent, finalMessage, hasVisiblePlan) {
  if (!finalMessage || !messageContent(finalMessage.message).trim()) {
    return;
  }

  if (typeof appendMessageReasoning === "function") appendMessageReasoning(parent, finalMessage.message);

  var summaryText = messageDecisionSummary(finalMessage.message).trim();
  if (summaryText && summaryText !== messageContent(finalMessage.message).trim()) {
    appendAgentDecisionMessage(parent, summaryText);
  }
  var goalText = messageGoal(finalMessage.message).trim();
  if (goalText && !hasVisiblePlan) {
    var goal = document.createElement("div");
    goal.className = "agent-message-goal";
    goal.textContent = "Цель: " + goalText;
    parent.appendChild(goal);
  }

  var answer = document.createElement("div");
  answer.className = "agent-run-final markdown";
  answer.innerHTML = markdown(messageContent(finalMessage.message));
  parent.appendChild(answer);
  enhanceMarkdown(answer);
}

function enhanceActivity(root) {
  Array.prototype.slice.call(root.querySelectorAll("pre code")).forEach(function (code) {
    highlightCode(code);
  });
}

async function deleteAgentRun(items, finalMessage) {
  var targets = (items || []).slice();
  if (finalMessage) {
    targets.push(finalMessage);
  }
  if (!targets.length || !window.confirm("Delete this agent run?")) {
    return;
  }

  for (var i = targets.length - 1; i >= 0; i -= 1) {
    await deleteMessage(targets[i].message, targets[i].index);
  }
}

function agentActionCountLabel(count) {
  if (!count) return "Ход выполнения";
  return "Инструменты · " + count;
}

function agentToolCallCount(timeline) {
  var count = 0;
  function append(activity) {
    if (!activity) return;
    var kind = activityKind(activity);
    var children = activityChildren(activity);
    if (kind === "tool_batch") {
      children.forEach(append);
      return;
    }
    if (kind === "tool" || kind === "verification" || kind === "control") {
      count += 1;
      return;
    }
    children.forEach(append);
  }
  (timeline || []).forEach(function (item) { append(item.activity); });
  return count;
}

function buildAgentRunTranscript(items, timeline, stats, includePlan, includePlanDecision) {
  var transcript = document.createElement("div");
  transcript.className = "agent-run-transcript";
  var planItem = findAgentPlanItem(items);
  if (planItem) {
    if (typeof appendMessageReasoning === "function") appendMessageReasoning(transcript, planItem.message);
    if (includePlan) {
      appendAgentRunPlan(transcript, planItem.activity);
    }
  }
  if (timeline.length) {
    appendAgentRunProcess(transcript, timeline, stats);
  }
  appendAgentRunArtifacts(transcript, timeline);
  return transcript;
}

function appendCollapsedAgentRun(parent, transcript, timeline, stats) {
  var details = document.createElement("details");
  details.className = "agent-run-history status-" + stats.status;

  var summary = document.createElement("summary");
  summary.className = "agent-run-history-summary";
  var icon = document.createElement("span");
  icon.className = "agent-run-history-icon";
  icon.setAttribute("aria-hidden", "true");
  summary.appendChild(icon);
  var title = document.createElement("span");
  title.className = "agent-run-history-title";
  title.textContent = agentActionCountLabel(agentToolCallCount(timeline));
  summary.appendChild(title);
  var meta = document.createElement("span");
  meta.className = "agent-run-history-meta";
  meta.textContent = [stats.elapsed, agentStatusLabel(stats.status)].filter(Boolean).join(" · ");
  summary.appendChild(meta);
  var caret = document.createElement("span");
  caret.className = "agent-run-history-caret";
  caret.setAttribute("aria-hidden", "true");
  caret.textContent = "›";
  summary.appendChild(caret);
  details.appendChild(summary);

  var content = document.createElement("div");
  content.className = "agent-run-history-content";
  content.appendChild(transcript);
  details.appendChild(content);
  parent.appendChild(details);
}

function renderAgentRunArticle(run) {
  var items = run.items || [];
  var finalMessage = run.finalMessage || null;
  var timeline = collectVisibleAgentTimelineItems(items, finalMessage);
  var stats = agentRunDisplayStats(agentRunStats(items), finalMessage, timeline);
  var node = document.createElement("article");
  node.className = "message assistant agent-run status-" + stats.status + (run.live ? " live" : "");

  var body = document.createElement("div");
  body.className = "agent-run-wrap";
  var dockCurrentPlan = !finalMessage && (!!currentActiveSend() || !!pendingAgentApprovalActivity());
  var planItem = findAgentPlanItem(items);
  var persistentPlan = !!finalMessage && !run.live && !!planItem;
  var transcript = buildAgentRunTranscript(items, timeline, stats, !dockCurrentPlan && !persistentPlan, !persistentPlan);
  if (finalMessage && !run.live) {
    appendAgentFinalAnswer(body, finalMessage, persistentPlan);
    if (persistentPlan) {
      appendAgentRunPlan(body, planItem.activity);
    }
    appendCollapsedAgentRun(body, transcript, timeline, stats);
  } else {
    body.appendChild(transcript);
    appendAgentFinalAnswer(body, finalMessage, persistentPlan);
  }
  if (!run.live) {
    items.forEach(function (item) { appendMessageArtifactCards(body, item.message); });
    if (finalMessage) appendMessageArtifactCards(body, finalMessage.message);
  }
  node.appendChild(body);

  if (!run.live) {
    appendAgentRunFooter(node, items, finalMessage);
  }
  enhanceActivity(body);
  return node;
}

function appendAgentRunFooter(node, items, finalMessage) {
  var footer = document.createElement("div");
  footer.className = "message-footer";
  var footerMeta = document.createElement("div");
  footerMeta.className = "message-footer-meta";
  var count = document.createElement("span");
  count.className = "message-usage";
  count.textContent = (items.length + (finalMessage ? 1 : 0)) + " сообщений";
  footerMeta.appendChild(count);

  var actions = document.createElement("div");
  actions.className = "message-actions";
  var last = finalMessage || items[items.length - 1];
  var historyActionsBlocked = !!currentActiveSend() || hasActiveMessageEdit();
  if (!historyActionsBlocked) {
    actions.appendChild(smallIconButton("Ответвить чат отсюда", "branch", function () {
      forkChatAtMessage(last.message, last.index);
    }));
  }
  actions.appendChild(smallIconButton(finalMessage ? "Копировать итоговый ответ" : "Копировать run", "copy", function () {
    copyText(finalMessage ? messageContent(finalMessage.message) : agentRunText(items));
    log(finalMessage ? "Итоговый ответ скопирован." : "Agent run скопирован.");
  }));
  if (!historyActionsBlocked) {
    actions.appendChild(smallIconButton("Удалить run", "trash", function () {
      deleteAgentRun(items, finalMessage);
    }));
  }

  footer.appendChild(footerMeta);
  footer.appendChild(actions);
  node.appendChild(footer);
}
