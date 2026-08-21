function activityValue(activity, pascal, camel, fallback) {
  activity = activity || {};
  return activity[pascal] !== undefined ? activity[pascal] : (activity[camel] !== undefined ? activity[camel] : fallback);
}

function activityStatusFromPhase(phase) {
  var value = (phase || "").toLowerCase();
  if (value === "completed") {
    return "completed";
  }
  if (value === "failed") {
    return "failed";
  }
  if (value === "waiting") {
    return "waiting";
  }
  return "running";
}

function normalizeProgressActivity(progress) {
  progress = progress || {};
  var activity = progress.activity || progress.Activity;
  if (activity) {
    var copy = cloneActivity(activity);
    var phase = (progress.phase || progress.Phase || "").toLowerCase();
    var message = progress.message || progress.Message || "";
    copy.__progressPhase = phase;
    if (message) {
      var progressTitle = message.replace(/[.]+$/, "");
      if (copy.ProgressTitle !== undefined) {
        copy.ProgressTitle = progressTitle;
      } else {
        copy.progressTitle = progressTitle;
      }
    }
    return copy;
  }

  var phase = (progress.phase || progress.Phase || "").toLowerCase();
  return {
    kind: "notice",
    title: phase === "thinking" ? "Думаю…" : (progress.message || progress.Message || "Выполняю…"),
    subtitle: phase || "working",
    status: activityStatusFromPhase(phase)
  };
}

function activityChildren(activity) {
  return activityValue(activity, "Children", "children", []) || [];
}

function activityStatus(activity) {
  return activityValue(activity, "Status", "status", "completed") || "completed";
}

function activityTitle(activity) {
  return activityValue(activity, "Title", "title", "Agent step") || "Agent step";
}

function activityProgressTitle(activity) {
  return activityValue(activity, "ProgressTitle", "progressTitle", "") || "";
}

function activityStepId(activity) {
  return activityValue(activity, "StepId", "stepId", "") || "";
}

function activityStepMessage(activity) {
  return activityValue(activity, "StepMessage", "stepMessage", "") || "";
}

function activityToolId(activity) {
  return activityValue(activity, "ToolId", "toolId", "") || "";
}

function activityToolCallId(activity) {
  return activityValue(activity, "ToolCallId", "toolCallId", "") || "";
}

function activityPendingId(activity) {
  return activityValue(activity, "PendingId", "pendingId", "") || "";
}

function activityArgumentsJson(activity) {
  return activityValue(activity, "ArgumentsJson", "argumentsJson", "") || "";
}

function activityDataJson(activity) {
  return activityValue(activity, "DataJson", "dataJson", "") || "";
}

function activityResultMessage(activity) {
  return activityValue(activity, "ResultMessage", "resultMessage", "") || "";
}

function activityKind(activity) {
  return (activityValue(activity, "Kind", "kind", "") || "").toLowerCase();
}

function activityText(activity) {
  var lines = [];
  function append(item, depth) {
    if (!item) {
      return;
    }
    var prefix = new Array(depth + 1).join("  ");
    var toolId = activityToolId(item);
    lines.push(prefix + activityTitle(item) + (toolId ? " (" + toolId + ")" : "") + " - " + activityStatus(item));
    var result = activityValue(item, "ResultMessage", "resultMessage", "");
    if (result) {
      lines.push(prefix + result);
    }
    activityChildren(item).forEach(function (child) {
      append(child, depth + 1);
    });
  }
  append(activity, 0);
  return lines.join("\n");
}

function activityTimelineKey(activity) {
  var stepId = activityStepId(activity);
  if (stepId && activityKind(activity) === "step") {
    return "step:" + stepId;
  }
  var pendingId = activityPendingId(activity);
  if (pendingId && !activityToolId(activity)) {
    return "pending:" + pendingId;
  }

  var runId = activityValue(activity, "RunId", "runId", "") || "";
  return [
    runId || "run",
    activityKind(activity) || "activity",
    activityToolCallId(activity),
    activityToolId(activity),
    activityArgumentsJson(activity)
  ].join("|");
}

function isActiveTimelineStatus(status) {
  return status === "running" || status === "waiting";
}

function recordActivityTimeline(items, activity) {
  if (!activity) {
    return null;
  }

  items = items || [];
  var copy = cloneActivity(activity);
  var key = activityTimelineKey(copy);
  var nextStatus = activityStatus(copy);
  copy.__timelineKey = key;

  for (var i = items.length - 1; i >= 0; i -= 1) {
    var existing = items[i];
    if (!existing || existing.__timelineKey !== key) {
      continue;
    }

    var existingStatus = activityStatus(existing);
    if (isActiveTimelineStatus(existingStatus) ||
        (existingStatus === nextStatus && activityResultMessage(existing) === activityResultMessage(copy))) {
      if (activityKind(copy) === "notice" && i < items.length - 1) {
        items.splice(i, 1);
        items.push(copy);
      } else {
        items[i] = copy;
      }
      return copy;
    }
    break;
  }

  items.push(copy);
  return copy;
}

