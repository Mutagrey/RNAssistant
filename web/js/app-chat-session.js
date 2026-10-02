function beginChatNavigation() {
  state.chatNavigationVersion = (state.chatNavigationVersion || 0) + 1;
  return state.chatNavigationVersion;
}

function applyChatNavigationState(response, version) {
  if (version !== state.chatNavigationVersion) return false;
  return applyChatState(response);
}

function sendChatPreference(type, payload) {
  var previous = state.chatPreferencePromise;
  var request = (async function () {
    if (previous) await previous.catch(function () {});
    return send(type, payload);
  })();
  state.chatPreferencePromise = request;
  var finished = function () {
    if (state.chatPreferencePromise === request) state.chatPreferencePromise = null;
  };
  request.then(finished, finished);
  return request;
}

function navigateChat(type, payload) {
  var previous = state.chatNavigationPromise;
  var selectionVersion = 0;
  if (type === "selectChat") {
    state.chatSelectionRequestVersion = (state.chatSelectionRequestVersion || 0) + 1;
    selectionVersion = state.chatSelectionRequestVersion;
  }
  state.chatNavigationPending = (state.chatNavigationPending || 0) + 1;
  if (typeof renderSendControls === "function") renderSendControls();
  var startedAt = window.performance && window.performance.now ? window.performance.now() : Date.now();
  var navigation = (async function () {
    if (previous) await previous.catch(function () {});
    if (state.chatSyncPromise) await state.chatSyncPromise;
    if (state.chatPreferencePromise) await state.chatPreferencePromise.catch(function () {});
    if (selectionVersion && selectionVersion !== state.chatSelectionRequestVersion) return null;
    if (typeof cancelModelCatalogLoad === "function") await cancelModelCatalogLoad();
    if (selectionVersion && selectionVersion !== state.chatSelectionRequestVersion) return null;
    var version = beginChatNavigation();
    var response = await send(type, payload);
    var bridgeMs = (window.performance && window.performance.now ? window.performance.now() : Date.now()) - startedAt;
    // Apply each completed switch before dispatching the next. If the next request
    // fails, the visible chat still matches the controller's committed binding.
    if (type === "init" || response.init) applyInitState(response.init || response);
    else if (response.state || response.State) applyChatNavigationState(response.state || response.State, version);
    else if (response.activeChatId !== undefined || response.ActiveChatId !== undefined)
      applyChatNavigationState(response, version);
    var renderMs = (window.performance && window.performance.now ? window.performance.now() : Date.now()) - startedAt - bridgeMs;
    if (bridgeMs + renderMs >= (type === "init" ? 500 : 250)) send("reportClientTiming", {
      kind: type === "init" ? "startup" : "chatNavigation",
      bridgeMs: Math.round(bridgeMs), renderMs: Math.round(renderMs), messages: (state.messages || []).length
    }).catch(function () {});
    return response;
  })();
  state.chatNavigationPromise = navigation;
  var finished = function () {
    state.chatNavigationPending -= 1;
    if (state.chatNavigationPromise === navigation) state.chatNavigationPromise = null;
    if (typeof renderSendControls === "function") renderSendControls();
  };
  navigation.then(finished, finished);
  return navigation;
}

function isOutlookMailboxKey(key) {
  return typeof key === "string" && key.indexOf("outlook-mailbox:") === 0;
}

async function attachOutlookMailbox(key, chatIdValue, createNew) {
  await navigateChat("attachOutlookMailbox", {
    documentKey: key, chatId: chatIdValue || "", createNew: !!createNew
  });
}

async function createChat() {
  if (state.chatNavigationPending) return;
  if (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
      !confirmDiscardHtmlWorkspaceChanges("Создать новый чат")) {
    return;
  }
  setControlBusy("newChatButton", true);
  try {
    await navigateChat("createChat", { title: "Новый чат" });
    clearSendError();
    log("Чат создан.");
  } catch (error) {
    log(error.detail || error.message, "error");
  } finally {
    setControlBusy("newChatButton", false);
  }
}

