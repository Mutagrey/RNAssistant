function recordChatRunActivityState(chatId, activity) {
  if (!chatId || !activity) return;
  var run = state.chatRuns[chatId] = state.chatRuns[chatId] || { activities: [], stream: "", modelAttemptActive: false, streamRetrying: false };
  if (typeof recordActivityTimeline === "function") {
    run.activity = recordActivityTimeline(run.activities, activity);
    return run.activity;
  }
  var copy = cloneActivity(activity);
  run.activities.push(copy);
  run.activity = copy;
  return copy;
}

// Addressed Office progress: transient run facts stay in chatRuns; the source borrows that run.
function applyOfficeChatProgress(progress, progressPending, requestId) {
  var isChatProgress = String(requestId || "").indexOf("inbox:") === 0 || progressPending && (progressPending.type === "confirmAgentTool" || progressPending.type === "editMessage");
  var progressChatId = progress.chatId || (progressPending && progressPending.payload && progressPending.payload.chatId) || "";
  var progressRunId = progress.runId || "";
  if (isChatProgress && !progressChatId) return;
  // Saved kernel state wins over a delayed progress frame for the same run.
  if (isChatProgress && progressRunId && typeof chatRunTerminalView === "function" &&
      chatRunTerminalView(progressChatId, progressRunId)) return;
  if (progressPending && typeof progressPending.onProgress === "function") progressPending.onProgress(progress);
  if (progressChatId && isChatProgress) {
    trackChatProgressRun(progressChatId, progressRunId).phase = progress.phase || "working";
  }
  var selected = isChatProgress ? publishOfficeChatRun(progressChatId, state.chatRuns[progressChatId]) :
    progressChatId === state.activeChatId;
  var contentReset = !!(progress.contentReset);
  var reasoningReset = !!(progress.reasoningReset);
  var contentDelta = progress.contentDelta || "";
  var reasoningDelta = progress.reasoningDelta || "";
  var reasoningComplete = !!(progress.reasoningComplete);
  var hasReasoningProgress = !!(reasoningDelta || reasoningComplete);
  var cancellingRun = isChatProgress && progressChatId && state.chatRuns[progressChatId];
  if (cancellingRun && cancellingRun.cancelRequested &&
      (contentReset || reasoningReset || contentDelta || hasReasoningProgress)) return;
  if ((contentReset || reasoningReset) && isChatProgress) {
    // A reset invalidates the draft immediately, including while a retry has no output.
    var resetRun = progressChatId ? state.chatRuns[progressChatId] : null;
    if (resetRun) {
      resetRun.streamRetrying = !!resetRun.modelAttemptActive;
      resetRun.modelAttemptActive = true;
      if (contentReset) resetRun.stream = "";
      if (reasoningReset) {
        resetRun.reasoning = "";
        resetRun.reasoningComplete = false;
      }
    }
    if (!contentDelta && !hasReasoningProgress) {
      if (!selected) scheduleChatProgressRender();
      else if (typeof scheduleLiveStreamRender === "function") scheduleLiveStreamRender();
      else renderMessages();
      return;
    }
  }
  if (contentDelta && isChatProgress) {
    var contentRun = progressChatId ? state.chatRuns[progressChatId] : null;
    if (contentRun) {
      contentRun.stream = (contentRun.stream || "") + contentDelta;
      contentRun.modelAttemptActive = true;
      contentRun.streamRetrying = false;
    }
    if (!selected) { scheduleChatProgressRender(); return; }
    if (typeof scheduleLiveStreamRender === "function") {
      scheduleLiveStreamRender();
    } else {
      renderMessages();
    }
    return;
  }
  if (hasReasoningProgress && isChatProgress) {
    var reasoningRun = progressChatId ? state.chatRuns[progressChatId] : null;
    if (reasoningRun) {
      reasoningRun.modelAttemptActive = true;
      if (reasoningDelta && reasoningRun.reasoningComplete) reasoningRun.reasoning = "";
      reasoningRun.reasoning = (reasoningRun.reasoning || "") + reasoningDelta;
      if (reasoningRun.reasoning.length > 24000) reasoningRun.reasoning = reasoningRun.reasoning.substring(0, 24000);
      reasoningRun.reasoningComplete = reasoningComplete;
    }
    if (selected) {
      if (typeof scheduleLiveStreamRender === "function") scheduleLiveStreamRender();
      else renderMessages();
    } else {
      scheduleChatProgressRender();
    }
    return;
  }
  if (isChatProgress) {
    var activityPhase = String(progress.phase || "").toLowerCase();
    if ((activityPhase === "thinking" || activityPhase === "preparing_context" || activityPhase === "queued_execution") && !progress.activity && progressChatId) {
      // The next request is being compiled; the previous request is no longer current.
      state.chatRuns[progressChatId].requestContext = { phase: activityPhase === "queued_execution" ? activityPhase : "preparing_context" };
      state.chatRuns[progressChatId].modelAttemptActive = false;
      state.chatRuns[progressChatId].streamRetrying = false;
      if (activityPhase === "queued_execution") {
        state.chatRuns[progressChatId].stream = "";
        state.chatRuns[progressChatId].reasoning = "";
        state.chatRuns[progressChatId].reasoningComplete = false;
      }
      if (selected && typeof renderContextMeter === "function") renderContextMeter();
    }
    var acceptedStepReasoning = null;
    if ((activityPhase === "acting" || activityPhase === "reflection") && progressChatId) {
      var acceptedRun = state.chatRuns[progressChatId];
      acceptedStepReasoning = { ReasoningContent: acceptedRun.reasoning || "" };
      acceptedRun.reasoning = ""; acceptedRun.reasoningComplete = false;
    }
    if ((activityPhase === "acting" || activityPhase === "reflection" || activityPhase === "tool_running") && progressChatId) {
      state.chatRuns[progressChatId].stream = "";
      state.chatRuns[progressChatId].modelAttemptActive = false;
      state.chatRuns[progressChatId].streamRetrying = false;
    }
    var normalizedActivity = normalizeProgressActivity(progress);
    if (acceptedStepReasoning) normalizedActivity.__reasoningMessage = acceptedStepReasoning;
    recordChatRunActivityState(progressChatId, normalizedActivity);
    var component = window.RNAssistantConversationPanel && RNAssistantConversationPanel.office(progressChatId);
    if (component) component.stream(true);
    if (state.chatRuns[progressChatId] && state.chatRuns[progressChatId].cancelRequested &&
        typeof updateChatModelStage === "function") updateChatModelStage(progressChatId);
  }
  if (!selected) { scheduleChatProgressRender(); return; }
  if (isChatProgress) {
    if (typeof scheduleLiveStreamRender === "function") scheduleLiveStreamRender(true);
    else renderMessages();
  }
  log("[" + (progress.phase || "working") + "] " + (progress.message || "Выполняю..."));
  return;
}

