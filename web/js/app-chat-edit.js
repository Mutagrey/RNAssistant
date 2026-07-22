function resetMessageEditState() {
  state.editingMessageId = "";
  state.editingMessageIndex = -1;
  state.editingText = "";
  state.editingBusy = false;
}

function hasActiveMessageEdit() {
  return !!state.editingMessageId || state.editingMessageIndex >= 0;
}

function normalizeEditedMessageText(text) {
  return String(text || "").replace(/\r\n/g, "\n").trim();
}

function findEditingMessage() {
  var messages = state.messages || [];
  var messageIndex;

  if (state.editingMessageId) {
    for (messageIndex = 0; messageIndex < messages.length; messageIndex += 1) {
      if (messageId(messages[messageIndex]) === state.editingMessageId) {
        return { message: messages[messageIndex], index: messageIndex };
      }
    }
    return null;
  }

  messageIndex = state.editingMessageIndex;
  return messageIndex >= 0 && messageIndex < messages.length
    ? { message: messages[messageIndex], index: messageIndex }
    : null;
}

function isEditingMessage(message, index) {
  if (!hasActiveMessageEdit() || !message) {
    return false;
  }

  var id = messageId(message);
  return state.editingMessageId && id
    ? state.editingMessageId === id
    : state.editingMessageIndex === index;
}

function canEditMessage(message) {
  return !!message &&
    !state.bridgeUnavailable &&
    !currentActiveSend() &&
    !message.Local &&
    !message.Pending &&
    !message.Failed &&
    !messageActivity(message) &&
    !!messageId(message) &&
    messageRole(message).toLowerCase() === "user";
}

function canSaveMessageEdit(message, index) {
  if (!isEditingMessage(message, index) ||
      state.editingBusy ||
      currentActiveSend() ||
      !state.activeChatId) {
    return false;
  }

  var text = normalizeEditedMessageText(state.editingText);
  return !!text && text !== normalizeEditedMessageText(messageContent(message));
}

function syncMessageEditTextarea(textarea) {
  if (!textarea) {
    return;
  }
  textarea.style.height = "auto";
  textarea.style.height = Math.min(textarea.scrollHeight, 240) + "px";
}

function syncInlineMessageEditorState(editor, message, index) {
  if (!editor) {
    return;
  }

  var saveButton = editor.querySelector(".message-inline-save");
  if (saveButton) {
    saveButton.disabled = !canSaveMessageEdit(message, index);
  }
  editor.classList.toggle("is-busy", !!state.editingBusy);
}

function focusInlineMessageEditor() {
  window.setTimeout(function () {
    var textarea = document.querySelector(".message-inline-editor textarea");
    if (!textarea) {
      return;
    }

    syncMessageEditTextarea(textarea);
    textarea.focus();
    textarea.selectionStart = textarea.value.length;
    textarea.selectionEnd = textarea.value.length;
  }, 0);
}

function startMessageEdit(message, index) {
  if (!canEditMessage(message)) {
    return;
  }

  state.editingMessageId = messageId(message);
  state.editingMessageIndex = index;
  state.editingText = messageContent(message);
  state.editingBusy = false;
  clearSendError();
  renderMessages();
  renderSendControls();
  focusInlineMessageEditor();
}

function cancelMessageEdit() {
  if (!hasActiveMessageEdit() || state.editingBusy) {
    return;
  }

  resetMessageEditState();
  renderMessages();
  renderSendControls();
}

function applyEditedMessagePreview(target, text) {
  if (!target || !target.message) {
    return;
  }

  var message = target.message;
  var trimmed = normalizeEditedMessageText(text);
  state.messages = (state.messages || []).slice(0, target.index + 1);
  message.Content = trimmed;
  message.content = trimmed;
  message.Activity = null;
  message.activity = null;
  state.messages[target.index] = message;
  state.liveActivity = null;
  state.liveAgentRun = null;
  state.liveStreamContent = null;
  state.htmlWorkspace = { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [] };
  state.htmlWorkspaceDirty = false;
  updateEstimatedContextUsage();
  renderMessages();
  renderContextMeter();
  if (typeof renderHtmlWorkspace === "function") {
    renderHtmlWorkspace();
  }
}

async function refreshChatAfterEditFailure(chatId) {
  try {
    var response = await send("listChats", {});
    if (state.activeChatId === chatId) {
      applyChatState(response);
    } else {
      applyChatCatalogState(response);
    }
  } catch (syncError) {
    log(syncError.detail || syncError.message);
  }
}

async function saveMessageEdit() {
  var target = findEditingMessage();
  if (!target || !canSaveMessageEdit(target.message, target.index)) {
    return;
  }

  var sentChatId = state.activeChatId;
  var sentMessageId = messageId(target.message);
  var text = normalizeEditedMessageText(state.editingText);
  state.editingBusy = true;
  clearSendError();
  applyEditedMessagePreview(target, text);
  setActivity("editing", "Перестраиваю чат с этого сообщения...");

  var request = send("editMessage", {
    chatId: sentChatId,
    id: sentMessageId,
    index: target.index,
    text: text
  });
  state.activeSends[sentChatId] = {
    requestId: request.requestId,
    text: text,
    attachments: messageAttachments(target.message).slice(),
    canceling: false,
    editing: true
  };
  beginChatRunTracking(sentChatId);
  renderChatRunControls();

  try {
    var response = await request;
    if (state.activeChatId === sentChatId) {
      applyChatState(response);
      clearSendError();
    } else {
      applyChatCatalogState(response);
    }
    log("Сообщение обновлено. Нижняя история перестроена заново.");
  } catch (error) {
    log(error.cancelled ? "Редактирование сообщения отменено." : error.detail || error.message);
    await refreshChatAfterEditFailure(sentChatId);
  } finally {
    delete state.activeSends[sentChatId];
    endChatRunTracking(sentChatId);
    if (state.activeChatId === sentChatId) {
      resetMessageEditState();
    }
    renderChatRunControls();
    if (state.activeChatId === sentChatId) {
      clearActivity();
    }
  }
}
