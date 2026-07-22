var CHAT_BOTTOM_THRESHOLD = 64;
var renderedMessagesChatId = null;

function chatDistanceFromBottom(box) {
  if (!box) {
    return 0;
  }
  return Math.max(0, box.scrollHeight - box.scrollTop - box.clientHeight);
}

function isChatNearBottom(box) {
  return !box || chatDistanceFromBottom(box) <= CHAT_BOTTOM_THRESHOLD;
}

function updateChatScrollButton() {
  var box = $("messages");
  var button = $("chatScrollBottomButton");
  if (!box || !button) {
    return;
  }

  var canScroll = box.scrollHeight > box.clientHeight + CHAT_BOTTOM_THRESHOLD;
  var visible = canScroll && !isChatNearBottom(box);
  button.classList.toggle("is-visible", visible);
  button.setAttribute("aria-hidden", visible ? "false" : "true");
  button.tabIndex = visible ? 0 : -1;
}

function scrollMessagesToBottom(smooth) {
  var box = $("messages");
  if (!box) {
    return;
  }

  if (smooth && typeof box.scrollTo === "function") {
    box.scrollTo({ top: box.scrollHeight, behavior: "smooth" });
  } else {
    box.scrollTop = box.scrollHeight;
  }
  updateChatScrollButton();
}

function syncChatScroll(shouldScroll, smooth) {
  if (shouldScroll) {
    scrollMessagesToBottom(smooth);
  } else {
    updateChatScrollButton();
  }

  if (window.requestAnimationFrame) {
    window.requestAnimationFrame(function () {
      if (shouldScroll) {
        scrollMessagesToBottom(false);
      } else {
        updateChatScrollButton();
      }
    });
  }
}

function bindMessageScrollControls() {
  var box = $("messages");
  var button = $("chatScrollBottomButton");
  if (box) {
    box.addEventListener("scroll", updateChatScrollButton, { passive: true });
  }
  if (button) {
    button.addEventListener("click", function () {
      scrollMessagesToBottom(true);
    });
  }
  updateChatScrollButton();
}

function messageUsageText(message) {
  var total = messageTotalTokens(message);
  var prompt = messagePromptTokens(message);
  var completion = messageCompletionTokens(message);
  if (total === null && prompt === null && completion === null) {
    return "";
  }

  var parts = [];
  if (total !== null && total !== undefined) {
    parts.push(total + " токенов");
  }
  if (prompt !== null && prompt !== undefined) {
    parts.push("вход " + prompt);
  }
  if (completion !== null && completion !== undefined) {
    parts.push("ответ " + completion);
  }
  return parts.join(" · ");
}

function applyPromptSuggestion(text) {
  var input = $("chatInput");
  if (!input) {
    return;
  }

  setChatInputText(text, true);
  renderSendControls();
}

function promptSuggestionButton(text) {
  var button = document.createElement("button");
  button.type = "button";
  button.className = "chat-empty-suggestion";
  button.textContent = text;
  button.addEventListener("click", function () {
    applyPromptSuggestion(text);
  });
  return button;
}

function renderChatEmptyState() {
  var empty = document.createElement("div");
  empty.className = "chat-empty";

  var mark = document.createElement("div");
  mark.className = "chat-empty-mark";
  mark.innerHTML = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M21 15a4 4 0 0 1-4 4H8l-5 3V7a4 4 0 0 1 4-4h10a4 4 0 0 1 4 4Z\"/><path d=\"M8 9h8\"/><path d=\"M8 13h5\"/></svg>";
  empty.appendChild(mark);

  var title = document.createElement("div");
  title.className = "chat-empty-title";
  title.textContent = state.bridgeUnavailable ? "Откройте панель из Office" : "Готов к работе с документом";
  empty.appendChild(title);

  var text = document.createElement("div");
  text.className = "chat-empty-text";
  text.textContent = state.bridgeUnavailable
    ? "Статический UI загружен, но WebView bridge RNAssistant недоступен. Чаты, контекст и инструменты заработают внутри add-in."
    : "Выберите контекст или задайте вопрос по текущему Office-файлу.";
  empty.appendChild(text);

  if (state.bridgeUnavailable) {
    return empty;
  }

  var suggestions = document.createElement("div");
  suggestions.className = "chat-empty-suggestions";
  suggestions.appendChild(promptSuggestionButton("Суммируй текущий документ"));
  suggestions.appendChild(promptSuggestionButton("Найди риски и слабые места"));
  suggestions.appendChild(promptSuggestionButton("Подготовь план правок"));
  empty.appendChild(suggestions);

  return empty;
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
    usageNode.textContent = message.Failed ? "Не отправлено" : (message.Pending ? "Отправляю..." : usage);
    meta.appendChild(usageNode);
  }

  var actions = document.createElement("div");
  actions.className = "message-actions";
  actions.appendChild(smallIconButton("Ответвить чат отсюда", "branch", function () {
    forkChatAtMessage(message, index);
  }));
  actions.appendChild(smallIconButton("Копировать сообщение", "copy", function () {
    copyText(activity ? activityText(activity) : messageContent(message));
    log("Сообщение скопировано.");
  }));
  actions.appendChild(smallIconButton("Удалить сообщение", "trash", function () {
    deleteMessage(message, index);
  }));

  if (meta.childNodes.length) {
    footer.appendChild(meta);
  }
  footer.appendChild(actions);
  node.appendChild(footer);
}

