var agentApproval = window.RNAssistantAgentApproval.create({
  state: state,
  currentActiveSend: function () { return typeof currentActiveSend === "function" ? currentActiveSend() : null; },
  primaryText: function (activity) { return activityPrimaryText(activity); },
  cancel: function (pendingId) { return cancelAgentTool(pendingId); },
  confirm: function (pendingId) { return confirmAgentTool(pendingId); }
});

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

function pendingAgentApprovalActivity() {
  return agentApproval.pendingActivity();
}

function renderAgentApprovalDock() {
  agentApproval.renderDock();
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

function completedAgentActionCountLabel(count) {
  var lastTwo = count % 100;
  var last = count % 10;
  var noun = lastTwo >= 11 && lastTwo <= 14
    ? "действий"
    : (last === 1 ? "действие" : (last >= 2 && last <= 4 ? "действия" : "действий"));
  return "Выполнено " + count + " " + noun;
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

function groupAgentRunSteps(timeline) {
  var steps = [];
  var current = null;
  var prelude = [];

  function startStep(stepId, message) {
    current = {
      id: stepId || ("legacy-" + steps.length),
      message: message || "",
      items: [],
      ambient: null
    };
    steps.push(current);
    return current;
  }

  (timeline || []).forEach(function (item) {
    var activity = item.activity;
    var kind = activityKind(activity);
    var stepId = activityStepId(activity);
    var stepMessage = activityStepMessage(activity);

    if (kind === "notice" && !stepId) {
      if (current) current.ambient = item;
      else prelude = [item];
      return;
    }

    if (kind === "step") {
      if (!current || !stepId || current.id !== stepId) {
        startStep(stepId, stepMessage || activityTitle(activity));
      } else if (!current.message) {
        current.message = stepMessage || activityTitle(activity);
      }
      current.marker = item;
      current.ambient = null;
      return;
    }

    if (!current || (stepId && current.id !== stepId)) {
      startStep(stepId, stepMessage);
    } else if (!current.message && stepMessage) {
      current.message = stepMessage;
    }
    current.items.push(item);
    current.ambient = null;
  });

  if (!steps.length && prelude.length) {
    startStep("prelude", "").items = prelude;
  }
  return steps;
}

function appendAgentStepMessage(parent, text) {
  text = String(text || "").trim();
  if (!text) return;
  var message = document.createElement("div");
  message.className = "agent-step-message markdown";
  message.innerHTML = markdown(text);
  parent.appendChild(message);
  enhanceMarkdown(message);
}

function appendCollapsedAgentStep(parent, step, isCurrent, finished) {
  var timeline = step.items || [];
  var stats = agentRunStats(timeline, !!finished);
  var ambient = isCurrent && step.ambient ? step.ambient.activity : null;
  var active = ambient || (isCurrent && stats.current && isActiveTimelineStatus(activityStatus(stats.current))
    ? stats.current
    : null);
  var effectiveStatus = active ? activityStatus(active) : stats.status;
  var actionCount = agentToolCallCount(timeline);
  var lastAction = null;
  for (var actionIndex = timeline.length - 1; actionIndex >= 0; actionIndex -= 1) {
    var actionKind = activityKind(timeline[actionIndex].activity);
    if (actionKind === "tool" || actionKind === "control" || actionKind === "diagnostic") {
      lastAction = timeline[actionIndex].activity;
      break;
    }
  }
  var completedBatch = !active && effectiveStatus === "completed" && actionCount > 1;
  var transcript = buildAgentRunTranscript(step.items, timeline, stats);
  var details = document.createElement("details");
  details.className = "agent-run-history agent-step-actions status-" + effectiveStatus;

  var summary = document.createElement("summary");
  summary.className = "agent-run-history-summary";
  var icon = document.createElement("span");
  icon.className = "agent-run-history-icon";
  icon.setAttribute("aria-hidden", "true");
  summary.appendChild(icon);
  var title = document.createElement("span");
  title.className = "agent-run-history-title";
  title.textContent = active
    ? activityPrimaryText(active)
    : (completedBatch
      ? completedAgentActionCountLabel(actionCount)
      : (lastAction ? activityPrimaryText(lastAction) : (step.message || "Выполняю…")));
  summary.appendChild(title);
  var meta = document.createElement("span");
  meta.className = "agent-run-history-meta";
  var exceptionalStatus = effectiveStatus === "waiting" || effectiveStatus === "failed" || effectiveStatus === "cancelled"
    ? agentStatusLabel(effectiveStatus)
    : "";
  meta.textContent = [stats.elapsed, exceptionalStatus].filter(Boolean).join(" · ");
  summary.appendChild(meta);
  var caret = document.createElement("span");
  caret.className = "agent-run-history-caret";
  caret.setAttribute("aria-hidden", "true");
  caret.textContent = "›";
  summary.appendChild(caret);
  details.appendChild(summary);

  var content = document.createElement("div");
  content.className = "agent-run-history-content";
  if (timeline.length) {
    content.appendChild(transcript);
  } else {
    var empty = document.createElement("div");
    empty.className = "agent-run-empty";
    empty.textContent = "Детали действия появятся после выполнения.";
    content.appendChild(empty);
  }
  details.appendChild(content);
  parent.appendChild(details);
}

function renderAgentRunArticle(run) {
  var items = run.items || [];
  var finalMessage = run.finalMessage || null;
  var timeline = collectVisibleAgentTimelineItems(items);
  var stats = agentRunStats(items, !!finalMessage && !run.live);
  var steps = groupAgentRunSteps(timeline);
  var node = document.createElement("article");
  node.className = "message assistant agent-run status-" + stats.status + (run.live ? " live" : "");

  var body = document.createElement("div");
  body.className = "agent-run-wrap";
  steps.forEach(function (step, stepIndex) {
    var section = document.createElement("section");
    section.className = "agent-model-step";
    appendAgentStepMessage(section, step.message);
    appendCollapsedAgentStep(section, step, stepIndex === steps.length - 1 && !!run.live,
      !run.live || stepIndex < steps.length - 1);
    body.appendChild(section);
  });
  if (finalMessage) {
    var finalSection = document.createElement("section");
    finalSection.className = "agent-final-step";
    appendAgentFinalAnswer(finalSection, finalMessage);
    body.appendChild(finalSection);
  }
  if (!run.live) {
    var renderedArtifactIds = {};
    items.forEach(function (item) { appendMessageArtifactCards(body, item.message, renderedArtifactIds); });
    if (finalMessage) appendMessageArtifactCards(body, finalMessage.message, renderedArtifactIds);
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
  var historyActionsBlocked = !!currentActiveSend() || hasActiveMessageEdit() ||
    (typeof pendingAgentApprovalActivity === "function" && !!pendingAgentApprovalActivity());
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
