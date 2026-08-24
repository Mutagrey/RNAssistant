async function createChat() {
  if (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
      !confirmDiscardHtmlWorkspaceChanges("Создать новый чат")) {
    return;
  }
  setControlBusy("newChatButton", true);
  try {
    applyChatState(await send("createChat", { title: "Новый чат" }));
    clearSendError();
    log("Чат создан.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    setControlBusy("newChatButton", false);
  }
}

async function createDocumentChat(documentItem) {
  if (!documentItem || !documentItem.documentKey ||
      (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
       !confirmDiscardHtmlWorkspaceChanges("Создать новый чат"))) {
    return;
  }

  delete state.collapsedChatDocuments[documentItem.key];
  try {
    applyChatState(await send("createDocumentChat", {
      title: "Новый чат",
      host: documentItem.host,
      documentKey: documentItem.documentKey,
      documentTitle: documentItem.title,
      documentPath: documentItem.path || ""
    }));
    clearSendError();
    log("Чат для документа создан.");
  } catch (error) {
    log(error.detail || error.message);
  }
}

async function selectChat(id) {
  if (!id || id === state.activeChatId) {
    return;
  }
  if (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
      !confirmDiscardHtmlWorkspaceChanges("Открыть другой чат")) {
    renderChatSessions();
    return;
  }

  try {
    applyChatState(await send("selectChat", { chatId: id }));
    restoreActiveChatRun();
    clearSendError();
    log("Чат открыт.");
  } catch (error) {
    log(error.detail || error.message);
    renderChatSessions();
  }
}

async function openActiveDocument(chatIdValue) {
  var targetChatId = typeof chatIdValue === "string" ? chatIdValue : state.activeChatId;
  if (!targetChatId) {
    return;
  }
  setControlBusy("openDocumentButton", true);
  try {
    var result = await send("openDocument", { chatId: targetChatId });
    log(result && result.launched ? "Документ открыт." : "Документ уже активен.");
  } catch (error) {
    log(error.detail || error.message);
    window.alert(error.message || "Не удалось открыть документ.");
  } finally {
    setControlBusy("openDocumentButton", false);
  }
}

async function activateDocument(documentKey) {
  if (!documentKey) return;
  if (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
      !confirmDiscardHtmlWorkspaceChanges("Переключить документ")) {
    return;
  }
  try {
    applyChatState(await send("activateDocument", { documentKey: documentKey }));
    log("Документ активирован.");
  } catch (error) {
    log(error.detail || error.message);
    window.alert(error.detail || error.message);
  }
}

async function deleteDocument(host, documentKey, title) {
  if (!host || !documentKey ||
      (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
       !confirmDiscardHtmlWorkspaceChanges("Удалить историю документа")) ||
      !window.confirm("Удалить документ «" + (title || "Документ") + "» из истории вместе со всеми чатами? Сам Office-файл удалён не будет.")) {
    return;
  }

  try {
    applyChatState(await send("deleteDocument", { host: host, documentKey: documentKey }));
    clearSendError();
    log("История документа удалена.");
  } catch (error) {
    log(error.detail || error.message);
    window.alert(error.detail || error.message);
  }
}

async function renameChat(chatIdValue) {
  var targetChatId = typeof chatIdValue === "string" ? chatIdValue : state.activeChatId;
  if (!targetChatId) {
    return;
  }

  var current = "";
  (state.chats || []).forEach(function (chat) {
    if (chatId(chat) === targetChatId) {
      current = chatTitle(chat);
    }
  });

  var title = window.prompt("Название чата", current || "Новый чат");
  if (title === null || !title.trim()) {
    return;
  }

  try {
    applyChatState(await send("renameChat", { chatId: targetChatId, title: title.trim() }));
    log("Чат переименован.");
  } catch (error) {
    log(error.detail || error.message);
  }
}

async function clearChat() {
  if (!state.activeChatId ||
      (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
       !confirmDiscardHtmlWorkspaceChanges("Очистить чат")) ||
      !window.confirm("Очистить этот чат?")) {
    return;
  }

  setControlBusy("clearChatButton", true);
  try {
    applyChatState(await send("clearChat", { chatId: state.activeChatId }));
    clearSendError();
    log("Чат очищен.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    setControlBusy("clearChatButton", false);
  }
}

async function compactChatContext() {
  if (!state.activeChatId || currentActiveSend()) return;
  var previousCheckpointId = state.activeContextCheckpointId || "";
  setControlBusy("compactContextButton", true);
  try {
    applyChatState(await send("compactChatContext", { chatId: state.activeChatId }));
    log(state.activeContextCheckpointId && state.activeContextCheckpointId !== previousCheckpointId
      ? "Ранний контекст сжат; полная история сохранена."
      : "Контекст пока не требует сжатия.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    setControlBusy("compactContextButton", false);
  }
}

async function deleteChat(chatIdValue) {
  var targetChatId = typeof chatIdValue === "string" ? chatIdValue : state.activeChatId;
  if (!targetChatId ||
      (targetChatId === state.activeChatId &&
       typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
       !confirmDiscardHtmlWorkspaceChanges("Удалить чат")) ||
      !window.confirm("Удалить этот чат?")) {
    return;
  }

  try {
    applyChatState(await send("deleteChat", { chatId: targetChatId }));
    clearSendError();
    log("Чат удален.");
  } catch (error) {
    log(error.detail || error.message);
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
  resetMessageEditState();
  state.appVersion = init.appVersion || init.AppVersion || "";
  state.host = init.host;
  state.title = init.title;
  state.officeContext = init.officeContext || null;
  state.bridgeToken = init.bridgeToken || init.BridgeToken || state.bridgeToken || "";
  state.settings = init.settings || {};
  state.hasApiKey = !!(init.hasApiKey || init.HasApiKey);
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
  state.activeChatMode = init.activeChatMode || init.ActiveChatMode || "agent";
  state.activeChatHtmlMode = !!(init.activeChatHtmlMode || init.ActiveChatHtmlMode);
  state.activeChatReasoning = !!(init.activeChatReasoning || init.ActiveChatReasoning);
  state.chats = init.chats || [];
  state.documents = init.documents || init.Documents || [];
  state.messages = init.messages || [];
  state.artifacts = init.artifacts || init.Artifacts || [];
  state.activeContextCheckpointId = init.activeContextCheckpointId || init.ActiveContextCheckpointId || "";
  state.activeHtmlArtifactId = init.activeHtmlArtifactId || init.ActiveHtmlArtifactId || "";
  state.activePlanArtifactId = init.activePlanArtifactId || init.ActivePlanArtifactId || "";
  $("toolsPath").textContent = state.toolsPath ? "Хранилище: " + state.toolsPath : "";
  if ($("skillsPath")) $("skillsPath").textContent = state.skillsPath ? "Хранилище: " + state.skillsPath : "";
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
  resetMessageEditState();
  state.appVersion = "";
  state.host = "";
  state.title = "";
  state.officeContext = null;
  state.chats = [];
  state.documents = [];
  state.activeChatId = "";
  state.activeChatHtmlMode = false;
  state.activeChatReasoning = false;
  state.messages = [];
  state.artifacts = [];
  state.activeContextCheckpointId = "";
  state.activeHtmlArtifactId = "";
  state.activePlanArtifactId = "";
  state.tools = [];
  state.skills = [];
  state.vba = { modules: [], backups: [], selectedModule: "" };
  state.toolsPath = "";
  state.skillsPath = "";
  state.context = {};
  state.contextUsage = { usedChars: 0, limitChars: 0, percent: 0, actual: false };
  state.htmlWorkspace = { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [] };
  state.htmlWorkspaceDirty = false;

  $("toolsPath").textContent = "";
  if ($("skillsPath")) $("skillsPath").textContent = "";
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

function chatNavigationSignature(payload) {
  var chats = payload.chats || payload.Chats || [];
  var documents = payload.documents || payload.Documents || [];
  return JSON.stringify({
    activeChatId: payload.activeChatId || payload.ActiveChatId || "",
    chats: chats.map(function (chat) {
      return [chatId(chat), chatTitle(chat), chatMessageCount(chat), chat.DocumentKey || chat.documentKey || "", chat.UpdatedUtc || chat.updatedUtc || ""];
    }),
    documents: documents.map(function (item) {
      return [item.documentKey || item.DocumentKey || "", item.title || item.Title || "", !!(item.isActive || item.IsActive)];
    })
  });
}

async function synchronizeChatState() {
  if (state.bridgeUnavailable || currentActiveSend() || document.hidden) return;
  try {
    var response = await send("listChats", {});
    var current = { activeChatId: state.activeChatId, chats: state.chats, documents: state.documents };
    if (chatNavigationSignature(response) !== chatNavigationSignature(current)) {
      applyChatState(response);
    }
  } catch (error) {
    logOnce("Не удалось синхронизировать список чатов: " + (error.detail || error.message));
  }
}

async function initialize() {
  if (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
      !confirmDiscardHtmlWorkspaceChanges("Обновить состояние")) {
    return;
  }
  try {
    var init = await send("init");
    applyInitState(init);
  } catch (error) {
    applyBridgeUnavailableState(error);
  }
}

async function clearRuntimeData() {
  if (!window.confirm("Удалить локальные чаты, контекст чатов, резервные копии VBA и кеш WebView RNAssistant? Настройки, API-ключ, пользовательские инструменты и навыки останутся.")) {
    return;
  }

  setControlBusy("clearRuntimeDataButton", true);
  try {
    var init = await send("clearRuntimeData", {});
    applyInitState(init);
    log("Локальные данные очищены.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    setControlBusy("clearRuntimeDataButton", false);
  }
}