async function refreshChatAfterSendFailure(chatId) {
  try {
    var readDetail = state.activeChatId === chatId && typeof loadChatState === "function";
    var response = readDetail
      ? await loadChatState(chatId)
      : await send("listChats", {});
    if (readDetail) applyChatStateForChat(response, chatId);
    else applyChatCatalogState(response);
    return true;
  } catch (syncError) {
    log(syncError.detail || syncError.message, "error");
    return false;
  }
}

function submitChatInput(value, scope) { return (scope || chatComposer().capture()).invoke("submit", value); }

function exactOfficeSteerTarget(chatId) {
  var view = chatId === state.activeChatId ? state.activeRunViewState : null;
  if (view && view.lifecycle === "awaiting_confirmation" && view.pendingConfirmation)
    return { runId: view.runId, pendingId: view.pendingConfirmation.pendingId };
  if (view && view.lifecycle === "running") return { runId: view.runId, pendingId: null };
  var live = state.chatRuns && state.chatRuns[chatId];
  return live && live.runId && chatInbox(chatId).running
    ? { runId: live.runId, pendingId: null } : { runId: null, pendingId: null };
}

function stopActiveSend() { return chatComposer().dispatch("stop"); }

function chatModelStageText(run) {
  if (run && run.cancelRequested) return "Останавливаю…";
  var phase = run && run.requestContext && run.requestContext.phase;
  return ({ queued_execution: "Ожидаю свободный слот выполнения…",
    preparing_context: "Подготавливаю контекст…", preparing: "Собираю запрос…",
    sending: "Думаю… Ожидаю ответ модели", headers: "Думаю… Ожидаю данные модели",
    first_chunk: "Получаю ответ модели…", persisting: "Сохраняю ответ модели…",
    completed: "Проверяю ответ и сохраняю шаг…", cancelled: "Запрос модели отменён",
    failed: "Запрос модели завершился ошибкой" })[phase] || "Получаю ответ модели…";
}

