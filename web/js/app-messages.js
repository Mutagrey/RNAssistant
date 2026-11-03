// Current singleton adapter. Retain the view while Dockview temporarily detaches its DOM.
var currentConversationView = null;
function messageView(box) {
  if (!box && currentConversationView) return currentConversationView;
  box = box || $("messages");
  var current = box === $("messages");
  var view = window.RNAssistantConversationView.get(box, {
    button: box === $("messages") ? $("chatScrollBottomButton") : null,
    clear: function () {
      if (typeof releaseMessageMediaThumbnails === "function") releaseMessageMediaThumbnails(box);
      if (typeof clearMarkdownEnhancements === "function") clearMarkdownEnhancements(box);
    }
  });
  if (current) currentConversationView = view;
  return view;
}
function officeConversationTarget() { return { source: "office", sessionId: state.activeChatId || "" }; }
// A render borrows one domain snapshot. It owns no copy of durable history.
function createMessagePresentation(view, data, commands) {
  var scope = view.scope(), target = scope.target;
  return { view: view, target: target, data: data, owner: commands && commands.owner, workspace: target.source === "workspace",
    actionKey: scope.generation + ":" + (commands ? commands.generation : ""),
    isCurrent: function () {
      return scope.isCurrent() && !!commands && commands.isCurrent() &&
        commands.target && commands.target.source === target.source &&
        commands.target.sessionId === target.sessionId && commands.target.workspaceId === target.workspaceId;
    },
    invoke: function (name, value) { if (this.isCurrent()) return commands.invoke(name, value); }
  };
}
function officeMessagePresentation(view, commands, snapshot) {
  var target = view.scope().target;
  if (!snapshot || !target || target.source !== "office" || target.sessionId !== snapshot.activeChatId)
    throw new Error("Источник ленты не соответствует представлению чата.");
  var run = snapshot.liveRun, runId = run && run.runId || "";
  var activities = run && run.activities && run.activities.length ? run.activities : run && run.activity ? [run.activity] : [];
  if (!runId) for (var i = activities.length - 1; i >= 0; i--) {
    runId = activities[i].RunId || activities[i].runId;
    if (runId) break;
  }
  return createMessagePresentation(view, {
    messages: snapshot.messages, start: snapshot.messageStartIndex, total: snapshot.messageTotalCount,
    live: { activities: activities, runId: runId || "", content: run && run.stream || "",
      reasoning: run && run.reasoning || "", reasoningComplete: !!(run && run.reasoningComplete),
      retrying: !!(run && run.streamRetrying), statusText: typeof chatModelStageText === "function" ? chatModelStageText(run) : "" }
  }, commands);
}
function createChatHistoryButton(direction, action) {
  var button = document.createElement("button");
  button.type = "button";
  button.className = "chat-history-more";
  button.textContent = direction === "latest" ? "Вернуться к последним сообщениям" : "Показать предыдущие сообщения";
  button.addEventListener("click", function (event) {
    button.disabled = true;
    Promise.resolve().then(function () { return action(event); })
      .finally(function () { button.disabled = false; });
  });
  return button;
}
function officeTranscriptActive() {
  return !window.RNAssistantWorkspaceChat || window.RNAssistantWorkspaceChat.owner() === "office";
}

function resetRenderedMessageUnits(box) { messageView(box).reset(); }

function messageVisibleAttachments(message) {
  var represented = typeof messageImageAttachmentIds === "function"
    ? messageImageAttachmentIds(message)
    : {};
  return messageAttachments(message).filter(function (attachment) {
    var id = String(attachment && (attachment.id !== undefined ? attachment.id : attachment.Id) || "").toLowerCase();
    return !id || !represented[id];
  });
}

