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
  if (!targets.length || !window.confirm("Delete this agent run?")) {
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

  function startStep(stepId, message) {
    current = {
      id: stepId || ("unscoped-" + steps.length),
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
  enhanceMarkdown(message, { enableJsonViewer: true, sourceText: text });
}

function appendCollapsedAgentStep(parent, step, isCurrent, finished) {
  var timeline = step.items || [];
  var stats = agentRunStats(timeline, !!finished, finished ? "completed" : "");
  var ambient = isCurrent && step.ambient ? step.ambient.activity : null;
  var active = ambient || (isCurrent && stats.current && isActiveTimelineStatus(activityStatus(stats.current))
    ? stats.current
    : null);
  var effectiveStatus = active ? activityStatus(active) : stats.status;
  var lastAction = null;
  for (var actionIndex = timeline.length - 1; actionIndex >= 0; actionIndex -= 1) {
    var actionKind = activityKind(timeline[actionIndex].activity);
    if (actionKind === "tool" || actionKind === "control" || actionKind === "diagnostic") {
      lastAction = timeline[actionIndex].activity;
      break;
    }
  }
  var transcript = buildAgentRunTranscript(step.items, timeline, stats);
  var details = document.createElement("details");
  details.className = "agent-run-history agent-step-actions status-" + effectiveStatus;

  var summary = document.createElement("summary");
  summary.className = "agent-run-history-summary";
  var title = document.createElement("span");
  title.className = "agent-run-history-title";
  title.textContent = active
    ? activityPrimaryText(active)
    : (lastAction ? activityPrimaryText(lastAction) : (step.message || "Выполняю…"));
  summary.appendChild(title);
  appendAgentRunSummaryState(summary, effectiveStatus);
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

function agentRunSummaryTitle(status, elapsed, runViewState) {
  var title;
  if (runViewState && runViewState.executionHealth === "unknown") {
    title = "Результат изменений не определён";
  } else if (runViewState && runViewState.executionHealth === "errors") {
    title = "Выполнение содержит ошибки";
  } else if (runViewState && runViewState.lifecycle === "awaiting_user") {
    title = "Ожидает ответа";
  } else if (runViewState && runViewState.lifecycle === "awaiting_confirmation") {
    title = "Ожидает подтверждения";
  } else if (status === "completed" && runViewState && !runViewState.verifiedWrites) {
    title = "Ответ получен";
  } else if (status === "failed") {
    title = "Прервано";
  } else if (status === "cancelled") {
    title = "Отменено";
  } else if (status === "waiting") {
    title = "Ожидание";
  } else if (status === "unknown") {
    title = "Результат не подтверждён runtime";
  } else {
    title = "Готово";
  }
  return title + (elapsed ? " за " + elapsed : "");
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

  var summary = document.createElement("summary");
  summary.className = "agent-run-history-summary";
  var actionCount = agentToolCallCount(timeline);
  var title = document.createElement("span");
  title.className = "agent-run-history-title";
  title.textContent = agentRunSummaryTitle(stats.status, stats.elapsed, stats.runViewState);
  summary.appendChild(title);
  appendAgentRunSummaryState(summary, stats.status);
  var caret = document.createElement("span");
  caret.className = "agent-run-history-caret";
  caret.setAttribute("aria-hidden", "true");
  caret.textContent = "›";
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
        agentRunStats(step.items, true, "completed")));
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
  caret.textContent = "›";
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
  var otherUnknownEffects = runViewState
    ? Math.max(0, runViewState.unknownEffects - runViewState.unverifiedWrites)
    : 0;
  var note = document.createElement("div");
  note.className = "message-outcome " + (health === "clean" ? "status-unknown" : "status-warning");
  note.setAttribute("data-runtime-health", health);
  note.setAttribute("role", health === "clean" ? "status" : "alert");
  if (!runViewState) {
    note.textContent = "Для этого run нет typed runtime state. Результат изменений не подтверждён.";
  } else if (health === "unknown" && runViewState.unverifiedWrites && !otherUnknownEffects) {
    note.textContent = "Есть исторические изменения без read-back; runtime не может подтвердить их эффект.";
  } else if (health === "unknown") {
    note.textContent = "Результат изменений не определён. Требуется проверка фактического состояния.";
  } else if (health === "errors") {
    note.textContent = "Выполнение содержит ошибки. Нельзя считать все изменения применёнными.";
  } else if (!runViewState.verifiedWrites) {
    note.textContent = "Ответ модели. Подтверждённых изменений нет.";
  } else {
    note.textContent = "Runtime: ошибки выполнения не зарегистрированы.";
  }
  if (runViewState && (runViewState.verifiedWrites || runViewState.noChangeWrites ||
      runViewState.unverifiedWrites || runViewState.failedCalls || runViewState.unknownEffects)) {
    note.textContent += " Runtime evidence: изменения — " + runViewState.verifiedWrites +
      ", без изменения — " + runViewState.noChangeWrites +
      ", исторические без read-back — " + runViewState.unverifiedWrites +
      ", ошибки вызовов — " + runViewState.failedCalls +
      ", прочие неизвестные эффекты — " + otherUnknownEffects + ".";
  }
  parent.appendChild(note);
  if (runId) {
    var actions = document.createElement("div");
    actions.className = "agent-inline-actions agent-run-journal-actions";
    var openJournal = document.createElement("button");
    openJournal.type = "button";
    openJournal.className = "agent-action-button secondary";
    openJournal.textContent = "Открыть журнал запуска";
    openJournal.addEventListener("click", function () {
      if (typeof window.openRunJournal !== "function") return;
      window.openRunJournal({
        chatId: state.activeChatId,
        runId: runId,
        filter: health === "clean" ? "all" : "problems"
      });
    });
    actions.appendChild(openJournal);
    parent.appendChild(actions);
  }
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
  if (run.live) {
    steps.forEach(function (step, stepIndex) {
      var section = document.createElement("section");
      section.className = "agent-model-step";
      appendAgentStepMessage(section, step.message);
      appendCollapsedAgentStep(section, step, stepIndex === steps.length - 1, stepIndex < steps.length - 1);
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
