function beginChatNavigation() {
  state.chatNavigationVersion = (state.chatNavigationVersion || 0) + 1;
  return state.chatNavigationVersion;
}

function applyChatNavigationState(response, version) {
  if (version !== state.chatNavigationVersion) return false;
  return applyChatState(response);
}

async function createChat() {
  if (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
      !confirmDiscardHtmlWorkspaceChanges("Создать новый чат")) {
    return;
  }
  var navigationVersion = beginChatNavigation();
  setControlBusy("newChatButton", true);
  try {
    applyChatNavigationState(await send("createChat", { title: "Новый чат" }), navigationVersion);
    clearSendError();
    log("Чат создан.");
  } catch (error) {
    log(error.detail || error.message, "error");
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
  var navigationVersion = beginChatNavigation();
  try {
    applyChatNavigationState(await send("createDocumentChat", {
      title: "Новый чат",
      host: documentItem.host,
      documentKey: documentItem.documentKey,
      documentTitle: documentItem.title,
      documentPath: documentItem.path || ""
    }), navigationVersion);
    clearSendError();
    log("Чат для документа создан.");
  } catch (error) {
    log(error.detail || error.message, "error");
  }
}

async function selectChat(id) {
  if (!id || (id === state.activeChatId && !state.pendingChatSelectionId)) {
    return;
  }
  if (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
      !confirmDiscardHtmlWorkspaceChanges("Открыть другой чат")) {
    renderChatSessions();
    return;
  }

  var navigationVersion = beginChatNavigation();
  state.pendingChatSelectionId = id;
  try {
    applyChatNavigationState(await send("selectChat", { chatId: id }), navigationVersion);
    clearSendError();
    log("Чат открыт.");
  } catch (error) {
    log(error.detail || error.message, "error");
    renderChatSessions();
  } finally {
    if (navigationVersion === state.chatNavigationVersion) {
      state.pendingChatSelectionId = "";
    }
  }
}

async function openActiveDocument(chatIdValue) {
  var targetChatId = typeof chatIdValue === "string" ? chatIdValue : state.activeChatId;
  if (!targetChatId) {
    return;
  }
  var navigationVersion = beginChatNavigation();
  setControlBusy("openDocumentButton", true);
  try {
    var result = await send("openDocument", { chatId: targetChatId });
    var chatState = result && (result.state || result.State);
    if (chatState) {
      applyChatNavigationState(chatState, navigationVersion);
    }
    log(result && result.launched ? "Документ открыт." : "Документ уже активен.");
  } catch (error) {
    log(error.detail || error.message, "error");
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
  var navigationVersion = beginChatNavigation();
  try {
    applyChatNavigationState(await send("activateDocument", { documentKey: documentKey }), navigationVersion);
    log("Документ активирован.");
  } catch (error) {
    log(error.detail || error.message, "error");
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

  var navigationVersion = beginChatNavigation();
  try {
    applyChatNavigationState(await send("deleteDocument", { host: host, documentKey: documentKey }), navigationVersion);
    clearSendError();
    log("История документа удалена.");
  } catch (error) {
    log(error.detail || error.message, "error");
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

  var navigationVersion = beginChatNavigation();
  try {
    applyChatNavigationState(await send("renameChat", { chatId: targetChatId, title: title.trim() }), navigationVersion);
    log("Чат переименован.");
  } catch (error) {
    log(error.detail || error.message, "error");
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
  var targetChatId = state.activeChatId;
  try {
    applyChatStateForChat(await send("clearChat", { chatId: targetChatId }), targetChatId);
    clearSendError();
    log("Чат очищен.");
  } catch (error) {
    log(error.detail || error.message, "error");
  } finally {
    setControlBusy("clearChatButton", false);
  }
}

async function compactChatContext() {
  if (!state.activeChatId || currentActiveSend()) return;
  var targetChatId = state.activeChatId;
  var previousCheckpointId = state.activeContextCheckpointId || "";
  setControlBusy("compactContextButton", true);
  try {
    applyChatStateForChat(await send("compactChatContext", { chatId: targetChatId }), targetChatId);
    log(state.activeContextCheckpointId && state.activeContextCheckpointId !== previousCheckpointId
      ? "Ранний контекст сжат; полная история сохранена."
      : "Контекст пока не требует сжатия.");
  } catch (error) {
    log(error.detail || error.message, "error");
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

  var navigationVersion = beginChatNavigation();
  try {
    applyChatNavigationState(await send("deleteChat", { chatId: targetChatId }), navigationVersion);
    clearSendError();
    log("Чат удален.");
  } catch (error) {
    log(error.detail || error.message, "error");
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

  var targetChatId = state.activeChatId;
  try {
    var response = await send("deleteMessage", { chatId: targetChatId, id: messageId(message), index: index });
    applyChatStateForChat(response, targetChatId);
    log("Сообщение удалено.");
  } catch (error) {
    showSendError(error.detail || error.message, state.failedSend ? state.failedSend.text : "");
    log(error.detail || error.message, "error");
  }
}

async function forkChatAtMessage(message, index) {
  if (!state.activeChatId) {
    return;
  }

  var navigationVersion = beginChatNavigation();
  try {
    applyChatNavigationState(await send("forkChat", { chatId: state.activeChatId, id: messageId(message), index: index }), navigationVersion);
    clearSendError();
    log("Ветка чата создана.");
  } catch (error) {
    log(error.detail || error.message, "error");
  }
}

function applyInitState(init) {
  state.chatStateApplyVersion = (state.chatStateApplyVersion || 0) + 1;
  init = init || {};
  var previousChatId = state.activeChatId || "";
  var nextChatId = init.activeChatId || init.ActiveChatId || "";
  var chatChanged = previousChatId !== nextChatId;
  if (chatChanged) captureChatDraft(previousChatId);
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
  state.hasHistorySecret = !!(init.hasHistorySecret || init.HasHistorySecret);
  state.tools = init.tools || [];
  state.skills = init.skills || [];
  if (typeof acceptToolLibraryState === "function") acceptToolLibraryState();
  if (typeof acceptSkillLibraryState === "function") acceptSkillLibraryState();
  state.toolsPath = init.toolsPath || "";
  state.skillsPath = init.skillsPath || "";
  state.context = init.context || {};
  state.contextUsage = init.contextUsage || {};
  state.htmlWorkspace = init.htmlWorkspace || init.HtmlWorkspace || { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [], redoBranches: [], recovery: { status: "empty", canMutate: true, candidates: [] } };
  state.htmlWorkspaceDirty = false;
  state.activeChatId = nextChatId;
  state.chatProjectionRevisions = {};
  window.RNAssistantRunViewState.accept(
    state.chatProjectionRevisions,
    nextChatId,
    window.RNAssistantRunViewState.sessionRevision(init));
  state.activeRunViewState = window.RNAssistantRunViewState.normalize(
    init.runViewState !== undefined ? init.runViewState : init.RunViewState);
  state.activeChatModel = init.activeChatModel || "";
  state.activeChatMode = init.activeChatMode || init.ActiveChatMode || "agent";
  state.activeChatReasoning = !!(init.activeChatReasoning || init.ActiveChatReasoning);
  state.chats = window.RNAssistantRunViewState.mergeCatalog(
    [], init.chats || init.Chats || [], state.chatProjectionRevisions);
  state.documents = init.documents || init.Documents || [];
  state.messages = init.messages || [];
  state.artifacts = init.artifacts || init.Artifacts || [];
  state.activeContextCheckpointId = init.activeContextCheckpointId || init.ActiveContextCheckpointId || "";
  state.activeHtmlArtifactId = init.activeHtmlArtifactId || init.ActiveHtmlArtifactId || "";
  state.activeTaskListArtifactId = init.activeTaskListArtifactId || init.ActiveTaskListArtifactId || "";
  state.activePlanDocumentArtifactId = init.activePlanDocumentArtifactId || init.ActivePlanDocumentArtifactId || "";
  if (chatChanged) restoreChatDraft(state.activeChatId);
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
  if (!state.modelCatalog.loaded && !state.modelCatalog.loading) {
    loadModelCatalog(false);
  }
  if (init.quickAction) {
    runQuickAction(init.quickAction);
  }
  if (chatChanged && typeof restoreActiveChatRun === "function") {
    restoreActiveChatRun();
  }
}

function applyBridgeUnavailableState(error) {
  var previousChatId = state.activeChatId || "";
  captureChatDraft(previousChatId);
  state.bridgeUnavailable = true;
  document.body.classList.add("bridge-unavailable");
  resetMessageEditState();
  state.appVersion = "";
  state.hasHistorySecret = false;
  state.host = "";
  state.title = "";
  state.officeContext = null;
  state.chats = [];
  state.documents = [];
  state.activeChatId = "";
  state.activeRunViewState = null;
  state.chatProjectionRevisions = {};
  state.activeChatReasoning = false;
  state.messages = [];
  state.liveActivity = null;
  state.liveAgentRun = null;
  state.liveStreamContent = null;
  if (typeof resetLiveReasoning === "function") resetLiveReasoning();
  state.artifacts = [];
  state.activeContextCheckpointId = "";
  state.activeHtmlArtifactId = "";
  state.activePlanArtifactId = "";
  state.tools = [];
  state.skills = [];
  if (typeof acceptToolLibraryState === "function") acceptToolLibraryState();
  if (typeof acceptSkillLibraryState === "function") acceptSkillLibraryState();
  state.vba = { modules: [], backups: [], selectedModule: "" };
  state.toolsPath = "";
  state.skillsPath = "";
  state.context = {};
  state.contextUsage = { usedChars: 0, limitChars: 0, percent: 0, actual: false };
  state.htmlWorkspace = { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [], redoBranches: [], recovery: { status: "empty", canMutate: true, candidates: [] } };
  state.htmlWorkspaceDirty = false;
  restoreChatDraft("");

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
  log((error && (error.detail || error.message)) || "WebView bridge is not available.", "error");
}

function chatNavigationSignature(payload) {
  var chats = payload.chats || payload.Chats || [];
  var documents = payload.documents || payload.Documents || [];
  return JSON.stringify({
    activeChatId: payload.activeChatId || payload.ActiveChatId || "",
    chats: chats.map(function (chat) {
      var runView = window.RNAssistantRunViewState.fromChatSummary(chat);
      return [
        chatId(chat), chatTitle(chat), chatMessageCount(chat),
        window.RNAssistantRunViewState.chatRevision(chat),
        runView ? runView.lifecycle : "", runView ? runView.executionHealth : "",
        chat.DocumentKey || chat.documentKey || "", chat.UpdatedUtc || chat.updatedUtc || "",
        chatJsonlByteLength(chat), chatCasBlobCount(chat), chatCasLogicalByteLength(chat),
        chatCasStoredByteLength(chat), chatCasMissingBlobCount(chat),
        chatCasReferenceIssueCount(chat), chatStorageWarningLevel(chat)
      ];
    }),
    documents: documents.map(function (item) {
      return [item.documentKey || item.DocumentKey || "", item.title || item.Title || "", !!(item.isActive || item.IsActive)];
    })
  });
}

async function synchronizeChatState(force) {
  if (state.bridgeUnavailable || (!force && (document.hidden || !document.hasFocus()))) return;
  if (state.chatSyncPromise) {
    var pendingSync = state.chatSyncPromise;
    if (!force) return pendingSync;
    await pendingSync;
    if (state.chatSyncPromise && state.chatSyncPromise !== pendingSync) return state.chatSyncPromise;
  }
  var navigationVersion = state.chatNavigationVersion || 0;
  var stateApplyVersion = state.chatStateApplyVersion || 0;
  state.chatSyncPromise = (async function () {
    try {
      var response = await send("listChats", {});
      var current = { activeChatId: state.activeChatId, chats: state.chats, documents: state.documents };
      if (navigationVersion === state.chatNavigationVersion &&
          stateApplyVersion === state.chatStateApplyVersion &&
          chatNavigationSignature(response) !== chatNavigationSignature(current)) {
        var responseChatId = response.activeChatId || response.ActiveChatId || "";
        if (currentActiveSend() && responseChatId === state.activeChatId) {
          applyChatCatalogState(response);
        } else {
          applyChatState(response);
        }
      }
    } catch (error) {
      logOnce("Не удалось синхронизировать список чатов: " + (error.detail || error.message), "warning");
    } finally {
      state.chatSyncPromise = null;
    }
  })();
  return state.chatSyncPromise;
}

async function initialize() {
  if (state.initializePromise) return state.initializePromise;
  if (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
      !confirmDiscardHtmlWorkspaceChanges("Обновить состояние")) {
    return;
  }
  var navigationVersion = beginChatNavigation();
  state.initializePromise = (async function () {
    try {
      var init = await send("init");
      if (navigationVersion === state.chatNavigationVersion) applyInitState(init);
    } catch (error) {
      if (navigationVersion === state.chatNavigationVersion) applyBridgeUnavailableState(error);
    } finally {
      state.initializePromise = null;
    }
  })();
  return state.initializePromise;
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
    log(error.detail || error.message, "error");
  } finally {
    setControlBusy("clearRuntimeDataButton", false);
  }
}
