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
    if ((activityPhase === "thinking" || activityPhase === "preparing_context") && !progress.activity && progressChatId) {
      // The next request is being compiled; the previous request is no longer current.
      state.chatRuns[progressChatId].requestContext = { phase: "preparing_context" };
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
    var response = state.activeChatId === chatId && typeof loadChatState === "function"
      ? await loadChatState(chatId)
      : await send("listChats", {});
    if (state.activeChatId === chatId) applyChatState(response);
    else applyChatCatalogState(response);
    return true;
  } catch (syncError) {
    log(syncError.detail || syncError.message, "error");
    return false;
  }
}

async function submitChatInput(value, scope) {
  scope = scope || chatComposer().capture();
  if (!scope.isCurrent() || !scope.target || scope.target.source !== "office" || !scope.target.sessionId) return;
  if (state.chatNavigationPending || state.initializePromise || state.chatDetailPending || state.chatDetailError) return;
  if (hasActiveMessageEdit()) {
    if (!currentActiveSend() && !window.RNAssistantConversationInput.isPending("office", { chatId: scope.target.sessionId })) {
      officeMessageEdit().text = $("chatInput").value;
      saveMessageEdit();
    } else {
      focusMessageEditComposer();
    }
    return;
  }
  if (window.RNAssistantConversationInput.isPending("office", { chatId: state.activeChatId })) {
    return;
  }

  var targetChatId = scope.target.sessionId;
  var text = $("chatInput").value.trim();
  var ingestion = typeof pendingChatResourceIngestion === "function"
    ? pendingChatResourceIngestion(targetChatId)
    : null;
  var attachments = (state.draftAttachments || []).slice();
  if (!text && !attachments.length && !ingestion) {
    return;
  }

  if (ingestion) {
    var releaseIngestion = window.RNAssistantConversationInput.begin("office", { chatId: targetChatId });
    if (!releaseIngestion) return;
    renderSendControls();
    var ingestionSucceeded = false;
    try {
      ingestionSucceeded = await ingestion !== false;
    } catch (error) {
      log(error.detail || error.message, "error");
    } finally {
      releaseIngestion();
      renderSendControls();
    }
    if (!ingestionSucceeded || state.activeChatId !== targetChatId ||
        window.RNAssistantConversationInput.isPending("office", { chatId: targetChatId })) {
      return;
    }
    attachments = (state.draftAttachments || []).slice();
  }

  if (!text && !attachments.length) {
    return;
  }

  var pendingApproval = typeof pendingAgentApprovalActivity === "function" && pendingAgentApprovalActivity();
  var target = exactOfficeSteerTarget(targetChatId);
  var delivery = currentActiveSend() || pendingApproval ? runningMessageDelivery() : "Queue";
  if (delivery === "Steer" && !target.runId) delivery = "Queue";
  await submitInboxMessage(text, attachments, targetChatId, delivery, null,
    delivery === "Steer" ? target.runId : null,
    delivery === "Steer" ? target.pendingId : null);
}

function exactOfficeSteerTarget(chatId) {
  var view = chatId === state.activeChatId ? state.activeRunViewState : null;
  if (view && view.lifecycle === "awaiting_confirmation" && view.pendingConfirmation)
    return { runId: view.runId, pendingId: view.pendingConfirmation.pendingId };
  if (view && view.lifecycle === "running") return { runId: view.runId, pendingId: null };
  var live = state.chatRuns && state.chatRuns[chatId];
  return live && live.runId && chatInbox(chatId).running
    ? { runId: live.runId, pendingId: null } : { runId: null, pendingId: null };
}

function stopActiveSend() {
  var activeSend = currentActiveSend();
  var chatId = state.activeChatId;
  if (!activeSend || activeSend.canceling) {
    return;
  }

  activeSend.canceling = true;
  setChatRunCancelling(chatId, true);
  renderSendControls();
  if (activeSend.inbox) {
    chatInbox(chatId).canceling = true;
    send("stopChatInbox", { chatId: chatId }).then(applyChatInbox).catch(function (error) {
      setChatRunCancelling(chatId, false);
      chatInbox(chatId).canceling = false; log(error.message, "error"); renderSendControls();
    });
    return;
  }
  var run = state.chatRuns[state.activeChatId] || {};
  var cancellation = run.runId
    ? cancelChatRun(state.activeChatId, run.runId)
    : cancelBridgeRequest(activeSend.requestId);
  cancellation.catch(function (error) {
    activeSend.canceling = false;
    setChatRunCancelling(chatId, false);
    log(error.detail || error.message, "error");
    renderSendControls();
  });
}

function chatModelStageText(run) {
  if (run && run.cancelRequested) return "Останавливаю…";
  var phase = run && run.requestContext && run.requestContext.phase;
  return ({ preparing_context: "Подготавливаю контекст…", preparing: "Собираю запрос…",
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
  var active = chatId === state.activeChatId ? state.activeRunViewState : null;
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

async function confirmAgentTool(pendingId) {
  if (!pendingId || currentActiveSend()) {
    return;
  }

  var chatId = state.activeChatId;
  var request = send("confirmAgentTool", { chatId: chatId, pendingId: pendingId });
  state.activeSends[chatId] = {
    requestId: request.requestId,
    text: "",
    attachments: [],
    canceling: false,
    confirming: true
  };
  beginChatRunTracking(chatId);
  renderChatRunControls();
  try {
    var response = await request;
    if (state.activeChatId === chatId) applyChatState(response);
    else applyChatCatalogState(response);
    log("Agent tool confirmed.");
  } catch (error) {
    await refreshChatAfterSendFailure(chatId);
    log(error.detail || error.message, "error");
  } finally {
    delete state.activeSends[chatId];
    endChatRunTracking(chatId);
    renderChatRunControls();
  }
}

async function cancelAgentTool(pendingId) {
  if (!pendingId) {
    return;
  }

  var chatId = state.activeChatId;
  var approvalDock = $("agentApprovalDock");
  if (approvalDock) {
    Array.prototype.slice.call(approvalDock.querySelectorAll("button")).forEach(function (button) {
      button.disabled = true;
    });
  }
  try {
    var response = await send("cancelAgentTool", { chatId: chatId, pendingId: pendingId });
    if (state.activeChatId === chatId) applyChatState(response);
    else applyChatCatalogState(response);
    log("Agent tool cancelled.", "warning");
  } catch (error) {
    await refreshChatAfterSendFailure(chatId);
    log(error.detail || error.message, "error");
    if (typeof renderAgentApprovalDock === "function") renderAgentApprovalDock();
  }
}