function cloneActivity(activity) {
  if (!activity) {
    return null;
  }

  return JSON.parse(JSON.stringify(activity));
}

function activityCountStatus(activity, counts) {
  if (!activity || !counts) {
    return;
  }

  var status = activityStatus(activity);
  counts.total += 1;
  counts[status] = (counts[status] || 0) + 1;
}

function collectRunActivities(items) {
  var activities = [];
  if (!items || !items.length) {
    return activities;
  }

  function append(activity) {
    if (!activity) {
      return;
    }
    activities.push(activity);
    activityChildren(activity).forEach(append);
  }

  var runActivities = items.map(function (item) { return item.activity; });
  runActivities.forEach(append);
  return activities;
}

function currentRunActivity(activities, finished) {
  if (finished) {
    return activities.length ? activities[activities.length - 1] : null;
  }
  var preferred = ["running", "waiting", "failed", "cancelled"];
  for (var i = 0; i < preferred.length; i += 1) {
    for (var j = activities.length - 1; j >= 0; j -= 1) {
      if (activityStatus(activities[j]) === preferred[i]) {
        return activities[j];
      }
    }
  }
  return activities.length ? activities[activities.length - 1] : null;
}

function formatElapsedTime(ms) {
  if (!ms || ms < 1000) {
    return "";
  }
  var seconds = Math.round(ms / 1000);
  if (seconds < 60) {
    return seconds + "s";
  }
  var minutes = Math.floor(seconds / 60);
  seconds = seconds % 60;
  if (minutes < 60) {
    return minutes + "m" + (seconds ? " " + seconds + "s" : "");
  }
  var hours = Math.floor(minutes / 60);
  minutes = minutes % 60;
  return hours + "h" + (minutes ? " " + minutes + "m" : "");
}

function agentRunElapsedText(items) {
  var dates = (items || []).map(function (item) {
    var value = messageCreatedUtc(item.message);
    var time = value ? Date.parse(value) : NaN;
    return isNaN(time) ? null : time;
  }).filter(function (time) { return time !== null; });
  if (dates.length < 2) {
    return "";
  }
  return formatElapsedTime(Math.max.apply(Math, dates) - Math.min.apply(Math, dates));
}

function agentRunStats(items, finished) {
  var counts = { total: 0 };
  var activities = collectRunActivities(items || []);
  activities.forEach(function (activity) {
    activityCountStatus(activity, counts);
  });
  var current = currentRunActivity(activities, !!finished);
  var elapsed = agentRunElapsedText(items || []);

  return {
    current: current,
    counts: counts,
    elapsed: elapsed,
    status: finished
      ? (counts.failed ? "completed_with_errors" : "completed")
      : (counts.failed ? "failed" : (counts.running ? "running" : (counts.waiting ? "waiting" : (counts.cancelled ? "cancelled" : "completed"))))
  };
}

function isAgentRunContinuation(message) {
  var kind = activityKind(messageActivity(message));
  return messageRole(message) === "assistant" &&
    (kind === "tool" || kind === "control" || kind === "diagnostic" || kind === "reasoning");
}

function canCollectAgentRunAt(index) {
  var message = state.messages[index];
  if (!message || messageProtocolMessage(message) || messageRole(message) !== "assistant" || !messageActivity(message)) {
    return false;
  }
  var runId = messageRunId(message);
  if (!runId) {
    return false;
  }
  var previous = index > 0 ? state.messages[index - 1] : null;
  return !previous || messageRunId(previous) !== runId || !messageActivity(previous);
}

function isAgentRunFinalMessage(message) {
  return messageRole(message) === "assistant"
    && !messageActivity(message)
    && !message.Pending
    && !message.Failed
    && !message.Local
    && !!messageContent(message).trim();
}

function messageRunId(message) {
  return message ? (message.RunId || message.runId || "") : "";
}

function collectAgentRun(startIndex) {
  var items = [{ message: state.messages[startIndex], index: startIndex, activity: messageActivity(state.messages[startIndex]) }];
  var index = startIndex + 1;
  while (index < state.messages.length) {
    var candidate = state.messages[index];
    if (messageProtocolMessage(candidate)) {
      index += 1;
      continue;
    }
    if (!isAgentRunContinuation(candidate)) {
      break;
    }
    items.push({ message: candidate, index: index, activity: messageActivity(candidate) });
    index += 1;
  }

  var finalMessage = null;
  while (index < state.messages.length && messageProtocolMessage(state.messages[index])) index += 1;
  if (index < state.messages.length && isAgentRunFinalMessage(state.messages[index])) {
    finalMessage = { message: state.messages[index], index: index };
    index += 1;
  }

  return { items: items, finalMessage: finalMessage, nextIndex: index };
}

function agentRunText(items) {
  return (items || []).map(function (item) {
    return activityText(item.activity);
  }).join("\n\n");
}
