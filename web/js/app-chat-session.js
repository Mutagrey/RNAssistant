function beginChatNavigation() {
  state.chatNavigationVersion = (state.chatNavigationVersion || 0) + 1;
  return state.chatNavigationVersion;
}

function applyChatNavigationState(response, version, type) {
  if (version !== state.chatNavigationVersion) return false;
  return applyChatState(response, type === "deleteChat");
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
  var selectionSource = type === "selectChat" ? officeChatSource(payload.chatId) : null;
  var selectionView = selectionSource && window.RNAssistantConversationPanel ? RNAssistantConversationPanel.selected() : null;
  var clock = function () { return window.performance && window.performance.now ? window.performance.now() : Date.now(); };
  var startedAt = clock(), sentAt = null, queueMs = 0;
  var timing = window.RNAssistantPerformanceDiagnostics;
  var timingId = timing ? timing.beginNavigation(type) : null;
  var previous = state.chatNavigationPromise;
  var selectionVersion = 0;
  if (type === "selectChat") {
    state.chatSelectionRequestVersion = (state.chatSelectionRequestVersion || 0) + 1;
    selectionVersion = state.chatSelectionRequestVersion;
    state.chatNavigationTargetId = payload.chatId;
  }
  function selectionIsCurrent() {
    return !selectionVersion || !window.RNAssistantConversationPanel ||
      selectionVersion === state.chatSelectionRequestVersion && RNAssistantConversationPanel.selected() === selectionView &&
        (!selectionView || selectionView.isCurrent());
  }
  state.chatNavigationPending = (state.chatNavigationPending || 0) + 1;
  if (selectionVersion && typeof renderChatSessions === "function") renderChatSessions();
  if (typeof renderSendControls === "function") renderSendControls();
  var navigation = (async function () {
    if (previous) await previous.catch(function () {});
    if (state.chatSyncPromise) await state.chatSyncPromise;
    if (state.chatPreferencePromise) await state.chatPreferencePromise.catch(function () {});
    if (selectionVersion && (selectionVersion !== state.chatSelectionRequestVersion || !selectionIsCurrent())) {
      if (timingId) timing.updateNavigation(timingId, { phase: "skipped" });
      return null;
    }
    if (typeof cancelModelCatalogLoad === "function") await cancelModelCatalogLoad();
    if (selectionVersion && (selectionVersion !== state.chatSelectionRequestVersion || !selectionIsCurrent())) {
      if (timingId) timing.updateNavigation(timingId, { phase: "skipped" });
      return null;
    }
    var version = beginChatNavigation();
    sentAt = clock(); queueMs = sentAt - startedAt;
    if (timingId) timing.updateNavigation(timingId, { phase: "waiting", queueMs: Math.round(queueMs) });
    var response = await send(type, payload);
    var bridgeMs = clock() - sentAt;
    // Native binding completes in order. Presentation belongs to the current view;
    // an older switch must not reopen a closed tab or replace a newer selection.
    var selectedState = response && (response.init || response.state || response);
    if (selectionSource) selectionSource.accept(selectedState);
    var selectionCurrent = selectionIsCurrent();
    var appliedNavigation = true;
    if (type === "init" || response.init) applyInitState(response.init || response, { preserveSelection: !selectionCurrent });
    else if (!selectionCurrent) { applyChatCatalogState(selectedState); appliedNavigation = false; }
    else if (response.state) appliedNavigation = applyChatNavigationState(selectedState, version, type);
    else if (response.activeChatId !== undefined)
      appliedNavigation = applyChatNavigationState(selectedState, version, type);
    if (type === "selectChat" && selectionCurrent && !appliedNavigation && state.chatDetailPending &&
        state.activeChatId === payload.chatId) {
      state.chatDetailPending = false;
      state.chatDetailError = "Состояние чата изменилось. Повторите загрузку.";
      renderChatSessions();
      renderMessages();
    }
    var renderMs = clock() - sentAt - bridgeMs;
    if (timingId) timing.updateNavigation(timingId, { phase: "completed", queueMs: Math.round(queueMs),
      bridgeMs: Math.round(bridgeMs), renderMs: Math.round(renderMs), messages: (state.messages || []).length });
    if (queueMs + bridgeMs + renderMs >= (type === "init" ? 500 : 250)) send("reportClientTiming", {
      kind: type === "init" ? "startup" : "chatNavigation",
      queueMs: Math.round(queueMs), bridgeMs: Math.round(bridgeMs), renderMs: Math.round(renderMs),
      messages: (state.messages || []).length
    }).catch(function () {});
    return selectionCurrent ? response : null;
  })();
  state.chatNavigationPromise = navigation;
  var finished = function () {
    state.chatNavigationPending -= 1;
    if (state.chatNavigationPromise === navigation) state.chatNavigationPromise = null;
    if (selectionVersion && selectionVersion === state.chatSelectionRequestVersion) {
      state.chatNavigationTargetId = "";
      if (typeof renderChatSessions === "function") renderChatSessions();
    }
    if (typeof renderSendControls === "function") renderSendControls();
  };
  navigation.then(finished, function (error) {
    if (timingId) timing.updateNavigation(timingId, { phase: "failed",
      queueMs: Math.round(sentAt ? queueMs : clock() - startedAt),
      bridgeMs: Math.round(sentAt ? clock() - sentAt : 0) });
    if (type === "selectChat" && selectionIsCurrent() && state.chatDetailPending && state.activeChatId === payload.chatId) {
      state.chatDetailPending = false;
      state.chatDetailError = error.detail || error.message || "Не удалось загрузить чат";
      renderChatSessions();
      renderMessages();
    }
    finished();
  });
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
  if (state.chatNavigationPending || !documentItem || !documentItem.documentKey) {
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
  if (!id) return;
  var alreadySelected = id === state.activeChatId && !state.chatNavigationPending && activeChatUsesCurrentDocument();

  // Placement follows the clicked target immediately. Native selection still
  // serializes document binding/persistence, but does not block an existing view.
  var chat = (state.chats || []).find(function (item) { return chatId(item) === id; });
  if (window.RNAssistantConversationPanel && (alreadySelected || chat && chatIsCurrentDocument(chat))) {
    if (!RNAssistantConversationPanel.open({ source: "office", sessionId: id })) return;
  }
  if (alreadySelected) return;

  try {
    if (!await navigateChat("selectChat", { chatId: id })) return;
    clearSendError();
    log("Чат открыт.");
  } catch (error) {
    log(error.detail || error.message, "error");
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
      !RNAssistantHtmlWorkspaceSurface.canDelete({ host: host, documentKey: documentKey }) ||
      !window.confirm("Удалить документ «" + (title || "Документ") + "» из истории вместе со всеми чатами? Сам Office-файл удалён не будет.")) {
    return;
  }

  try {
    await navigateChat("deleteDocument", { host: host, documentKey: documentKey });
    RNAssistantHtmlWorkspaceSurface.closeDeleted({ host: host, documentKey: documentKey });
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
  if (!targetChatId || state.chatDeletions[targetChatId] ||
      !RNAssistantHtmlWorkspaceSurface.canDelete({ chatId: targetChatId }) ||
      !window.confirm("Удалить этот чат?")) {
    return;
  }

  state.chatDeletions[targetChatId] = true;
  renderChatSessions({ navigationOnly: true });
  try {
    await window.RNAssistantChatDrafts.flushOffice(targetChatId);
    await navigateChat("deleteChat", { chatId: targetChatId });
    RNAssistantConversationPanel.closeDeleted({ source: "office", sessionId: targetChatId });
    RNAssistantHtmlWorkspaceSurface.closeDeleted({ chatId: targetChatId });
    window.RNAssistantChatDrafts.forgetOffice(targetChatId);
    clearSendError();
    log("Чат удален.");
  } catch (error) {
    log(error.detail || error.message, "error");
  } finally {
    delete state.chatDeletions[targetChatId];
    renderChatSessions({ navigationOnly: true });
  }
}

function deleteMessage(message, index, scope) { return (scope || chatComposer().capture()).invoke("messageAction", { name: "delete", message: message, index: index }); }
function forkChatAtMessage(message, index) { return chatComposer().dispatch("messageAction", { name: "fork", message: message, index: index }); }

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

function applyInitState(init, options) {
  init = init || {};
  state.bridgeUnavailable = false;
  state.bridgeFailure = null;
  document.body.classList.remove("bridge-unavailable");
  state.appVersion = init.appVersion || "";
  state.host = init.host;
  state.officeHostChatAvailable = !!(init.officeHostChatAvailable);
  state.title = init.title;
  state.officeContext = init.officeContext || null;
  state.settings = init.settings || {};
  if (typeof resetModelCatalog === "function") resetModelCatalog();
  state.prompts = init.prompts;
  if (typeof releasePromptEditorContext === "function") releasePromptEditorContext();
  state.hasApiKey = !!(init.hasApiKey);
  state.hasHistorySecret = !!(init.hasHistorySecret);
  if (typeof cancelToolLibraryWrite === "function") cancelToolLibraryWrite();
  if (typeof cancelToolSourceRead === "function") cancelToolSourceRead();
  if (typeof cancelToolDocumentationRead === "function") cancelToolDocumentationRead();
  if (typeof cancelSkillSourceRead === "function") cancelSkillSourceRead();
  if (typeof cancelSkillSourceWrite === "function") cancelSkillSourceWrite();
  state.tools = [];
  state.skills = [];
  if (typeof resetInstructionLibraries === "function") resetInstructionLibraries();
  state.toolLibraryDirty = false;
  state.skillLibraryDirty = false;
  state.toolsPath = init.toolsPath || "";
  state.skillsPath = init.skillsPath || "";
  // Init and navigation apply one chat projection. The bridge owns its token.
  state.chatProjectionRevisions = {};
  state.chats = [];
  state.htmlWorkspace = null;
  if (options && options.preserveSelection) applyChatCatalogState(init);
  else { clearOfficeChatProjection(); applyChatState(init); }
  $("toolsPath").textContent = state.toolsPath ? "Хранилище: " + state.toolsPath : "";
  if ($("skillsPath")) $("skillsPath").textContent = state.skillsPath ? "Хранилище: " + state.skillsPath : "";
  renderSettings();
  renderVisibleSecondarySurfaces();
  if (window.RNAssistantConversationPanel) RNAssistantConversationPanel.ready();
  log("Initialized " + init.host);
  if (init.quickAction && !init.detailDeferred && !(options && options.preserveSelection)) {
    runQuickAction(init.quickAction);
  }
}

function applyBridgeUnavailableState(error) {
  var previousChatId = state.activeChatId || "";
  captureChatDraft(previousChatId);
  state.bridgeUnavailable = true;
  state.bridgeFailure = {
    code: error && error.code || "bridge_init_failed",
    message: error && error.message || "Не удалось загрузить панель RN Assistant."
  };
  if (typeof cancelVbaModuleRead === "function") cancelVbaModuleRead();
  if (typeof cancelVbaModuleWrite === "function") cancelVbaModuleWrite();
  if (typeof cancelSkillSourceRead === "function") cancelSkillSourceRead();
  if (typeof cancelSkillSourceWrite === "function") cancelSkillSourceWrite();
  if (typeof cancelToolLibraryWrite === "function") cancelToolLibraryWrite();
  if (typeof cancelToolSourceRead === "function") cancelToolSourceRead();
  if (typeof cancelToolDocumentationRead === "function") cancelToolDocumentationRead();
  state.bridgeToken = "";
  state.bridgeOperations = null;
  if (typeof releasePromptEditorContext === "function") releasePromptEditorContext();
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
  state.chatDetailPending = false;
  state.chatDetailError = "";
  state.chatProjectionRevisions = {};
  clearOfficeChatProjection();
  state.tools = [];
  state.skills = [];
  if (typeof acceptToolLibraryState === "function") acceptToolLibraryState();
  if (typeof acceptSkillLibraryState === "function") acceptSkillLibraryState();
  state.vba = { modules: [], backups: [], selectedModule: "" };
  state.toolsPath = "";
  state.skillsPath = "";
  state.htmlWorkspace = { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [], redoBranches: [], recovery: { status: "empty", canMutate: true, candidates: [] } };
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
  log(state.bridgeFailure.message, "error");
}

function chatNavigationSignature(payload) {
  var chats = payload.chats || [];
  var documents = payload.documents || [];
  return JSON.stringify({
    activeChatId: payload.activeChatId || "",
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
  return payload.activeChatId || "";
}

function catalogChatSummary(payload, id) {
  var chats = (payload && (payload.chats)) || [];
  for (var index = 0; index < chats.length; index++) {
    if (chatId(chats[index]) === id) return chats[index];
  }
  return null;
}

function catalogChatRevision(payload, id) {
  var summary = catalogChatSummary(payload, id);
  return summary ? window.RNAssistantRunViewState.chatRevision(summary) : null;
}

function shouldReloadChatDetailFromCatalog(payload, selectedChatId) {
  var responseChatId = selectedChatId || payloadActiveChatId(payload);
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
  return officeChatSource(targetChatId).read(function (target) {
    return send("getChatState", { chatId: target.sessionId });
  });
}

async function retryChatDetail() {
  if (!state.chatDetailError || state.chatNavigationPending) return;
  if (!state.activeChatId) return initialize();
  var targetChatId = state.activeChatId;
  var version = state.chatNavigationVersion;
  var projection = officeChatProjection();
  state.chatDetailPending = true;
  state.chatDetailError = "";
  renderChatSessions();
  renderMessages();
  if (typeof renderSendControls === "function") renderSendControls();
  try {
    var detail = await loadChatState(targetChatId);
    if (projection === officeChatProjection() && state.activeChatId === targetChatId && state.chatNavigationVersion === version &&
        !applyChatState(detail)) {
      state.chatDetailPending = false;
      state.chatDetailError = "Состояние чата изменилось. Повторите загрузку.";
      renderChatSessions();
      renderMessages();
      if (typeof renderSendControls === "function") renderSendControls();
    }
  } catch (error) {
    if (projection === officeChatProjection() && state.activeChatId === targetChatId && state.chatNavigationVersion === version) {
      state.chatDetailPending = false;
      state.chatDetailError = error.detail || error.message || "Не удалось загрузить чат";
      renderChatSessions();
      renderMessages();
      if (typeof renderSendControls === "function") renderSendControls();
    }
  }
}

async function synchronizeChatState(force) {
  // DOM focus events are not an explicit forced refresh.
  force = force === true;
  if (state.bridgeUnavailable || state.chatNavigationPending || state.initializePromise || state.officeHostChatPending ||
      (!force && (document.hidden || !document.hasFocus() || currentActiveSend() ||
        typeof isPanelActive === "function" && !isPanelActive("chat") ||
        window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() !== "office"))) return;
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
        var panels = window.RNAssistantConversationPanel;
        var component = panels && panels.office(state.activeChatId);
        var responseChatId = panels ? component && component.target.sessionId : payloadActiveChatId(response);
        var reloadDetail = (!panels || !!component) && shouldReloadChatDetailFromCatalog(response, responseChatId);
        applyChatCatalogState(response);
        if (reloadDetail) {
          // Catalog selection is a backend pointer, not permission to open a tab.
          // The view observer owns late reads, visibility and disposal.
          if (component) { await component.refresh(); return; }
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
  state.initializePromise = (async function () {
    try {
      var init = await navigateChat("init");
      try { await window.RNAssistantChatDrafts.hydrate(); }
      catch (error) {
        window.RNAssistantChatDrafts.hydrationFailed(error);
        log("Не удалось восстановить черновики: " + (error.detail || error.message), "error");
      }
      if (init.detailDeferred) {
        try {
          await navigateChat("getChatState", {});
          if (init.quickAction) runQuickAction(init.quickAction);
        } catch (error) {
          // A slow/unavailable history must not revoke the handshake or settings.
          state.chatDetailPending = false;
          state.chatDetailError = error.detail || error.message || "Не удалось загрузить историю чатов";
          renderChatSessions();
          renderMessages();
          log(state.chatDetailError, "error");
        }
      }
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
  if (!window.confirm("Удалить локальные чаты, контекст чатов и резервные копии VBA? Настройки, API-ключ, пользовательские инструменты и навыки останутся.")) {
    return;
  }

  setControlBusy("clearRuntimeDataButton", true);
  try {
    await window.RNAssistantChatDrafts.flushAll();
    var init = await send("clearRuntimeData", {});
    RNAssistantConversationPanel.each(function (component) { RNAssistantConversationPanel.closeDeleted(component.target); });
    window.RNAssistantChatDrafts.reset();
    state.activeChatId = "";
    state.draftAttachments = [];
    applyInitState(init);
    log("Локальные данные очищены.");
  } catch (error) {
    log(error.detail || error.message, "error");
  } finally {
    setControlBusy("clearRuntimeDataButton", false);
  }
}
