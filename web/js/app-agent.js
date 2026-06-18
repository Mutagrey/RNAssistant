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

function renderActivityNode(activity, nested) {
  var node = document.createElement("div");
  var status = activityStatus(activity);
  node.className = "agent-activity" + (nested ? " nested" : "") + " status-" + status;

  var row = document.createElement("div");
  row.className = "agent-activity-row";

  var mark = document.createElement("span");
  mark.className = "agent-activity-mark";
  mark.setAttribute("aria-hidden", "true");
  row.appendChild(mark);

  var text = document.createElement("div");
  text.className = "agent-activity-text";

  var title = document.createElement("div");
  title.className = "agent-activity-title";
  title.textContent = activityTitle(activity);
  text.appendChild(title);

  var metaParts = [];
  var subtitle = activityValue(activity, "Subtitle", "subtitle", "");
  var toolId = activityToolId(activity);
  var result = activityValue(activity, "ResultMessage", "resultMessage", "");
  metaParts.push(status);
  if (toolId) {
    metaParts.push(toolId);
  } else if (subtitle) {
    metaParts.push(subtitle);
  }
  if (result) {
    metaParts.push(result);
  }
  var meta = document.createElement("div");
  meta.className = "agent-activity-meta";
  meta.textContent = metaParts.join(" · ");
  text.appendChild(meta);
  row.appendChild(text);
  node.appendChild(row);

  appendActivityDetails(node, activity);
  return node;
}

function appendActivityDetails(node, activity) {
  var children = activityChildren(activity);
  var argumentsJson = activityValue(activity, "ArgumentsJson", "argumentsJson", "");
  var dataJson = activityValue(activity, "DataJson", "dataJson", "");
  if (!children.length && !argumentsJson && !dataJson) {
    return;
  }

  var details = document.createElement("details");
  details.className = "agent-activity-details";
  var summary = document.createElement("summary");
  summary.textContent = children.length ? "Details and nested steps" : "Details";
  details.appendChild(summary);

  if (children.length) {
    var childList = document.createElement("div");
    childList.className = "agent-activity-children";
    children.forEach(function (child) {
      childList.appendChild(renderActivityNode(child, true));
    });
    details.appendChild(childList);
  }

  appendActivityData(details, "Arguments", argumentsJson);
  appendActivityData(details, "Result data", dataJson);
  node.appendChild(details);
}

function enhanceActivity(root) {
  Array.prototype.slice.call(root.querySelectorAll("pre code")).forEach(function (code) {
    highlightCode(code);
  });
}

async function deleteAgentRun(items) {
  if (!items || !items.length || !window.confirm("Delete this agent run?")) {
    return;
  }

  for (var i = items.length - 1; i >= 0; i -= 1) {
    await deleteMessage(items[i].message, items[i].index);
  }
}

function renderAgentRunArticle(run) {
  var items = run.items || [];
  var stats = agentRunStats(items);
  var node = document.createElement("article");
  node.className = "message assistant agent-run status-" + stats.status;

  var body = document.createElement("div");
  body.className = "agent-run-wrap";

  var header = document.createElement("div");
  header.className = "agent-run-header";
  var title = document.createElement("div");
  title.className = "agent-run-title";
  title.textContent = "Agent run";
  var meta = document.createElement("div");
  meta.className = "agent-run-meta";
  meta.textContent = stats.text;
  header.appendChild(title);
  header.appendChild(meta);
  body.appendChild(header);

  var steps = document.createElement("div");
  steps.className = "agent-run-steps";
  items.forEach(function (item) {
    steps.appendChild(renderActivityNode(item.activity, false));
  });
  body.appendChild(steps);
  node.appendChild(body);

  appendAgentRunFooter(node, items);
  enhanceActivity(body);
  return node;
}

function appendAgentRunFooter(node, items) {
  var footer = document.createElement("div");
  footer.className = "message-footer";
  var footerMeta = document.createElement("div");
  footerMeta.className = "message-footer-meta";
  var role = document.createElement("span");
  role.className = "role";
  role.textContent = "assistant";
  footerMeta.appendChild(role);
  var count = document.createElement("span");
  count.className = "message-usage";
  count.textContent = items.length + " messages";
  footerMeta.appendChild(count);

  var actions = document.createElement("div");
  actions.className = "message-actions";
  var last = items[items.length - 1];
  actions.appendChild(smallIconButton("Fork from this run", "branch", function () {
    forkChatAtMessage(last.message, last.index);
  }));
  actions.appendChild(smallIconButton("Copy run", "copy", function () {
    copyText(agentRunText(items));
    log("Agent run copied.");
  }));
  actions.appendChild(smallIconButton("Delete run", "trash", function () {
    deleteAgentRun(items);
  }));

  footer.appendChild(footerMeta);
  footer.appendChild(actions);
  node.appendChild(footer);
}
