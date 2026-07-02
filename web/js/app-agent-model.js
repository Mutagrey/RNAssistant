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
    return activity;
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

function activityToolId(activity) {
  return activityValue(activity, "ToolId", "toolId", "") || "";
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
  if (pendingId) {
    return "pending:" + pendingId;
  }

  var kind = activityKind(activity);
  if (kind === "plan") {
    return "plan";
  }

  return [
    kind || "activity",
    activityToolId(activity),
    activityTitle(activity),
    activityArgumentsJson(activity)
  ].join("|");
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

  var key = activityTimelineKey(activity);
  var copy = cloneActivity(activity);
  copy.__timelineKey = key;

  state.liveAgentRun.forEach(function (item) {
    if (!item || item.__timelineKey === key || activityStatus(item) !== "running") {
      return;
    }
    if (item.Status !== undefined) {
      item.Status = "completed";
    } else {
      item.status = "completed";
    }
  });

  for (var i = state.liveAgentRun.length - 1; i >= 0; i -= 1) {
    if (state.liveAgentRun[i] && state.liveAgentRun[i].__timelineKey === key) {
      state.liveAgentRun[i] = copy;
      return;
    }
  }

  state.liveAgentRun.push(copy);
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
    status: counts.failed ? "failed" : (counts.running ? "running" : (counts.waiting ? "waiting" : (counts.cancelled ? "cancelled" : (counts.planned && counts.planned === counts.total ? "planned" : "completed"))))
  };
}

function isAgentRunStart(message) {
  return messageRole(message) === "assistant" && activityKind(messageActivity(message)) === "plan";
}

function isAgentRunContinuation(message) {
  var kind = activityKind(messageActivity(message));
  return messageRole(message) === "assistant" && (kind === "tool" || kind === "retry");
}

function isAgentRunFinalMessage(message) {
  return messageRole(message) === "assistant"
    && !messageActivity(message)
    && !message.Pending
    && !message.Failed
    && !message.Local
    && !!messageContent(message).trim();
}

function collectAgentRun(startIndex) {
  var items = [{ message: state.messages[startIndex], index: startIndex, activity: messageActivity(state.messages[startIndex]) }];
  var index = startIndex + 1;
  while (index < state.messages.length && isAgentRunContinuation(state.messages[index])) {
    items.push({ message: state.messages[index], index: index, activity: messageActivity(state.messages[index]) });
    index += 1;
  }

  var finalMessage = null;
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
