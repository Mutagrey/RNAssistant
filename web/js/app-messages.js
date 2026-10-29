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
var suspendedOfficeTranscript = null;
function officeTranscriptActive() {
  return !window.RNAssistantWorkspaceChat || window.RNAssistantWorkspaceChat.owner() === "office";
}

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
  if (!officeTranscriptActive()) return;
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
  if (!officeTranscriptActive()) return;
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
      if (!officeTranscriptActive()) return;
      if (box.scrollTop !== chatScrollPosition) {
        chatScrollInteraction += 1;
        shouldFollowChatScroll(box);
        chatScrollPosition = box.scrollTop;
      }
      updateChatScrollButton();
    }, { passive: true });
    function interrupt(upward) {
      if (!officeTranscriptActive()) return;
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

function suspendOfficeTranscript() {
  var box = $("messages");
  var visible = box.getBoundingClientRect().height > 0;
  suspendedOfficeTranscript = { chatId: state.activeChatId, following: chatScrollFollowing,
    viewport: visible ? captureMessageViewport(box) : { top: chatScrollPosition } };
  resetRenderedMessageUnits(box);
}

function resumeOfficeTranscript() {
  var saved = suspendedOfficeTranscript;
  suspendedOfficeTranscript = null;
  renderMessages({ fullReset: true, forceScroll: true });
  if (saved && saved.chatId === state.activeChatId && !saved.following) {
    restoreMessageViewport($("messages"), saved.viewport);
    chatScrollFollowing = false;
    updateChatScrollButton();
  }
}

function validChatTokenCount(value) {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : null;
}

function chatTokenNumber(value) {
  return value.toLocaleString("ru-RU").replace(/\u00a0/g, "\u202f");
}

function messageUsageText(message) {
  var total = validChatTokenCount(messageTotalTokens(message));
  var prompt = validChatTokenCount(messagePromptTokens(message));
  var completion = validChatTokenCount(messageCompletionTokens(message));
  if (total === null && prompt === null && completion === null) {
    return "";
  }

  var parts = [];
  if (total !== null) {
    parts.push(chatTokenNumber(total) + " токенов");
  }
  if (prompt !== null) {
    parts.push("вход " + chatTokenNumber(prompt));
  }
  if (completion !== null) {
    parts.push("ответ " + chatTokenNumber(completion));
  }
  return parts.join(" · ");
}

function chatRunTokenUsage(messages, startIndex, endIndex) {
  var runId = messageRunId(messages[startIndex]);
  var first = startIndex;
  while (runId && first > 0 && messageProtocolMessage(messages[first - 1]) &&
      messageRunId(messages[first - 1]) === runId) first -= 1;

  var protocol = [], visible = [];
  for (var index = first; index < endIndex; index += 1) {
    var message = messages[index];
    if (messageRole(message) !== "assistant" || runId && messageRunId(message) !== runId) continue;
    var values = [messagePromptTokens(message), messageCompletionTokens(message), messageTotalTokens(message)];
    if (!values.some(function (value) { return validChatTokenCount(value) !== null; })) continue;
    (messageProtocolMessage(message) ? protocol : visible).push(message);
  }
  // A terminal runtime projection may repeat the final accepted response's
  // usage. The accepted protocol records count each provider answer once.
  var source = protocol.length ? protocol : visible;
  if (!source.length) return null;
  var usage = { PromptTokens: null, CompletionTokens: null, TotalTokens: null };
  [["PromptTokens", messagePromptTokens], ["CompletionTokens", messageCompletionTokens],
    ["TotalTokens", messageTotalTokens]].forEach(function (field) {
    source.forEach(function (message) {
      var value = validChatTokenCount(field[1](message));
      if (value !== null) usage[field[0]] = (usage[field[0]] || 0) + value;
    });
  });
  return usage;
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

function renderChatEmptyState(options) {
  var empty = document.createElement("div");
  empty.className = "chat-empty";

  var mark = document.createElement("div");
  mark.className = "chat-empty-mark";
  mark.innerHTML = window.RNAssistantIcons.render("chat");
  empty.appendChild(mark);

  var title = document.createElement("div");
  title.className = "chat-empty-title";
  var bridgePresent = !!(window.chrome && window.chrome.webview);
  title.textContent = options ? options.title : state.bridgeUnavailable
    ? "Панель RN Assistant недоступна" :
    "Готов к работе с документом";
  empty.appendChild(title);

  var text = document.createElement("div");
  text.className = "chat-empty-text";
  text.textContent = options ? options.text : state.bridgeUnavailable
    ? (bridgePresent
      ? ((state.bridgeFailure && state.bridgeFailure.message) || "Не удалось инициализировать RN Assistant.")
      : "Связь с RN Assistant недоступна в этом окне. Повторно откройте панель.")
    : "Выберите контекст или задайте вопрос по текущему Office-файлу.";
  empty.appendChild(text);

  if (options) {
    var customButtons = options.buttons || (options.button ? [options.button] : []);
    if (customButtons.length) { var customSuggestions = document.createElement("div"); customSuggestions.className = "chat-empty-suggestions";
      customButtons.forEach(function (button) { customSuggestions.appendChild(button); }); empty.appendChild(customSuggestions); }
    return empty;
  }
  if (state.bridgeUnavailable) {
    if (bridgePresent && (!state.bridgeFailure || state.bridgeFailure.code !== "bridge_contract_mismatch")) {
      var retryActions = document.createElement("div");
      retryActions.className = "chat-empty-suggestions";
      var retry = document.createElement("button");
      retry.type = "button";
      retry.className = "chat-empty-suggestion";
      retry.textContent = "Повторить загрузку";
      retry.addEventListener("click", function () { initialize(); });
      retryActions.appendChild(retry);
      empty.appendChild(retryActions);
    }
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

var chatCopyFeedbackTimer = null;
function copyChatMessage(text) {
  return window.copyTextResult(text).then(function () { showChatCopyFeedback("Сообщение скопировано", false); },
    function () { showChatCopyFeedback("Не удалось скопировать сообщение. Выделите текст и скопируйте его вручную.", true); });
}

function showChatCopyFeedback(text, failed) {
  var toast = $("chatCopyFeedback");
  if (!toast) {
    toast = document.createElement("div"); toast.id = "chatCopyFeedback"; document.body.appendChild(toast);
  }
  clearTimeout(chatCopyFeedbackTimer);
  toast.className = "chat-toast" + (failed ? " is-error" : "");
  toast.setAttribute("role", failed ? "alert" : "status");
  toast.textContent = text; toast.hidden = false;
  if (failed) {
    var close = document.createElement("button"); close.type = "button";
    close.className = "icon-btn"; close.setAttribute("aria-label", "Закрыть уведомление"); close.textContent = "×";
    close.addEventListener("click", function () { toast.hidden = true; }); toast.appendChild(close);
  } else chatCopyFeedbackTimer = setTimeout(function () { toast.hidden = true; }, 2400);
}

function appendMessageFooter(node, message, index, activity, options) {
  options = options || {};
  var footer = document.createElement("div");
  footer.className = "message-footer";

  var meta = document.createElement("div");
  meta.className = "message-footer-meta";

  var usage = messageUsageText(options.usage || message);
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
  } else if (!options.workspace && !runViewState && messageRole(message) === "assistant" &&
      !messageProtocolMessage(message) && !activity && !message.Pending && !message.Failed && !message.Local &&
      !message.ReflectionDecision && messageContent(message).trim()) {
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
    copyChatMessage(activity ? activityText(activity) : messageContent(message));
    log("Сообщение скопировано.");
  }));
  if (!activity) appendMessageInspectorAction(actions, message);
  var canFork = options.workspace
    ? window.RNAssistantWorkspaceChat.canMessageAction("fork", message, index)
    : !historyActionsBlocked;
  if (canFork) {
    actions.appendChild(smallIconButton("Ответвить чат отсюда", "branch", function () {
      if (options.workspace) window.RNAssistantWorkspaceChat.messageAction("fork", message, index);
      else forkChatAtMessage(message, index);
    }));
  }
  var canEdit = options.workspace
    ? window.RNAssistantWorkspaceChat.canMessageAction("edit", message, index)
    : messageSupportsEdit(message, index, activity);
  if (canEdit) {
    actions.appendChild(smallIconButton("Изменить сообщение", "edit", function () {
      if (options.workspace) window.RNAssistantWorkspaceChat.messageAction("edit", message, index);
      else startMessageEdit(message, index);
    }));
  }

  var canDelete = options.workspace
    ? window.RNAssistantWorkspaceChat.canMessageAction("delete", message, index)
    : !historyActionsBlocked;
  if (canDelete) {
    actions.appendChild(smallIconButton("Удалить сообщение", "trash", function () {
      if (options.workspace) window.RNAssistantWorkspaceChat.messageAction("delete", message, index);
      else deleteMessage(message, index);
    }));
  }

  var assistant = messageRole(message) === "assistant";
  if (assistant) footer.appendChild(actions);
  if (meta.childNodes.length) footer.appendChild(meta);
  if (!assistant) footer.appendChild(actions);
  node.appendChild(footer);
}

function appendMessageInspectorAction(parent, message) {
  if (!window.RNAssistantChatInspector || typeof window.RNAssistantChatInspector.openActivity !== "function" ||
      messageRole(message) !== "assistant") return;
  var target = window.RNAssistantChatInspector.target();
  var runId = messageRunId(message);
  if ((!target.chatId && (!target.workspaceId || !target.sessionId)) || !runId) return;
  var button = smallIconButton("Открыть ход работы", "clock", function (event) {
    event.preventDefault(); event.stopPropagation();
    window.RNAssistantChatInspector.openActivity(Object.assign({}, target, { runId: runId }), button);
  });
  button.classList.add("message-inspect-action");
  parent.appendChild(button);
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
    currentActivity: options.current ? activity : null,
    workspace: !!options.workspace
  }));
  node.appendChild(body);

  if (message && !options.live) appendMessageFooter(node, message, index, activity, options);

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
  var icon = document.createElement("span");
  icon.innerHTML = window.RNAssistantIcons.render("compaction");
  var title = document.createElement("span");
  title.className = "context-compaction-title";
  title.textContent = activityTitle(activity) || "Контекст сжат";
  var subtitle = document.createElement("span");
  subtitle.className = "context-compaction-subtitle";
  subtitle.textContent = activityValue(activity, "Subtitle", "subtitle", "") || "Ранняя история свернута";
  var caret = document.createElement("span");
  caret.className = "context-compaction-caret";
  caret.setAttribute("aria-hidden", "true");
  label.appendChild(icon.firstElementChild);
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

