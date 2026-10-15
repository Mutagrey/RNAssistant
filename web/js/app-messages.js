var CHAT_BOTTOM_THRESHOLD = 64;
var renderedMessagesChatId = null;
var renderedMessageUnits = {};
var renderedLiveRunBase = null;
var renderedLiveRunSuppressed = false;
var renderedLiveRunBaseSignature = null;
var renderedLiveRunPastActivities = {};
var renderedLiveRunPastSteps = {};
var chatScrollFollowing = true;
var chatScrollInteraction = 0;
var chatScrollPosition = 0;

function resetRenderedMessageUnits(box) {
  if (typeof resetMessageMediaThumbnails === "function") resetMessageMediaThumbnails();
  if (box && typeof clearMarkdownEnhancements === "function") clearMarkdownEnhancements(box);
  renderedMessageUnits = {};
  renderedLiveRunBase = null;
  renderedLiveRunBaseSignature = null;
  renderedLiveRunSuppressed = false;
  renderedLiveRunPastActivities = {};
  renderedLiveRunPastSteps = {};
  if (box) box.textContent = "";
}

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

function shouldFollowChatScroll(box) {
  var atBottom = chatDistanceFromBottom(box) <= 2;
  // Layout growth alone is not a scroll gesture; shrinking content or a taller
  // viewport can also clamp scrollTop upward while the reader is still at the end.
  if (box.scrollTop > chatScrollPosition) chatScrollFollowing = atBottom;
  else if (box.scrollTop < chatScrollPosition && !atBottom) chatScrollFollowing = false;
  return chatScrollFollowing;
}

function updateChatScrollButton() {
  var box = $("messages");
  var button = $("chatScrollBottomButton");
  if (!box || !button) {
    return;
  }

  var canScroll = box.scrollHeight > box.clientHeight + 2;
  var visible = canScroll && (!isChatNearBottom(box) || !chatScrollFollowing && chatDistanceFromBottom(box) > 2);
  button.classList.toggle("is-visible", visible);
  button.setAttribute("aria-hidden", visible ? "false" : "true");
  button.tabIndex = visible ? 0 : -1;
}

function scrollMessagesToBottom(smooth) {
  var box = $("messages");
  if (!box) {
    return;
  }

  chatScrollFollowing = true;
  if (smooth && typeof box.scrollTo === "function" &&
      !(window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches)) {
    box.scrollTo({ top: box.scrollHeight, behavior: "smooth" });
  } else {
    box.scrollTop = box.scrollHeight;
  }
  chatScrollPosition = box.scrollTop;
  updateChatScrollButton();
}

function syncChatScroll(shouldScroll, smooth) {
  if (shouldScroll) {
    scrollMessagesToBottom(smooth);
  } else {
    updateChatScrollButton();
  }

  var interaction = chatScrollInteraction, chatId = state.activeChatId;
  var top = $("messages").scrollTop;
  if (shouldScroll && window.requestAnimationFrame) {
    window.requestAnimationFrame(function () {
      if (interaction === chatScrollInteraction && chatId === state.activeChatId &&
          chatScrollFollowing && $("messages").scrollTop === top) scrollMessagesToBottom(false);
    });
  }
}

