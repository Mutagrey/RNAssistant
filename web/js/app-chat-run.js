function removeLocalMessage(text) {
  for (var i = state.messages.length - 1; i >= 0; i -= 1) {
    if (state.messages[i] && state.messages[i].Local && messageContent(state.messages[i]) === text) {
      state.messages.splice(i, 1);
      return true;
    }
  }
  return false;
}

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

async function sendChat(text, attachments, targetChatId) {
  attachments = attachments || [];
  var sentChatId = targetChatId || state.activeChatId;
  if (!sentChatId || state.activeSends[sentChatId]) return;
  var knownMessageIds = {};
  (state.messages || []).forEach(function (message) {
    var id = message && !message.Local ? (message.Id || message.id || "") : "";
    if (id) knownMessageIds[id] = true;
  });
  var request = send("sendChat", {
    chatId: sentChatId,
    text: text,
    resourceDraftIds: attachments.map(attachmentId)
  });
  var activeSend = { requestId: request.requestId, text: text, attachments: attachments, canceling: false };
  state.activeSends[sentChatId] = activeSend;
  beginChatRunTracking(sentChatId);
  renderSendControls();
  renderChatSessions();
  try {
    var response = await request;
    if (state.activeChatId === sentChatId) applyChatState(response);
    else applyChatCatalogState(response);
    if (state.activeChatId === sentChatId) clearSendError();
    if (response.toolResults && response.toolResults.length) {
      logToolResults(response.toolResults);
    }
  } catch (error) {
    var failedRunId = state.chatRuns[sentChatId] && state.chatRuns[sentChatId].runId
      ? state.chatRuns[sentChatId].runId
      : "";
    var refreshed = await refreshChatAfterSendFailure(sentChatId);
    var activeChatStillSelected = state.activeChatId === sentChatId;
    var persisted = activeChatStillSelected && refreshed && (state.messages || []).some(function (message) {
      return matchesPersistedSend(message, knownMessageIds, failedRunId, text, attachments);
    });
    if (error.cancelled) {
      if (activeChatStillSelected && !persisted) {
        removeLocalMessage(text);
        if (!$("chatInput").value.trim()) setChatInputText(text, false);
        state.draftAttachments = attachments.slice();
        renderAttachmentDrafts();
        updateEstimatedContextUsage();
        renderContextMeter();
        clearSendError();
      }
      renderChatSessions();
      log(persisted ? "Chat request cancelled and recorded in history." : "Chat request cancelled.", "warning");
    } else {
      if (activeChatStillSelected && !persisted) {
        if (!markLocalMessage(text, { Pending: false, Failed: true })) {
          state.messages.push({
            Id: "local-" + Date.now(),
            Role: "user",
            Content: text,
            Attachments: attachments,
            Local: true,
            Pending: false,
            Failed: true
          });
        }
        renderMessages();
        showSendError(error.detail || error.message, text);
        state.failedSend.attachments = attachments;
      } else if (persisted) {
        clearSendError();
      }
      log(error.message, "error");
      if (error.detail && error.detail !== error.message) {
        log(error.detail, "error");
      }
    }
  } finally {
    if (state.activeSends[sentChatId] === activeSend) delete state.activeSends[sentChatId];
    endChatRunTracking(sentChatId);
    renderSendControls();
    if (state.activeChatId === sentChatId) renderMessages();
    renderChatSessions();
    renderModelControls();
    renderSendControls();
  }
}