function renderMessageArticle(message, index, options) {
  options = options || {};
  var node = document.createElement("article");
  node.className = "message " + messageRole(message) + (message.Pending ? " pending" : "") +
    (message.Failed ? " failed" : "");
  if (messageRole(message) === "assistant") node.setAttribute("aria-label",
    message.ReflectionDecision ? "Промежуточное решение агента" : "Ответ агента");
  var activity = messageActivity(message);
  if (activity) {
    if (activityKind(activity) === "compaction" && activityStatus(activity) === "completed") {
      return renderCompactionArticle(message, activity);
    }
    var activityArticle = renderActivityArticle(message, index, activity,
      { live: false, current: false, workspace: !!options.workspace });
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
    if (options.workspace) appendWorkspaceRunOutcome(activityArticle, message);
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

  if (!message.ReflectionDecision && (!options.workspace || messageContent(message).trim()))
    appendChatMessageText(node, message);
  var runView = options.workspace ? window.RNAssistantRunViewState.fromMessage(message) : null;
  appendAcceptedResponseDetails(node, message, runView);
  if (options.workspace) appendWorkspaceRunOutcome(node, message, runView);
  appendMessageFooter(node, message, index, null, options);
  return node;
}

function appendWorkspaceRunOutcome(node, message, runView) {
  runView = runView || window.RNAssistantRunViewState.fromMessage(message);
  if (runView && ["completed", "failed", "cancelled"].indexOf(runView.lifecycle) >= 0 &&
      (runView.lifecycle !== "completed" || runView.executionHealth !== "clean" ||
        ["partial", "unable_to_complete"].indexOf(runView.modelOutcome) >= 0)) {
    appendAgentRunViewState(node, runView, messageRunId(message), false);
  }
}

function appendAcceptedResponseDetails(node, message, runView) {
  if (message.ReflectionDecision) {
    var step = document.createElement("div"); step.className = "workspace-reflection-step";
    var title = document.createElement("strong"); title.textContent = "Промежуточное решение";
    var decision = document.createElement("span"); decision.textContent = message.ReflectionDecision;
    step.append(title, decision);
    var commentary = messageContent(message).trim();
    if (commentary && commentary !== message.ReflectionDecision.trim()) {
      var messageText = document.createElement("span"); messageText.textContent = commentary;
      step.appendChild(messageText);
    }
    node.appendChild(step);
  }
  var claimsInRunNotice = runNoticeIncludesModelResults(runView);
  var results = runView && runView.modelResults.length ? runView.modelResults.map(function (item) {
    return { Summary: item.summary, Assessment: item.assessment, Evidence: item.evidence };
  }) : message.Results;
  if (results && results.length && !claimsInRunNotice)
    node.appendChild(renderModelResults(results));
  var facts = window.RNAssistantRunViewState.effectFacts(runView);
  if (facts) {
    var evidence = document.createElement("div");
    evidence.className = "agent-run-effect-facts";
    evidence.textContent = facts;
    node.appendChild(evidence);
  }
}

function renderModelResults(results) {
  var incomplete = results.some(function (item) { return item.Assessment !== "satisfied"; });
  var details = document.createElement("details"); details.className = "agent-model-results";
  details.open = incomplete;
  var summary = document.createElement("summary");
  summary.textContent = incomplete ? "Утверждения и ссылки на результаты" : "Оценка модели и источники";
  details.appendChild(summary);
  if (!incomplete) {
    details.classList.add("is-compact");
    var caveat = document.createElement("p");
    caveat.textContent = "Достижение цели отдельно не проверено.";
    details.appendChild(caveat);
  }
  var list = document.createElement("ol");
  results.forEach(function (item) {
    var row = document.createElement("li");
    row.textContent = item.Summary + " · " +
      window.RNAssistantRunViewState.assessmentLabel(item.Assessment);
    if (Array.isArray(item.Evidence) && item.Evidence.length) {
      var refs = document.createElement("small"); refs.textContent = " · результаты: " + item.Evidence.join(", ");
      row.appendChild(refs);
    }
    list.appendChild(row);
  });
  details.appendChild(list);
  if (!incomplete) return details;
  var note = document.createElement("div"); note.className = "message-outcome agent-run-notice status-warning";
  note.setAttribute("role", "status");
  var copy = document.createElement("div"); copy.className = "agent-run-notice-copy";
  var heading = document.createElement("span");
  heading.textContent = "Итоги по оценке модели. Достижение цели отдельно не проверено.";
  copy.append(heading, details); note.appendChild(copy);
  return note;
}

function appendChatMessageText(node, message) {
  var body = document.createElement("div"); body.className = "markdown";
  var content = messageContent(message);
  body.innerHTML = markdown(content, { preserveLineBreaks: messageRole(message) === "user" });
  node.appendChild(body);
  enhanceMarkdown(body, { enableVisuals: true, enableJsonViewer: true, sourceText: content });
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
      items.push({ message: activity.__reasoningMessage || null, index: -1, activity: activity });
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
  var run = state.chatRuns && state.chatRuns[state.activeChatId];
  paintLiveModelOutput(section, {
    content: state.liveStreamContent || "",
    reasoning: state.liveReasoning || "",
    reasoningComplete: !!state.liveReasoningComplete,
    retrying: !!(run && run.streamRetrying)
  });
}

// The same live model leaf is used by Office and workspace chat transcripts.
function paintLiveModelOutput(section, output) {
  var previous = section.liveOutput || {};
  var content = output.content || "";
  var reasoning = output.reasoning || "";
  var reasoningComplete = !!output.reasoningComplete;
  var reasoningTruncated = !!output.reasoningTruncated ||
    (typeof MAX_LIVE_REASONING_CHARS === "number" && reasoning.length >= MAX_LIVE_REASONING_CHARS);
  if (previous.reasoning !== reasoning || previous.reasoningComplete !== reasoningComplete ||
      previous.reasoningTruncated !== reasoningTruncated) {
    var oldReasoning = section.querySelector(".reasoning-block");
    if (reasoning && typeof reasoningBlock === "function") {
      if (oldReasoning) {
        updateReasoningBlock(oldReasoning, reasoning, null, !reasoningComplete, reasoningTruncated);
      } else {
        var block = reasoningBlock(reasoning, null, !reasoningComplete, reasoningTruncated);
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
    if (content) {
      if (!body) {
        body = document.createElement("div");
        body.className = "agent-step-message markdown agent-stream-message";
        section.insertBefore(body, status);
      }
      // Keep the live text node attached while tokens arrive. Rendering Markdown
      // afresh here replaces every child on each delta and makes the draft blink.
      if (body.childNodes.length === 1 && body.firstChild.nodeType === 3)
        body.firstChild.nodeValue = content;
      else body.textContent = content;
      // The accepted message is rendered as Markdown once it is saved.
    } else if (body) section.removeChild(body);
  }
  var label = output.retrying ? "Повторная попытка…" : content || output.showStatus ? "Формируется…" : "";
  if (status.hidden !== !label) status.hidden = !label;
  if (status.textContent !== label) status.textContent = label;
  section.liveOutput = { content: content, reasoning: reasoning,
    reasoningComplete: reasoningComplete, reasoningTruncated: reasoningTruncated };
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
    reflectionDecision: message.ReflectionDecision || message.reflectionDecision || "",
    results: message.Results || message.results || null,
    resourceRefs: message.ResourceRefs || message.resourceRefs || null,
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
    usage: run.usage || null,
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

function appendSavedRunUnit(units, run, workspace, actionsSignature, key, update) {
  appendMessageUnit(units, key || agentRunUnitKey(run), agentRunUnitSignature(run),
    function () { return renderAgentRunArticle(run); }, actionsSignature, function (node) {
      refreshMessageFooter(node, function (target) {
        if (!run.items.length && run.finalMessage)
          appendMessageFooter(target, run.finalMessage.message, run.finalMessage.index, null,
            { workspace: workspace, usage: run.usage });
        else appendAgentRunFooter(target, run.items, run.finalMessage, workspace, run.usage);
      });
    }, update);
}

function appendSavedMessageUnit(units, message, index, workspace, actionsSignature) {
  appendMessageUnit(units, "message:" + (messageId(message) || index), messageUnitSignature(message),
    function () { return renderMessageArticle(message, index, workspace ? { workspace: true } : null); },
    actionsSignature, function (node) {
      refreshMessageFooter(node, function (target) {
        appendMessageFooter(target, message, index, messageActivity(message), workspace ? { workspace: true } : null);
      });
    });
}

// One saved-history projection for Office, project and plugin ChatView owners.
// Owners supply only their live state and history actions around these entries.
function forEachChatDisplayEntry(messages, visible, onRun, onMessage) {
  messages = messages || [];
  var lastRunIndexes = Object.create(null);
  messages.forEach(function (message, index) {
    var runId = messageRunId(message);
    if (runId) lastRunIndexes[runId] = index;
  });
  for (var index = 0; index < messages.length; index += 1) {
    var message = messages[index];
    if (messageProtocolMessage(message) || visible && !visible(message)) continue;
    if (canCollectAgentRunAt(index, messages)) {
      var run = collectAgentRun(index, messages);
      run.usage = chatRunTokenUsage(messages, index, run.nextIndex);
      run.continued = !run.finalMessage &&
        lastRunIndexes[agentRunId(run.items, run.finalMessage)] >= run.nextIndex;
      onRun(run, index);
      index = run.nextIndex - 1;
    } else onMessage(message, index);
  }
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
  forEachChatDisplayEntry(state.messages, null, function (run) {
      if (liveRunId && agentRunId(run.items, run.finalMessage) === liveRunId) {
        var savedView = agentRunViewState(run.items, run.finalMessage);
        var terminalRun = savedView && ["completed", "failed", "cancelled"].indexOf(savedView.lifecycle) >= 0;
        if (!run.finalMessage && !terminalRun && run.nextIndex > lastVisibleIndex) {
          renderedLiveRunBase = run;
          renderedLiveRunBaseSignature = agentRunUnitSignature(run);
          return;
        }
        if (run.finalMessage || terminalRun) renderedLiveRunSuppressed = true;
        else run.items.forEach(function (item) {
          renderedLiveRunPastActivities[activityTimelineKey(item.activity)] = true;
          var stepId = activityStepId(item.activity);
          if (stepId) renderedLiveRunPastSteps[stepId] = true;
        });
      }
      appendSavedRunUnit(units, run, false, actionsSignature);
    }, function (message, index) {
      appendSavedMessageUnit(units, message, index, false, actionsSignature + ":" + index);
    });

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

function messageDisclosureKey(details, index) {
  return details.getAttribute("data-disclosure-key") || details.className + ":" + index;
}

function messageDisclosureSnapshot(node) {
  var result = Object.create(null);
  if (!node || !node.querySelectorAll) return result;
  Array.prototype.forEach.call(node.querySelectorAll("details"), function (details, index) {
    var key = messageDisclosureKey(details, index);
    if (details.savedDisclosureState) Object.keys(details.savedDisclosureState).forEach(function (savedKey) {
      result[savedKey] = details.savedDisclosureState[savedKey];
    });
    result[key] = details.open;
  });
  return result;
}

function restoreMessageDisclosures(node, snapshot) {
  if (!node || !node.querySelectorAll) return;
  Array.prototype.forEach.call(node.querySelectorAll("details"), function (details) {
    if (!details.ensureDisclosureContent) return;
    details.savedDisclosureState = snapshot;
    if (snapshot[details.getAttribute("data-disclosure-key")]) details.ensureDisclosureContent();
  });
  Array.prototype.forEach.call(node.querySelectorAll("details"), function (details, index) {
    var key = messageDisclosureKey(details, index);
    if (Object.prototype.hasOwnProperty.call(snapshot, key)) details.open = snapshot[key];
  });
}

function reconcileMessageUnits(box, units, liveOnly, cachedUnits) {
  var previousCache = cachedUnits === undefined ? renderedMessageUnits : cachedUnits;
  var nextCache = Object.create(null);
  if (liveOnly) Object.keys(previousCache).forEach(function (key) {
    if (key.indexOf("live:") !== 0) nextCache[key] = previousCache[key];
  });
  units.forEach(function (unit) {
    var cached = previousCache[unit.key];
    var node = cached && cached.signature === unit.signature ? cached.node : null;
    if (node && cached.actionsSignature !== unit.actionsSignature && unit.refreshActions) {
      unit.refreshActions(node);
    }
    if (node && unit.update) unit.update(node);
    if (!node) {
      var disclosures = messageDisclosureSnapshot(cached && cached.node);
      var focused = document.activeElement;
      var focusKey = null;
      if (focused && focused.tagName === "SUMMARY" && cached && cached.node.contains(focused)) {
        var oldDetails = cached.node.querySelectorAll("details");
        var focusedIndex = Array.prototype.indexOf.call(oldDetails, focused.parentNode);
        if (focusedIndex >= 0) focusKey = messageDisclosureKey(focused.parentNode, focusedIndex);
      }
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
        if (focusKey) Array.prototype.some.call(node.querySelectorAll("details"), function (details, index) {
          if (messageDisclosureKey(details, index) !== focusKey) return false;
          details.querySelector("summary").focus({ preventScroll: true });
          return true;
        });
      }
    }
    nextCache[unit.key] = { signature: unit.signature, actionsSignature: unit.actionsSignature, node: node };
  });
  Object.keys(previousCache).forEach(function (key) {
    if (!nextCache[key] && previousCache[key].node) {
      var removed = previousCache[key].node;
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
    var previous = previousCache[unit.key];
    if (previous && previous.node !== node && previous.node.parentNode === box) {
      if (cursor === previous.node) cursor = previous.node.nextSibling;
      box.removeChild(previous.node);
    }
    if (node !== cursor) box.insertBefore(node, cursor);
    else cursor = cursor.nextSibling;
  });
  if (cachedUnits === undefined) renderedMessageUnits = nextCache;
  return nextCache;
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
  if (!officeTranscriptActive()) return;
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
  if (!officeTranscriptActive()) return;
  options = options || {};
  if (window.RNAssistantArtifactCatalog) window.RNAssistantArtifactCatalog.sync();
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
    if (window.RNAssistantHtmlWorkspaceAnnotations) window.RNAssistantHtmlWorkspaceAnnotations.sync();
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
    if (window.RNAssistantHtmlWorkspaceAnnotations) window.RNAssistantHtmlWorkspaceAnnotations.sync();
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
  if (window.RNAssistantHtmlWorkspaceAnnotations) window.RNAssistantHtmlWorkspaceAnnotations.sync();
}