function updateChatModelStage(chatId) {
  var run = state.chatRuns && state.chatRuns[chatId];
  if (!run) return;
  var activity = { Kind: "notice", RunId: run.runId, Status: "running", Title: chatModelStageText(run) };
  recordChatRunActivityState(chatId, activity);
  var component = window.RNAssistantConversationPanel && RNAssistantConversationPanel.office(chatId);
  if (component) component.stream(true);
  if (!publishOfficeChatRun(chatId, run)) return;
  if (typeof scheduleLiveStreamRender === "function") scheduleLiveStreamRender(true);
}

function setChatRunCancelling(chatId, cancelling) {
  var run = state.chatRuns && state.chatRuns[chatId];
  if (!run) return;
  run.cancelRequested = cancelling;
  if (cancelling) {
    run.stream = ""; run.reasoning = ""; run.reasoningComplete = false; run.streamRetrying = false;
  }
  updateChatModelStage(chatId);
}

function currentActiveSend() {
  var active = state.activeSends[state.activeChatId];
  if (active) return active;
  var inbox = typeof chatInbox === "function" ? chatInbox() : null;
  return inbox && inbox.running ? { inbox: true, canceling: !!inbox.canceling } : null;
}

function renderChatRunControls() {
  renderMessages();
  renderChatSessions();
  renderModelControls();
  renderSendControls();
}

function beginChatRunTracking(chatId) {
  if (!chatId) {
    return;
  }

  state.chatRuns[chatId] = {
    activities: [],
    stream: "",
    modelAttemptActive: false,
    streamRetrying: false,
    reasoning: "",
    reasoningComplete: false
  };
  publishOfficeChatRun(chatId, state.chatRuns[chatId]);
}

function trackChatProgressRun(chatId, runId) {
  if (!chatId) return null;
  var tracked = state.chatRuns[chatId];
  if (runId && tracked && tracked.runId && tracked.runId !== runId) {
    beginChatRunTracking(chatId);
    tracked = state.chatRuns[chatId];
  }
  if (!tracked) {
    tracked = state.chatRuns[chatId] = {
      activities: [], stream: "", modelAttemptActive: false, streamRetrying: false,
      reasoning: "", reasoningComplete: false
    };
  }
  if (runId) tracked.runId = runId;
  return tracked;
}

function chatRunTerminalView(chatId, runId) {
  if (!chatId || !runId) return null;
  var component = window.RNAssistantConversationPanel && RNAssistantConversationPanel.office(chatId);
  var active = component ? component.snapshot().activeRunViewState : chatId === state.activeChatId ? state.activeRunViewState : null;
  if (active && active.runId === runId && active.lifecycle !== "running") return active;
  var summary = (state.chats || []).find(function (chat) { return chatId === (chat.Id || chat.id); });
  var view = summary && window.RNAssistantRunViewState.fromChatSummary(summary);
  return view && view.runId === runId && view.lifecycle !== "running" ? view : null;
}

function reconcileTerminalChatRuns() {
  Object.keys(state.chatRuns || {}).forEach(function (id) {
    var tracked = state.chatRuns[id];
    if (tracked && chatRunTerminalView(id, tracked.runId)) endChatRunTracking(id);
  });
}

function endChatRunTracking(chatId) {
  if (!chatId) {
    return;
  }

  delete state.chatRuns[chatId];
  publishOfficeChatRun(chatId, null);
}

function restoreActiveChatRun(render) {
  publishOfficeChatRun(state.activeChatId, state.chatRuns[state.activeChatId]);
  if (render !== false) {
    renderMessages();
    renderSendControls();
  }
}

function confirmAgentTool(pendingId) { return chatComposer().dispatch("confirm", pendingId); }
function cancelAgentTool(pendingId) { return chatComposer().dispatch("reject", pendingId); }
