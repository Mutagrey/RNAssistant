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
      if (phase === "routing") {
        if (copy.Title !== undefined) {
          copy.Title = progressTitle;
        } else {
          copy.title = progressTitle;
        }
      }
      if (phase === "plan") {
        copy.__decisionMessage = message.trim();
      }
    }
    return copy;
  }

  return {
    kind: "notice",
    title: progress.message || progress.Message || "Выполняю...",
    subtitle: progress.phase || progress.Phase || "working",
    status: activityStatusFromPhase(progress.phase || progress.Phase)
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

function activityToolId(activity) {
  return activityValue(activity, "ToolId", "toolId", "") || "";
}

function activityBatchId(activity) {
  return activityValue(activity, "BatchId", "batchId", "") || "";
}

function activityPendingId(activity) {
  return activityValue(activity, "PendingId", "pendingId", "") || "";
}

function activityExecutionStatus(activity) {
  return activityValue(activity, "ExecutionStatus", "executionStatus", "") || "";
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
  var pendingId = activityPendingId(activity);
  if (pendingId && !activityToolId(activity)) {
    return "pending:" + pendingId;
  }

  var runId = activityValue(activity, "RunId", "runId", "") || "";
  var kind = activityKind(activity);
  if (kind === "plan") {
    return [runId || "run", "plan"].join("|");
  }

  return [
    runId || "run",
    kind || "activity",
    activityBatchId(activity),
    activityToolId(activity),
    activityArgumentsJson(activity)
  ].join("|");
}

function setActivityStatusValue(activity, status) {
  if (!activity) {
    return;
  }
  if (activity.Status !== undefined) {
    activity.Status = status;
  } else {
    activity.status = status;
  }
}

function isActiveTimelineStatus(status) {
  return status === "planned" || status === "running" || status === "waiting";
}

function recordActivityTimeline(items, activity) {
  if (!activity) {
    return null;
  }

  items = items || [];
  var copy = cloneActivity(activity);
  var key = activityTimelineKey(copy);
  var nextStatus = activityStatus(copy);
  var kind = activityKind(copy);
  copy.__timelineKey = key;

  if (kind !== "plan" && isActiveTimelineStatus(nextStatus)) {
    items.forEach(function (item) {
      if (!item || item.__timelineKey === key || !isActiveTimelineStatus(activityStatus(item))) {
        return;
      }
      setActivityStatusValue(item, "completed");
    });
  }

  for (var i = items.length - 1; i >= 0; i -= 1) {
    var existing = items[i];
    if (!existing || existing.__timelineKey !== key) {
      continue;
    }

    if (!copy.__decisionMessage && existing.__decisionMessage) {
      copy.__decisionMessage = existing.__decisionMessage;
    }

    if (kind === "plan") {
      items[i] = copy;
      return copy;
    }

    var existingStatus = activityStatus(existing);
    if (isActiveTimelineStatus(existingStatus) ||
        (existingStatus === nextStatus && activityResultMessage(existing) === activityResultMessage(copy))) {
      items[i] = copy;
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

function recordLiveAgentActivity(activity) {
  if (!activity) {
    return;
  }

  if (!state.liveAgentRun) {
    state.liveAgentRun = [];
  }
  return recordActivityTimeline(state.liveAgentRun, activity);
}

function activityCountStatus(activity, counts) {
  if (!activity || !counts) {
    return;
  }

  var status = activityStatus(activity);
  counts.total += 1;
  counts[status] = (counts[status] || 0) + 1;
  activityChildren(activity).forEach(function (child) {
    activityCountStatus(child, counts);
  });
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

  var first = items[0].activity;
  var runActivities = items.length > 1 && activityKind(first) === "plan"
    ? items.slice(1).map(function (item) { return item.activity; })
    : (activityKind(first) === "plan" ? activityChildren(first) : items.map(function (item) { return item.activity; }));
  runActivities.forEach(append);
  return activities;
}

function currentRunActivity(activities) {
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

function agentRunStats(items) {
  var counts = { total: 0 };
  var activities = collectRunActivities(items || []);
  activities.forEach(function (activity) {
    activityCountStatus(activity, counts);
  });
  var current = currentRunActivity(activities);
  var elapsed = agentRunElapsedText(items || []);

  var parts = [];
  if (counts.total) {
    parts.push((counts.completed || 0) + "/" + counts.total + " completed");
  }
  if (counts.failed) {
    parts.push(counts.failed + " failed");
  }
  if (counts.cancelled) {
    parts.push(counts.cancelled + " cancelled");
  }
  if (counts.running) {
    parts.push(counts.running + " running");
  }
  if (counts.waiting) {
    parts.push(counts.waiting + " waiting");
  }
  if (counts.planned) {
    parts.push(counts.planned + " planned");
  }
  if (counts.incomplete) {
    parts.push(counts.incomplete + " incomplete");
  }
  if (elapsed) {
    parts.push("elapsed " + elapsed);
  }
  if (current) {
    parts.push("current: " + activityTitle(current));
  }
  if (!parts.length) {
    parts.push("planned");
  }

  return {
    text: parts.join(" · "),
    current: current,
    counts: counts,
    elapsed: elapsed,
    status: counts.failed ? "failed" : (counts.running ? "running" : (counts.waiting ? "waiting" : (counts.incomplete ? "incomplete" : (counts.cancelled ? "cancelled" : (counts.planned && counts.planned === counts.total ? "planned" : "completed")))))
  };
}

function isAgentRunStart(message) {
  return messageRole(message) === "assistant" && activityKind(messageActivity(message)) === "plan";
}

function isAgentRunContinuation(message) {
  var kind = activityKind(messageActivity(message));
  return messageRole(message) === "assistant" &&
    (kind === "plan" || kind === "tool" || kind === "tool_batch" || kind === "verification" || kind === "retry" || kind === "diagnostic");
}

function canCollectAgentRunAt(index) {
  var message = state.messages[index];
  if (!message || messageProtocolMessage(message) || messageRole(message) !== "assistant" || !messageActivity(message)) {
    return false;
  }
  if (isAgentRunStart(message)) {
    return true;
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
  var runId = messageRunId(state.messages[startIndex]);
  var index = startIndex + 1;
  while (index < state.messages.length) {
    var candidate = state.messages[index];
    if (messageProtocolMessage(candidate) && (!runId || messageRunId(candidate) === runId)) {
      index += 1;
      continue;
    }
    if (!(runId ? (messageRunId(candidate) === runId && !!messageActivity(candidate)) : isAgentRunContinuation(candidate))) {
      break;
    }
    items.push({ message: candidate, index: index, activity: messageActivity(candidate) });
    index += 1;
  }

  var finalMessage = null;
  while (index < state.messages.length && messageProtocolMessage(state.messages[index]) &&
      (!runId || messageRunId(state.messages[index]) === runId)) index += 1;
  if (index < state.messages.length && isAgentRunFinalMessage(state.messages[index]) &&
      (!runId || messageRunId(state.messages[index]) === runId)) {
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