async function createDocumentChat(documentItem) {
  if (state.chatNavigationPending || !documentItem || !documentItem.documentKey ||
      (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
       !confirmDiscardHtmlWorkspaceChanges("Создать новый чат"))) {
    return;
  }

  delete state.collapsedChatDocuments[documentItem.key];
  try {
    if (isOutlookMailboxKey(documentItem.documentKey)) {
      await attachOutlookMailbox(documentItem.documentKey, "", true);
      return;
    }
    await navigateChat("createDocumentChat", {
      title: "Новый чат",
      host: documentItem.host,
      documentKey: documentItem.documentKey,
      documentTitle: documentItem.title,
      documentPath: documentItem.path || ""
    });
    clearSendError();
    log("Чат для документа создан.");
  } catch (error) {
    log(error.detail || error.message, "error");
  }
}

async function selectChat(id) {
  if (!id || (id === state.activeChatId && !state.chatNavigationPending)) {
    return;
  }
  if (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
      !confirmDiscardHtmlWorkspaceChanges("Открыть другой чат")) {
    renderChatSessions();
    return;
  }

  try {
    if (!await navigateChat("selectChat", { chatId: id })) return;
    clearSendError();
    log("Чат открыт.");
  } catch (error) {
    log(error.detail || error.message, "error");
    renderChatSessions();
  }
}

async function loadPreviousChatMessages(event) {
  if (state.messagePageBusy || !state.activeChatId || state.messageStartIndex <= 0) return;
  var button = event && event.currentTarget;
  var chatIdValue = state.activeChatId;
  var beforeIndex = state.messageStartIndex;
  var revision = state.chatProjectionRevisions[chatIdValue];
  var navigationVersion = state.chatNavigationVersion;
  state.messagePageBusy = true;
  if (button) button.disabled = true;
  try {
    var page = await send("getPreviousChatMessages", { chatId: chatIdValue, beforeIndex: beforeIndex });
    if (state.activeChatId !== chatIdValue || state.messageStartIndex !== beforeIndex ||
        state.chatProjectionRevisions[chatIdValue] !== revision ||
        state.chatNavigationVersion !== navigationVersion) return;
    if (page.chatId !== chatIdValue || page.sessionRevision !== revision) {
      var refreshed = await loadChatState(chatIdValue);
      if (state.activeChatId === chatIdValue && state.messageStartIndex === beforeIndex &&
          state.chatProjectionRevisions[chatIdValue] === revision &&
          state.chatNavigationVersion === navigationVersion)
        applyChatState(refreshed);
      return;
    }
    var older = page.messages || [];
    if (page.startIndex < 0 || page.startIndex + older.length !== beforeIndex) {
      throw new Error("Неверная граница страницы истории чата.");
    }
    var box = $("messages");
    var previousScrollHeight = box ? box.scrollHeight : 0;
    state.artifacts = state.artifacts || [];
    var knownArtifacts = Object.create(null);
    (state.artifacts || []).forEach(function (artifact) { knownArtifacts[String(artifact.id || artifact.Id || "").toLowerCase()] = true; });
    (page.artifacts || []).forEach(function (artifact) {
      var id = String(artifact.id || artifact.Id || "").toLowerCase();
      if (id && !knownArtifacts[id]) { state.artifacts.push(artifact); knownArtifacts[id] = true; }
    });
    var library = state.artifactLibrary || {};
    var removed = library.removedResourceUris || library.RemovedResourceUris || [];
    var knownRemoved = Object.create(null);
    removed.forEach(function (uri) { knownRemoved[String(uri).toLowerCase()] = true; });
    (page.removedResourceUris || []).forEach(function (uri) {
      var key = String(uri).toLowerCase();
      if (key && !knownRemoved[key]) { removed.push(uri); knownRemoved[key] = true; }
    });
    library.removedResourceUris = removed;
    state.messages = older.concat(state.messages || []).slice(0, 240);
    state.messageStartIndex = page.startIndex;
    state.messageTotalCount = page.totalCount;
    renderMessages({ preserveTop: true, previousScrollHeight: previousScrollHeight });
    renderChatSessions();
  } catch (error) {
    log(error.detail || error.message, "error");
  } finally {
    state.messagePageBusy = false;
    if (button) button.disabled = false;
  }
}

