var CHAT_BOTTOM_THRESHOLD = 64;
var renderedMessagesChatId = null;
var renderedMessageUnits = {};
var renderedLiveRunBase = null;
var renderedLiveRunSuppressed = false;
var renderedLiveRunBaseSignature = null;

function resetRenderedMessageUnits(box) {
  if (box && typeof clearMarkdownEnhancements === "function") clearMarkdownEnhancements(box);
  renderedMessageUnits = {};
  renderedLiveRunBase = null;
  renderedLiveRunBaseSignature = null;
  renderedLiveRunSuppressed = false;
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

function liveAgentActivities() {
  return state.liveAgentRun && state.liveAgentRun.length
    ? state.liveAgentRun : (state.liveActivity ? [state.liveActivity] : []);
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
  var activities = liveAgentActivities();
  if (!activities.length || renderedLiveRunSuppressed) return null;
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
  var node = renderAgentRunArticle({ live: true, items: items, finalMessage: null });
  if (renderedLiveRunBase && typeof appendAgentRunResourceCards === "function") {
    appendAgentRunResourceCards(node.querySelector(".agent-run-wrap"), renderedLiveRunBase.items, null);
  }
  return node;
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
  live.appendChild(body);
  enhanceMarkdown(body, { enableJsonViewer: true, sourceText: state.liveStreamContent, streaming: true });
  return live;
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
    items: (run.items || []).map(function (item) {
      return { index: item.index, signature: messageUnitSignature(item.message) };
    }),
    final: run.finalMessage ? {
      index: run.finalMessage.index,
      signature: messageUnitSignature(run.finalMessage.message)
    } : null
  });
}

function appendMessageUnit(units, key, signature, render, actionsSignature, refreshActions) {
  units.push({ key: key, signature: signature, render: render,
    actionsSignature: actionsSignature, refreshActions: refreshActions });
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
  var actionsSignature = messageActionsSignature();
  var liveRunId = liveAgentRunId();
  renderedLiveRunBase = null;
  renderedLiveRunBaseSignature = null;
  renderedLiveRunSuppressed = false;
  for (var index = 0; index < state.messages.length; index += 1) {
    if (messageProtocolMessage(state.messages[index])) {
      continue;
    }
    if (canCollectAgentRunAt(index)) {
      var run = collectAgentRun(index);
      if (liveRunId && agentRunId(run.items, run.finalMessage) === liveRunId) {
        if (!run.finalMessage) {
          renderedLiveRunBase = run;
          renderedLiveRunBaseSignature = agentRunUnitSignature(run);
          index = run.nextIndex - 1;
          continue;
        }
        renderedLiveRunSuppressed = true;
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

  return units.concat(buildLiveMessageUnits());
}

function buildLiveMessageUnits() {
  var units = [];
  if (!renderedLiveRunSuppressed && liveAgentActivities().length) {
    appendMessageUnit(units, "live:agent-run",
      JSON.stringify({ stream: "agent", value: liveAgentActivities(),
        base: renderedLiveRunBaseSignature }),
      function () { return renderLiveAgentRun(); });
  }

  if (!renderedLiveRunSuppressed && typeof renderLiveReasoningMessage === "function" && state.liveReasoning) {
    appendMessageUnit(units, "live:reasoning",
      JSON.stringify({ reasoning: state.liveReasoning || "", complete: !!state.liveReasoningComplete }),
      function () { return renderLiveReasoningMessage(); });
  }

  if (!renderedLiveRunSuppressed && state.liveStreamContent) {
    appendMessageUnit(units, "live:stream",
      JSON.stringify({ content: state.liveStreamContent }),
      function () { return renderLiveStreamMessage(); });
  }

  return units;
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
    if (!node) {
      var disclosures = messageDisclosureSnapshot(cached && cached.node);
      if (cached && cached.node && typeof clearMarkdownEnhancements === "function") {
        clearMarkdownEnhancements(cached.node);
      }
      node = unit.render();
      restoreMessageDisclosures(node, disclosures);
    }
    nextCache[unit.key] = { signature: unit.signature, actionsSignature: unit.actionsSignature, node: node };
  });
  Object.keys(renderedMessageUnits).forEach(function (key) {
    if (!nextCache[key] && renderedMessageUnits[key].node) {
      var removed = renderedMessageUnits[key].node;
      if (typeof clearMarkdownEnhancements === "function") clearMarkdownEnhancements(removed);
      if (removed.parentNode === box) box.removeChild(removed);
    }
  });

  // Keep unchanged nodes attached: detaching the transcript restarts embedded
  // viewers and invalidates layout/selection even when all signatures match.
  var cursor = box.firstChild;
  if (liveOnly) {
    cursor = null;
    Object.keys(renderedMessageUnits).some(function (key) {
      var node = renderedMessageUnits[key].node;
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

function scheduleLiveStreamRender() {
  if (state.liveStreamRenderPending) {
    return;
  }
  state.liveStreamRenderPending = true;
  var render = function () {
    state.liveStreamRenderPending = false;
    renderStreamingMessages();
  };
  if (window.requestAnimationFrame) {
    window.requestAnimationFrame(render);
  } else {
    window.setTimeout(render, 16);
  }
}

function renderStreamingMessages() {
  if (typeof isPanelActive === "function" && !isPanelActive("chat")) return;
  if (renderedMessagesChatId !== state.activeChatId || !Object.keys(renderedMessageUnits).length) {
    renderMessages();
    return;
  }
  var box = $("messages");
  var shouldScroll = isChatNearBottom(box);
  reconcileMessageUnits(box, buildLiveMessageUnits(), true);
  syncChatScroll(shouldScroll, false);
}

function renderMessages(options) {
  if (typeof isPanelActive === "function" && !isPanelActive("chat")) return;
  options = options || {};
  if (typeof resetMessageMediaThumbnails === "function") resetMessageMediaThumbnails();
  if (typeof renderChatResourceNavigation === "function") renderChatResourceNavigation();
  var box = $("messages");
  var chatChanged = renderedMessagesChatId !== state.activeChatId;
  var shouldScroll = !!options.forceScroll || chatChanged || isChatNearBottom(box);

  renderedMessagesChatId = state.activeChatId;
  if (chatChanged || options.fullReset) resetRenderedMessageUnits(box);
  var visibleMessages = (state.messages || []).filter(function (message) { return !messageProtocolMessage(message); });
  if (!visibleMessages.length && !state.liveStreamContent && !state.liveReasoning && !state.liveActivity && !(state.liveAgentRun && state.liveAgentRun.length)) {
    resetRenderedMessageUnits(box);
    box.appendChild(renderChatEmptyState());
    renderAgentPlanDock();
    renderAgentApprovalDock();
    syncChatScroll(false, false);
    return;
  }

  if (!Object.keys(renderedMessageUnits).length) box.textContent = "";
  reconcileMessageUnits(box, buildMessageUnits());

  renderAgentPlanDock();
  renderAgentApprovalDock();
  syncChatScroll(shouldScroll, false);
}
