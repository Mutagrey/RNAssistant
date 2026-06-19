async function createChat() {
  setActivity("loading", "Создаю чат...");
  try {
    applyChatState(await send("createChat", { title: "New chat" }));
    clearSendError();
    log("Chat created.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    clearActivity();
  }
}

async function selectChat(id) {
  if (!id || id === state.activeChatId) {
    return;
  }

  setActivity("loading", "Открываю чат...");
  try {
    applyChatState(await send("selectChat", { chatId: id }));
    clearSendError();
    log("Chat selected.");
  } catch (error) {
    log(error.detail || error.message);
    renderChatSessions();
  } finally {
    clearActivity();
  }
}

async function renameChat() {
  if (!state.activeChatId) {
    return;
  }

  var current = "";
  (state.chats || []).forEach(function (chat) {
    if (chatId(chat) === state.activeChatId) {
      current = chatTitle(chat);
    }
  });

  var title = window.prompt("Chat name", current || "New chat");
  if (title === null || !title.trim()) {
    return;
  }

  try {
    applyChatState(await send("renameChat", { chatId: state.activeChatId, title: title.trim() }));
    log("Chat renamed.");
  } catch (error) {
    log(error.detail || error.message);
  }
}

async function clearChat() {
  if (!state.activeChatId || !window.confirm("Clear this chat?")) {
    return;
  }

  setActivity("clearing", "Очищаю чат...");
  try {
    applyChatState(await send("clearChat", { chatId: state.activeChatId }));
    clearSendError();
    log("Chat cleared.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    clearActivity();
  }
}

async function deleteChat() {
  if (!state.activeChatId || !window.confirm("Delete this chat?")) {
    return;
  }

  setActivity("clearing", "Удаляю чат...");
  try {
    applyChatState(await send("deleteChat", { chatId: state.activeChatId }));
    clearSendError();
    log("Chat deleted.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    clearActivity();
  }
}

async function deleteMessage(message, index) {
  if (message && message.Local) {
    state.messages.splice(index, 1);
    if (message.Failed) {
      clearSendError();
    }
    updateEstimatedContextUsage();
    renderMessages();
    renderChatSessions();
    renderContextMeter();
    return;
  }

  try {
    var response = await send("deleteMessage", { chatId: state.activeChatId, id: messageId(message), index: index });
    applyChatState(response);
    log("Message deleted.");
  } catch (error) {
    showSendError(error.detail || error.message, state.failedSend ? state.failedSend.text : "");
    log(error.detail || error.message);
  }
}

async function forkChatAtMessage(message, index) {
  if (!state.activeChatId) {
    return;
  }

  try {
    applyChatState(await send("forkChat", { chatId: state.activeChatId, id: messageId(message), index: index }));
    clearSendError();
    log("Chat branch created.");
  } catch (error) {
    log(error.detail || error.message);
  }
}

function applyInitState(init) {
  state.host = init.host;
  state.title = init.title;
  state.settings = init.settings || {};
  state.tools = init.tools || [];
  state.skills = init.skills || [];
  state.toolsPath = init.toolsPath || "";
  state.skillsPath = init.skillsPath || "";
  state.context = init.context || {};
  state.contextUsage = init.contextUsage || {};
  state.activeChatId = init.activeChatId || "";
  state.activeChatModel = init.activeChatModel || "";
  state.chats = init.chats || [];
  state.messages = init.messages || [];
  $("docLine").textContent = init.host + " - " + init.title;
  $("toolsPath").textContent = state.toolsPath ? "Storage: " + state.toolsPath : "";
  $("skillsPath").textContent = state.skillsPath ? "Storage: " + state.skillsPath : "";
  renderSettings();
  renderTools();
  renderSkills();
  renderContext(true);
  renderChatSessions();
  renderMessages();
  renderContextMeter();
  log("Initialized " + init.host);
  loadModelCatalog(false);
  if (init.quickAction) {
    runQuickAction(init.quickAction);
  }
}

async function initialize() {
  setActivity("loading", "Загружаю состояние...");
  try {
    var init = await send("init");
    applyInitState(init);
  } catch (error) {
    log(error.message);
  } finally {
    clearActivity();
  }
}

async function clearRuntimeData() {
  if (!window.confirm("Delete all local chats, chat context, VBA backups, and WebView cache for RNAssistant? Settings, API key, and custom tools and skills will stay.")) {
    return;
  }

  setActivity("clearing", "Очищаю локальные данные...");
  try {
    var init = await send("clearRuntimeData", {});
    applyInitState(init);
    log("Runtime data cleared.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    clearActivity();
  }
}

function renderSendControls() {
  var isSending = !!state.activeSend;
  var isCanceling = isSending && !!state.activeSend.canceling;
  var sendButton = $("sendButton");
  var stopButton = $("stopButton");
  var stopText = $("stopButtonText");
  var input = $("chatInput");
  var clearButton = $("clearInputButton");
  var modelSelect = $("chatModelSelect");

  if (sendButton) {
    sendButton.classList.toggle("hidden", isSending);
    sendButton.disabled = isSending || state.modelSaving;
  }
  if (stopButton) {
    stopButton.classList.toggle("hidden", !isSending);
    stopButton.disabled = isCanceling;
  }
  if (stopText) {
    stopText.textContent = isCanceling ? "Canceling" : "Stop";
  }
  if (input) {
    input.readOnly = isSending;
  }
  if (clearButton) {
    clearButton.disabled = isSending;
  }
  if (modelSelect) {
    modelSelect.disabled = isSending || state.modelCatalog.loading || state.modelSaving || !state.activeChatId;
  }
}

function removeLocalMessage(text) {
  for (var i = state.messages.length - 1; i >= 0; i -= 1) {
    if (state.messages[i] && state.messages[i].Local && messageContent(state.messages[i]) === text) {
      state.messages.splice(i, 1);
      return true;
    }
  }
  return false;
}

async function sendChat(text) {
  setActivity("thinking", "Модель думает...");
  var request = send("sendChat", { chatId: state.activeChatId, text: text });
  state.activeSend = { requestId: request.requestId, text: text, canceling: false };
  renderSendControls();
  try {
    var response = await request;
    applyChatState(response);
    clearSendError();
    if (response.toolResults && response.toolResults.length) {
      logToolResults(response.toolResults);
    }
  } catch (error) {
    if (error.cancelled) {
      removeLocalMessage(text);
      if (!$("chatInput").value.trim()) {
        $("chatInput").value = text;
      }
      updateEstimatedContextUsage();
      renderChatSessions();
      renderContextMeter();
      clearSendError();
      log("Chat request cancelled.");
    } else {
      markLocalMessage(text, { Pending: false, Failed: true });
      renderMessages();
      showSendError(error.detail || error.message, text);
      log(error.message);
      if (error.detail && error.detail !== error.message) {
        log(error.detail);
      }
    }
  } finally {
    state.activeSend = null;
    state.liveActivity = null;
    renderSendControls();
    renderMessages();
    renderModelControls();
    renderSendControls();
    clearActivity();
  }
}

function submitChatInput() {
  if (state.activeSend || state.modelSaving) {
    return;
  }

  var text = $("chatInput").value.trim();
  if (!text) {
    return;
  }

  $("chatInput").value = "";
  clearSendError();
  state.messages.push({ Id: "local-" + Date.now(), Role: "user", Content: text, Local: true, Pending: true });
  updateEstimatedContextUsage();
  renderMessages();
  renderChatSessions();
  renderContextMeter();
  sendChat(text);
}

function retryFailedSend() {
  if (state.activeSend || !state.failedSend || !state.failedSend.text) {
    return;
  }

  markLocalMessage(state.failedSend.text, { Pending: true, Failed: false });
  updateEstimatedContextUsage();
  renderMessages();
  renderChatSessions();
  renderContextMeter();
  var text = state.failedSend.text;
  clearSendError();
  sendChat(text);
}

function stopActiveSend() {
  if (!state.activeSend || state.activeSend.canceling) {
    return;
  }

  state.activeSend.canceling = true;
  setActivity("canceling", "Отменяю ответ...");
  renderSendControls();
  cancelBridgeRequest(state.activeSend.requestId).catch(function (error) {
    log(error.detail || error.message);
  });
}

async function confirmAgentTool(pendingId) {
  if (!pendingId) {
    return;
  }

  setActivity("executing", "Исполняю подтвержденный tool...");
  try {
    applyChatState(await send("confirmAgentTool", { chatId: state.activeChatId, pendingId: pendingId }));
    log("Agent tool confirmed.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    clearActivity();
  }
}

async function cancelAgentTool(pendingId) {
  if (!pendingId) {
    return;
  }

  setActivity("canceling", "Отменяю tool...");
  try {
    applyChatState(await send("cancelAgentTool", { chatId: state.activeChatId, pendingId: pendingId }));
    log("Agent tool cancelled.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    clearActivity();
  }
}

async function runQuickAction(action) {
  var response = await send("quickAction", { action: action });
  if (response.prompt === "/open-settings") {
    switchTab("settings");
    return;
  }
  if (response.prompt === "/open-context") {
    switchTab("context");
    return;
  }
  $("chatInput").value = response.prompt || "";
  switchTab("chat");
}

function bindChatActions() {
  $("refreshButton").addEventListener("click", initialize);
  $("chatSessionSelect").addEventListener("change", function () { selectChat($("chatSessionSelect").value); });
  $("newChatButton").addEventListener("click", createChat);
  $("renameChatButton").addEventListener("click", renameChat);
  $("clearChatButton").addEventListener("click", clearChat);
  $("deleteChatButton").addEventListener("click", deleteChat);
  $("retrySendButton").addEventListener("click", retryFailedSend);
  $("stopButton").addEventListener("click", stopActiveSend);
  $("clearInputButton").addEventListener("click", function () { $("chatInput").value = ""; });
  $("chatInput").addEventListener("keydown", function (event) {
    if (event.key === "Enter" && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
      event.preventDefault();
      submitChatInput();
    }
  });
  $("chatForm").addEventListener("submit", function (event) {
    event.preventDefault();
    submitChatInput();
  });
}
