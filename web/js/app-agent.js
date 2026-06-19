function renderActivityNode(activity, nested, current) {
  var node = document.createElement("div");
  var status = activityStatus(activity);
  node.className = "agent-activity" + (nested ? " nested" : "") + (current ? " current" : "") + " status-" + status;

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
  var result = activityResultMessage(activity);
  metaParts.push(status);
  if (!result && subtitle && !toolId) {
    metaParts.push(subtitle);
  }
  if (result) {
    metaParts.push(result);
  }
  var meta = document.createElement("div");
  meta.className = "agent-activity-meta";
  meta.textContent = metaParts.join(" · ");
  text.appendChild(meta);
  appendActivityBadges(text, activity);
  row.appendChild(text);
  node.appendChild(row);

  appendActivityConfirmationPanel(node, activity);
  appendActivityErrorPanel(node, activity);
  appendActivityDetails(node, activity);
  return node;
}

function appendActivityBadges(parent, activity) {
  var badges = activityDataBadges(activity);
  if (!badges.length) {
    return;
  }

  var row = document.createElement("div");
  row.className = "agent-activity-badges";
  badges.forEach(function (badge) {
    var item = document.createElement("span");
    item.className = "agent-data-badge";
    item.textContent = badge;
    row.appendChild(item);
  });
  parent.appendChild(row);
}

function createAgentTextButton(label, className, onClick) {
  var button = document.createElement("button");
  button.type = "button";
  button.className = "agent-action-button " + (className || "secondary");
  button.textContent = label;
  button.addEventListener("click", onClick);
  return button;
}

function appendActivityConfirmationPanel(node, activity) {
  var pendingId = activityPendingId(activity);
  if (!pendingId || activityStatus(activity) !== "waiting") {
    return;
  }

  var panel = document.createElement("div");
  panel.className = "agent-confirm-panel";

  var reason = document.createElement("div");
  reason.className = "agent-confirm-reason";
  reason.textContent = activityResultMessage(activity) || "Tool waits for confirmation.";
  panel.appendChild(reason);

  var actions = document.createElement("div");
  actions.className = "agent-inline-actions";
  actions.appendChild(createAgentTextButton("Confirm", "primary", function () {
    confirmAgentTool(pendingId);
  }));
  actions.appendChild(createAgentTextButton("Cancel", "secondary", function () {
    cancelAgentTool(pendingId);
  }));
  panel.appendChild(actions);
  node.appendChild(panel);
}

function appendActivityErrorPanel(node, activity) {
  if (activityStatus(activity) !== "failed") {
    return;
  }

  var result = activityResultMessage(activity);
  var toolId = activityToolId(activity);
  var argumentsJson = activityArgumentsJson(activity);
  var panel = document.createElement("div");
  panel.className = "agent-error-panel";

  var reason = document.createElement("div");
  reason.className = "agent-error-reason";
  reason.textContent = result || "Step failed.";
  panel.appendChild(reason);

  var meta = document.createElement("div");
  meta.className = "agent-error-meta";
  meta.textContent = toolId ? ("Tool: " + toolId) : "Tool step";
  panel.appendChild(meta);

  var actions = document.createElement("div");
  actions.className = "agent-inline-actions";
  actions.appendChild(createAgentCopyButton("Copy diagnostics", [
    "Title: " + activityTitle(activity),
    "Tool: " + toolId,
    "Status: " + activityStatus(activity),
    "Reason: " + result,
    "Arguments:",
    prettyJsonText(argumentsJson)
  ].join("\n")));
  panel.appendChild(actions);
  node.appendChild(panel);
}

function appendActivityDetails(node, activity) {
  var children = activityChildren(activity);
  var argumentsJson = activityArgumentsJson(activity);
  var dataJson = activityDataJson(activity);
  if (!children.length && !argumentsJson && !dataJson) {
    return;
  }

  var details = document.createElement("details");
  details.className = "agent-activity-details";
  var summary = document.createElement("summary");
  summary.textContent = children.length ? "Nested steps and details" : "Details";
  details.appendChild(summary);

  if (children.length) {
    var childList = document.createElement("div");
    childList.className = "agent-activity-children";
    children.forEach(function (child) {
      childList.appendChild(renderActivityNode(child, true, false));
    });
    details.appendChild(childList);
  }

  appendArgumentsData(details, argumentsJson);
  appendActivityData(details, "Result data", dataJson, "Copy result");
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
  node.className = "message assistant agent-run status-" + stats.status + (run.live ? " live" : "");

  var body = document.createElement("div");
  body.className = "agent-run-wrap";

  var header = document.createElement("div");
  header.className = "agent-run-header";
  var title = document.createElement("div");
  title.className = "agent-run-title";
  title.textContent = "Agent run";
  var summary = document.createElement("div");
  summary.className = "agent-run-summary";
  summary.appendChild(title);
  if (stats.current) {
    var current = document.createElement("div");
    current.className = "agent-run-current";
    current.textContent = "Current: " + activityTitle(stats.current);
    summary.appendChild(current);
  }
  var meta = document.createElement("div");
  meta.className = "agent-run-meta";
  meta.textContent = stats.text;
  header.appendChild(summary);
  header.appendChild(meta);
  body.appendChild(header);

  var steps = document.createElement("div");
  steps.className = "agent-run-steps";
  items.forEach(function (item) {
    var isCurrent = stats.current && item.activity === stats.current;
    steps.appendChild(renderActivityNode(item.activity, false, isCurrent));
  });
  body.appendChild(steps);
  node.appendChild(body);

  if (!run.live) {
    appendAgentRunFooter(node, items);
  }
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