function bindMessageScrollControls() {
  var box = $("messages");
  var button = $("chatScrollBottomButton");
  if (box) {
    box.addEventListener("scroll", function () {
      if (box.scrollTop !== chatScrollPosition) {
        chatScrollInteraction += 1;
        shouldFollowChatScroll(box);
        chatScrollPosition = box.scrollTop;
      }
      updateChatScrollButton();
    }, { passive: true });
    function interrupt(upward) {
      chatScrollInteraction += 1;
      if (upward) chatScrollFollowing = false;
    }
    box.addEventListener("wheel", function (event) { interrupt(event.deltaY < 0); }, { passive: true });
    box.addEventListener("pointerdown", function () { interrupt(false); }, { passive: true });
    var touchY = 0;
    box.addEventListener("touchstart", function (event) { touchY = event.touches[0].clientY; interrupt(false); }, { passive: true });
    box.addEventListener("touchmove", function (event) {
      var next = event.touches[0].clientY; interrupt(next > touchY); touchY = next;
    }, { passive: true });
    box.addEventListener("keydown", function (event) {
      if (/^(INPUT|TEXTAREA|SELECT)$/.test(event.target.tagName) || event.target.isContentEditable) return;
      if (["ArrowUp", "PageUp", "Home", "ArrowDown", "PageDown", "End", " "].indexOf(event.key) >= 0)
        interrupt(event.key === "ArrowUp" || event.key === "PageUp" || event.key === "Home" || event.key === " " && event.shiftKey);
    });
    if (window.ResizeObserver) {
      var resizeObserver = new window.ResizeObserver(function () { syncChatScroll(shouldFollowChatScroll(box), false); });
      resizeObserver.observe(box);
      Array.prototype.forEach.call(box.children, function (node) { resizeObserver.observe(node, { box: "border-box" }); });
      // The scroll box keeps its size when images/viewers change message height.
      // Track direct message nodes, releasing detached ones after reconciliation.
      var contentObserver = new window.MutationObserver(function (records) {
        records.forEach(function (record) {
          Array.prototype.forEach.call(record.removedNodes, function (node) {
            if (node.nodeType === 1 && node.parentNode !== box) resizeObserver.unobserve(node);
          });
          Array.prototype.forEach.call(record.addedNodes, function (node) {
            if (node.nodeType === 1 && node.parentNode === box) resizeObserver.observe(node, { box: "border-box" });
          });
        });
      });
      contentObserver.observe(box, { childList: true });
    }
  }
  if (button) {
    button.addEventListener("click", function () {
      scrollMessagesToBottom(false);
    });
  }
  updateChatScrollButton();
}

function captureMessageViewport(box) {
  var snapshot = { top: box.scrollTop };
  if (!box.getBoundingClientRect) return snapshot;
  var top = box.getBoundingClientRect().top;
  Array.prototype.some.call(box.children, function (node) {
    if (node.getBoundingClientRect().bottom <= top) return false;
    snapshot.key = node.messageUnitKey;
    snapshot.offset = node.getBoundingClientRect().top - top;
    return true;
  });
  return snapshot;
}

function restoreMessageViewport(box, snapshot) {
  var unit = snapshot.key && renderedMessageUnits[snapshot.key];
  if (unit && unit.node && unit.node.getBoundingClientRect) {
    box.scrollTop += unit.node.getBoundingClientRect().top - box.getBoundingClientRect().top - snapshot.offset;
  } else box.scrollTop = snapshot.top;
  chatScrollPosition = box.scrollTop;
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
  title.textContent = state.bridgeUnavailable ? "Откройте панель из Office" :
    "Готов к работе с документом";
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
  // Run articles already show the outcome with its cause or question in the body.
  if (runViewState && !node.classList.contains("agent-run") &&
      runViewState.lifecycle !== "completed" && runViewState.lifecycle !== "running") {
    var outcome = document.createElement("span");
    outcome.className = "message-outcome status-" +
      (runViewState.executionHealth === "unknown" && runViewState.lifecycle === "failed"
        ? "warning" : window.RNAssistantRunViewState.displayStatus(runViewState, runViewState.lifecycle));
    outcome.textContent = conversationOutcomeLabel(runViewState);
    if (outcome.textContent) {
      meta.appendChild(outcome);
      footer.classList.add("has-status");
    }
  } else if (!runViewState && messageRole(message) === "assistant" &&
      !messageProtocolMessage(message) && !activity && !message.Pending && !message.Failed && !message.Local &&
      messageContent(message).trim()) {
    var incompatibleOutcome = document.createElement("span");
    incompatibleOutcome.className = "message-outcome status-unknown";
    incompatibleOutcome.textContent = "Нет typed runtime state · требуется новый запуск";
    meta.appendChild(incompatibleOutcome);
    footer.classList.add("has-status");
  }
  if (message.Failed) footer.classList.add("has-status");

  var actions = document.createElement("div");
  actions.className = "message-actions";
  var historyActionsBlocked = !!currentActiveSend() || hasActiveMessageEdit() ||
    (typeof pendingAgentApprovalActivity === "function" && !!pendingAgentApprovalActivity());
  actions.appendChild(smallIconButton("Копировать сообщение", "copy", function () {
    copyText(activity ? activityText(activity) : messageContent(message));
    log("Сообщение скопировано.");
  }));
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

  if (!historyActionsBlocked) {
    actions.appendChild(smallIconButton("Удалить сообщение", "trash", function () {
      deleteMessage(message, index);
    }));
  }

  var assistant = messageRole(message) === "assistant";
  if (assistant) footer.appendChild(actions);
  if (meta.childNodes.length) footer.appendChild(meta);
  if (!assistant) footer.appendChild(actions);
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
  body.innerHTML = markdown(content, { preserveLineBreaks: messageRole(message) === "user" });
  node.appendChild(body);
  appendMessageFooter(node, message, index, null);

  enhanceMarkdown(body, { enableJsonViewer: true, sourceText: content });

  return node;
}

