function messageUsageText(message) {
  var total = messageTotalTokens(message);
  var prompt = messagePromptTokens(message);
  var completion = messageCompletionTokens(message);
  if (total === null && prompt === null && completion === null) {
    return "";
  }

  var parts = [];
  if (total !== null && total !== undefined) {
    parts.push(total + " tokens");
  }
  if (prompt !== null && prompt !== undefined) {
    parts.push("in " + prompt);
  }
  if (completion !== null && completion !== undefined) {
    parts.push("out " + completion);
  }
  return parts.join(" · ");
}

function appendMessageFooter(node, message, index, activity) {
  var footer = document.createElement("div");
  footer.className = "message-footer";

  var meta = document.createElement("div");
  meta.className = "message-footer-meta";

  var usage = messageUsageText(message);
  if (usage || message.Pending || message.Failed) {
    var usageNode = document.createElement("span");
    usageNode.className = "message-usage";
    usageNode.textContent = message.Failed ? "Not sent" : (message.Pending ? "Sending..." : usage);
    meta.appendChild(usageNode);
  }

  var actions = document.createElement("div");
  actions.className = "message-actions";
  actions.appendChild(smallIconButton("Fork from this message", "branch", function () {
    forkChatAtMessage(message, index);
  }));
  actions.appendChild(smallIconButton("Copy message", "copy", function () {
    copyText(activity ? activityText(activity) : messageContent(message));
    log("Message copied.");
  }));
  actions.appendChild(smallIconButton("Delete message", "trash", function () {
    deleteMessage(message, index);
  }));

  if (meta.childNodes.length) {
    footer.appendChild(meta);
  }
  footer.appendChild(actions);
  node.appendChild(footer);
}

function renderMessageArticle(message, index) {
  var node = document.createElement("article");
  node.className = "message " + messageRole(message) + (message.Pending ? " pending" : "") + (message.Failed ? " failed" : "");
  var activity = messageActivity(message);

  var body = document.createElement("div");
  if (activity) {
    body.className = "agent-activity-wrap";
    body.appendChild(renderActivityNode(activity, false));
  } else {
    body.className = "markdown";
    body.innerHTML = markdown(messageContent(message));
  }
  node.appendChild(body);
  appendMessageFooter(node, message, index, activity);

  if (activity) {
    enhanceActivity(body);
  } else {
    enhanceMarkdown(body);
  }

  return node;
}

function renderLiveActivity() {
  if (state.liveAgentRun && state.liveAgentRun.length) {
    return renderAgentRunArticle({
      live: true,
      items: state.liveAgentRun.map(function (activity) {
        return {
          message: { Role: "assistant", Content: "", Activity: activity },
          index: -1,
          activity: activity
        };
      })
    });
  }

  if (!state.liveActivity) {
    return null;
  }

  var live = document.createElement("article");
  live.className = "message assistant pending agent-live";
  var liveBody = document.createElement("div");
  liveBody.className = "agent-activity-wrap";
  liveBody.appendChild(renderActivityNode(state.liveActivity, false));
  live.appendChild(liveBody);
  enhanceActivity(liveBody);
  return live;
}

function renderMessages() {
  var box = $("messages");
  box.innerHTML = "";
  var index = 0;
  while (index < state.messages.length) {
    if (isAgentRunStart(state.messages[index])) {
      var run = collectAgentRun(index);
      box.appendChild(renderAgentRunArticle(run));
      index = run.nextIndex;
      continue;
    }

    box.appendChild(renderMessageArticle(state.messages[index], index));
    index += 1;
  }

  var live = renderLiveActivity();
  if (live) {
    box.appendChild(live);
  }

  box.scrollTop = box.scrollHeight;
}
