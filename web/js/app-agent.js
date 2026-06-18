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
