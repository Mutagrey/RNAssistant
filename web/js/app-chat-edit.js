function officeMessageEdit() { return chatComposer().edit(officeComposerTarget()); }
function resetMessageEditState(options) { return chatComposer().cancelEdit(officeComposerTarget(), options); }
function hasActiveMessageEdit() { return !!officeMessageEdit(); }

function normalizeEditedMessageText(text) {
  return String(text || "").replace(/\r\n/g, "\n").trim();
}

function findEditingMessage() {
  var edit = officeMessageEdit();
  if (!edit) return null;
  var messages = state.messages || [];
  var messageIndex;

  if (edit.messageId) {
    for (messageIndex = 0; messageIndex < messages.length; messageIndex += 1) {
      if (messageId(messages[messageIndex]) === edit.messageId) {
        return { message: messages[messageIndex], index: messageIndex };
      }
    }
    return null;
  }

  messageIndex = edit.index;
  return messageIndex >= 0 && messageIndex < messages.length
    ? { message: messages[messageIndex], index: messageIndex }
    : null;
}

function isEditingMessage(message, index) {
  if (!hasActiveMessageEdit() || !message) {
    return false;
  }

  var id = messageId(message);
  return officeMessageEdit().messageId && id
    ? officeMessageEdit().messageId === id
    : officeMessageEdit().index === index;
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
      officeMessageEdit().busy ||
      currentActiveSend() ||
      !state.activeChatId) {
    return false;
  }

  var text = normalizeEditedMessageText(officeMessageEdit().text);
  return !!text;
}

function focusMessageEditComposer() {
  var edit = officeMessageEdit();
  if (!edit || !chatComposer().current(officeComposerTarget())) return;
  chatComposer().defer(function () {
    if (officeMessageEdit() !== edit) return;
    var input = $("chatInput");
    resizeChatInput(); input.focus(); input.selectionStart = input.selectionEnd = input.value.length;
  });
}

function startMessageEdit(message, index) {
  if (!canEditMessage(message)) {
    return;
  }

  var edit = chatComposer().startEdit(officeComposerTarget(), {
    messageId: messageId(message), index: index, text: messageContent(message) });
  if (!edit) return;
  clearSendError();
  renderMessages();
  setChatInputText(officeMessageEdit().text, true);
  renderSendControls();
}

function cancelMessageEdit() {
  if (!hasActiveMessageEdit() || officeMessageEdit().busy) {
    return;
  }

  resetMessageEditState({ focus: true });
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
  publishOfficeChatRun(state.activeChatId, null);
  renderMessages();
  renderContextMeter();
  if (typeof renderHtmlWorkspace === "function") {
    renderHtmlWorkspace();
  }
}

async function refreshChatAfterEditFailure(chatId) {
  try {
    var response = state.activeChatId === chatId && typeof loadChatState === "function"
      ? await loadChatState(chatId)
      : await send("listChats", {});
    if (state.activeChatId === chatId) {
      applyChatState(response);
    } else {
      applyChatCatalogState(response);
    }
  } catch (syncError) {
    log(syncError.detail || syncError.message, "error");
  }
}

async function saveMessageEdit() {
  var target = findEditingMessage();
  if (!target || !canSaveMessageEdit(target.message, target.index)) {
    return;
  }

  var sentChatId = state.activeChatId;
  var edit = officeMessageEdit(), address = officeComposerTarget(sentChatId);
  var sentMessageId = messageId(target.message);
  var text = normalizeEditedMessageText(edit.text);
  var textChanged = text !== normalizeEditedMessageText(messageContent(target.message));
  edit.busy = true;
  clearSendError();
  applyEditedMessagePreview(target, text);
  try {
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

    var response = await request;
    if (chatComposer().edit(address) === edit) chatComposer().cancelEdit(address);
    if (state.activeChatId === sentChatId) {
      applyChatState(response);
      clearSendError();
    } else {
      applyChatCatalogState(response);
    }
    log(textChanged
      ? "Сообщение изменено и отправлено заново. Последующая история перестроена."
      : "Сообщение отправлено заново. Последующая история перестроена.");
  } catch (error) {
    log(error.cancelled ? "Редактирование сообщения отменено." : error.detail || error.message, error.cancelled ? "warning" : "error");
    await refreshChatAfterEditFailure(sentChatId);
  } finally {
    edit.busy = false;
    delete state.activeSends[sentChatId];
    endChatRunTracking(sentChatId);
    renderChatRunControls();
  }
}