function liveAgentActivities() {
  return state.liveAgentRun && state.liveAgentRun.length
    ? state.liveAgentRun : (state.liveActivity ? [state.liveActivity] : []);
}

function liveAgentTailActivities() {
  return liveAgentActivities().filter(function (activity) {
    return !renderedLiveRunPastActivities[activityTimelineKey(activity)] &&
      !(activityKind(activity) === "step" && renderedLiveRunPastSteps[activityStepId(activity)]);
  });
}

function liveAgentRunId() {
  var tracked = state.chatRuns && state.chatRuns[state.activeChatId];
  if (tracked && tracked.runId) return tracked.runId;
  var activities = liveAgentActivities();
  for (var i = activities.length - 1; i >= 0; i -= 1) {
    var id = activities[i].RunId || activities[i].runId;
    if (id) return id;
  }
  return "";
}

function renderLiveAgentRun() {
  var activities = liveAgentTailActivities();
  if ((!activities.length && !renderedLiveRunBase && !hasLiveAgentOutput()) || renderedLiveRunSuppressed) return null;
  var items = renderedLiveRunBase ? renderedLiveRunBase.items.slice() : [];
  var byKey = Object.create(null);
  items.forEach(function (item, index) { byKey[activityTimelineKey(item.activity)] = index; });
  activities.forEach(function (activity) {
    var key = activityTimelineKey(activity);
    var index = byKey[key];
    if (index !== undefined) {
      // Durable terminal results win over a stale live running snapshot.
      if (isActiveTimelineStatus(activityStatus(items[index].activity))) {
        items[index] = Object.assign({}, items[index], { activity: activity });
      }
    } else {
      byKey[key] = items.length;
      items.push({ message: null, index: -1, activity: activity });
    }
  });
  var node = renderAgentRunArticle({ live: true, items: items, finalMessage: null, hasLiveDraft: hasLiveAgentDraft() });
  node.liveOutputVisible = hasLiveAgentOutput();
  node.liveDraftVisible = hasLiveAgentDraft();
  node.liveRunId = liveAgentRunId();
  if (renderedLiveRunBase && typeof appendAgentRunResourceCards === "function") {
    appendAgentRunResourceCards(node.querySelector(".agent-run-wrap"), renderedLiveRunBase.items, null);
  }
  return node;
}

function hasLiveAgentOutput() {
  return !!(state.liveReasoning || hasLiveAgentDraft());
}

function hasLiveAgentDraft() {
  var run = state.chatRuns && state.chatRuns[state.activeChatId];
  return !!(state.liveStreamContent || run && run.streamRetrying);
}

