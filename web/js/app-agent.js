var agentApproval = window.RNAssistantAgentApproval.create({
  state: state,
  currentActiveSend: function () { return typeof currentActiveSend === "function" ? currentActiveSend() : null; },
  primaryText: function (pending) { return pending.toolName || "Действие"; },
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
    if (item.includeReasoning !== false && typeof appendMessageReasoning === "function") appendMessageReasoning(entry, item.reasoningMessage || item.message);
    var isCurrent = stats.current && activityContains(item.activity, stats.current);
    entry.agentReconcileKey = activityTimelineKey(item.activity) + ":" + (messageId(item.message) || item.index);
    entry.agentReconcileSignature = JSON.stringify([item.activity, item.reasoningMessage || item.message,
      !!isCurrent, !!stats.liveFeed, !!stats.terminal, !!stats.workspace]);
    var activityContext = {
      messageId: messageId(item.message),
      index: item.index,
      message: item.message,
      currentActivity: stats.current,
      renderInlineArtifacts: false,
      liveFeed: !!stats.liveFeed,
      terminal: !!stats.terminal,
      workspace: !!stats.workspace
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
    // An accepted no-call reflection has no tool origin/StepId in workspace
    // history. Keep each saved message; a run-level fallback key merges them.
    if (activityKind(item.activity) === "step" && !activityStepId(item.activity)) {
      result.push(nextItem);
      return;
    }
    var key = activityTimelineKey(item.activity);
    var existingIndex = latestByKey[key];
    if (existingIndex !== undefined) {
      if (isAmbientAgentActivity(item.activity) && isAmbientAgentActivity(result[existingIndex].activity)) {
        result.splice(existingIndex, 1);
        Object.keys(latestByKey).forEach(function (knownKey) {
          if (latestByKey[knownKey] > existingIndex) latestByKey[knownKey] -= 1;
        });
        latestByKey[key] = result.length;
        result.push(nextItem);
        return;
      }
      var existingStatus = activityStatus(result[existingIndex].activity);
      var nextStatus = activityStatus(item.activity);
      if (activityToolCallId(item.activity) && !isActiveTimelineStatus(existingStatus) && isActiveTimelineStatus(nextStatus)) return;
      if ((activityToolCallId(item.activity) && existingStatus !== "failed") || activityKind(item.activity) === "step" || existingStatus === "running" || existingStatus === "waiting") {
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
  var content = messageContent(finalMessage.message);
  answer.innerHTML = markdown(content);
  parent.appendChild(answer);
  enhanceMarkdown(answer, { enableVisuals: true, enableJsonViewer: true, sourceText: content });
}

function enhanceActivity(root) {
  Array.prototype.slice.call(root.querySelectorAll("pre code")).forEach(function (code) {
    highlightCode(code);
  });
}

async function deleteAgentRun(items, finalMessage, presentation) {
  var targets = (items || []).slice();
  if (finalMessage) {
    targets.push(finalMessage);
  }
  if (!targets.length || !presentation || !presentation.isCurrent() || !window.confirm("Удалить сообщения этого запуска?")) {
    return;
  }

  for (var i = targets.length - 1; i >= 0; i -= 1) {
    // Navigation during an awaited deletion must never redirect the remaining commands.
    if (!presentation.isCurrent()) {
      log("Удаление остальных сообщений остановлено: выбран другой чат.", "warning");
      break;
    }
    await presentation.invoke("messageAction", { name: "delete", message: targets[i].message, index: targets[i].index });
  }
}

function agentCountText(count, one, few, many) {
  var lastTwo = count % 100;
  var last = count % 10;
  var noun = lastTwo >= 11 && lastTwo <= 14
    ? many
    : (last === 1 ? one : (last >= 2 && last <= 4 ? few : many));
  return count + " " + noun;
}

function agentActionCountText(count) {
  return agentCountText(count, "действие", "действия", "действий");
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

function groupAgentRunSteps(timeline) {
  var steps = [];
  var current = null;
  var seenSteps = Object.create(null);

  function startStep(stepId, message, reason) {
    current = {
      id: stepId || ("unscoped-" + steps.length),
      message: message || "",
      reason: reason || "",
      items: [],
      ambient: null
    };
    steps.push(current);
    if (stepId) seenSteps[stepId] = current;
    return current;
  }

  (timeline || []).forEach(function (item) {
    var activity = item.activity;
    var kind = activityKind(activity);
    var stepId = activityStepId(activity);
    var stepMessage = activityStepMessage(activity);
    var stepReason = activityStepReason(activity);

    if (kind === "notice" && !stepId && isActiveTimelineStatus(activityStatus(activity))) {
      if (!current) startStep("ambient", "");
      current.ambient = item;
      return;
    }
    if (kind === "notice" && !stepId) {
      startStep("notice-" + steps.length, "").items.push(item);
      current = null;
      return;
    }

    if (kind === "step") {
      // Live markers can arrive after the same step was admitted from saved calls.
      if (stepId && seenSteps[stepId]) {
        var existingStep = seenSteps[stepId];
        existingStep.marker = item;
        if (!existingStep.message) existingStep.message = stepMessage;
        if (!existingStep.reason) existingStep.reason = stepReason;
        return;
      }
      if (!current || !stepId || current.id !== stepId) {
        startStep(stepId, stepMessage, stepReason);
      } else if (!current.message) {
        current.message = stepMessage;
      }
      if (!current.reason) current.reason = stepReason;
      current.marker = item;
      current.ambient = null;
      return;
    }

    if (!current || (stepId && current.id !== stepId)) {
      startStep(stepId, stepMessage, stepReason);
    } else if (!current.message && stepMessage) {
      current.message = stepMessage;
    }
    if (!current.reason) current.reason = stepReason;
    current.items.push(item);
    current.ambient = null;
  });

  return steps;
}

function agentReflectionText(decision, commentary) {
  decision = String(decision || "").trim();
  commentary = String(commentary || "").trim();
  return decision && commentary && decision !== commentary
    ? decision + "\n\n" + commentary : decision || commentary;
}

function appendAgentReflectionStep(parent, decision, commentary) {
  var text = agentReflectionText(decision, commentary);
  if (!text) return;
  var step = document.createElement("div");
  step.className = "agent-reflection-step";
  step.agentReconcileSignature = text;
  step.setAttribute("aria-label", "Промежуточное решение агента");
  var icon = document.createElement("span");
  icon.className = "agent-reflection-icon";
  icon.setAttribute("aria-hidden", "true");
  if (window.RNAssistantIcons) icon.innerHTML = window.RNAssistantIcons.render("bulb");
  step.appendChild(icon);
  var body = document.createElement("div");
  body.className = "agent-reflection-body markdown";
  body.innerHTML = markdown(text);
  step.appendChild(body);
  parent.appendChild(step);
  enhanceMarkdown(body, { enableVisuals: true, enableJsonViewer: true, sourceText: text });
}

function appendAgentStepMessage(parent, text, marker) {
  text = String(text || "").trim();
  if (marker && (activityTitle(marker.activity) === "Промежуточное решение" ||
      marker.message && marker.message.ReflectionDecision)) {
    appendAgentReflectionStep(parent, marker.message && marker.message.ReflectionDecision || text, text);
    return;
  }
  if (!text) return;
  var message = document.createElement("div");
  message.className = "agent-step-message markdown";
  message.agentReconcileSignature = text;
  message.innerHTML = markdown(text);
  parent.appendChild(message);
  enhanceMarkdown(message, { enableVisuals: true, enableJsonViewer: true, sourceText: text });
}

function appendAgentStepReason(parent, step) {
  var reason = String(step.reason || "").trim();
  if (!reason || reason === String(step.message || "").trim()) return;
  var line = document.createElement("div");
  line.className = "agent-step-reason";
  line.agentReconcileSignature = reason;
  line.textContent = reason;
  parent.appendChild(line);
}

// Presentation groups follow comments; model-step and call identities stay intact.
function groupAgentRunUpdates(steps) {
  var groups = [];
  (steps || []).forEach(function (step) {
    var silent = !String(step.message || "").trim() && !String(step.reason || "").trim();
    var previous = groups[groups.length - 1];
    if (previous && silent) {
      previous.items = previous.items.concat(step.items || []);
      previous.ambient = step.ambient;
      if (!previous.marker || activityTitle(previous.marker.activity) !== "Промежуточное решение")
        previous.marker = step.marker || previous.marker;
    } else {
      groups.push({ id: step.id, message: step.message, reason: step.reason, items: (step.items || []).slice(),
        ambient: step.ambient, marker: step.marker });
    }
  });
  return groups;
}

function appendAgentActionStage(parent, actions, stats) {
  if (!actions.length) return;
  if (actions.length === 1) {
    appendAgentRunProcess(parent, actions, stats);
    return;
  }
  var details = document.createElement("details");
  var active = !!stats.current && actions.some(function (item) { return activityContains(item.activity, stats.current) && activityPresentationState(item.activity) === "running"; });
  var issues = actions.filter(function (item) { return ["failed", "warning", "unknown", "partial", "cancelled"].indexOf(activityPresentationState(item.activity)) >= 0; });
  details.className = "agent-run-history agent-action-stage " + (active ? "agent-active-actions" : "agent-completed-actions");
  details.open = active || issues.length > 0;
  details.setAttribute("data-disclosure-key", "actions:" + activityTimelineKey(actions[0].activity));
  var summary = document.createElement("summary");
  summary.className = "agent-run-history-summary";
  var symbol = document.createElement("span");
  symbol.className = "agent-stage-symbol";
  symbol.setAttribute("aria-hidden", "true");
  symbol.innerHTML = window.RNAssistantIcons ? window.RNAssistantIcons.render("layers") : '<svg data-rna-icon="layers"></svg>';
  summary.appendChild(symbol);
  var title = document.createElement("span");
  title.className = "agent-run-history-title" + (active ? " agent-status-shimmer" : "");
  title.textContent = activityPrimaryText(active ? stats.current : actions[0].activity);
  summary.appendChild(title);
  var count = document.createElement("span");
  count.className = "agent-work-count";
  count.textContent = agentActionCountText(actions.length);
  summary.appendChild(count);
  if (issues.length) {
    var issue = document.createElement("span");
    issue.className = "agent-run-history-issues";
    issue.textContent = "Есть замечания";
    summary.appendChild(issue);
  }
  var caret = document.createElement("span");
  caret.className = "agent-run-history-caret";
  caret.setAttribute("aria-hidden", "true");
  summary.appendChild(caret);
  details.appendChild(summary);
  appendAgentRunProcess(details, actions, stats);
  parent.appendChild(details);
}

function appendLiveAgentStep(parent, step, isLast, suppressAmbient, suppressArtifacts, terminal, workspace, suppressReasoning) {
  var actions = [];
  function append(item, activity, nested) {
    if (activityKind(activity) !== "step" && activityKind(activity) !== "group") {
      actions.push({ message: item.message, index: item.index, activity: activity,
        reasoningMessage: item.reasoningMessage, includeReasoning: !nested && !suppressReasoning });
    }
    activityChildren(activity).forEach(function (child) { append(item, child, true); });
  }
  (step.items || []).forEach(function (item) { append(item, item.activity); });
  var stats = agentRunStats((step.items || []).filter(function (item) {
    return activityKind(item.activity) !== "notice";
  }), !isLast, null);
  stats.terminal = !!terminal;
  stats.workspace = !!workspace;
  var active = !terminal && isLast && stats.current && isActiveTimelineStatus(activityStatus(stats.current))
    ? stats.current : null;
  stats.current = active;
  stats.liveFeed = true;
  var adjacent = [];
  function flush() {
    appendAgentActionStage(parent, adjacent, stats);
    adjacent = [];
  }
  actions.forEach(function (item) {
    var kind = activityKind(item.activity);
    var presentation = activityPresentationState(item.activity);
    var groupable = (kind === "tool" || kind === "control") &&
      activityToolId(item.activity) !== "core.ask" && presentation !== "waiting";
    if (groupable) {
      adjacent.push(item);
    } else {
      flush();
      appendAgentRunProcess(parent, [item], stats);
    }
    var artifacts = document.createElement("div");
    if (!suppressArtifacts) appendActivityArtifacts(artifacts, item.activity, {
      message: item.message, messageId: messageId(item.message), index: item.index, workspace: !!workspace });
    if (artifacts.childNodes.length) {
      flush();
      artifacts.className = "agent-run-artifacts";
      parent.appendChild(artifacts);
    }
  });
  flush();
  var ambient = step.ambient || (step.items || []).filter(function (item) {
    return activityKind(item.activity) === "notice";
  }).slice(-1)[0];
  if (!terminal && isLast && !active && ambient && !suppressAmbient) {
    parent.appendChild(renderActivityRow(ambient.activity, true, false, {
      hideIcon: true, liveFeed: true, currentActivity: ambient.activity
    }));
  }
}

function appendAgentRunSummaryState(summary, status) {
  var labels = {
    running: "",
    waiting: "!",
    warning: "!",
    failed: "×",
    cancelled: "–"
  };
  if (!Object.prototype.hasOwnProperty.call(labels, status)) return;
  var mark = document.createElement("span");
  mark.className = "agent-run-history-state status-" + status;
  mark.setAttribute("aria-hidden", "true");
  mark.textContent = labels[status];
  summary.appendChild(mark);
}

function appendAgentRunOverview(parent, steps, timeline, stats) {
  var actionCount = agentToolCallCount(timeline);
  if (!actionCount && !(steps || []).some(function (step) { return (step.items || []).length; })) return null;
  var details = document.createElement("details");
  details.className = "agent-run-history agent-run-overview status-" + stats.status;
  details.setAttribute("data-disclosure-key", "overview");
  details.open = false;

  var summary = document.createElement("summary");
  summary.className = "agent-run-history-summary";
  var title = document.createElement("span");
  title.className = "agent-run-history-title";
  title.textContent = actionCount
    ? (stats.elapsed ? "Работа заняла " + stats.elapsed : "Ход работы")
    : "Ход работы";
  if (actionCount) {
    var count = document.createElement("span");
    count.className = "agent-work-count";
    count.textContent = agentActionCountText(actionCount);
    summary.appendChild(title);
    summary.appendChild(count);
  }
  if (!actionCount) summary.appendChild(title);
  if (timeline.some(function (item) { return ["failed", "warning", "unknown", "partial"].indexOf(activityPresentationState(item.activity)) >= 0; })) {
    var issues = document.createElement("span");
    issues.className = "agent-run-history-issues";
    issues.textContent = "Есть замечания";
    summary.appendChild(issues);
  }
  appendAgentRunSummaryState(summary,
    stats.status === "failed" && stats.runViewState && stats.runViewState.executionHealth === "unknown"
      ? "warning" : stats.status);
  var caret = document.createElement("span");
  caret.className = "agent-run-history-caret";
  caret.setAttribute("aria-hidden", "true");
  summary.appendChild(caret);
  summary.setAttribute("aria-label", title.textContent + (actionCount ? ". " + agentActionCountText(actionCount) : ""));
  summary.title = actionCount ? agentActionCountText(actionCount) : "Показать шаги запуска";
  details.appendChild(summary);

  var content = null;
  details.ensureDisclosureContent = function () {
    if (content) return;
    content = document.createElement("div");
    content.className = "agent-run-history-content agent-run-overview-content";
    (steps || []).forEach(function (step) {
      var historyItems = (step.items || []).filter(function (item) {
        return activityKind(item.activity) !== "step";
      });
      if (!historyItems.length) return;
      var section = document.createElement("section");
      section.className = "agent-model-step agent-model-step-history";
      appendLiveAgentStep(section, { items: historyItems }, false, true, true, true, stats.workspace);
      content.appendChild(section);
    });
    if (!content.childNodes.length) {
      var empty = document.createElement("div");
      empty.className = "agent-run-empty";
      empty.textContent = "Подробности выполнения не записаны.";
      content.appendChild(empty);
    }
    details.appendChild(content);
    if (details.savedDisclosureState && typeof restoreMessageDisclosures === "function") {
      restoreMessageDisclosures(content, details.savedDisclosureState);
    }
    enhanceActivity(content);
  };
  details.addEventListener("toggle", function () {
    if (details.open) details.ensureDisclosureContent();
    else if (content) {
      details.savedDisclosureState = typeof messageDisclosureSnapshot === "function" ? messageDisclosureSnapshot(content) : {};
      if (typeof clearMarkdownEnhancements === "function") clearMarkdownEnhancements(content);
      details.removeChild(content);
      content = null;
    }
  });
  parent.appendChild(details);
  if (details.open) details.ensureDisclosureContent();
  return details;
}

function appendAgentRunViewState(parent, runViewState) {
  // Effects and assessments remain in details. Only an actual interruption
  // needs a separate line, and a visible diagnostic already supplies that line.
  if (!runViewState || ["failed", "cancelled"].indexOf(runViewState.lifecycle) < 0) return;
  var diagnostic = parent.querySelector(".kind-diagnostic");
  if (diagnostic && diagnostic.getAttribute("data-failure-reason") === runViewState.reason) return;
  var note = document.createElement("div");
  note.className = "message-outcome agent-run-notice " +
    (runViewState.lifecycle === "failed" ? "status-failed" : "status-history");
  note.setAttribute("role", "status");
  note.setAttribute("data-runtime-health", runViewState.executionHealth);
  note.textContent = runViewState.lifecycle === "failed"
    ? window.RNAssistantRunViewState.failureReasonLabel(runViewState.reason) : "Выполнение отменено.";
  parent.appendChild(note);
}

function agentRunId(items, finalMessage) {
  if (finalMessage && messageRunId(finalMessage.message)) return messageRunId(finalMessage.message);
  for (var index = (items || []).length - 1; index >= 0; index -= 1) {
    if (items[index] && messageRunId(items[index].message)) return messageRunId(items[index].message);
  }
  return "";
}

function renderAgentRunArticle(run, presentation) {
  var items = run.items || [];
  var finalMessage = run.finalMessage || null;
  var timeline = collectVisibleAgentTimelineItems(items);
  var timingItems = timeline.slice();
  if (finalMessage) timingItems.push(finalMessage);
  var runViewState = agentRunViewState(items, finalMessage);
  var stats = agentRunStats(timingItems, !!finalMessage && !run.live, runViewState);
  stats.workspace = !!run.workspace;
  var steps = groupAgentRunSteps(timeline);
  var node = document.createElement("article");
  node.setAttribute("aria-label", "Ответ агента");
  node.className = "message assistant agent-run status-" + stats.status + (run.live ? " live" : "");

  var body = document.createElement("div");
  body.className = "agent-run-wrap";
  var process = document.createElement("div");
  process.className = "agent-run-actions" + (finalMessage && agentToolCallCount(timeline) ? " has-actions" : "");
  var expanded = !finalMessage || run.live || (runViewState && ["running", "awaiting_user", "awaiting_confirmation"].indexOf(runViewState.lifecycle) >= 0);
  // Accepted commentary and reasons stay visible; only completed action details
  // fold into history. A terminal transition must not erase accepted text.
  var updates = groupAgentRunUpdates(steps);
  updates.forEach(function (step, stepIndex) {
    var section = document.createElement("section");
    section.className = "agent-model-step";
    section.agentReconcileKey = step.id;
    section.agentReconcileSignature = JSON.stringify([step.message, step.reason, step.items, step.ambient, step.marker,
      stepIndex === updates.length - 1, !!run.hasLiveDraft, !!run.live, !!run.workspace,
      runViewState && runViewState.lifecycle]);
    var reasoningMessage = step.marker && (step.marker.reasoningMessage || step.marker.message);
    var hasStepReasoning = typeof messageHasReasoning === "function" && messageHasReasoning(reasoningMessage);
    if (hasStepReasoning && typeof appendMessageReasoning === "function") appendMessageReasoning(section, reasoningMessage);
    appendAgentStepMessage(section, step.message, step.marker);
    appendAgentStepReason(section, step);
    if (expanded) appendLiveAgentStep(section, step, stepIndex === updates.length - 1, run.hasLiveDraft, false,
      !run.live && runViewState && ["failed", "cancelled", "completed"].indexOf(runViewState.lifecycle) >= 0, run.workspace, hasStepReasoning);
    if (section.childNodes.length) process.appendChild(section);
  });
  if (!expanded) appendAgentRunOverview(process, steps, timeline, stats);
  if (process.childNodes.length || run.live) body.appendChild(process);
  if (run.live && run.workspaceProgress) {
    var stream = document.createElement("section");
    stream.className = "agent-model-step agent-stream-step";
    paintLiveModelOutput(stream, run.workspaceProgress);
    process.appendChild(stream);
  }
  // A terminal interruption stays visible even when the action trace is collapsed.
  if (!run.live) appendAgentRunViewState(body, runViewState);
  if (finalMessage) {
    var finalSection = document.createElement("section");
    finalSection.className = "agent-final-step";
    if (!run.live && !run.continued && runViewState &&
        runViewState.lifecycle === "awaiting_user" && runViewState.reason === "awaiting_user") {
      finalSection.setAttribute("data-run-interaction", "true");
      var title = document.createElement("strong");
      title.className = "agent-result-title";
      title.textContent = window.RNAssistantRunViewState.outcomeLabel(runViewState);
      finalSection.appendChild(title);
    }
    appendAgentFinalAnswer(finalSection, finalMessage);
    if (typeof appendAcceptedResponseDetails === "function")
      appendAcceptedResponseDetails(finalSection, finalMessage.message, runViewState);
    body.appendChild(finalSection);
  }
  if (!expanded) appendAgentRunArtifacts(body, timeline, !!run.workspace);
  if (!run.live && !run.workspace && typeof appendAgentRunResourceCards === "function") {
    appendAgentRunResourceCards(body, items, finalMessage);
  }
  if (!run.live && !run.workspace && !run.continued && presentation && typeof appendRunChanges === "function") {
    appendRunChanges(body, presentation.target.sessionId, agentRunId(items, finalMessage));
  }
  node.appendChild(body);

  if (!run.live) {
    if (!items.length && finalMessage && typeof appendMessageFooter === "function") {
      appendMessageFooter(node, finalMessage.message, finalMessage.index, null,
        { workspace: !!run.workspace, usage: run.usage, presentation: presentation });
    } else {
      appendAgentRunFooter(node, items, finalMessage, presentation, run.usage);
    }
  }
  enhanceActivity(body);
  return node;
}

function appendAgentRunFooter(node, items, finalMessage, presentation, usage) {
  var footer = document.createElement("div");
  footer.className = "message-footer";
  var footerMeta = document.createElement("div");
  footerMeta.className = "message-footer-meta";
  var usageText = typeof messageUsageText === "function" && usage ? messageUsageText(usage) : "";
  var meta = document.createElement("span");
  meta.className = usageText ? "message-usage" : "message-count";
  meta.textContent = usageText ? "За запуск: " + usageText : agentCountText(items.length + (finalMessage ? 1 : 0),
    "сообщение", "сообщения", "сообщений");
  if (usageText) meta.title = "Сумма usage принятых ответов модели за запуск; размер одного запроса показан в контексте модели.";
  footerMeta.appendChild(meta);

  var actions = document.createElement("div");
  actions.className = "message-actions";
  var last = finalMessage || items[items.length - 1];
  actions.appendChild(smallIconButton(finalMessage ? "Копировать итоговый ответ" : "Копировать ход работы", "copy", function () {
    copyChatMessage(finalMessage ? messageContent(finalMessage.message) : agentRunText(items));
    log(finalMessage ? "Итоговый ответ скопирован." : "Ход работы скопирован.");
  }));
  if (last && typeof appendMessageInspectorAction === "function") appendMessageInspectorAction(actions, last.message, presentation);
  if (last && typeof appendMessageHistoryAction === "function")
    appendMessageHistoryAction(actions, "Ответвить чат отсюда", "branch", "fork", last.message, last.index, presentation);
  if (last && presentation && presentation.invoke("canMessageAction", { name: "delete", message: last.message, index: last.index })) {
    actions.appendChild(smallIconButton("Удалить сообщения запуска", "trash", function () {
      return deleteAgentRun(items, finalMessage, presentation);
    }));
  }

  footer.appendChild(actions);
  footer.appendChild(footerMeta);
  node.appendChild(footer);
}
