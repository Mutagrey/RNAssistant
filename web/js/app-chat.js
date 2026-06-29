async function createChat() {
  setActivity("loading", "Создаю чат...");
  try {
    applyChatState(await send("createChat", { title: "Новый чат" }));
    clearSendError();
    log("Чат создан.");
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
    log("Чат открыт.");
  } catch (error) {
    log(error.detail || error.message);
    renderChatSessions();
  } finally {
    clearActivity();
  }
}

async function openActiveDocument() {
  if (!state.activeChatId) {
    return;
  }
  try {
    var result = await send("openDocument", { chatId: state.activeChatId });
    log(result && result.launched ? "Документ открыт." : "Документ уже активен.");
  } catch (error) {
    log(error.detail || error.message);
    window.alert(error.detail || error.message);
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

  var title = window.prompt("Название чата", current || "Новый чат");
  if (title === null || !title.trim()) {
    return;
  }

  try {
    applyChatState(await send("renameChat", { chatId: state.activeChatId, title: title.trim() }));
    log("Чат переименован.");
  } catch (error) {
    log(error.detail || error.message);
  }
}

async function clearChat() {
  if (!state.activeChatId || !window.confirm("Очистить этот чат?")) {
    return;
  }

  setActivity("clearing", "Очищаю чат...");
  try {
    applyChatState(await send("clearChat", { chatId: state.activeChatId }));
    clearSendError();
    log("Чат очищен.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    clearActivity();
  }
}

async function deleteChat() {
  if (!state.activeChatId || !window.confirm("Удалить этот чат?")) {
    return;
  }

  setActivity("clearing", "Удаляю чат...");
  try {
    applyChatState(await send("deleteChat", { chatId: state.activeChatId }));
    clearSendError();
    log("Чат удален.");
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
    log("Сообщение удалено.");
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
    log("Ветка чата создана.");
  } catch (error) {
    log(error.detail || error.message);
  }
}

function applyInitState(init) {
  state.bridgeUnavailable = false;
  document.body.classList.remove("bridge-unavailable");
  state.host = init.host;
  state.title = init.title;
  state.officeContext = init.officeContext || null;
  state.bridgeToken = init.bridgeToken || init.BridgeToken || state.bridgeToken || "";
  state.settings = init.settings || {};
  state.tools = init.tools || [];
  state.skills = init.skills || [];
  state.toolsPath = init.toolsPath || "";
  state.skillsPath = init.skillsPath || "";
  state.context = init.context || {};
  state.contextUsage = init.contextUsage || {};
  state.htmlWorkspace = init.htmlWorkspace || init.HtmlWorkspace || { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [] };
  state.htmlWorkspaceDirty = false;
  state.activeChatId = init.activeChatId || "";
  state.activeChatModel = init.activeChatModel || "";
  state.activeChatHtmlMode = !!(init.activeChatHtmlMode || init.ActiveChatHtmlMode);
  state.chats = init.chats || [];
  state.messages = init.messages || [];
  $("docLine").textContent = formatOfficeContextLine(init.officeContext, init.host, init.title);
    $("toolsPath").textContent = state.toolsPath ? "Хранилище: " + state.toolsPath : "";
    $("skillsPath").textContent = state.skillsPath ? "Хранилище: " + state.skillsPath : "";
  renderSettings();
  renderTools();
  renderSkills();
  renderContext(true);
  renderChatSessions();
  renderMessages();
  renderContextMeter();
  if (typeof renderHtmlWorkspace === "function") {
    renderHtmlWorkspace();
  }
  if (typeof updateVbaMacroRunState === "function") {
    updateVbaMacroRunState();
  }
  log("Initialized " + init.host);
  loadModelCatalog(false);
  if (init.quickAction) {
    runQuickAction(init.quickAction);
  }
}

function applyBridgeUnavailableState(error) {
  state.bridgeUnavailable = true;
  document.body.classList.add("bridge-unavailable");
  state.host = "";
  state.title = "";
  state.officeContext = null;
  state.chats = [];
  state.activeChatId = "";
  state.activeChatHtmlMode = false;
  state.messages = [];
  state.tools = [];
  state.skills = [];
  state.vba = { modules: [], backups: [], selectedModule: "" };
  state.toolsPath = "";
  state.skillsPath = "";
  state.context = {};
  state.contextUsage = { usedChars: 0, limitChars: 0, percent: 0, actual: false };
  state.htmlWorkspace = { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [] };
  state.htmlWorkspaceDirty = false;

  $("docLine").textContent = "Office bridge недоступен";
  $("toolsPath").textContent = "";
  $("skillsPath").textContent = "";
  renderSettings();
  renderTools();
  renderSkills();
  renderContext(true);
  renderChatSessions();
  renderMessages();
  renderContextMeter();
  if (typeof renderHtmlWorkspace === "function") {
    renderHtmlWorkspace();
  }
  renderModelControls();
  renderSendControls();
  if (typeof renderVbaProject === "function") {
    renderVbaProject();
  }
  if (typeof updateVbaMacroRunState === "function") {
    updateVbaMacroRunState();
  }
  log((error && (error.detail || error.message)) || "WebView bridge is not available.");
}

function formatOfficeContextLine(context, host, title) {
  if (!context) {
    return (host || "") + " - " + (title || "");
  }

  var parts = [];
  parts.push(context.Host || context.host || host || "");
  parts.push(context.DocumentTitle || context.documentTitle || title || "");

  var container = context.ContainerName || context.containerName || "";
  var selection = context.SelectionAddress || context.selectionAddress || "";
  if (container && selection && parts[0].toLowerCase() === "excel") {
    parts.push(container + "!" + selection);
  } else if (container && selection) {
    parts.push(container + " · " + selection);
  } else if (selection) {
    parts.push(selection);
  } else if (container) {
    parts.push(container);
  }

  return parts.filter(function (part) { return !!part; }).join(" · ");
}

async function initialize() {
  setActivity("loading", "Загружаю состояние...");
  try {
    var init = await send("init");
    applyInitState(init);
  } catch (error) {
    applyBridgeUnavailableState(error);
  } finally {
    clearActivity();
  }
}

async function clearRuntimeData() {
  if (!window.confirm("Удалить локальные чаты, контекст чатов, резервные копии VBA и кеш WebView RNAssistant? Настройки, API-ключ, пользовательские инструменты и навыки останутся.")) {
    return;
  }

  setActivity("clearing", "Очищаю локальные данные...");
  try {
    var init = await send("clearRuntimeData", {});
    applyInitState(init);
    log("Локальные данные очищены.");
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
  var currentDocumentAvailable = typeof activeChatUsesCurrentDocument !== "function" || activeChatUsesCurrentDocument();

  if (sendButton) {
    sendButton.classList.toggle("hidden", isSending);
    sendButton.disabled = isSending || state.modelSaving || state.bridgeUnavailable || !state.activeChatId;
  }
  if (stopButton) {
    stopButton.classList.toggle("hidden", !isSending);
    stopButton.disabled = isCanceling;
  }
  if (stopText) {
    stopText.textContent = isCanceling ? "Отмена" : "Стоп";
  }
  if (input) {
    input.readOnly = isSending || state.bridgeUnavailable;
    input.placeholder = state.bridgeUnavailable
      ? "Откройте RNAssistant внутри Office, чтобы начать чат..."
      : (currentDocumentAvailable ? "Спросите про текущий документ..." : "Обсудите сохранённый контекст...");
  }
  if (clearButton) {
    clearButton.disabled = isSending;
  }
  if (modelSelect) {
    modelSelect.disabled = isSending || state.modelCatalog.loading || state.modelSaving || state.bridgeUnavailable || !state.activeChatId;
  }
  if ($("addSelectionContextButton")) {
    $("addSelectionContextButton").disabled = isSending || state.bridgeUnavailable || !currentDocumentAvailable;
  }
  if ($("toggleVbaContextButton")) {
    $("toggleVbaContextButton").disabled = isSending || state.bridgeUnavailable || !currentDocumentAvailable;
  }
  if ($("toggleHtmlModeButton")) {
    $("toggleHtmlModeButton").disabled = isSending || state.bridgeUnavailable || !state.activeChatId;
  }
  if ($("attachFileButton")) {
    $("attachFileButton").disabled = isSending || state.bridgeUnavailable || !state.activeChatId;
  }
  if (typeof renderHtmlModeToggle === "function") {
    renderHtmlModeToggle();
  }
  updateComposerInputState();
}

function updateComposerInputState() {
  var input = $("chatInput");
  var form = $("chatForm");
  var clearButton = $("clearInputButton");
  var hasText = !!(input && input.value.trim());
  var hasAttachments = !!(state.draftAttachments && state.draftAttachments.length);

  if (form) {
    form.classList.toggle("has-input", hasText || hasAttachments);
  }
  if (clearButton) {
    clearButton.hidden = !hasText;
  }
}

function setChatInputText(text, shouldFocus) {
  var input = $("chatInput");
  if (!input) {
    return;
  }

  input.value = text || "";
  updateComposerInputState();
  if (shouldFocus) {
    input.focus();
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

async function sendChat(text, attachments) {
  attachments = attachments || [];
  setActivity("thinking", "Модель думает...");
  var request = send("sendChat", {
    chatId: state.activeChatId,
    text: text,
    attachmentIds: attachments.map(attachmentId)
  });
  state.activeSend = { requestId: request.requestId, text: text, attachments: attachments, canceling: false };
  state.liveAgentRun = [];
  renderSendControls();
  try {
    var response = await request;
    applyChatState(response);
    clearDraftAttachments();
    clearSendError();
    if (response.toolResults && response.toolResults.length) {
      logToolResults(response.toolResults);
    }
  } catch (error) {
    if (error.cancelled) {
      removeLocalMessage(text);
      if (!$("chatInput").value.trim()) {
        setChatInputText(text, false);
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
      state.failedSend.attachments = attachments;
      log(error.message);
      if (error.detail && error.detail !== error.message) {
        log(error.detail);
      }
    }
  } finally {
    state.activeSend = null;
    state.liveActivity = null;
    state.liveAgentRun = null;
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
  var attachments = (state.draftAttachments || []).slice();
  if (!text && !attachments.length) {
    return;
  }

  setChatInputText("", false);
  clearSendError();
  state.messages.push({ Id: "local-" + Date.now(), Role: "user", Content: text, Attachments: attachments, Local: true, Pending: true });
  updateEstimatedContextUsage();
  renderMessages({ forceScroll: true });
  renderChatSessions();
  renderContextMeter();
  sendChat(text, attachments);
}

function retryFailedSend() {
  if (state.activeSend || !state.failedSend || (!state.failedSend.text && !(state.failedSend.attachments || []).length)) {
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
    switchTab("chat");
    if (typeof setContextManagerOpen === "function") {
      setContextManagerOpen(true);
    }
    return;
  }
  setChatInputText(response.prompt || "", false);
  switchTab("chat");
}

async function toggleChatHtmlMode() {
  if (!state.activeChatId || state.bridgeUnavailable || state.activeSend) {
    return;
  }

  setActivity("saving", "Переключаю HTML mode...");
  try {
    applyChatState(await send("setChatHtmlMode", {
      chatId: state.activeChatId,
      enabled: !state.activeChatHtmlMode
    }));
    log(state.activeChatHtmlMode ? "HTML mode включен." : "HTML mode выключен.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    clearActivity();
  }
}

function bindChatActions() {
  bindMessageScrollControls();
  bindAttachmentActions();
  $("refreshButton").addEventListener("click", initialize);
  $("chatSessionSelect").addEventListener("change", function () { selectChat($("chatSessionSelect").value); });
  $("newChatButton").addEventListener("click", createChat);
  $("openDocumentButton").addEventListener("click", openActiveDocument);
  $("chatSearchInput").addEventListener("input", function () {
    state.chatSearch = $("chatSearchInput").value || "";
    renderChatSessionList(state.chats || []);
  });
  $("toggleHtmlModeButton").addEventListener("click", toggleChatHtmlMode);
  $("renameChatButton").addEventListener("click", renameChat);
  $("clearChatButton").addEventListener("click", clearChat);
  $("deleteChatButton").addEventListener("click", deleteChat);
  $("retrySendButton").addEventListener("click", retryFailedSend);
  $("stopButton").addEventListener("click", stopActiveSend);
  $("clearInputButton").addEventListener("click", function () { setChatInputText("", true); });
  $("chatInput").addEventListener("input", updateComposerInputState);
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
  updateComposerInputState();
}
