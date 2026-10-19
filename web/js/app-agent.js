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

function groupAgentRunSteps(timeline) {
  var steps = [];
  var current = null;
  var seenSteps = Object.create(null);

  function startStep(stepId, message) {
    current = {
      id: stepId || ("unscoped-" + steps.length),
      message: message || "",
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
      if (stepId && seenSteps[stepId]) return;
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

// Presentation groups follow comments; model-step and call identities stay intact.
function groupAgentRunUpdates(steps) {
  var groups = [];
  (steps || []).forEach(function (step) {
    var silent = !String(step.message || "").trim();
    var previous = groups[groups.length - 1];
    if (previous && silent) {
      previous.items = previous.items.concat(step.items || []);
      previous.ambient = step.ambient;
    } else {
      groups.push({ message: step.message, items: (step.items || []).slice(),
        ambient: step.ambient });
    }
  });
  return groups;
}

function appendAgentCompletedActions(parent, actions, stats) {
  if (!actions.length) return;
  var details = document.createElement("details");
  var active = actions.some(function (item) { return isActiveTimelineStatus(activityStatus(item.activity)); });
  details.className = "agent-run-history " + (active ? "agent-active-actions" : "agent-completed-actions");
  details.open = active;
  details.setAttribute("data-disclosure-key", "actions:" + activityTimelineKey(actions[0].activity));
  var summary = document.createElement("summary");
  summary.className = "agent-run-history-summary";
  var title = document.createElement("span");
  title.className = "agent-run-history-title";
  title.textContent = activityPrimaryText(actions[active ? actions.length - 1 : 0].activity) + " · " + agentActionCountText(actions.length);
  summary.appendChild(title);
  var caret = document.createElement("span");
  caret.className = "agent-run-history-caret";
  caret.setAttribute("aria-hidden", "true");
  summary.appendChild(caret);
  details.appendChild(summary);
  appendAgentRunProcess(details, actions, stats);
  parent.appendChild(details);
}

function appendLiveAgentStep(parent, step, isLast, suppressAmbient, suppressArtifacts) {
  var actions = [];
  function append(item, activity, nested) {
    if (activityKind(activity) !== "step" && activityKind(activity) !== "group") {
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
  var adjacent = [];
  function flush() {
    appendAgentCompletedActions(parent, adjacent, stats);
    adjacent = [];
  }
  actions.forEach(function (item) {
    var kind = activityKind(item.activity);
    var presentation = activityPresentationState(item.activity);
    var groupable = (kind === "tool" || kind === "control") &&
      (presentation === "completed" || presentation === "running");
    if (groupable) {
      if (adjacent.length && isActiveTimelineStatus(activityStatus(adjacent[0].activity)) !==
          isActiveTimelineStatus(activityStatus(item.activity))) flush();
      adjacent.push(item);
    } else {
      flush();
      appendAgentRunProcess(parent, [item], stats);
    }
    var artifacts = document.createElement("div");
    if (!suppressArtifacts) appendActivityArtifacts(artifacts, item.activity, { message: item.message, messageId: messageId(item.message), index: item.index });
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
  if (isLast && !active && ambient && !suppressAmbient) {
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
  if (!(steps || []).some(function (step) { return (step.items && step.items.length) || String(step.message || "").trim(); })) return null;
  var details = document.createElement("details");
  details.className = "agent-run-history agent-run-overview status-" + stats.status;
  details.setAttribute("data-disclosure-key", "overview");

  var summary = document.createElement("summary");
  summary.className = "agent-run-history-summary";
  var title = document.createElement("span");
  title.className = "agent-run-history-title";
  title.textContent = actionCount
    ? "Ход работы" + (stats.elapsed ? " · " + stats.elapsed : "") + " · " + agentActionCountText(actionCount)
    : "Ход работы" + (stats.elapsed ? " · " + stats.elapsed : "");
  summary.appendChild(title);
  if ((stats.runViewState && stats.runViewState.executionHealth !== "clean") ||
      timeline.some(function (item) { return ["failed", "warning", "unknown", "partial"].indexOf(activityPresentationState(item.activity)) >= 0; })) {
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
      if (!historyItems.length && !String(step.message || "").trim()) return;
      var section = document.createElement("section");
      section.className = "agent-model-step agent-model-step-history";
      appendAgentStepMessage(section, step.message);
      appendLiveAgentStep(section, { items: historyItems }, false, true, true);
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
  return details;
}

function appendAgentRunViewState(parent, runViewState, runId) {
  var health = runViewState ? runViewState.executionHealth : "unknown";
  var uncertain = health === "unknown";
  var goalUnassessed = runViewState && runViewState.goalVerification === "not_assessed" &&
    runViewState.lifecycle === "completed" && runViewState.reason === "model_done";
  if (health === "clean" && runViewState && !goalUnassessed &&
      ["failed", "cancelled"].indexOf(runViewState.lifecycle) < 0) return;
  var cause = runViewState && runViewState.lifecycle === "failed"
    ? window.RNAssistantRunViewState.failureReasonLabel(runViewState.reason)
    : runViewState && runViewState.lifecycle === "cancelled" ? "Выполнение отменено." : "";
  var note = document.createElement("div");
  note.className = "message-outcome " + (uncertain ? "status-warning" : runViewState && runViewState.lifecycle === "failed" ? "status-failed" :
    health === "errors" ? "status-warning" : "status-history");
  note.setAttribute("data-runtime-health", health);
  note.setAttribute("role", uncertain ? "alert" : "status");
  if (!runViewState) {
    note.textContent = "Сведения о выполнении недоступны. Результат изменений не подтверждён.";
  } else if (uncertain) {
    note.textContent = (cause ? cause + " " : "") + "Есть действия с неподтверждённым результатом: " + runViewState.unknownEffects +
      ". Проверьте фактическое состояние перед повторной записью.";
  } else if (cause) {
    note.textContent = cause;
  } else if (goalUnassessed) {
    note.textContent = (health === "errors"
      ? "В ходе работы были неудачные попытки: " + runViewState.failedCalls + ". "
      : "Модель завершила ответ. ") + "Подтверждённые записи: " + runViewState.verifiedWrites +
      "; без изменения: " + runViewState.noChangeWrites +
      ". Соответствие всем требованиям автоматически не проверено.";
  } else {
    note.textContent = "В ходе работы были неудачные попытки.";
  }
  if (runId) {
    var openJournal = document.createElement("button");
    openJournal.type = "button";
    openJournal.className = "agent-action-button agent-details-link";
    openJournal.textContent = uncertain ? "Что проверить" : "Причины и детали";
    var sourceChatId = state.activeChatId;
    openJournal.addEventListener("click", function () {
      if (state.activeChatId !== sourceChatId || typeof window.openRunJournal !== "function") return;
      window.openRunJournal({ chatId: sourceChatId, runId: runId, filter: "problems" });
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
  node.setAttribute("aria-label", "Ответ агента");
  node.className = "message assistant agent-run status-" + stats.status + (run.live ? " live" : "");

  var body = document.createElement("div");
  body.className = "agent-run-wrap";
  var process = document.createElement("div");
  process.className = "agent-run-actions" + (finalMessage && agentToolCallCount(timeline) ? " has-actions" : "");
  var expanded = !finalMessage || run.live || (runViewState && ["running", "awaiting_user", "awaiting_confirmation"].indexOf(runViewState.lifecycle) >= 0);
  // A step's narration and actions share the same disclosure. Final answers and
  // pending interactions remain outside the completed history.
  var updates = expanded ? groupAgentRunUpdates(steps) : [];
  updates.forEach(function (step, stepIndex) {
    var section = document.createElement("section");
    section.className = "agent-model-step";
    appendAgentStepMessage(section, step.message);
    appendLiveAgentStep(section, step, stepIndex === updates.length - 1, run.hasLiveDraft);
    if (section.childNodes.length) process.appendChild(section);
  });
  if (!expanded) appendAgentRunOverview(process, steps, timeline, stats);
  if (process.childNodes.length || run.live) body.appendChild(process);
  // This warning is outside collapsed trace and never derived from the model's prose.
  if (!run.live) appendAgentRunViewState(body, runViewState, agentRunId(items, finalMessage));
  if (finalMessage) {
    var finalSection = document.createElement("section");
    finalSection.className = "agent-final-step";
    if (!run.live && !run.continued && runViewState &&
        ((runViewState.lifecycle === "awaiting_user" && runViewState.reason === "model_needs_input") ||
         (runViewState.lifecycle === "completed" && runViewState.reason === "model_blocked"))) {
      finalSection.className += " agent-result-card";
      finalSection.setAttribute("data-run-result-card", "true");
      var title = document.createElement("strong");
      title.className = "agent-result-title";
      title.textContent = window.RNAssistantRunViewState.outcomeLabel(runViewState);
      finalSection.appendChild(title);
    }
    appendAgentFinalAnswer(finalSection, finalMessage);
    body.appendChild(finalSection);
  }
  if (!expanded) appendAgentRunArtifacts(body, timeline);
  if (!run.live && typeof appendAgentRunResourceCards === "function") {
    appendAgentRunResourceCards(body, items, finalMessage);
  }
  if (!run.live && !run.continued && typeof appendRunChanges === "function") {
    appendRunChanges(body, state.activeChatId, agentRunId(items, finalMessage));
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
  actions.appendChild(smallIconButton(finalMessage ? "Копировать итоговый ответ" : "Копировать ход работы", "copy", function () {
    copyText(finalMessage ? messageContent(finalMessage.message) : agentRunText(items));
    log(finalMessage ? "Итоговый ответ скопирован." : "Ход работы скопирован.");
  }));
  if (!historyActionsBlocked) {
    actions.appendChild(smallIconButton("Ответвить чат отсюда", "branch", function () {
      forkChatAtMessage(last.message, last.index);
    }));
  }

  if (!historyActionsBlocked) {
    actions.appendChild(smallIconButton("Удалить сообщения запуска", "trash", function () {
      deleteAgentRun(items, finalMessage);
    }));
  }

  footer.appendChild(actions);
  footer.appendChild(footerMeta);
  node.appendChild(footer);
}