function matchesPersistedSend(message, knownMessageIds, runId, text, attachments) {
  var id = message ? (message.Id || message.id || "") : "";
  var role = message ? (message.Role || message.role || "") : "";
  if (!id || knownMessageIds[id] || String(role).toLowerCase() !== "user") return false;

  var messageRunId = message.RunId || message.runId || "";
  if (runId) return messageRunId === runId;
  if (messageContent(message) !== text) return false;

  var expectedIds = (attachments || []).map(attachmentId).sort();
  var actualIds = (message.Attachments || message.attachments || []).map(attachmentId).sort();
  var attachmentsMatch = expectedIds.length === actualIds.length && expectedIds.every(function (value, index) {
    return value === actualIds[index];
  });
  return attachmentsMatch;
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
  if (hasActiveMessageEdit()) {
    if (!currentActiveSend() && !state.modelSaving && !state.modeSaving && !state.reasoningSaving) {
      state.editingText = $("chatInput").value;
      saveMessageEdit();
    } else {
      focusMessageEditComposer();
    }
    return;
  }
  if (currentActiveSend() || state.modelSaving || state.modeSaving || state.reasoningSaving) {
    return;
  }
  if (isPendingChatSubmit(state.activeChatId)) {
    return;
  }
  if (typeof pendingAgentApprovalActivity === "function" && pendingAgentApprovalActivity()) {
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
    if (!ingestionSucceeded || state.activeChatId !== targetChatId || currentActiveSend() ||
        state.modelSaving || state.modeSaving || state.reasoningSaving ||
        (typeof pendingAgentApprovalActivity === "function" && pendingAgentApprovalActivity())) {
      return;
    }
    attachments = (state.draftAttachments || []).slice();
  }

  if (!text && !attachments.length) {
    return;
  }

  setChatInputText("", false);
  clearSendError();
  state.messages.push({ Id: "local-" + Date.now(), Role: "user", Content: text, Attachments: attachments, Local: true, Pending: true });
  clearDraftAttachments();
  updateEstimatedContextUsage();
  renderMessages({ forceScroll: true });
  renderChatSessions();
  renderContextMeter();
  sendChat(text, attachments, targetChatId);
}

function retryFailedSend() {
  if (currentActiveSend() || isPendingChatSubmit(state.activeChatId) || hasActiveMessageEdit() ||
    (typeof pendingAgentApprovalActivity === "function" && pendingAgentApprovalActivity()) ||
    !state.failedSend || (!state.failedSend.text && !(state.failedSend.attachments || []).length)) {
    return;
  }

  markLocalMessage(state.failedSend.text, { Pending: true, Failed: false });
  updateEstimatedContextUsage();
  renderMessages({ forceScroll: true });
  renderChatSessions();
  renderContextMeter();
  var text = state.failedSend.text;
  var attachments = state.failedSend.attachments || [];
  clearSendError();
  sendChat(text, attachments, state.activeChatId);
}

function stopActiveSend() {
  var activeSend = currentActiveSend();
  if (!activeSend || activeSend.canceling) {
    return;
  }

  activeSend.canceling = true;
  renderSendControls();
  var run = state.chatRuns[state.activeChatId] || {};
  var cancellation = run.runId
    ? cancelChatRun(state.activeChatId, run.runId)
    : cancelBridgeRequest(activeSend.requestId);
  cancellation.catch(function (error) {
    log(error.detail || error.message, "error");
  });
}

function currentActiveSend() {
  return state.activeSends[state.activeChatId] || null;
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
    streamResetPending: false,
    reasoning: "",
    reasoningComplete: false,
    reasoningResetPending: false
  };
  if (state.activeChatId !== chatId) {
    return;
  }

  state.liveActivity = null;
  state.liveAgentRun = [];
  state.liveStreamContent = "";
  resetLiveReasoning();
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

function restoreActiveChatRun() {
  var run = state.chatRuns[state.activeChatId];
  state.liveAgentRun = run && run.activities ? run.activities : null;
  state.liveStreamContent = run && run.stream ? run.stream : null;
  state.liveReasoning = run && run.reasoning ? run.reasoning : "";
  state.liveReasoningComplete = !!(run && run.reasoningComplete);
  state.liveActivity = state.liveAgentRun && state.liveAgentRun.length ? state.liveAgentRun[state.liveAgentRun.length - 1] : null;
  renderMessages();
  renderSendControls();
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
