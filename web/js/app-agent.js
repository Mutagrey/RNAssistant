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
      renderInlineArtifacts: false,
      liveFeed: !!stats.liveFeed
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
      if (activityToolCallId(item.activity) && !isActiveTimelineStatus(existingStatus) && isActiveTimelineStatus(nextStatus)) return;
      if (activityToolCallId(item.activity) || activityKind(item.activity) === "step" || existingStatus === "running" || existingStatus === "waiting" ||
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
  var content = messageContent(finalMessage.message);
  answer.innerHTML = markdown(content);
  parent.appendChild(answer);
  enhanceMarkdown(answer, { enableJsonViewer: true, sourceText: content });
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
  if (!targets.length || !window.confirm("Удалить сообщения этого запуска?")) {
    return;
  }

  for (var i = targets.length - 1; i >= 0; i -= 1) {
    await deleteMessage(targets[i].message, targets[i].index);
  }
}

function agentActionCountText(count) {
  var lastTwo = count % 100;
  var last = count % 10;
  var noun = lastTwo >= 11 && lastTwo <= 14
    ? "действий"
    : (last === 1 ? "действие" : (last >= 2 && last <= 4 ? "действия" : "действий"));
  return count + " " + noun;
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
  var stepsById = Object.create(null);

  function startStep(stepId, message) {
    if (stepId && stepsById[stepId]) {
      current = stepsById[stepId];
      if (!current.message && message) current.message = message;
      return current;
    }
    current = {
      id: stepId || ("unscoped-" + steps.length),
      message: message || "",
      items: [],
      ambient: null
    };
    steps.push(current);
    if (stepId) stepsById[stepId] = current;
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
  enhanceMarkdown(message, { enableJsonViewer: true, sourceText: text });
}

function appendLiveAgentStep(parent, step, isLast) {
  var actions = [];
  function append(item, activity, nested) {
    if (activityKind(activity) !== "notice") {
      actions.push({ message: item.message, index: item.index, activity: activity,
        reasoningMessage: item.reasoningMessage, includeReasoning: !nested });
    }
    activityChildren(activity).forEach(function (child) { append(item, child, true); });
  }
  (step.items || []).forEach(function (item) { append(item, item.activity); });
  var stats = agentRunStats((step.items || []).filter(function (item) {
    return activityKind(item.activity) !== "notice";
  }), !isLast, null);
  var active = isLast && stats.current && isActiveTimelineStatus(activityStatus(stats.current))
    ? stats.current : null;
  stats.current = active;
  stats.liveFeed = true;
  if (actions.length) {
    appendAgentRunProcess(parent, actions, stats);
    appendAgentRunArtifacts(parent, step.items || []);
  }
  var ambient = step.ambient || (step.items || []).filter(function (item) {
    return activityKind(item.activity) === "notice";
  }).slice(-1)[0];
  if (isLast && !active && ambient) {
    parent.appendChild(renderActivityRow(ambient.activity, true, false, {
      hideIcon: true, liveFeed: true, currentActivity: ambient.activity
    }));
  }
}

function agentRunSummaryTitle(status, elapsed, runViewState) {
  var title = "Действия";
  if (runViewState) {
    if (runViewState.lifecycle === "failed") title = "Работа остановлена";
    else if (runViewState.lifecycle === "cancelled") title = "Работа отменена";
    else if (runViewState.lifecycle === "awaiting_user") title = "Ожидает ответа";
    else if (runViewState.lifecycle === "awaiting_confirmation") title = "Ожидает подтверждения";
  }
  return title + (elapsed ? " · " + elapsed : "");
}

function appendAgentRunSummaryState(summary, status) {
  var labels = {
    running: "",
    waiting: "!",
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
  var details = document.createElement("details");
  details.className = "agent-run-history agent-run-overview status-" + stats.status;
  details.setAttribute("data-disclosure-key", "overview");

  var summary = document.createElement("summary");
  summary.className = "agent-run-history-summary";
  var actionCount = agentToolCallCount(timeline);
  var title = document.createElement("span");
  title.className = "agent-run-history-title";
  title.textContent = agentRunSummaryTitle(stats.status, stats.elapsed, stats.runViewState) + " · " + actionCount;
  summary.appendChild(title);
  appendAgentRunSummaryState(summary, stats.status);
  var caret = document.createElement("span");
  caret.className = "agent-run-history-caret";
  caret.setAttribute("aria-hidden", "true");
  summary.appendChild(caret);
  summary.setAttribute("aria-label", title.textContent + ". " + agentActionCountText(actionCount));
  summary.title = agentActionCountText(actionCount);
  details.appendChild(summary);

  var content = document.createElement("div");
  content.className = "agent-run-history-content agent-run-overview-content";
  (steps || []).forEach(function (step) {
    var section = document.createElement("section");
    section.className = "agent-model-step agent-model-step-history";
    appendAgentStepMessage(section, step.message);
    if ((step.items || []).length) {
      section.appendChild(buildAgentRunTranscript(
        step.items,
        step.items,
        agentRunStats(step.items, true, null)));
    }
    content.appendChild(section);
  });
  if (!content.childNodes.length) {
    var empty = document.createElement("div");
    empty.className = "agent-run-empty";
    empty.textContent = "Подробности выполнения не записаны.";
    content.appendChild(empty);
  }
  details.appendChild(content);
  parent.appendChild(details);
  return details;
}

function agentRunOutcomeReason(activity) {
  var reason = String(activityResultMessage(activity) || "").trim();
  if (reason === "Execution was cancelled before a result was recorded.") {
    return "Выполнение отменено до получения результата.";
  }
  if (reason === "Execution stopped before a result was recorded.") {
    return "Выполнение остановлено до получения результата.";
  }
  return reason;
}

function appendAgentRunOutcome(parent, activity, overview) {
  if (!activity) return;
  var status = activityStatus(activity);
  var outcome = document.createElement("button");
  outcome.type = "button";
  outcome.className = "agent-run-outcome status-" + status;
  outcome.title = "Показать ход выполнения";

  var copy = document.createElement("span");
  copy.className = "agent-run-outcome-copy";
  var reasonText = agentRunOutcomeReason(activity);
  copy.textContent = reasonText || (status === "cancelled"
    ? "Выполнение отменено"
    : "Не удалось: " + activityPrimaryText(activity));
  outcome.appendChild(copy);
  var caret = document.createElement("span");
  caret.className = "agent-run-outcome-caret";
  caret.setAttribute("aria-hidden", "true");
  outcome.appendChild(caret);
  outcome.title = copy.textContent + " · Показать ход выполнения";
  outcome.setAttribute("aria-label", outcome.title);
  outcome.addEventListener("click", function () {
    overview.open = true;
    overview.scrollIntoView({ block: "nearest" });
  });
  parent.appendChild(outcome);
}

function appendAgentRunViewState(parent, runViewState, runId) {
  var health = runViewState ? runViewState.executionHealth : "unknown";
  var uncertain = health === "unknown";
  if (health === "clean" && runViewState && runViewState.lifecycle !== "failed") return;
  var note = document.createElement("div");
  note.className = "message-outcome " + (uncertain ? "status-warning" : "status-history");
  note.setAttribute("data-runtime-health", health);
  note.setAttribute("role", uncertain ? "alert" : "status");
  if (!runViewState) {
    note.textContent = "Сведения о выполнении недоступны. Результат изменений не подтверждён.";
  } else if (uncertain) {
    note.textContent = "Есть действия с неподтверждённым результатом: " + runViewState.unknownEffects +
      ". Проверьте фактическое состояние перед повторной записью.";
  } else if (runViewState.lifecycle === "failed") {
    note.textContent = "Работа остановлена. Причина доступна в деталях выполнения.";
  } else {
    note.textContent = "Неудачных попыток в ходе работы: " + runViewState.failedCalls + ".";
  }
  if (runId) {
    var openJournal = document.createElement("button");
    openJournal.type = "button";
    openJournal.className = "agent-action-button agent-details-link";
    openJournal.textContent = uncertain ? "Что проверить" : "Причины и детали";
    openJournal.addEventListener("click", function () {
      if (typeof window.openRunJournal !== "function") return;
      window.openRunJournal({ chatId: state.activeChatId, runId: runId, filter: "problems" });
    });
    note.appendChild(openJournal);
  }
  parent.appendChild(note);
}

function agentRunId(items, finalMessage) {
  if (finalMessage && messageRunId(finalMessage.message)) return messageRunId(finalMessage.message);
  for (var index = (items || []).length - 1; index >= 0; index -= 1) {
    if (items[index] && messageRunId(items[index].message)) return messageRunId(items[index].message);
  }
  return "";
}

function renderAgentRunArticle(run) {
  var items = run.items || [];
  var finalMessage = run.finalMessage || null;
  var timeline = collectVisibleAgentTimelineItems(items);
  var timingItems = timeline.slice();
  if (finalMessage) timingItems.push(finalMessage);
  var runViewState = agentRunViewState(items, finalMessage);
  var stats = agentRunStats(timingItems, !!finalMessage && !run.live, runViewState);
  var steps = groupAgentRunSteps(timeline);
  var node = document.createElement("article");
  node.className = "message assistant agent-run status-" + stats.status + (run.live ? " live" : "");

  var body = document.createElement("div");
  body.className = "agent-run-wrap";
  var expanded = run.live || (runViewState && ["running", "awaiting_user", "awaiting_confirmation"].indexOf(runViewState.lifecycle) >= 0);
  if (expanded) {
    steps.forEach(function (step, stepIndex) {
      var section = document.createElement("section");
      section.className = "agent-model-step";
      appendAgentStepMessage(section, step.message);
      appendLiveAgentStep(section, step, stepIndex === steps.length - 1);
      body.appendChild(section);
    });
  } else {
    var overview = appendAgentRunOverview(body, steps, timeline, stats);
    var currentStatus = stats.current ? activityStatus(stats.current) : "";
    if (!finalMessage && (currentStatus === "failed" || currentStatus === "cancelled")) {
      appendAgentRunOutcome(body, stats.current, overview);
    }
  }
  // This warning is outside collapsed trace and never derived from the model's prose.
  if (!run.live) appendAgentRunViewState(body, runViewState, agentRunId(items, finalMessage));
  if (finalMessage) {
    var finalSection = document.createElement("section");
    finalSection.className = "agent-final-step";
    appendAgentFinalAnswer(finalSection, finalMessage);
    body.appendChild(finalSection);
  }
  if (!run.live && typeof appendAgentRunResourceCards === "function") {
    appendAgentRunResourceCards(body, items, finalMessage);
  }
  node.appendChild(body);

  if (!run.live) {
    if (!items.length && finalMessage && typeof appendMessageFooter === "function") {
      appendMessageFooter(node, finalMessage.message, finalMessage.index, null);
    } else {
      appendAgentRunFooter(node, items, finalMessage);
    }
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
  actions.appendChild(smallIconButton(finalMessage ? "Копировать итоговый ответ" : "Копировать ход работы", "copy", function () {
    copyText(finalMessage ? messageContent(finalMessage.message) : agentRunText(items));
    log(finalMessage ? "Итоговый ответ скопирован." : "Ход работы скопирован.");
  }));
  if (!historyActionsBlocked) {
    actions.appendChild(smallIconButton("Удалить сообщения запуска", "trash", function () {
      deleteAgentRun(items, finalMessage);
    }));
  }

  footer.appendChild(footerMeta);
  footer.appendChild(actions);
  node.appendChild(footer);
}
