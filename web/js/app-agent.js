function renderActivityNode(activity, nested, current, context) {
  var node = document.createElement("div");
  var status = activityStatus(activity);
  node.className = "agent-activity" + (nested ? " nested" : "") + (current ? " current" : "") + " status-" + status;

  var expandable = activityHasDetails(activity);
  if (expandable) {
    var details = document.createElement("details");
    details.className = "agent-activity-toggle";
    details.open = current || status === "failed" || status === "waiting";
    details.appendChild(renderActivityRow(activity, current, true, context));
    appendActivityDetailsContent(details, activity, context);
    node.appendChild(details);
  } else {
    node.appendChild(renderActivityRow(activity, current, false, context));
  }

  appendActivityArtifacts(node, activity, context);
  return node;
}

function renderActivityRow(activity, current, expandable, context) {
  var row = document.createElement(expandable ? "summary" : "div");
  var status = activityStatus(activity);
  var title = activityPrimaryText(activity);
  var comment = activityCommentText(activity);
  row.className = "agent-activity-row";
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
  commentNode.className = "agent-activity-comment";
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
  return activityToolId(activity) || activityTitle(activity);
}

function activityCommentText(activity) {
  var toolId = activityToolId(activity);
  var title = activityTitle(activity);
  var result = activityResultMessage(activity);
  var subtitle = activityValue(activity, "Subtitle", "subtitle", "");
  if (result) {
    return result;
  }
  if (toolId && title && title !== toolId) {
    return title;
  }
  return subtitle || "";
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
  if (!pendingId || activityStatus(activity) !== "waiting") {
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
  var argumentsJson = activityArgumentsJson(activity);
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
    "Reason: " + result,
    "Arguments:",
    prettyJsonText(argumentsJson)
  ].join("\n")));
  panel.appendChild(actions);
  node.appendChild(panel);
}

function appendActivityDetailsContent(node, activity, context) {
  var children = activityChildren(activity);
  var argumentsJson = activityArgumentsJson(activity);
  var dataJson = activityDataJson(activity);

  var body = document.createElement("div");
  body.className = "agent-activity-detail-body";

  appendActivityConfirmationPanel(body, activity);
  appendActivityErrorPanel(body, activity);

  if (children.length) {
    var childList = document.createElement("div");
    childList.className = "agent-activity-children";
    children.forEach(function (child) {
      childList.appendChild(renderActivityNode(child, true, context && activityContains(child, context.currentActivity), context));
    });
    body.appendChild(childList);
  }

  appendArgumentsData(body, argumentsJson);
  appendActivityData(body, "Результат", dataJson, "Копировать результат");
  node.appendChild(body);
}

function appendActivityArtifacts(node, activity, context) {
  if (typeof tryRenderChartArtifact === "function") {
    var chart = tryRenderChartArtifact(activity, context || {});
    if (chart) {
      node.appendChild(chart);
    }
  }
  if (typeof tryRenderHtmlArtifact === "function") {
    var html = tryRenderHtmlArtifact(activity, context || {});
    if (html) {
      node.appendChild(html);
    }
  }
}

function agentStatusLabel(status) {
  var labels = {
    completed: "Готово",
    running: "Выполняется",
    waiting: "Ждет подтверждения",
    failed: "Ошибка",
    cancelled: "Отменено",
    planned: "Запланировано"
  };
  return labels[status] || status || "Статус";
}

function renderAgentRunStatus(stats) {
  var status = document.createElement("span");
  status.className = "agent-run-status agent-run-status-" + stats.status;
  status.textContent = agentStatusLabel(stats.status);
  return status;
}

function appendAgentRunDetails(parent, items, stats) {
  var steps = document.createElement("div");
  steps.className = "agent-run-steps";
  var timeline = collectAgentRunTimelineItems(items);
  timeline.forEach(function (item) {
    var isCurrent = stats.current && activityContains(item.activity, stats.current);
    steps.appendChild(renderActivityNode(item.activity, false, isCurrent, {
      messageId: messageId(item.message),
      index: item.index,
      message: item.message,
      currentActivity: stats.current
    }));
  });
  if (!timeline.length) {
    var empty = document.createElement("div");
    empty.className = "agent-run-empty";
    empty.textContent = "Шаги пока не получены.";
    steps.appendChild(empty);
  }
  parent.appendChild(steps);
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
  var stats = agentRunStats(items);
  var node = document.createElement("article");
  node.className = "message assistant agent-run status-" + stats.status + (run.live ? " live" : "");

  var body = document.createElement("div");
  body.className = "agent-run-wrap";

  var header = document.createElement("div");
  header.className = "agent-run-header";
  var summary = document.createElement("div");
  summary.className = "agent-run-summary";
  summary.appendChild(renderAgentRunStatus(stats));
  if (stats.current && stats.status !== "completed") {
    var current = document.createElement("div");
    current.className = "agent-run-current";
    current.textContent = activityPrimaryText(stats.current);
    summary.appendChild(current);
  }
  var meta = document.createElement("div");
  meta.className = "agent-run-meta";
  meta.textContent = (stats.counts.total || 0) + " шаг(ов)" + (stats.elapsed ? " · " + stats.elapsed : "");
  header.appendChild(summary);
  header.appendChild(meta);
  body.appendChild(header);

  appendAgentFinalAnswer(body, finalMessage);
  appendAgentRunDetails(body, items, stats);
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
  actions.appendChild(smallIconButton("Ответвить чат отсюда", "branch", function () {
    forkChatAtMessage(last.message, last.index);
  }));
  actions.appendChild(smallIconButton(finalMessage ? "Копировать итоговый ответ" : "Копировать run", "copy", function () {
    copyText(finalMessage ? messageContent(finalMessage.message) : agentRunText(items));
    log(finalMessage ? "Итоговый ответ скопирован." : "Agent run скопирован.");
  }));
  actions.appendChild(smallIconButton("Удалить run", "trash", function () {
    deleteAgentRun(items, finalMessage);
  }));

  footer.appendChild(footerMeta);
  footer.appendChild(actions);
  node.appendChild(footer);
}