async function jumpToLatestChatMessages() {
  if (!state.activeChatId) return;
  var chatIdValue = state.activeChatId;
  var navigationVersion = state.chatNavigationVersion;
  var revision = state.chatProjectionRevisions[chatIdValue];
  try {
    var response = await loadChatState(chatIdValue);
    if (state.activeChatId === chatIdValue && state.chatNavigationVersion === navigationVersion &&
        state.chatProjectionRevisions[chatIdValue] === revision)
      applyChatState(response);
  } catch (error) {
    log(error.detail || error.message, "error");
  }
}

async function openActiveDocument(chatIdValue) {
  var targetChatId = typeof chatIdValue === "string" ? chatIdValue : state.activeChatId;
  if (!targetChatId) {
    return;
  }
  setControlBusy("openDocumentButton", true);
  try {
    var mailboxChat = (state.chats || []).find(function (chat) { return chatId(chat) === targetChatId; });
    if (mailboxChat && isOutlookMailboxKey(chatDocumentKey(mailboxChat))) {
      await attachOutlookMailbox(chatDocumentKey(mailboxChat), targetChatId, false);
      return;
    }
    var result = await navigateChat("openDocument", { chatId: targetChatId });
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
  try {
    if (isOutlookMailboxKey(documentKey)) {
      await attachOutlookMailbox(documentKey, "", false);
      return;
    }
    await navigateChat("activateDocument", { documentKey: documentKey });
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

  try {
    await navigateChat("deleteDocument", { host: host, documentKey: documentKey });
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

  try {
    await navigateChat("renameChat", { chatId: targetChatId, title: title.trim() });
    log("Чат переименован.");
  } catch (error) {
    log(error.detail || error.message, "error");
  }
}

async function clearChat() {
  if (state.chatNavigationPending || !state.activeChatId ||
      (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
       !confirmDiscardHtmlWorkspaceChanges("Очистить чат")) ||
      !window.confirm("Очистить этот чат?")) {
    return;
  }

  setControlBusy("clearChatButton", true);
  var targetChatId = state.activeChatId;
  try {
    await navigateChat("clearChat", { chatId: targetChatId });
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

  try {
    await navigateChat("deleteChat", { chatId: targetChatId });
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
  if (!state.activeChatId || state.chatNavigationPending) {
    return;
  }

  try {
    await navigateChat("forkChat", { chatId: state.activeChatId, id: messageId(message), index: index });
    clearSendError();
    log("Ветка чата создана.");
  } catch (error) {
    log(error.detail || error.message, "error");
  }
}

function renderVisibleSecondarySurfaces() {
  if (typeof isPanelActive === "function" && isPanelActive("instructions") && typeof renderInstructions === "function") {
    renderInstructions();
  }
  if (typeof isPanelActive === "function" && isPanelActive("artifacts") && typeof renderHtmlWorkspace === "function") {
    renderHtmlWorkspace();
  }
  if (typeof isPanelActive === "function" && isPanelActive("vba")) {
    if (typeof renderVbaProject === "function") renderVbaProject();
    if (typeof updateVbaMacroRunState === "function") updateVbaMacroRunState();
  }
}

function applyInitState(init) {
  state.chatStateApplyVersion = (state.chatStateApplyVersion || 0) + 1;
  init = init || {};
  var previousChatId = state.activeChatId || "";
  var nextChatId = init.activeChatId || init.ActiveChatId || "";
  var chatChanged = previousChatId !== nextChatId;
  if (chatChanged) {
    captureChatDraft(previousChatId);
    if (typeof cancelVbaModuleRead === "function") cancelVbaModuleRead();
    if (typeof cancelVbaModuleWrite === "function") cancelVbaModuleWrite();
  }
  state.bridgeUnavailable = false;
  document.body.classList.remove("bridge-unavailable");
  resetMessageEditState();
  state.appVersion = init.appVersion || init.AppVersion || "";
  state.host = init.host;
  state.officeHostChatAvailable = !!(init.officeHostChatAvailable || init.OfficeHostChatAvailable);
  state.title = init.title;
  state.officeContext = init.officeContext || null;
  state.bridgeToken = init.bridgeToken || init.BridgeToken || state.bridgeToken || "";
  state.settings = init.settings || {};
  if (typeof resetModelCatalog === "function") resetModelCatalog();
  state.prompts = init.prompts;
  if (typeof releasePromptEditorContext === "function") releasePromptEditorContext();
  if (typeof window.cancelHtmlWorkspaceWrite === "function") window.cancelHtmlWorkspaceWrite();
  if (typeof window.cancelHtmlWorkspaceRead === "function") window.cancelHtmlWorkspaceRead();
  state.hasApiKey = !!(init.hasApiKey || init.HasApiKey);
  state.hasHistorySecret = !!(init.hasHistorySecret || init.HasHistorySecret);
  if (typeof cancelToolLibraryWrite === "function") cancelToolLibraryWrite();
  if (typeof cancelToolSourceRead === "function") cancelToolSourceRead();
  if (typeof cancelToolDocumentationRead === "function") cancelToolDocumentationRead();
  state.tools = toolLibraryItemsFromContract(init.tools);
  if (typeof cancelSkillSourceRead === "function") cancelSkillSourceRead();
  if (typeof cancelSkillSourceWrite === "function") cancelSkillSourceWrite();
  state.skills = skillLibraryItemsFromContract(init.skills);
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
  state.messageStartIndex = init.messageStartIndex || init.MessageStartIndex || 0;
  state.messageTotalCount = init.messageTotalCount || init.MessageTotalCount || state.messages.length;
  state.artifacts = init.artifacts || init.Artifacts || [];
  state.artifactLibrary = init.artifactLibrary || init.ArtifactLibrary || { sessionRevision: 0, heads: [] };
  state.activeContextCheckpointId = init.activeContextCheckpointId || init.ActiveContextCheckpointId || "";
  state.activeHtmlArtifactId = init.activeHtmlArtifactId || init.ActiveHtmlArtifactId || "";
  state.activeTaskListArtifactId = init.activeTaskListArtifactId || init.ActiveTaskListArtifactId || "";
  state.activePlanDocumentArtifactId = init.activePlanDocumentArtifactId || init.ActivePlanDocumentArtifactId || "";
  if (chatChanged) restoreChatDraft(state.activeChatId);
  $("toolsPath").textContent = state.toolsPath ? "Хранилище: " + state.toolsPath : "";
  if ($("skillsPath")) $("skillsPath").textContent = state.skillsPath ? "Хранилище: " + state.skillsPath : "";
  renderSettings();
  renderChatSessions();
  renderMessages();
  renderContextMeter();
  renderVisibleSecondarySurfaces();
  log("Initialized " + init.host);
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
  if (typeof cancelVbaModuleRead === "function") cancelVbaModuleRead();
  if (typeof cancelVbaModuleWrite === "function") cancelVbaModuleWrite();
  if (typeof cancelSkillSourceRead === "function") cancelSkillSourceRead();
  if (typeof cancelSkillSourceWrite === "function") cancelSkillSourceWrite();
  if (typeof cancelToolLibraryWrite === "function") cancelToolLibraryWrite();
  if (typeof cancelToolSourceRead === "function") cancelToolSourceRead();
  if (typeof cancelToolDocumentationRead === "function") cancelToolDocumentationRead();
  state.bridgeToken = "";
  if (typeof releasePromptEditorContext === "function") releasePromptEditorContext();
  if (typeof window.cancelHtmlWorkspaceWrite === "function") window.cancelHtmlWorkspaceWrite();
  if (typeof window.cancelHtmlWorkspaceRead === "function") window.cancelHtmlWorkspaceRead();
  document.body.classList.add("bridge-unavailable");
  resetMessageEditState();
  state.appVersion = "";
  state.hasHistorySecret = false;
  state.host = "";
  state.officeHostChatAvailable = false;
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
  state.artifactLibrary = { sessionRevision: 0, heads: [] };
  state.activeContextCheckpointId = "";
  state.activeHtmlArtifactId = "";
  state.activePlanDocumentArtifactId = "";
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
  renderChatSessions();
  renderMessages();
  renderContextMeter();
  renderVisibleSecondarySurfaces();
  renderModelControls();
  renderSendControls();
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

function payloadActiveChatId(payload) {
  payload = payload || {};
  return payload.activeChatId || payload.ActiveChatId || "";
}

function catalogChatSummary(payload, id) {
  var chats = (payload && (payload.chats || payload.Chats)) || [];
  for (var index = 0; index < chats.length; index++) {
    if (chatId(chats[index]) === id) return chats[index];
  }
  return null;
}

function catalogChatRevision(payload, id) {
  var summary = catalogChatSummary(payload, id);
  return summary ? window.RNAssistantRunViewState.chatRevision(summary) : null;
}

function shouldReloadChatDetailFromCatalog(payload) {
  var responseChatId = payloadActiveChatId(payload);
  if (!responseChatId || currentActiveSend()) return false;
  if (responseChatId !== state.activeChatId) return true;
  var revision = catalogChatRevision(payload, responseChatId);
  if (revision === null) return false;
  var known = Object.prototype.hasOwnProperty.call(state.chatProjectionRevisions || {}, responseChatId)
    ? state.chatProjectionRevisions[responseChatId]
    : null;
  return known === null || revision > known;
}

async function loadChatState(chatIdValue) {
  var targetChatId = chatIdValue || state.activeChatId || "";
  if (!targetChatId) return initialize();
  return send("getChatState", { chatId: targetChatId });
}

async function synchronizeChatState(force) {
  // DOM focus events are not an explicit forced refresh.
  force = force === true;
  if (state.bridgeUnavailable || state.chatNavigationPending || state.initializePromise || state.officeHostChatPending ||
      (!force && (document.hidden || !document.hasFocus() || currentActiveSend()))) return;
  if (state.chatSyncPromise) {
    var pendingSync = state.chatSyncPromise;
    if (!force) return pendingSync;
    await pendingSync;
    if (state.chatNavigationPending || state.initializePromise || state.officeHostChatPending) return;
    if (state.chatSyncPromise && state.chatSyncPromise !== pendingSync) return state.chatSyncPromise;
  }
  var navigationVersion = state.chatNavigationVersion || 0;
  var stateApplyVersion = state.chatStateApplyVersion || 0;
  state.chatSyncPromise = (async function () {
    try {
      var response = await send("listChats", {});
      var current = { activeChatId: state.activeChatId, chats: state.chats, documents: state.documents };
      if (!state.chatNavigationPending && navigationVersion === state.chatNavigationVersion &&
          stateApplyVersion === state.chatStateApplyVersion &&
          chatNavigationSignature(response) !== chatNavigationSignature(current)) {
        var responseChatId = payloadActiveChatId(response);
        var reloadDetail = shouldReloadChatDetailFromCatalog(response);
        applyChatCatalogState(response);
        if (reloadDetail) {
          var detailNavigationVersion = state.chatNavigationVersion || 0;
          var detail = await loadChatState(responseChatId);
          if (!state.chatNavigationPending && detailNavigationVersion === state.chatNavigationVersion && !currentActiveSend()) {
            applyChatState(detail);
          }
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
  state.initializePromise = (async function () {
    try {
      await navigateChat("init");
    } catch (error) {
      applyBridgeUnavailableState(error);
    } finally {
      state.initializePromise = null;
      if (typeof renderSendControls === "function") renderSendControls();
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