function shouldFollowChatScroll(box) { return messageView(box).shouldFollow(); }
function updateChatScrollButton(box) { messageView(box).updateButton(); }
function scrollMessagesToBottom(smooth, box) { messageView(box).bottom(smooth); }
function syncChatScroll(shouldScroll, smooth, box) { messageView(box).sync(shouldScroll, smooth); }
function bindMessageScrollControls() { messageView().bind(); }
function captureMessageViewport(box) { return messageView(box).capture(); }
function restoreMessageViewport(box, snapshot) { messageView(box).restore(snapshot); }

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
    return message && message.UsageIncomplete ? "API не сообщил расход токенов" : "";
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
  return (message && message.UsageIncomplete ? "неполные данные API: " : "") + parts.join(" · ");
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
    if (messageProtocolMessage(message)) {
      var origin = message.AcceptedCallOrigin || message.acceptedCallOrigin;
      // A batch has one provider response; only its first call carries usage.
      if (!origin || Number(origin.CallIndex !== undefined ? origin.CallIndex : origin.callIndex) === 0) protocol.push(message);
      continue;
    }
    var values = [messagePromptTokens(message), messageCompletionTokens(message), messageTotalTokens(message)];
    if (!values.some(function (value) { return validChatTokenCount(value) !== null; })) continue;
    visible.push(message);
  }
  // A terminal runtime projection may repeat the final accepted response's
  // usage. The accepted protocol records count each provider answer once.
  var source = protocol.length ? protocol : visible;
  if (!source.length) return null;
  var usage = { PromptTokens: null, CompletionTokens: null, TotalTokens: null,
    UsageIncomplete: source.some(function (message) {
      return [messagePromptTokens(message), messageCompletionTokens(message), messageTotalTokens(message)]
        .some(function (value) { return validChatTokenCount(value) === null; });
    }) };
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
    usageNode.textContent = message.Failed ? "Не отправлено" : options.usage ? "За запуск: " + usage : usage;
    if (options.usage) usageNode.title = "Сумма usage принятых ответов модели за запуск; размер одного запроса показан в контексте модели.";
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
  actions.appendChild(smallIconButton("Копировать сообщение", "copy", function () {
    copyChatMessage(message.ReflectionDecision
      ? agentReflectionText(message.ReflectionDecision, messageContent(message))
      : activity ? activityText(activity) : messageContent(message));
    log("Сообщение скопировано.");
  }));
  if (!activity) appendMessageInspectorAction(actions, message, options.presentation);
  appendMessageHistoryAction(actions, "Ответвить чат отсюда", "branch", "fork", message, index, options.presentation);
  appendMessageHistoryAction(actions, "Изменить сообщение", "edit", "edit", message, index, options.presentation);
  appendMessageHistoryAction(actions, "Удалить сообщение", "trash", "delete", message, index, options.presentation);

  var assistant = messageRole(message) === "assistant";
  if (assistant) footer.appendChild(actions);
  if (meta.childNodes.length) footer.appendChild(meta);
  if (!assistant) footer.appendChild(actions);
  node.appendChild(footer);
}

function appendMessageHistoryAction(parent, title, icon, name, message, index, presentation) {
  var action = { name: name, message: message, index: index };
  if (!presentation || !presentation.invoke("canMessageAction", action)) return;
  parent.appendChild(smallIconButton(title, icon, function () { return presentation.invoke("messageAction", action); }));
}