// Keep streamed output inside its run and update only this leaf on token paints.
function refreshLiveAgentOutput(node) {
  if (!hasLiveAgentOutput()) return;
  var section = node.querySelector(".agent-stream-step");
  if (!section) {
    section = document.createElement("section");
    section.className = "agent-model-step agent-stream-step";
    node.querySelector(".agent-run-actions").appendChild(section);
  }
  var previous = section.liveOutput || {};
  var content = state.liveStreamContent || "";
  var reasoning = state.liveReasoning || "";
  var reasoningComplete = !!state.liveReasoningComplete;
  if (previous.reasoning !== reasoning || previous.reasoningComplete !== reasoningComplete) {
    var oldReasoning = section.querySelector(".reasoning-block");
    if (reasoning && typeof reasoningBlock === "function") {
      if (oldReasoning) {
        updateReasoningBlock(oldReasoning, reasoning, null, !reasoningComplete, reasoning.length >= MAX_LIVE_REASONING_CHARS);
      } else {
        var block = reasoningBlock(reasoning, null, !reasoningComplete, reasoning.length >= MAX_LIVE_REASONING_CHARS);
        block.setAttribute("data-disclosure-key", "live-reasoning");
        section.insertBefore(block, section.firstChild);
      }
    } else if (oldReasoning) section.removeChild(oldReasoning);
  }
  var status = section.querySelector(".agent-stream-status");
  if (!status) {
    status = document.createElement("div");
    status.className = "agent-stream-status";
    status.setAttribute("role", "status");
    section.appendChild(status);
  }
  if (previous.content !== content) {
    var body = section.querySelector(".agent-stream-message");
    if (body && typeof clearMarkdownEnhancements === "function") clearMarkdownEnhancements(body);
    if (content) {
      if (!body) {
        body = document.createElement("div");
        body.className = "agent-step-message markdown agent-stream-message";
        section.insertBefore(body, status);
      }
      body.innerHTML = markdown(content);
      enhanceMarkdown(body, { enableJsonViewer: true, sourceText: content, streaming: true });
    } else if (body) section.removeChild(body);
  }
  var run = state.chatRuns && state.chatRuns[state.activeChatId];
  var label = run && run.streamRetrying ? "Повторная попытка…" : content ? "Формируется…" : "";
  if (status.hidden !== !label) status.hidden = !label;
  if (status.textContent !== label) status.textContent = label;
  section.liveOutput = { content: content, reasoning: reasoning, reasoningComplete: reasoningComplete };
}

function messageUnitSignature(message) {
  var activity = messageActivity(message);
  return JSON.stringify({
    id: messageId(message),
    role: messageRole(message),
    runId: typeof messageRunId === "function" ? messageRunId(message) : "",
    content: messageContent(message),
    pending: !!message.Pending,
    failed: !!message.Failed,
    local: !!message.Local,
    activity: activity || null,
    runViewState: message.RunViewState || message.runViewState || null,
    attachments: messageAttachments(message),
    artifacts: message.Artifacts || message.artifacts || null,
    reasoning: {
      content: message.ReasoningContent || message.reasoningContent || "",
      tokens: message.ReasoningTokens || message.reasoningTokens || null,
      truncated: !!(message.ReasoningTruncated || message.reasoningTruncated)
    },
    usage: {
      total: messageTotalTokens(message),
      prompt: messagePromptTokens(message),
      completion: messageCompletionTokens(message)
    }
  });
}

function agentRunUnitKey(run) {
  var items = run.items || [];
  var finalMessage = run.finalMessage || null;
  if (run.live) return "live:agent-run";
  var runId = typeof agentRunId === "function" ? agentRunId(items, finalMessage) : "";
  var first = items[0] || finalMessage;
  if (runId) return "run:" + runId + ":segment:" + (messageId(first && first.message) || (first ? first.index : 0));
  return "run:index:" + (first ? first.index : 0);
}

function agentRunUnitSignature(run) {
  return JSON.stringify({
    live: !!run.live,
    continued: !!run.continued,
    items: (run.items || []).map(function (item) {
      return { index: item.index, signature: messageUnitSignature(item.message) };
    }),
    final: run.finalMessage ? {
      index: run.finalMessage.index,
      signature: messageUnitSignature(run.finalMessage.message)
    } : null
  });
}

function appendMessageUnit(units, key, signature, render, actionsSignature, refreshActions, update) {
  units.push({ key: key, signature: signature, render: render,
    actionsSignature: actionsSignature, refreshActions: refreshActions, update: update });
}

function messageActionsSignature() {
  return JSON.stringify({
    sending: !!currentActiveSend(),
    editing: hasActiveMessageEdit(),
    approval: typeof pendingAgentApprovalActivity === "function" && !!pendingAgentApprovalActivity(),
    bridgeUnavailable: !!state.bridgeUnavailable
  });
}

function refreshMessageFooter(node, append) {
  var footer = Array.prototype.find.call(node.children, function (child) {
    return child.classList.contains("message-footer");
  });
  if (!footer) return;
  node.removeChild(footer);
  append(node);
}

