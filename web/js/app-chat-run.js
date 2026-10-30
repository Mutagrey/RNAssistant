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

async function submitChatInput() {
  if (state.chatNavigationPending || state.initializePromise || state.chatDetailPending || state.chatDetailError) return;
  if (hasActiveMessageEdit()) {
    if (!currentActiveSend() && !state.modelSaving && !state.modeSaving && !state.reasoningSaving) {
      state.editingText = $("chatInput").value;
      saveMessageEdit();
    } else {
      focusMessageEditComposer();
    }
    return;
  }
  if (state.modelSaving || state.modeSaving || state.reasoningSaving) {
    return;
  }
  if (window.RNAssistantConversationInput.isPending("office", { chatId: state.activeChatId })) {
    return;
  }

  var targetChatId = state.activeChatId;
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
        state.modelSaving || state.modeSaving || state.reasoningSaving) {
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
  var stored = recordChatRunActivityState(chatId, activity);
  if (chatId !== state.activeChatId) return;
  state.liveActivity = stored || activity;
  state.liveAgentRun = run.activities;
  if (typeof scheduleLiveStreamRender === "function") scheduleLiveStreamRender(true);
}

function setChatRunCancelling(chatId, cancelling) {
  var run = state.chatRuns && state.chatRuns[chatId];
  if (!run) return;
  run.cancelRequested = cancelling;
  if (cancelling) {
    run.stream = ""; run.reasoning = ""; run.reasoningComplete = false; run.streamRetrying = false;
    if (chatId === state.activeChatId) { state.liveStreamContent = null; resetLiveReasoning(); }
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
  if (state.activeChatId !== chatId) {
    return;
  }

  state.liveActivity = null;
  state.liveAgentRun = [];
  state.liveStreamContent = "";
  resetLiveReasoning();
}

function trackChatProgressRun(chatId, runId) {
  if (!chatId) return null;
  var tracked = state.chatRuns[chatId];
  if (chatId === state.activeChatId && runId && (!tracked || tracked.runId !== runId)) {
    // A new run must retire the previous run's cached projection, even if its
    // first progress event is a stream reset/delta rather than an activity.
    state.liveFullRenderPending = true;
  }
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
  if (state.activeChatId !== chatId) {
    return;
  }

  state.liveActivity = null;
  state.liveAgentRun = null;
  state.liveStreamContent = null;
  resetLiveReasoning();
}

function restoreActiveChatRun(render) {
  var run = state.chatRuns[state.activeChatId];
  state.liveAgentRun = run && run.activities ? run.activities : null;
  state.liveStreamContent = run && run.stream ? run.stream : null;
  state.liveReasoning = run && run.reasoning ? run.reasoning : "";
  state.liveReasoningComplete = !!(run && run.reasoningComplete);
  state.liveActivity = state.liveAgentRun && state.liveAgentRun.length ? state.liveAgentRun[state.liveAgentRun.length - 1] : null;
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
