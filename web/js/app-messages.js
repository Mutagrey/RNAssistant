var CHAT_BOTTOM_THRESHOLD = 64;
var renderedMessagesChatId = null;

function messageVisibleAttachments(message) {
  var represented = typeof messageImageAttachmentIds === "function"
    ? messageImageAttachmentIds(message)
    : {};
  return messageAttachments(message).filter(function (attachment) {
    var id = String(attachment && (attachment.id !== undefined ? attachment.id : attachment.Id) || "").toLowerCase();
    return !id || !represented[id];
  });
}

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
  var qualificationRun = typeof window.activeQualificationRun === "function"
    ? window.activeQualificationRun() : null;
  var empty = document.createElement("div");
  empty.className = "chat-empty";

  var mark = document.createElement("div");
  mark.className = "chat-empty-mark";
  mark.innerHTML = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M21 15a4 4 0 0 1-4 4H8l-5 3V7a4 4 0 0 1 4-4h10a4 4 0 0 1 4 4Z\"/><path d=\"M8 9h8\"/><path d=\"M8 13h5\"/></svg>";
  empty.appendChild(mark);

  var title = document.createElement("div");
  title.className = "chat-empty-title";
  title.textContent = state.bridgeUnavailable ? "Откройте панель из Office" :
    (qualificationRun ? "Qualification run · " + String(qualificationRun.status || qualificationRun.Status || "") :
      "Готов к работе с документом");
  empty.appendChild(title);

  var text = document.createElement("div");
  text.className = "chat-empty-text";
  text.textContent = state.bridgeUnavailable
    ? "Статический UI загружен, но WebView bridge RNAssistant недоступен. Чаты, контекст и инструменты заработают внутри add-in."
    : (qualificationRun
      ? "Этот чат хранит только qualification events. Продолжите или изучите evidence во встроенном центре проверок."
      : "Выберите контекст или задайте вопрос по текущему Office-файлу.");
  empty.appendChild(text);

  if (state.bridgeUnavailable) {
    return empty;
  }

  if (!qualificationRun) {
    var suggestions = document.createElement("div");
    suggestions.className = "chat-empty-suggestions";
    suggestions.appendChild(promptSuggestionButton("Суммируй текущий документ"));
    suggestions.appendChild(promptSuggestionButton("Найди риски и слабые места"));
    suggestions.appendChild(promptSuggestionButton("Подготовь план правок"));
    empty.appendChild(suggestions);
  }

  var qualification = document.createElement("button");
  qualification.type = "button";
  qualification.className = "chat-empty-qualification";
  qualification.textContent = qualificationRun ? "Продолжить проверку" : "Проверить RNAssistant";
  qualification.addEventListener("click", function () {
    if (typeof window.openQualificationCenter === "function") window.openQualificationCenter();
  });
  empty.appendChild(qualification);

  return empty;
}

function messageSupportsEdit(message, index, activity) {
  return !activity && !currentActiveSend() && !hasActiveMessageEdit() && canEditMessage(message) && !isEditingMessage(message, index);
}