function renderActivityArticle(message, index, activity, options) {
  options = options || {};
  var node = document.createElement("article");
  var classes = ["message", "assistant", "is-activity-message"];
  if (options.live) {
    classes.push("pending", "agent-live");
  } else {
    if (message && message.Pending) {
      classes.push("pending");
    }
    if (message && message.Failed) {
      classes.push("failed");
    }
  }
  if (options.current) {
    classes.push("is-current-activity");
  }
  node.className = classes.join(" ");

  if (message) {
    var attachments = messageAttachments(message);
    if (attachments.length) {
      var attachmentBox = document.createElement("div");
      attachmentBox.className = "message-attachments";
      attachments.forEach(function (attachment) {
        attachmentBox.appendChild(attachmentCard(attachment, false));
      });
      node.appendChild(attachmentBox);
    }
  }

  var body = document.createElement("div");
  body.className = "agent-activity-wrap";
  body.appendChild(renderActivityNode(activity, false, !!options.current, {
    messageId: message ? messageId(message) : "",
    index: index,
    message: message || null,
    currentActivity: options.current ? activity : null
  }));
  node.appendChild(body);

  if (message && !options.live) {
    appendMessageFooter(node, message, index, activity);
  }

  enhanceActivity(body);
  return node;
}

function renderMessageArticle(message, index) {
  var node = document.createElement("article");
  node.className = "message " + messageRole(message) + (message.Pending ? " pending" : "") + (message.Failed ? " failed" : "");
  var activity = messageActivity(message);
  if (activity) {
    return renderActivityArticle(message, index, activity, { live: false, current: false });
  }
  var attachments = messageAttachments(message);

  if (attachments.length) {
    var attachmentBox = document.createElement("div");
    attachmentBox.className = "message-attachments";
    attachments.forEach(function (attachment) {
      attachmentBox.appendChild(attachmentCard(attachment, false));
    });
    node.appendChild(attachmentBox);
  }

  var body = document.createElement("div");
  body.className = "markdown";
  body.innerHTML = markdown(messageContent(message));
  node.appendChild(body);
  appendMessageFooter(node, message, index, null);

  enhanceMarkdown(body);

  return node;
}

function getLiveActivityState() {
  var activities = state.liveAgentRun && state.liveAgentRun.length ? state.liveAgentRun : null;
  if (!activities && !state.liveActivity) {
    return null;
  }

  var current = activities ? activities[activities.length - 1] : state.liveActivity;
  var trail = [];
  if (activities && activities.length) {
    var currentIndex = Math.max(activities.length - 1, 0);
    trail = activities.slice(0, currentIndex);
  }

  return {
    trail: trail,
    current: current
  };
}

function renderLiveActivityTrail() {
  var liveState = getLiveActivityState();
  if (!liveState || !liveState.trail.length) {
    return [];
  }

  return liveState.trail.map(function (activity) {
    return renderActivityArticle(null, -1, activity, { live: true, current: false });
  });
}

function renderLiveActivity() {
  var liveState = getLiveActivityState();
  if (!liveState || !liveState.current) {
    return null;
  }

  return renderActivityArticle(null, -1, liveState.current, { live: true, current: true });
}

function renderLiveStreamMessage() {
  if (!state.liveStreamContent) {
    return null;
  }

  var live = document.createElement("article");
  live.className = "message assistant pending streaming-message";
  var body = document.createElement("div");
  body.className = "markdown";
  body.innerHTML = markdown(state.liveStreamContent);
  var cursor = document.createElement("span");
  cursor.className = "streaming-cursor";
  cursor.setAttribute("aria-hidden", "true");
  body.appendChild(cursor);
  live.appendChild(body);
  enhanceMarkdown(body);
  return live;
}

function scheduleLiveStreamRender() {
  if (state.liveStreamRenderPending) {
    return;
  }
  state.liveStreamRenderPending = true;
  var render = function () {
    state.liveStreamRenderPending = false;
    renderMessages();
  };
  if (window.requestAnimationFrame) {
    window.requestAnimationFrame(render);
  } else {
    window.setTimeout(render, 16);
  }
}

function renderMessages(options) {
  options = options || {};
  var box = $("messages");
  var chatChanged = renderedMessagesChatId !== state.activeChatId;
  var shouldScroll = !!options.forceScroll || chatChanged || isChatNearBottom(box);

  renderedMessagesChatId = state.activeChatId;
  box.innerHTML = "";
  if (!state.messages.length && !state.liveStreamContent && !state.liveActivity && !(state.liveAgentRun && state.liveAgentRun.length)) {
    box.appendChild(renderChatEmptyState());
    syncChatScroll(false, false);
    return;
  }

  for (var index = 0; index < state.messages.length; index += 1) {
    box.appendChild(renderMessageArticle(state.messages[index], index));
  }

  renderLiveActivityTrail().forEach(function (node) {
    box.appendChild(node);
  });

  var live = renderLiveActivity();
  if (live) {
    box.appendChild(live);
  }

  var stream = renderLiveStreamMessage();
  if (stream) {
    box.appendChild(stream);
  }

  syncChatScroll(shouldScroll, false);
}
