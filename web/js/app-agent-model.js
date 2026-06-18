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
    title: progress.message || progress.Message || "Working...",
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

function agentRunStats(items) {
  var counts = { total: 0 };
  var runActivities = items.length > 1
    ? items.slice(1).map(function (item) { return item.activity; })
    : activityChildren(items[0].activity);

  runActivities.forEach(function (activity) {
    activityCountStatus(activity, counts);
  });

  var parts = [];
  if (counts.total) {
    parts.push(counts.total + " step" + (counts.total === 1 ? "" : "s"));
  }
  if (counts.completed) {
    parts.push(counts.completed + " completed");
  }
  if (counts.running) {
    parts.push(counts.running + " running");
  }
  if (counts.failed) {
    parts.push(counts.failed + " failed");
  }
  if (counts.waiting) {
    parts.push(counts.waiting + " waiting");
  }
  if (!parts.length) {
    parts.push("planned");
  }

  return {
    text: parts.join(" · "),
    status: counts.failed ? "failed" : (counts.running ? "running" : (counts.waiting ? "waiting" : "completed"))
  };
}

function isAgentRunStart(message) {
  return messageRole(message) === "assistant" && activityKind(messageActivity(message)) === "plan";
}

function isAgentRunContinuation(message) {
  var kind = activityKind(messageActivity(message));
  return messageRole(message) === "assistant" && (kind === "tool" || kind === "retry");
}

function collectAgentRun(startIndex) {
  var items = [{ message: state.messages[startIndex], index: startIndex, activity: messageActivity(state.messages[startIndex]) }];
  var index = startIndex + 1;
  while (index < state.messages.length && isAgentRunContinuation(state.messages[index])) {
    items.push({ message: state.messages[index], index: index, activity: messageActivity(state.messages[index]) });
    index += 1;
  }
  return { items: items, nextIndex: index };
}

function agentRunText(items) {
  return (items || []).map(function (item) {
    return activityText(item.activity);
  }).join("\n\n");
}