function appendMessageFooter(node, message, index, activity) {
  var footer = document.createElement("div");
  footer.className = "message-footer";

  var meta = document.createElement("div");
  meta.className = "message-footer-meta";

  var usage = messageUsageText(message);
  if (usage || message.Failed) {
    var usageNode = document.createElement("span");
    usageNode.className = "message-usage";
    usageNode.textContent = message.Failed ? "Не отправлено" : usage;
    meta.appendChild(usageNode);
  }
  var runViewState = messageRunViewState(message);
  if (runViewState && runViewState.lifecycle !== "completed" && runViewState.lifecycle !== "running") {
    var outcome = document.createElement("span");
    outcome.className = "message-outcome status-" +
      window.RNAssistantRunViewState.displayStatus(runViewState, runViewState.lifecycle);
    outcome.textContent = conversationOutcomeLabel(runViewState);
    if (outcome.textContent) meta.appendChild(outcome);
  } else if (!runViewState && messageRole(message) === "assistant" &&
      !messageProtocolMessage(message) && !activity && !message.Pending && !message.Failed && !message.Local &&
      messageContent(message).trim()) {
    var incompatibleOutcome = document.createElement("span");
    incompatibleOutcome.className = "message-outcome status-unknown";
    incompatibleOutcome.textContent = "Нет typed runtime state · требуется новый запуск";
    meta.appendChild(incompatibleOutcome);
  }

  var actions = document.createElement("div");
  actions.className = "message-actions";
  var historyActionsBlocked = !!currentActiveSend() || hasActiveMessageEdit() ||
    (typeof pendingAgentApprovalActivity === "function" && !!pendingAgentApprovalActivity());
  if (!historyActionsBlocked) {
    actions.appendChild(smallIconButton("Ответвить чат отсюда", "branch", function () {
      forkChatAtMessage(message, index);
    }));
  }
  if (messageSupportsEdit(message, index, activity)) {
    actions.appendChild(smallIconButton("Изменить сообщение", "edit", function () {
      startMessageEdit(message, index);
    }));
  }
  actions.appendChild(smallIconButton("Копировать сообщение", "copy", function () {
    copyText(activity ? activityText(activity) : messageContent(message));
    log("Сообщение скопировано.");
  }));
  if (!historyActionsBlocked) {
    actions.appendChild(smallIconButton("Удалить сообщение", "trash", function () {
      deleteMessage(message, index);
    }));
  }

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
    if (typeof appendMessageMediaGallery === "function") appendMessageMediaGallery(node, message);
    var attachments = messageVisibleAttachments(message);
    if (attachments.length) {
      var attachmentBox = document.createElement("div");
      attachmentBox.className = "message-attachments";
      attachments.forEach(function (attachment) {
        attachmentBox.appendChild(attachmentCard(attachment, false,
          message.Local ? (message.Pending ? "preparing" : "draft") : "committed"));
      });
      node.appendChild(attachmentBox);
    }
    appendMessageArtifactCards(node, message);
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

function renderCompactionArticle(message, activity) {
  var node = document.createElement("article");
  node.className = "message assistant is-compaction-message";

  var details = document.createElement("details");
  details.className = "context-compaction-divider";

  var summary = document.createElement("summary");
  summary.className = "context-compaction-summary";
  var leftLine = document.createElement("span");
  leftLine.className = "context-compaction-line";
  var rightLine = document.createElement("span");
  rightLine.className = "context-compaction-line";

  var label = document.createElement("span");
  label.className = "context-compaction-label";
  var icon = document.createElement("svg");
  icon.setAttribute("viewBox", "0 0 24 24");
  icon.setAttribute("aria-hidden", "true");
  icon.innerHTML = "<path d=\"M5 8h14M8 12h8M10 16h4\"/>";
  var title = document.createElement("span");
  title.className = "context-compaction-title";
  title.textContent = activityTitle(activity) || "Контекст сжат";
  var subtitle = document.createElement("span");
  subtitle.className = "context-compaction-subtitle";
  subtitle.textContent = activityValue(activity, "Subtitle", "subtitle", "") || "Ранняя история свернута";
  var caret = document.createElement("span");
  caret.className = "context-compaction-caret";
  caret.setAttribute("aria-hidden", "true");
  caret.textContent = "›";
  label.appendChild(icon);
  label.appendChild(title);
  label.appendChild(subtitle);
  label.appendChild(caret);
  summary.appendChild(leftLine);
  summary.appendChild(label);
  summary.appendChild(rightLine);
  details.appendChild(summary);

  var body = document.createElement("div");
  body.className = "context-compaction-body";
  var note = document.createElement("div");
  note.className = "context-compaction-note";
  note.textContent = "Исходная история сохранена; это резюме заменяет её раннюю часть только в активном контексте модели.";
  body.appendChild(note);
  var markdownBody = document.createElement("div");
  markdownBody.className = "markdown context-compaction-markdown";
  var compactionText = activityResultMessage(activity) || messageContent(message);
  markdownBody.innerHTML = markdown(compactionText);
  body.appendChild(markdownBody);
  details.appendChild(body);
  node.appendChild(details);
  enhanceMarkdown(markdownBody, { enableJsonViewer: true, sourceText: compactionText });
  return node;
}

function renderMessageArticle(message, index) {
  var node = document.createElement("article");
  node.className = "message " + messageRole(message) + (message.Pending ? " pending" : "") +
    (message.Failed ? " failed" : "");
  var activity = messageActivity(message);
  if (activity) {
    if (activityKind(activity) === "compaction" && activityStatus(activity) === "completed") {
      return renderCompactionArticle(message, activity);
    }
    var activityArticle = renderActivityArticle(message, index, activity, { live: false, current: false });
    if (typeof appendMessageReasoning === "function") {
      var activityReasoning = reasoningBlock(
        reasoningValue(message, "ReasoningContent", "reasoningContent", ""),
        reasoningValue(message, "ReasoningTokens", "reasoningTokens", null),
        false,
        !!reasoningValue(message, "ReasoningTruncated", "reasoningTruncated", false));
      if (activityReasoning) {
        var activityBody = activityArticle.querySelector(".agent-activity-wrap");
        activityArticle.insertBefore(activityReasoning, activityBody || activityArticle.firstChild);
      }
    }
    return activityArticle;
  }
  if (typeof appendMessageMediaGallery === "function") appendMessageMediaGallery(node, message);
  var attachments = messageVisibleAttachments(message);

  if (attachments.length) {
    var attachmentBox = document.createElement("div");
    attachmentBox.className = "message-attachments";
    attachments.forEach(function (attachment) {
      attachmentBox.appendChild(attachmentCard(attachment, false,
        message.Local ? (message.Pending ? "preparing" : "draft") : "committed"));
    });
    node.appendChild(attachmentBox);
  }
  appendMessageArtifactCards(node, message);

  if (typeof appendMessageReasoning === "function") appendMessageReasoning(node, message);

  var body = document.createElement("div");
  body.className = "markdown";
  var content = messageContent(message);
  body.innerHTML = markdown(content);
  node.appendChild(body);
  appendMessageFooter(node, message, index, null);

  enhanceMarkdown(body, { enableJsonViewer: true, sourceText: content });

  return node;
}

function renderLiveAgentRun() {
  var activities = state.liveAgentRun && state.liveAgentRun.length
    ? state.liveAgentRun
    : (state.liveActivity ? [state.liveActivity] : []);
  if (!activities.length) return null;
  return renderAgentRunArticle({
    live: true,
    items: activities.map(function (activity) {
      return { message: null, index: -1, activity: activity };
    }),
    finalMessage: null
  });
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
  cursor.style.setProperty("--streaming-dot-phase", -(Date.now() % 1200) + "ms");
  for (var dotIndex = 0; dotIndex < 3; dotIndex += 1) {
    var dot = document.createElement("span");
    dot.className = "streaming-cursor-dot";
    cursor.appendChild(dot);
  }
  body.appendChild(cursor);
  live.appendChild(body);
  enhanceMarkdown(body, { enableJsonViewer: true, sourceText: state.liveStreamContent, streaming: true });
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
  if (typeof resetMessageMediaThumbnails === "function") resetMessageMediaThumbnails();
  if (typeof renderChatResourceNavigation === "function") renderChatResourceNavigation();
  var box = $("messages");
  var chatChanged = renderedMessagesChatId !== state.activeChatId;
  var shouldScroll = !!options.forceScroll || chatChanged || isChatNearBottom(box);

  renderedMessagesChatId = state.activeChatId;
  if (typeof clearMarkdownEnhancements === "function") clearMarkdownEnhancements(box);
  box.innerHTML = "";
  var visibleMessages = (state.messages || []).filter(function (message) { return !messageProtocolMessage(message); });
  if (!visibleMessages.length && !state.liveStreamContent && !state.liveReasoning && !state.liveActivity && !(state.liveAgentRun && state.liveAgentRun.length)) {
    box.appendChild(renderChatEmptyState());
    renderAgentPlanDock();
    renderAgentApprovalDock();
    syncChatScroll(false, false);
    return;
  }

  for (var index = 0; index < state.messages.length; index += 1) {
    if (messageProtocolMessage(state.messages[index])) {
      continue;
    }
    if (canCollectAgentRunAt(index)) {
      var run = collectAgentRun(index);
      box.appendChild(renderAgentRunArticle(run));
      index = run.nextIndex - 1;
    } else {
      box.appendChild(renderMessageArticle(state.messages[index], index));
    }
  }

  var live = renderLiveAgentRun();
  if (live) {
    box.appendChild(live);
  }

  var liveReasoning = typeof renderLiveReasoningMessage === "function" ? renderLiveReasoningMessage() : null;
  if (liveReasoning) {
    box.appendChild(liveReasoning);
  }

  var stream = renderLiveStreamMessage();
  if (stream) {
    box.appendChild(stream);
  }

  renderAgentPlanDock();
  renderAgentApprovalDock();
  syncChatScroll(shouldScroll, false);
}