function appendMessageInspectorAction(parent, message, presentation) {
  if (!window.RNAssistantChatInspector || typeof window.RNAssistantChatInspector.openActivity !== "function" ||
      messageRole(message) !== "assistant") return;
  if (!presentation) return;
  var address = presentation.target;
  var target = address.source === "office" ? { owner: "office", chatId: address.sessionId }
    : { owner: presentation.owner, workspaceId: address.workspaceId, sessionId: address.sessionId };
  var runId = messageRunId(message);
  if ((!target.chatId && (!target.workspaceId || !target.sessionId)) || !runId) return;
  var button = smallIconButton("Открыть ход работы", "clock", function (event) {
    event.preventDefault(); event.stopPropagation();
    if (presentation.isCurrent()) window.RNAssistantChatInspector.openActivity(Object.assign({}, target, { runId: runId }), button);
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
  if (message.ReflectionDecision && activityKind(activity) === "step") {
    if (typeof appendMessageReasoning === "function") appendMessageReasoning(node, message);
    appendAcceptedResponseDetails(node, message, window.RNAssistantRunViewState.fromMessage(message));
    appendMessageFooter(node, message, index, activity, options);
    return node;
  }
  if (activity) {
    if (activityKind(activity) === "compaction" && activityStatus(activity) === "completed") {
      return renderCompactionArticle(message, activity);
    }
    var activityArticle = renderActivityArticle(message, index, activity,
      { live: false, current: false, workspace: !!options.workspace, presentation: options.presentation });
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
    appendAgentRunViewState(node, runView);
  }
}

function appendAcceptedResponseDetails(node, message, runView) {
  if (message.ReflectionDecision) {
    appendAgentReflectionStep(node, message.ReflectionDecision, messageContent(message));
  }
  var results = runView && runView.modelResults.length ? runView.modelResults.map(function (item) {
    return { Summary: item.summary, Assessment: item.assessment, Evidence: item.evidence };
  }) : message.Results;
  var details = results && results.length ? renderModelResults(results) : null;
  if (details) node.appendChild(details);
  var facts = window.RNAssistantRunViewState.effectFacts(runView);
  if (facts) {
    var evidence = document.createElement("div");
    evidence.className = "agent-run-effect-facts";
    evidence.textContent = facts;
    if (!details) {
      details = document.createElement("details"); details.className = "agent-model-results is-compact";
      var summary = document.createElement("summary"); summary.textContent = "Данные выполнения";
      details.appendChild(summary); node.appendChild(details);
    }
    details.appendChild(evidence);
  }
}

function renderModelResults(results) {
  var details = document.createElement("details"); details.className = "agent-model-results is-compact";
  var summary = document.createElement("summary");
  summary.textContent = "Оценка модели и источники";
  details.appendChild(summary);
  var caveat = document.createElement("p");
  caveat.textContent = "Достижение цели отдельно не проверено.";
  details.appendChild(caveat);
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
  return details;
}

function appendChatMessageText(node, message) {
  var body = document.createElement("div"); body.className = "markdown";
  var content = messageContent(message);
  body.innerHTML = markdown(content, { preserveLineBreaks: messageRole(message) === "user" });
  node.appendChild(body);
  enhanceMarkdown(body, { enableVisuals: true, enableJsonViewer: true, sourceText: content });
}

function liveAgentTailActivities(presentation) {
  var cache = presentation.view.cache;
  return presentation.data.live.activities.filter(function (activity) {
    return !cache.pastActivities[activityTimelineKey(activity)] &&
      !(activityKind(activity) === "step" && cache.pastSteps[activityStepId(activity)]);
  });
}

function renderLiveAgentRun(presentation) {
  var activities = liveAgentTailActivities(presentation);
  if ((!activities.length && !presentation.view.cache.liveBase && !hasLiveAgentOutput(presentation)) || presentation.view.cache.liveSuppressed) return null;
  var items = presentation.view.cache.liveBase ? presentation.view.cache.liveBase.items.slice() : [];
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
  var node = renderAgentRunArticle({ live: true, items: items, finalMessage: null, hasLiveDraft: hasLiveAgentDraft(presentation) }, presentation);
  node.liveOutputVisible = hasLiveAgentOutput(presentation);
  node.liveDraftVisible = hasLiveAgentDraft(presentation);
  node.liveRunId = presentation.data.live.runId;
  if (presentation.view.cache.liveBase && typeof appendAgentRunResourceCards === "function") {
    appendAgentRunResourceCards(node.querySelector(".agent-run-wrap"), presentation.view.cache.liveBase.items, null);
  }
  return node;
}

function hasLiveAgentOutput(presentation) {
  return !!(presentation.data.live.reasoning || hasLiveAgentDraft(presentation));
}

function hasLiveAgentDraft(presentation) {
  return !!(presentation.data.live.content || presentation.data.live.retrying);
}

// Keep streamed output inside its run and update only this leaf on token paints.
function refreshLiveAgentOutput(node, presentation) {
  if (!hasLiveAgentOutput(presentation)) return;
  var section = node.querySelector(".agent-stream-step");
  if (!section) {
    section = document.createElement("section");
    section.className = "agent-model-step agent-stream-step";
    node.querySelector(".agent-run-actions").appendChild(section);
  }
  paintLiveModelOutput(section, presentation.data.live);
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
  var label = output.retrying ? "Повторная попытка…" : content || output.showStatus
    ? output.statusText || "Получаю ответ модели…" : "";
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

function appendSavedRunUnit(units, run, presentation, actionsSignature, key, update) {
  appendMessageUnit(units, key || agentRunUnitKey(run), agentRunUnitSignature(run),
    function () { return renderAgentRunArticle(run, presentation); }, actionsSignature, function (node) {
      refreshMessageFooter(node, function (target) {
        if (!run.items.length && run.finalMessage)
          appendMessageFooter(target, run.finalMessage.message, run.finalMessage.index, null,
            { workspace: presentation.workspace, usage: run.usage, presentation: presentation });
        else appendAgentRunFooter(target, run.items, run.finalMessage, presentation, run.usage);
      });
    }, update);
  units[units.length - 1].patch = function (node) { patchAgentRunArticle(node, run, presentation); };
}

function appendSavedMessageUnit(units, message, index, presentation, actionsSignature) {
  appendMessageUnit(units, "message:" + (messageId(message) || index), messageUnitSignature(message),
    function () { return renderMessageArticle(message, index, { workspace: presentation.workspace, presentation: presentation }); },
    actionsSignature, function (node) {
      refreshMessageFooter(node, function (target) {
        appendMessageFooter(target, message, index, messageActivity(message), { workspace: presentation.workspace, presentation: presentation });
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

function buildMessageUnits(presentation, actionsSignature, pendingUnits) {
  var units = [], data = presentation.data, cache = presentation.view.cache;
  actionsSignature = presentation.actionKey + ":" + (actionsSignature || "");
  if (data.start > 0) {
    appendMessageUnit(units, "older-messages", data.start + ":" + presentation.actionKey, function () {
      return createChatHistoryButton("previous", function () { return presentation.invoke("loadPrevious"); });
    }, "", null);
  }
  var liveRunId = data.live.runId;
  cache.liveRunId = liveRunId;
  cache.liveBase = null;
  cache.liveBaseSignature = null;
  cache.liveSuppressed = false;
  cache.pastActivities = {};
  cache.pastSteps = {};
  var lastVisibleIndex = data.messages.length - 1;
  while (lastVisibleIndex >= 0 && messageProtocolMessage(data.messages[lastVisibleIndex])) lastVisibleIndex -= 1;
  forEachChatDisplayEntry(data.messages, null, function (run) {
      if (liveRunId && agentRunId(run.items, run.finalMessage) === liveRunId) {
        var savedView = agentRunViewState(run.items, run.finalMessage);
        var terminalRun = savedView && ["completed", "failed", "cancelled"].indexOf(savedView.lifecycle) >= 0;
        if (!run.finalMessage && !terminalRun && run.nextIndex > lastVisibleIndex) {
          cache.liveBase = run;
          cache.liveBaseSignature = agentRunUnitSignature(run);
          return;
        }
        if (run.finalMessage || terminalRun) cache.liveSuppressed = true;
        else run.items.forEach(function (item) {
          cache.pastActivities[activityTimelineKey(item.activity)] = true;
          var stepId = activityStepId(item.activity);
          if (stepId) cache.pastSteps[stepId] = true;
        });
      }
      appendSavedRunUnit(units, run, presentation, actionsSignature);
    }, function (message, index) {
      appendSavedMessageUnit(units, message, index, presentation, actionsSignature + ":" + index);
    });

  if (data.start + data.messages.length < data.total) {
    appendMessageUnit(units, "latest-messages", data.total + ":" + presentation.actionKey, function () {
      return createChatHistoryButton("latest", function () { return presentation.invoke("loadLatest"); });
    }, "", null);
  }

  return units.concat(buildLiveMessageUnits(presentation, pendingUnits));
}

function buildLiveMessageUnits(presentation, pendingUnits) {
  var units = [], cache = presentation.view.cache;
  var activities = liveAgentTailActivities(presentation);
  if (!cache.liveSuppressed && (activities.length || cache.liveBase || hasLiveAgentOutput(presentation))) {
    appendMessageUnit(units, "live:agent-run",
      JSON.stringify({ stream: "agent", runId: presentation.data.live.runId, value: activities,
        base: cache.liveBaseSignature, hasOutput: hasLiveAgentOutput(presentation), hasDraft: hasLiveAgentDraft(presentation) }),
      function () { return renderLiveAgentRun(presentation); }, "", null, function (node) { refreshLiveAgentOutput(node, presentation); });
    units[units.length - 1].patch = function (node) {
      var replacement = renderLiveAgentRun(presentation);
      patchAgentRunArticle(node, null, presentation, replacement);
      node.liveOutputVisible = replacement.liveOutputVisible;
      node.liveDraftVisible = replacement.liveDraftVisible;
      node.liveRunId = replacement.liveRunId;
      refreshLiveAgentOutput(node, presentation);
    };
  }

  return units.concat(pendingUnits || []);
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

function patchAgentRunArticle(node, run, presentation, replacement) {
  replacement = replacement || renderAgentRunArticle(run, presentation);
  function release(part) {
    if (typeof releaseMessageMediaThumbnails === "function") releaseMessageMediaThumbnails(part);
    if (typeof clearMarkdownEnhancements === "function") clearMarkdownEnhancements(part);
  }
  function reuse(selector) {
    var prior = Object.create(null);
    Array.prototype.forEach.call(node.querySelectorAll(selector), function (part) {
      if (part.agentReconcileKey) prior[part.agentReconcileKey] = part;
    });
    Array.prototype.forEach.call(replacement.querySelectorAll(selector), function (part) {
      var saved = prior[part.agentReconcileKey];
      if (!saved || saved.agentReconcileSignature !== part.agentReconcileSignature) return;
      if (part === saved) return;
      release(part);
      part.replaceWith(saved);
    });
  }
  // Reuse completed step and call nodes while the active step changes.
  reuse(".agent-model-step");
  reuse(".agent-transcript-entry");
  var priorGroups = Object.create(null);
  Array.prototype.forEach.call(node.querySelectorAll(".agent-action-stage[data-disclosure-key]"), function (group) {
    priorGroups[group.getAttribute("data-disclosure-key")] = group;
  });
  Array.prototype.forEach.call(replacement.querySelectorAll(".agent-action-stage[data-disclosure-key]"), function (group) {
    var saved = priorGroups[group.getAttribute("data-disclosure-key")];
    if (!saved || saved === group) return;
    var oldChildren = Array.prototype.slice.call(saved.children);
    saved.className = group.className;
    saved.replaceChildren.apply(saved, Array.prototype.slice.call(group.childNodes));
    oldChildren.forEach(release);
    group.replaceWith(saved);
  });
  var oldChildren = Array.prototype.slice.call(node.children);
  node.className = replacement.className;
  node.replaceChildren.apply(node, Array.prototype.slice.call(replacement.childNodes));
  oldChildren.forEach(release);
}

function reconcileMessageUnits(box, units, liveOnly) {
  var view = messageView(box), previousCache = view.cache.units;
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
    if (!node && cached && cached.node && unit.patch) {
      var savedDisclosures = messageDisclosureSnapshot(cached.node);
      var focusedSummary = document.activeElement;
      var focusedKey = focusedSummary && focusedSummary.tagName === "SUMMARY" && cached.node.contains(focusedSummary)
        ? messageDisclosureKey(focusedSummary.parentNode,
          Array.prototype.indexOf.call(cached.node.querySelectorAll("details"), focusedSummary.parentNode)) : null;
      unit.patch(cached.node);
      restoreMessageDisclosures(cached.node, savedDisclosures);
      if (focusedSummary && cached.node.contains(focusedSummary) && document.activeElement !== focusedSummary) {
        focusedSummary.focus({ preventScroll: true });
      } else if (focusedKey && document.activeElement !== focusedSummary) {
        Array.prototype.some.call(cached.node.querySelectorAll("details"), function (details, index) {
          if (messageDisclosureKey(details, index) !== focusedKey) return false;
          details.querySelector("summary").focus({ preventScroll: true });
          return true;
        });
      }
      node = cached.node;
    }
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
  // Attach the saved successor before retiring a live unit with a different key.
  // Otherwise the temporary height loss clamps the reader's scroll position.
  Object.keys(previousCache).forEach(function (key) {
    if (!nextCache[key] && previousCache[key].node) {
      var removed = previousCache[key].node;
      if (typeof releaseMessageMediaThumbnails === "function") releaseMessageMediaThumbnails(removed);
      if (typeof clearMarkdownEnhancements === "function") clearMarkdownEnhancements(removed);
      if (removed.parentNode === box) box.removeChild(removed);
    }
  });
  view.cache.units = nextCache;
  return nextCache;
}

function scheduleLiveStreamRender(fullProjection) {
  if (!officeTranscriptActive()) return;
  var view = messageView(), commands = chatComposer().capture();
  var run = state.chatRuns && state.chatRuns[state.activeChatId];
  if (fullProjection || run && (run.runId || "") !== (view.cache.liveRunId || "")) view.cache.fullRenderPending = true;
  view.schedule("stream", function () {
    if (!commands.isCurrent()) return;
    var full = view.cache.fullRenderPending;
    view.cache.fullRenderPending = false;
    if (full) renderMessages(); else renderStreamingMessages(true);
  });
}

function renderStreamingMessages(outputOnly) {
  if (currentConversationView && !currentConversationView.canRender()) return;
  if (typeof isPanelActive === "function" && !isPanelActive("chat")) return;
  if (!officeTranscriptActive()) return;
  var view = messageView(), live = view.cache.units["live:agent-run"];
  if (!messageView().matches(officeConversationTarget()) || !live && !Object.keys(messageView().cache.units).length) {
    renderMessages();
    return;
  }
  if (outputOnly && messageView().cache.liveSuppressed) return;
  var box = $("messages"), presentation = officeMessagePresentation(view, chatComposer().capture(), officeChatSnapshot());
  var shouldScroll = shouldFollowChatScroll(box);
  var viewport = !shouldScroll ? captureMessageViewport(box) : null;
  if (outputOnly && live && live.node.liveOutputVisible && hasLiveAgentOutput(presentation) &&
      live.node.liveDraftVisible === hasLiveAgentDraft(presentation) && live.node.liveRunId === presentation.data.live.runId) {
    // Token frames do not regroup/serialize activities, rebuild units or detach history.
    refreshLiveAgentOutput(live.node, presentation);
  } else {
    reconcileMessageUnits(box, buildLiveMessageUnits(presentation, typeof buildPendingInputUnits === "function" ? buildPendingInputUnits() : []), true);
  }
  if (!shouldScroll) restoreMessageViewport(box, viewport);
  syncChatScroll(shouldScroll, false);
}

function renderMessages(options) {
  if (currentConversationView && !currentConversationView.canRender()) return;
  if (typeof isPanelActive === "function" && !isPanelActive("chat")) return;
  if (!officeTranscriptActive()) return;
  options = options || {};
  if (window.RNAssistantArtifactCatalog) window.RNAssistantArtifactCatalog.sync();
  var box = $("messages");
  var view = messageView(box), frame = view.begin(officeConversationTarget(), options);
  var presentation = officeMessagePresentation(view, chatComposer().capture(), officeChatSnapshot());
  view.cache.fullRenderPending = false;
  var viewport = frame.viewport, shouldScroll = frame.follow;
  if (options.fullReset && !frame.changed) resetRenderedMessageUnits(box);
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
  if (!(typeof pendingChatInputs === "function" && pendingChatInputs().length) && !visibleMessages.length && !state.messageStartIndex && !hasLiveAgentOutput(presentation) && !state.liveActivity && !(state.liveAgentRun && state.liveAgentRun.length)) {
    resetRenderedMessageUnits(box);
    box.appendChild(renderChatEmptyState());
    view.follow();
    renderAgentPlanDock();
    renderAgentApprovalDock();
    syncChatScroll(false, false);
    if (window.RNAssistantHtmlWorkspaceAnnotations) window.RNAssistantHtmlWorkspaceAnnotations.sync();
    return;
  }

  if (!Object.keys(messageView().cache.units).length) box.textContent = "";
  reconcileMessageUnits(box, buildMessageUnits(presentation, messageActionsSignature(),
    typeof buildPendingInputUnits === "function" ? buildPendingInputUnits() : []));

  renderAgentPlanDock();
  renderAgentApprovalDock();
  if (options.preserveTop && (!viewport || !viewport.key)) {
    box.scrollTop += box.scrollHeight - options.previousScrollHeight;
    view.positionChanged();
  } else if (!shouldScroll && viewport) restoreMessageViewport(box, viewport);
  syncChatScroll(shouldScroll, false);
  if (window.RNAssistantHtmlWorkspaceAnnotations) window.RNAssistantHtmlWorkspaceAnnotations.sync();
}
