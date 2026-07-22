function renderActivityNode(activity, nested, current, context) {
  var node = document.createElement("div");
  var status = activityStatus(activity);
  node.className = "agent-activity" + (nested ? " nested" : "") + (current ? " current" : "") + " status-" + status;

  var expandable = activityHasDetails(activity);
  if (expandable) {
    var details = document.createElement("details");
    details.className = "agent-activity-toggle";
    details.open = current || status === "running" || status === "failed" || status === "waiting";
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

  var name = document.createElement("span");
  name.className = "agent-activity-name";
  name.textContent = title;
  row.appendChild(name);

  var commentNode = document.createElement("span");
  commentNode.className = "agent-activity-comment" + (comment ? "" : " is-empty");
  commentNode.textContent = comment;
  row.appendChild(commentNode);

  var metaParts = [agentStatusLabel(status)];
  var time = activityTimeText(context);
  if (time) {
    metaParts.unshift(time);
  }
  var meta = document.createElement("div");
  meta.className = "agent-activity-meta";
  meta.textContent = metaParts.join(" · ");
  row.appendChild(meta);

  var caret = document.createElement("span");
  caret.className = "agent-activity-caret" + (expandable ? "" : " is-hidden");
  caret.setAttribute("aria-hidden", "true");
  caret.textContent = "›";
  row.appendChild(caret);
  return row;
}

function activityPrimaryText(activity) {
  var progressTitle = typeof activityProgressTitle === "function" ? activityProgressTitle(activity) : "";
  if (progressTitle) {
    return progressTitle;
  }

  var title = activityTitle(activity);
  var toolId = activityToolId(activity);
  if (title && title !== toolId && title !== "Tool step" && title !== "Agent step") {
    return title;
  }

  var labels = {
    reasoning: "Анализирую задачу",
    verification: "Проверяю результат",
    retry: "Повторяю шаг",
    tool: "Выполняю действие",
    plan: "Формирую план"
  };
  return labels[activityKind(activity)] || toolId || title || "Выполняю шаг";
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

function appendActivityConfirmationPanel(node, activity) {
  var pendingId = activityPendingId(activity);
  if (!pendingId || activityStatus(activity) !== "waiting" || currentActiveSend()) {
    return;
  }

  var panel = document.createElement("div");
  panel.className = "agent-confirm-panel";

  var reason = document.createElement("div");
  reason.className = "agent-confirm-reason";
  reason.textContent = activityResultMessage(activity) || "Инструмент ждет подтверждения.";
  panel.appendChild(reason);

  var actions = document.createElement("div");
  actions.className = "agent-inline-actions";
  actions.appendChild(createAgentTextButton("Подтвердить", "primary", function () {
    confirmAgentTool(pendingId);
  }));
  actions.appendChild(createAgentTextButton("Отменить", "secondary", function () {
    cancelAgentTool(pendingId);
  }));
  panel.appendChild(actions);
  node.appendChild(panel);
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

  appendActivityConfirmationPanel(body, activity);
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
    planned: "В плане"
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

function appendAgentRunProcess(parent, timeline, stats, finalMessage) {
  var process = document.createElement("details");
  process.className = "agent-run-process status-" + stats.status;
  process.open = stats.status === "waiting" || stats.status === "failed";

  var summary = document.createElement("summary");
  summary.className = "agent-run-process-summary";
  var label = document.createElement("span");
  label.className = "agent-run-process-label";
  label.textContent = stats.status === "running" && stats.current
    ? activityPrimaryText(stats.current)
    : "Ход работы · " + ((timeline && timeline.length) || 0);
  summary.appendChild(label);

  var meta = document.createElement("span");
  meta.className = "agent-run-process-meta";
  meta.textContent = [agentStatusLabel(stats.status), stats.elapsed].filter(Boolean).join(" · ");
  summary.appendChild(meta);
  process.appendChild(summary);

  var steps = document.createElement("div");
  steps.className = "agent-run-steps";
  timeline.forEach(function (item) {
    var isCurrent = stats.current && activityContains(item.activity, stats.current);
    steps.appendChild(renderActivityNode(item.activity, false, isCurrent, {
      messageId: messageId(item.message),
      index: item.index,
      message: item.message,
      currentActivity: stats.current,
      renderInlineArtifacts: false
    }));
  });
  if (!timeline.length) {
    var empty = document.createElement("div");
    empty.className = "agent-run-empty";
    empty.textContent = "Шаги пока не получены.";
    steps.appendChild(empty);
  }
  process.appendChild(steps);
  parent.appendChild(process);
}

function collectAgentRunTimelineItems(items) {
  items = items || [];
  if (!items.length) {
    return [];
  }
  var first = items[0].activity;
  if (items.length > 1 && activityKind(first) === "plan") {
    return items.slice(1);
  }
  if (activityKind(first) === "plan") {
    return activityChildren(first).map(function (activity) {
      return {
        message: items[0].message,
        index: items[0].index,
        activity: activity
      };
    });
  }
  return items.slice();
}

function hasAgentFinalAnswer(finalMessage) {
  return !!(finalMessage && messageContent(finalMessage.message).trim());
}

function collectVisibleAgentTimelineItems(items, finalMessage) {
  var timeline = collectAgentRunTimelineItems(items);
  return timeline.filter(function (item, index) {
    return !isRecoveredFailureItem(timeline, index, finalMessage);
  });
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
  return hasAgentFinalAnswer(finalMessage);
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
        (counts.cancelled ? "cancelled" :
          (counts.planned && counts.planned === counts.total ? "planned" : "completed"))));
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

function appendAgentFinalAnswer(parent, finalMessage) {
  if (!finalMessage || !messageContent(finalMessage.message).trim()) {
    return;
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

function renderAgentRunArticle(run) {
  var items = run.items || [];
  var finalMessage = run.finalMessage || null;
  var timeline = collectVisibleAgentTimelineItems(items, finalMessage);
  var stats = agentRunDisplayStats(agentRunStats(items), finalMessage, timeline);
  var node = document.createElement("article");
  node.className = "message assistant agent-run status-" + stats.status + (run.live ? " live" : "");

  var body = document.createElement("div");
  body.className = "agent-run-wrap";

  appendAgentFinalAnswer(body, finalMessage);
  appendAgentRunArtifacts(body, timeline);
  appendAgentRunProcess(body, timeline, stats, finalMessage);
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
