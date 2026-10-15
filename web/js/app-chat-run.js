function pendingChatSubmitMap() {
  state.pendingChatSubmits = state.pendingChatSubmits || {};
  return state.pendingChatSubmits;
}

function isPendingChatSubmit(chatId) {
  return !!(chatId && pendingChatSubmitMap()[chatId]);
}

function setPendingChatSubmit(chatId, pending) {
  if (!chatId) return;
  if (pending) pendingChatSubmitMap()[chatId] = true;
  else delete pendingChatSubmitMap()[chatId];
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
  if (isPendingChatSubmit(state.activeChatId)) {
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
    setPendingChatSubmit(targetChatId, true);
    renderSendControls();
    var ingestionSucceeded = false;
    try {
      ingestionSucceeded = await ingestion !== false;
    } catch (error) {
      log(error.detail || error.message, "error");
    } finally {
      setPendingChatSubmit(targetChatId, false);
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

  var delivery = currentActiveSend() || (typeof pendingAgentApprovalActivity === "function" && pendingAgentApprovalActivity())
    ? runningMessageDelivery() : "Steer";
  await submitInboxMessage(text, attachments, targetChatId, delivery);
}

function stopActiveSend() {
  var activeSend = currentActiveSend();
  if (!activeSend || activeSend.canceling) {
    return;
  }

  activeSend.canceling = true;
  renderSendControls();
  if (activeSend.inbox) {
    chatInbox().canceling = true;
    send("stopChatInbox", { chatId: state.activeChatId }).then(applyChatInbox).catch(function (error) {
      chatInbox().canceling = false; log(error.message, "error"); renderSendControls();
    });
    return;
  }
  var run = state.chatRuns[state.activeChatId] || {};
  var cancellation = run.runId
    ? cancelChatRun(state.activeChatId, run.runId)
    : cancelBridgeRequest(activeSend.requestId);
  cancellation.catch(function (error) {
    log(error.detail || error.message, "error");
  });
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