function buildMessageUnits() {
  var units = [];
  if (state.messageStartIndex > 0) {
    appendMessageUnit(units, "older-messages", String(state.messageStartIndex), function () {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "chat-history-more";
      button.textContent = "Показать предыдущие сообщения";
      button.addEventListener("click", loadPreviousChatMessages);
      return button;
    }, "", null);
  }
  var actionsSignature = messageActionsSignature();
  var liveRunId = liveAgentRunId();
  renderedLiveRunBase = null;
  renderedLiveRunBaseSignature = null;
  renderedLiveRunSuppressed = false;
  renderedLiveRunPastActivities = {};
  renderedLiveRunPastSteps = {};
  var lastVisibleIndex = state.messages.length - 1;
  while (lastVisibleIndex >= 0 && messageProtocolMessage(state.messages[lastVisibleIndex])) lastVisibleIndex -= 1;
  var lastRunIndexes = {};
  state.messages.forEach(function (message, index) { if (messageRunId(message)) lastRunIndexes[messageRunId(message)] = index; });
  for (var index = 0; index < state.messages.length; index += 1) {
    if (messageProtocolMessage(state.messages[index])) {
      continue;
    }
    if (canCollectAgentRunAt(index)) {
      var run = collectAgentRun(index);
      run.continued = !run.finalMessage && lastRunIndexes[agentRunId(run.items, run.finalMessage)] >= run.nextIndex;
      if (liveRunId && agentRunId(run.items, run.finalMessage) === liveRunId) {
        var savedView = agentRunViewState(run.items, run.finalMessage);
        var terminalRun = savedView && ["completed", "failed", "cancelled"].indexOf(savedView.lifecycle) >= 0;
        if (!run.finalMessage && !terminalRun && run.nextIndex > lastVisibleIndex) {
          renderedLiveRunBase = run;
          renderedLiveRunBaseSignature = agentRunUnitSignature(run);
          index = run.nextIndex - 1;
          continue;
        }
        if (run.finalMessage || terminalRun) renderedLiveRunSuppressed = true;
        else run.items.forEach(function (item) {
          renderedLiveRunPastActivities[activityTimelineKey(item.activity)] = true;
          var stepId = activityStepId(item.activity);
          if (stepId) renderedLiveRunPastSteps[stepId] = true;
        });
      }
      appendMessageUnit(units, agentRunUnitKey(run), agentRunUnitSignature(run), (function (capturedRun) {
        return function () { return renderAgentRunArticle(capturedRun); };
      }(run)), actionsSignature, (function (capturedRun) {
        return function (node) {
          refreshMessageFooter(node, function (target) {
            if (!capturedRun.items.length && capturedRun.finalMessage) {
              appendMessageFooter(target, capturedRun.finalMessage.message, capturedRun.finalMessage.index, null);
            } else {
              appendAgentRunFooter(target, capturedRun.items, capturedRun.finalMessage);
            }
          });
        };
      }(run)));
      index = run.nextIndex - 1;
    } else {
      appendMessageUnit(units, "message:" + (messageId(state.messages[index]) || index),
        messageUnitSignature(state.messages[index]), (function (message, messageIndex) {
          return function () { return renderMessageArticle(message, messageIndex); };
        }(state.messages[index], index)), actionsSignature + ":" + index, (function (message, messageIndex) {
          return function (node) {
            refreshMessageFooter(node, function (target) {
              appendMessageFooter(target, message, messageIndex, messageActivity(message));
            });
          };
        }(state.messages[index], index)));
    }
  }

  if (state.messageStartIndex + state.messages.length < state.messageTotalCount) {
    appendMessageUnit(units, "latest-messages", String(state.messageTotalCount), function () {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "chat-history-more";
      button.textContent = "Вернуться к последним сообщениям";
      button.addEventListener("click", jumpToLatestChatMessages);
      return button;
    }, "", null);
  }

  return units.concat(buildLiveMessageUnits());
}

