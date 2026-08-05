function renderActivityNode(activity, nested, current, context) {
  var node = document.createElement("div");
  var status = activityStatus(activity);
  var kind = activityKind(activity) || "activity";
  node.className = "agent-activity kind-" + kind + (nested ? " nested" : "") + (current ? " current" : "") + " status-" + status;

  var expandable = activityHasDetails(activity);
  if (expandable) {
    var details = document.createElement("details");
    details.className = "agent-activity-toggle";
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

function agentDecisionText(item) {
  if (!item) return "";
  if (item.decisionText) return String(item.decisionText).trim();
  var activity = item.activity || {};
  var explicit = activity.__decisionMessage || activityValue(activity, "DecisionMessage", "decisionMessage", "");
  if (explicit) return String(explicit).trim();
  if ((activityKind(activity) === "plan" || activityStatus(activity) === "planned") && item.message) {
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
    appendAgentDecisionMessage(entry, agentDecisionText(item));
    var isCurrent = stats.current && activityContains(item.activity, stats.current);
    entry.appendChild(renderActivityNode(item.activity, false, isCurrent, {
      messageId: messageId(item.message),
      index: item.index,
      message: item.message,
      currentActivity: stats.current,
      renderInlineArtifacts: false
    }));
    process.appendChild(entry);
  });
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
    return [];
  }
  return items.slice();
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
      decisionText: agentDecisionText(item)
    };
    var key = activityTimelineKey(item.activity);
    var existingIndex = latestByKey[key];
    if (existingIndex !== undefined) {
      var existingStatus = activityStatus(result[existingIndex].activity);
      var nextStatus = activityStatus(item.activity);
      if (existingStatus === "planned" || existingStatus === "running" || existingStatus === "waiting" ||
          (existingStatus === "failed" && nextStatus === "completed")) {
        nextItem.decisionText = result[existingIndex].decisionText || nextItem.decisionText;
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
  if (!current) {
    current = steps.filter(function (step) {
      return normalizePlanStepStatus(activityStatus(step)) === "pending";
    })[0] || steps.filter(function (step) {
      var status = normalizePlanStepStatus(activityStatus(step));
      return status === "failed" || status === "cancelled";
    })[0] || (steps.length ? steps[steps.length - 1] : null);
  }
  return { steps: steps, completed: completed, total: steps.length, current: current };
}

function planStatusMark(status) {
  var marks = { completed: "✓", running: "•", waiting: "!", failed: "×", cancelled: "–", pending: "" };
  return marks[normalizePlanStepStatus(status)] || "";
}

function renderAgentPlanSteps(plan) {
  var list = document.createElement("ol");
  list.className = "agent-plan-list";
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
  var mark = document.createElement("span");
  mark.className = "agent-plan-card-mark";
  mark.setAttribute("aria-hidden", "true");
  mark.textContent = "☷";
  summary.appendChild(mark);
  var heading = document.createElement("strong");
  heading.textContent = "План";
  summary.appendChild(heading);
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
  details.appendChild(renderAgentPlanSteps(plan));
  dock.replaceChildren(details);
  dock.classList.remove("hidden");
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

function agentActionCountLabel(count) {
  if (!count) return "Ход выполнения";
  var mod10 = count % 10;
  var mod100 = count % 100;
  var word = mod10 === 1 && mod100 !== 11
    ? "действие"
    : (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14) ? "действия" : "действий");
  return "Выполнено " + count + " " + word;
}

function buildAgentRunTranscript(items, timeline, stats, includePlan) {
  var transcript = document.createElement("div");
  transcript.className = "agent-run-transcript";
  var planItem = findAgentPlanItem(items);
  if (planItem) {
    appendAgentDecisionMessage(transcript, agentDecisionText(planItem));
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
  title.textContent = agentActionCountLabel(timeline.length);
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
  var transcript = buildAgentRunTranscript(items, timeline, stats, !dockCurrentPlan);
  if (finalMessage && !run.live) {
    appendCollapsedAgentRun(body, transcript, timeline, stats);
  } else {
    body.appendChild(transcript);
  }
  appendAgentFinalAnswer(body, finalMessage);
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
