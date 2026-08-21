function renderActivityNode(activity, nested, current, context) {
  var node = document.createElement("div");
  var status = activityStatus(activity);
  var kind = activityKind(activity) || "activity";
  node.className = "agent-activity kind-" + kind + (nested ? " nested" : "") + (current ? " current" : "") + " status-" + status;

  var expandable = activityHasDetails(activity);
  if (expandable) {
    var details = document.createElement("details");
    details.className = "agent-activity-toggle";
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
    var statusLabels = {
      completed: "Действие выполнено",
      running: "Выполняю действие",
      waiting: "Подтвердите действие",
      failed: "Действие завершилось ошибкой",
      cancelled: "Действие отменено"
    };
    return statusLabels[activityStatus(activity)] || "Выполняю действие";
  }

  var labels = {
    reasoning: "Анализирую задачу",
    tool: "Выполняю действие",
    control: "Выполняю действие",
    diagnostic: "Ошибка ответа агента"
  };
  return labels[activityKind(activity)] || toolId || title || "Выполняю шаг";
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
    if (itemKind === "diagnostic") {
      appendAgentDiagnosticMessage(entry, agentDiagnosticText(item));
    }
    var isCurrent = stats.current && activityContains(item.activity, stats.current);
    var activityContext = {
      messageId: messageId(item.message),
      index: item.index,
      message: item.message,
      currentActivity: stats.current,
      renderInlineArtifacts: false
    };
    entry.appendChild(renderActivityNode(item.activity, false, isCurrent, activityContext));
    process.appendChild(entry);
  });
  parent.appendChild(process);
}

function collectAgentRunTimelineItems(items) {
  items = items || [];
  return items.filter(function (item) { return !!item; });
}

function collectVisibleAgentTimelineItems(items) {
  return collapseAgentTimelineItems(collectAgentRunTimelineItems(items));
}

function collapseAgentTimelineItems(timeline) {
  var result = [];
  var latestByKey = {};
  (timeline || []).forEach(function (item) {
    var nextItem = {
      message: item.message,
      index: item.index,
      activity: item.activity,
      reasoningMessage: typeof messageHasReasoning === "function" && messageHasReasoning(item.message) ? item.message : null
    };
    var key = activityTimelineKey(item.activity);
    var existingIndex = latestByKey[key];
    if (existingIndex !== undefined) {
      var existingStatus = activityStatus(result[existingIndex].activity);
      var nextStatus = activityStatus(item.activity);
      if (existingStatus === "running" || existingStatus === "waiting" ||
          (existingStatus === "failed" && nextStatus === "completed")) {
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
  meta.textContent = "Нужно подтверждение";
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

function appendAgentFinalAnswer(parent, finalMessage) {
  if (!finalMessage || !messageContent(finalMessage.message).trim()) {
    return;
  }

  if (typeof appendMessageReasoning === "function") appendMessageReasoning(parent, finalMessage.message);

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
  return "Действия · " + count;
}

function agentToolCallCount(timeline) {
  var count = 0;
  function append(activity) {
    if (!activity) return;
    var kind = activityKind(activity);
    var children = activityChildren(activity);
    if (kind === "tool" || kind === "control") {
      count += 1;
      return;
    }
    children.forEach(append);
  }
  (timeline || []).forEach(function (item) { append(item.activity); });
  return count;
}

function buildAgentRunTranscript(items, timeline, stats) {
  var transcript = document.createElement("div");
  transcript.className = "agent-run-transcript";
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
  var timeline = collectVisibleAgentTimelineItems(items);
  var stats = agentRunStats(items, !!finalMessage && !run.live);
  var node = document.createElement("article");
  node.className = "message assistant agent-run status-" + stats.status + (run.live ? " live" : "");

  var body = document.createElement("div");
  body.className = "agent-run-wrap";
  var transcript = buildAgentRunTranscript(items, timeline, stats);
  if (finalMessage && !run.live) {
    appendAgentFinalAnswer(body, finalMessage);
    appendCollapsedAgentRun(body, transcript, timeline, stats);
  } else {
    body.appendChild(transcript);
    appendAgentFinalAnswer(body, finalMessage);
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