function buildLiveMessageUnits() {
  var units = [];
  var activities = liveAgentTailActivities();
  if (!renderedLiveRunSuppressed && (activities.length || renderedLiveRunBase || hasLiveAgentOutput())) {
    appendMessageUnit(units, "live:agent-run",
      JSON.stringify({ stream: "agent", runId: liveAgentRunId(), value: activities,
        base: renderedLiveRunBaseSignature, hasOutput: hasLiveAgentOutput(), hasDraft: hasLiveAgentDraft() }),
      function () { return renderLiveAgentRun(); }, "", null, refreshLiveAgentOutput);
  }

  return units.concat(typeof buildPendingInputUnits === "function" ? buildPendingInputUnits() : []);
}

function messageDisclosureSnapshot(node) {
  var result = Object.create(null);
  if (!node || !node.querySelectorAll) return result;
  Array.prototype.forEach.call(node.querySelectorAll("details"), function (details, index) {
    var key = details.getAttribute("data-disclosure-key") || details.className + ":" + index;
    result[key] = details.open;
  });
  return result;
}

function restoreMessageDisclosures(node, snapshot) {
  if (!node || !node.querySelectorAll) return;
  Array.prototype.forEach.call(node.querySelectorAll("details"), function (details, index) {
    var key = details.getAttribute("data-disclosure-key") || details.className + ":" + index;
    if (Object.prototype.hasOwnProperty.call(snapshot, key)) details.open = snapshot[key];
  });
}

function reconcileMessageUnits(box, units, liveOnly) {
  var nextCache = {};
  if (liveOnly) Object.keys(renderedMessageUnits).forEach(function (key) {
    if (key.indexOf("live:") !== 0) nextCache[key] = renderedMessageUnits[key];
  });
  units.forEach(function (unit) {
    var cached = renderedMessageUnits[unit.key];
    var node = cached && cached.signature === unit.signature ? cached.node : null;
    if (node && cached.actionsSignature !== unit.actionsSignature && unit.refreshActions) {
      unit.refreshActions(node);
    }
    if (node && unit.update) unit.update(node);
    if (!node) {
      var disclosures = messageDisclosureSnapshot(cached && cached.node);
      if (cached && cached.node && typeof releaseMessageMediaThumbnails === "function") {
        releaseMessageMediaThumbnails(cached.node);
      }
      if (cached && cached.node && typeof clearMarkdownEnhancements === "function") {
        clearMarkdownEnhancements(cached.node);
      }
      node = unit.render();
      if (unit.update) unit.update(node);
      restoreMessageDisclosures(node, disclosures);
      node.messageUnitKey = unit.key;
      // Insert first: removing a tall live article first temporarily clamps scrollTop.
      if (cached && cached.node && cached.node.parentNode === box) {
        box.insertBefore(node, cached.node);
        box.removeChild(cached.node);
      }
    }
    nextCache[unit.key] = { signature: unit.signature, actionsSignature: unit.actionsSignature, node: node };
  });
  Object.keys(renderedMessageUnits).forEach(function (key) {
    if (!nextCache[key] && renderedMessageUnits[key].node) {
      var removed = renderedMessageUnits[key].node;
      if (typeof releaseMessageMediaThumbnails === "function") releaseMessageMediaThumbnails(removed);
      if (typeof clearMarkdownEnhancements === "function") clearMarkdownEnhancements(removed);
      if (removed.parentNode === box) box.removeChild(removed);
    }
  });

  // Keep unchanged nodes attached: detaching the transcript restarts embedded
  // viewers and invalidates layout/selection even when all signatures match.
  var cursor = box.firstChild;
  if (liveOnly) {
    cursor = null;
    Object.keys(nextCache).some(function (key) {
      var node = nextCache[key].node;
      if (key.indexOf("live:") === 0 && node && node.parentNode === box) {
        cursor = node;
        return true;
      }
      return false;
    });
  }
  units.forEach(function (unit) {
    var node = nextCache[unit.key].node;
    var previous = renderedMessageUnits[unit.key];
    if (previous && previous.node !== node && previous.node.parentNode === box) {
      if (cursor === previous.node) cursor = previous.node.nextSibling;
      box.removeChild(previous.node);
    }
    if (node !== cursor) box.insertBefore(node, cursor);
    else cursor = cursor.nextSibling;
  });
  renderedMessageUnits = nextCache;
}

