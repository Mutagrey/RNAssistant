function removeLocalMessage(text) {
  for (var i = state.messages.length - 1; i >= 0; i -= 1) {
    if (state.messages[i] && state.messages[i].Local && messageContent(state.messages[i]) === text) {
      state.messages.splice(i, 1);
      return true;
    }
  }
  return false;
}

async function sendChat(text, attachments) {
  attachments = attachments || [];
  var sentChatId = state.activeChatId;
  var request = send("sendChat", {
    chatId: state.activeChatId,
    text: text,
    attachmentIds: attachments.map(attachmentId)
  });
  state.activeSends[sentChatId] = { requestId: request.requestId, text: text, attachments: attachments, canceling: false };
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
    if (error.cancelled) {
      if (state.activeChatId === sentChatId) {
        removeLocalMessage(text);
        if (!$("chatInput").value.trim()) setChatInputText(text, false);
        state.draftAttachments = attachments.slice();
        renderDraftAttachments();
        updateEstimatedContextUsage();
        renderContextMeter();
        clearSendError();
      }
      renderChatSessions();
      log("Chat request cancelled.");
    } else {
      if (state.activeChatId === sentChatId) {
        markLocalMessage(text, { Pending: false, Failed: true });
        renderMessages();
        showSendError(error.detail || error.message, text);
        state.failedSend.attachments = attachments;
      }
      log(error.message);
      if (error.detail && error.detail !== error.message) {
        log(error.detail);
      }
    }
  } finally {
    delete state.activeSends[sentChatId];
    endChatRunTracking(sentChatId);
    renderSendControls();
    if (state.activeChatId === sentChatId) renderMessages();
    renderChatSessions();
    renderModelControls();
    renderSendControls();
  }
}

async function submitChatInput() {
  if (hasActiveMessageEdit()) {
    if (!currentActiveSend() && !state.modelSaving && !state.reasoningSaving) {
      state.editingText = $("chatInput").value;
      saveMessageEdit();
    } else {
      focusMessageEditComposer();
    }
    return;
  }
  if (currentActiveSend() || state.modelSaving || state.reasoningSaving) {
    return;
  }

  var text = $("chatInput").value.trim();
  var attachments = (state.draftAttachments || []).slice();
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
  sendChat(text, attachments);
}

function retryFailedSend() {
  if (currentActiveSend() || hasActiveMessageEdit() || !state.failedSend || (!state.failedSend.text && !(state.failedSend.attachments || []).length)) {
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
  sendChat(text, attachments);
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
    log(error.detail || error.message);
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

  state.chatRuns[chatId] = { activities: [], stream: "", reasoning: "", reasoningComplete: false };
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
    log(error.detail || error.message);
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

  var approvalDock = $("agentApprovalDock");
  if (approvalDock) {
    Array.prototype.slice.call(approvalDock.querySelectorAll("button")).forEach(function (button) {
      button.disabled = true;
    });
  }
  try {
    applyChatState(await send("cancelAgentTool", { chatId: state.activeChatId, pendingId: pendingId }));
    log("Agent tool cancelled.");
  } catch (error) {
    log(error.detail || error.message);
    if (typeof renderAgentApprovalDock === "function") renderAgentApprovalDock();
  }
}