function scheduleLiveStreamRender(fullProjection) {
  if (fullProjection) state.liveFullRenderPending = true;
  if (state.liveStreamRenderPending) {
    return;
  }
  state.liveStreamRenderPending = true;
  var render = function () {
    state.liveStreamRenderPending = false;
    var full = state.liveFullRenderPending;
    state.liveFullRenderPending = false;
    if (full) renderMessages();
    else renderStreamingMessages(true);
  };
  if (window.requestAnimationFrame) {
    window.requestAnimationFrame(render);
  } else {
    window.setTimeout(render, 16);
  }
}

function renderStreamingMessages(outputOnly) {
  if (typeof isPanelActive === "function" && !isPanelActive("chat")) return;
  var live = renderedMessageUnits["live:agent-run"];
  if (renderedMessagesChatId !== state.activeChatId || !live && !Object.keys(renderedMessageUnits).length) {
    renderMessages();
    return;
  }
  if (outputOnly && renderedLiveRunSuppressed) return;
  var box = $("messages");
  var shouldScroll = shouldFollowChatScroll(box);
  var viewport = !shouldScroll ? captureMessageViewport(box) : null;
  if (outputOnly && live && live.node.liveOutputVisible && hasLiveAgentOutput() &&
      live.node.liveDraftVisible === hasLiveAgentDraft() && live.node.liveRunId === liveAgentRunId()) {
    // Token frames do not regroup/serialize activities, rebuild units or detach history.
    refreshLiveAgentOutput(live.node);
  } else {
    reconcileMessageUnits(box, buildLiveMessageUnits(), true);
  }
  if (!shouldScroll) restoreMessageViewport(box, viewport);
  syncChatScroll(shouldScroll, false);
}

function renderMessages(options) {
  if (typeof isPanelActive === "function" && !isPanelActive("chat")) return;
  options = options || {};
  if (typeof renderChatResourceNavigation === "function") renderChatResourceNavigation();
  var box = $("messages");
  var chatChanged = renderedMessagesChatId !== state.activeChatId;
  var viewport = captureMessageViewport(box);
  if (chatChanged || options.forceScroll) chatScrollFollowing = true;
  var shouldScroll = !options.preserveTop && (!!options.forceScroll || chatChanged || shouldFollowChatScroll(box));

  renderedMessagesChatId = state.activeChatId;
  if (chatChanged || options.fullReset) resetRenderedMessageUnits(box);
  if (chatChanged) chatScrollPosition = box.scrollTop;
  if (state.chatDetailPending || state.chatDetailError) {
    resetRenderedMessageUnits(box);
    var notice = document.createElement("div");
    notice.className = "chat-history-more";
    notice.textContent = state.chatDetailPending ? "Загрузка чата…" : state.chatDetailError;
    box.appendChild(notice);
    if (state.chatDetailError && typeof retryChatDetail === "function") {
      var retry = document.createElement("button");
      retry.type = "button";
      retry.className = "chat-history-more";
      retry.textContent = "Повторить загрузку";
      retry.addEventListener("click", retryChatDetail);
      box.appendChild(retry);
    }
    return;
  }
  var visibleMessages = (state.messages || []).filter(function (message) { return !messageProtocolMessage(message); });
  if (!(typeof pendingChatInputs === "function" && pendingChatInputs().length) && !visibleMessages.length && !state.messageStartIndex && !hasLiveAgentOutput() && !state.liveActivity && !(state.liveAgentRun && state.liveAgentRun.length)) {
    resetRenderedMessageUnits(box);
    box.appendChild(renderChatEmptyState());
    chatScrollFollowing = true;
    chatScrollPosition = box.scrollTop;
    renderAgentPlanDock();
    renderAgentApprovalDock();
    syncChatScroll(false, false);
    return;
  }

  if (!Object.keys(renderedMessageUnits).length) box.textContent = "";
  reconcileMessageUnits(box, buildMessageUnits());

  renderAgentPlanDock();
  renderAgentApprovalDock();
  if (options.preserveTop && !viewport.key) {
    box.scrollTop += box.scrollHeight - options.previousScrollHeight;
    chatScrollPosition = box.scrollTop;
  } else if (!shouldScroll) restoreMessageViewport(box, viewport);
  syncChatScroll(shouldScroll, false);
}
