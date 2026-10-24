function sortedChatSessions(chats) {
  return (chats || []).slice().sort(function (left, right) {
    var leftTime = Date.parse(left.LastActivityUtc || left.lastActivityUtc) || 0;
    var rightTime = Date.parse(right.LastActivityUtc || right.lastActivityUtc) || 0;
    if (leftTime !== rightTime) return rightTime - leftTime;
    var leftId = chatId(left);
    var rightId = chatId(right);
    return leftId < rightId ? -1 : (leftId > rightId ? 1 : 0);
  });
}

function renderChatSessions() {
  if (typeof isPanelActive === "function" && !isPanelActive("chat")) return;
  var select = $("chatSessionSelect");
  if (!select) {
    return;
  }

  select.innerHTML = "";
  var chats = sortedChatSessions(state.chats);
  if (!chats.length) {
    var empty = document.createElement("option");
    empty.value = "";
    empty.textContent = state.bridgeUnavailable ? "Панель недоступна" : "Нет чатов";
    select.appendChild(empty);
  }
  chats.forEach(function (chat) {
    var option = document.createElement("option");
    option.value = chatId(chat);
    option.textContent = chatTitle(chat);
    select.appendChild(option);
  });
  select.value = state.activeChatId || "";
  select.disabled = !chats.length || state.bridgeUnavailable;
  renderChatSessionList(chats);

  var activeChat = activeChatSummary();
  var isCurrentDocument = !activeChat || chatIsCurrentDocument(activeChat);
  $("activeChatTitle").textContent = activeChat ? chatTitle(activeChat) : "Новый чат";
  var subtitle = [];
  if (state.activeChatId) {
    var visibleCount = (state.messages || []).filter(function (message) { return !messageProtocolMessage(message); }).length;
    subtitle.push(state.chatDetailPending ? "Загрузка чата…" : state.chatDetailError ? "Ошибка загрузки чата" : state.messageStartIndex > 0
      ? "Показано " + visibleCount + " · история доступна выше"
      : formatChatMessageCount(visibleCount));
  }
  if (activeChat) {
    subtitle = subtitle.concat([chatDocumentTitle(activeChat), chatHost(activeChat)].filter(Boolean));
  }
  $("activeChatSubtitle").textContent = subtitle.join(" · ");
  $("documentNotice").classList.toggle("hidden", isCurrentDocument);
  $("openDocumentButton").hidden = isCurrentDocument || !chatDocumentPath(activeChat);

  var hasActive = !!state.activeChatId;
  var compactableMessages = (state.messages || []).filter(function (message) {
    return !messageActivity(message);
  }).length;
  $("newChatButton").disabled = !!state.bridgeUnavailable;
  if ($("compactContextButton")) {
    $("compactContextButton").disabled = !hasActive || compactableMessages < 3 || !!currentActiveSend();
    $("compactContextButton").hidden = !hasActive || compactableMessages < 3;
  }
  renderSendControls();
  if (window.RNAssistantChatInspector) window.RNAssistantChatInspector.render();
}

function activeChatSummary() {
  return (state.chats || []).filter(function (chat) {
    return chatId(chat) === state.activeChatId;
  })[0] || null;
}

function chatSummaryRunViewState(chat) {
  return window.RNAssistantRunViewState.fromChatSummary(chat);
}

function formatChatMessageCount(count) {
  count = Math.max(0, Number(count) || 0);
  var mod100 = count % 100;
  var mod10 = count % 10;
  var noun = mod100 >= 11 && mod100 <= 14
    ? "сообщений"
    : (mod10 === 1 ? "сообщение" : (mod10 >= 2 && mod10 <= 4 ? "сообщения" : "сообщений"));
  return count + " " + noun;
}

function activeChatUsesCurrentDocument() {
  var active = activeChatSummary();
  return !active || chatIsCurrentDocument(active);
}

function chatDraftStore() {
  state.chatDrafts = state.chatDrafts || {};
  return state.chatDrafts;
}

var chatDraftVersion = 0;
var workspacePendingInputs = Object.create(null);
var draftWrites = Object.create(null);
var draftStoreReady = false;
var draftStoreLoadError = null;
var draftStoreGeneration = 0;
function persistentDraft(key) {
  var saved = chatDraftStore()[key];
  if (key.indexOf("office:") === 0) {
    var chatIdValue = key.slice(7), officePending = (state.inboxSubmissions || {})[chatIdValue];
    return { Owner: "office", ChatId: chatIdValue, Text: saved ? saved.text : "",
      ResourceDraftIds: saved ? (saved.attachments || []).map(officeDraftAttachmentId) : [],
      NextAnnotationNumber: saved ? saved.nextAnnotationNumber || 1 : 1,
      PendingOfficeInput: officePending && !officePending.Answers ? { OperationId: officePending.OperationId,
        Text: officePending.Text, ResourceDraftIds: officePending.Attachments.map(officeDraftAttachmentId),
        Delivery: officePending.Delivery } : null };
  }
  var pair = JSON.parse(key.slice(10)), pending = workspacePendingInputs[key];
  return { Owner: "workspace", WorkspaceId: pair[0], SessionId: pair[1],
    Text: saved ? saved.text : "", Mode: saved ? saved.mode : "agent", Reasoning: saved ? saved.reasoning : false,
    ResourceDraftIds: [], PendingInput: pending ? { RequestId: pending.requestId, Message: pending.message,
      Mode: pending.Mode, Reasoning: pending.ReasoningEnabled } : null };
}
function persistDraft(key) {
  if (!key || !draftStoreReady) return Promise.resolve();
  var entry = draftWrites[key] || (draftWrites[key] = { revision: 0, saved: 0, running: null });
  ++entry.revision;
  if (!entry.running) {
    var generation = draftStoreGeneration;
    entry.running = (async function () {
      while (entry.saved !== entry.revision && generation === draftStoreGeneration) {
        var revision = entry.revision, snapshot = persistentDraft(key);
        var result = await send("saveChatDraft", snapshot);
        if (!result || result.saved !== true) throw new Error("Черновик не был сохранён.");
        entry.saved = revision;
      }
    })();
    entry.running.then(function () { entry.running = null; }, function (error) {
      entry.running = null;
      log("Черновик не сохранён: " + (error.detail || error.message), "error");
    });
  }
  return entry.running;
}
function requirePersistedDraft(key) {
  if (!draftStoreReady) return Promise.reject(draftStoreLoadError || new Error("Черновики ещё не восстановлены."));
  return persistDraft(key);
}
function flushWorkspaceDraft(target) { return requirePersistedDraft(workspaceDraftKey(target)); }
function flushOfficeDraft(chatIdValue) { return requirePersistedDraft(officeDraftKey(chatIdValue)); }
async function hydrateChatDrafts() {
  if (draftStoreReady) return;
  var response = await send("getChatDrafts", {});
  (response.drafts || []).forEach(function (view) {
    var item = view.draft || {}, key = item.Owner === "office" ? officeDraftKey(item.ChatId) :
      workspaceDraftKey({ workspaceId: item.WorkspaceId, sessionId: item.SessionId });
    if (!key || chatDraftStore()[key]) return;
    if (item.Owner === "office") {
      var restoredAttachments = (item.ResourceDraftIds || []).map(function (id) {
        return (view.attachments || []).find(function (attachment) { return officeDraftAttachmentId(attachment) === id; }) ||
          { Id: id, FileName: "Недоступное вложение", Kind: "file", Size: 0,
            ExtractionWarning: "Подготовленный файл недоступен. Проверьте историю отправки или удалите вложение.", unavailable: true };
      });
      chatDraftStore()[key] = { text: item.Text || "", attachments: restoredAttachments,
        nextAnnotationNumber: item.NextAnnotationNumber || 1, version: ++chatDraftVersion };
      var officePending = item.PendingOfficeInput;
      if (officePending) {
        var attachmentIds = officePending.ResourceDraftIds || [];
        var pendingAttachments = attachmentIds.map(function (id) {
          return restoredAttachments.find(function (attachment) { return officeDraftAttachmentId(attachment) === id; }) ||
            { Id: id, unavailable: true };
        });
        var fingerprint = JSON.stringify([item.ChatId, officePending.Text, attachmentIds, officePending.Delivery, null]);
        var submission = { fingerprint: fingerprint, OperationId: officePending.OperationId,
          Text: officePending.Text, Attachments: pendingAttachments, Delivery: officePending.Delivery,
          Answers: null, localStatus: "unconfirmed" };
        state.inboxSubmissions = state.inboxSubmissions || {};
        state.inboxSubmissions[item.ChatId] = submission;
        if (typeof chatInputEchoes === "function") chatInputEchoes(item.ChatId).push(submission);
      }
      if (view.attachmentError) {
        if (typeof setAttachmentStatus === "function") setAttachmentStatus(item.ChatId, view.attachmentError, "warning");
        else log(view.attachmentError, "warning");
      }
    } else if (item.Owner === "workspace") {
      chatDraftStore()[key] = { text: item.Text || "", mode: item.Mode || "agent",
        reasoning: !!item.Reasoning, version: ++chatDraftVersion };
      var pending = item.PendingInput;
      if (pending) workspacePendingInputs[key] = { sessionId: item.SessionId, requestId: pending.RequestId,
        message: pending.Message, Mode: pending.Mode, ReasoningEnabled: !!pending.Reasoning };
    }
  });
  draftStoreReady = true;
  draftStoreLoadError = null;
  if (state.activeChatId) restoreChatDraft(state.activeChatId);
}
function resetChatDrafts() {
  ++draftStoreGeneration;
  state.chatDrafts = {};
  state.inboxSubmissions = {};
  state.chatInputEchoes = {};
  workspacePendingInputs = Object.create(null);
  draftWrites = Object.create(null);
  draftStoreReady = true;
  draftStoreLoadError = null;
}
function failChatDraftHydration(error) { draftStoreLoadError = error; }
function flushAllChatDrafts() {
  return Promise.all(Object.keys(draftWrites).map(function (key) {
    return draftWrites[key].running || Promise.resolve();
  }));
}
function forgetOfficeDraft(chatIdValue) {
  var key = officeDraftKey(chatIdValue);
  if (key) { delete chatDraftStore()[key]; persistDraft(key).catch(function () {}); }
}
function workspaceDraftKey(target) {
  return target && target.workspaceId && target.sessionId
    ? "workspace:" + JSON.stringify([target.workspaceId, target.sessionId]) : null;
}
function readWorkspaceDraft(target) {
  var key = workspaceDraftKey(target);
  return key ? chatDraftStore()[key] || null : null;
}
function saveWorkspaceDraft(target, draft) {
  var key = workspaceDraftKey(target);
  if (!key) return null;
  var saved = { text: draft.text || "", mode: draft.mode === "plan" ? "plan" : "agent",
    reasoning: !!draft.reasoning, version: ++chatDraftVersion };
  chatDraftStore()[key] = saved;
  persistDraft(key).catch(function () {});
  return saved;
}
function acceptWorkspaceDraft(target, text, version) {
  var saved = readWorkspaceDraft(target);
  if (!saved || saved.version !== version || saved.text.trim() !== text) return null;
  return saveWorkspaceDraft(target, { text: "", mode: saved.mode, reasoning: saved.reasoning });
}
function forgetWorkspaceDraft(target) {
  var key = workspaceDraftKey(target);
  if (key) { delete chatDraftStore()[key]; delete workspacePendingInputs[key]; persistDraft(key).catch(function () {}); }
}
function prepareWorkspaceInput(target, text, options) {
  var key = workspaceDraftKey(target), pending = workspacePendingInputs[key];
  if (!pending || pending.message !== text || pending.Mode !== options.Mode || pending.ReasoningEnabled !== options.ReasoningEnabled)
    pending = workspacePendingInputs[key] = { sessionId: target.sessionId, requestId: crypto.randomUUID(),
      message: text, Mode: options.Mode, ReasoningEnabled: options.ReasoningEnabled };
  persistDraft(key).catch(function () {});
  return pending;
}
function acknowledgeWorkspaceInput(target, input) {
  var key = workspaceDraftKey(target);
  if (workspacePendingInputs[key] === input) { delete workspacePendingInputs[key]; persistDraft(key).catch(function () {}); }
}
window.RNAssistantChatDrafts = { readWorkspace: readWorkspaceDraft, saveWorkspace: saveWorkspaceDraft,
  acceptWorkspace: acceptWorkspaceDraft, forgetWorkspace: forgetWorkspaceDraft,
  prepareWorkspaceInput: prepareWorkspaceInput, acknowledgeWorkspaceInput: acknowledgeWorkspaceInput,
  flushWorkspace: flushWorkspaceDraft, flushOffice: flushOfficeDraft,
  flushAll: flushAllChatDrafts, hydrate: hydrateChatDrafts, hydrationFailed: failChatDraftHydration,
  reset: resetChatDrafts };

function officeDraftKey(chatIdValue) { return chatIdValue ? "office:" + chatIdValue : null; }
function readOfficeDraft(chatIdValue) {
  var key = officeDraftKey(chatIdValue);
  return key ? chatDraftStore()[key] || null : null;
}

function officeChatDraftText() {
  if (!window.RNAssistantWorkspaceChat || window.RNAssistantWorkspaceChat.owner() === "office")
    return $("chatInput") ? $("chatInput").value : "";
  var draft = readOfficeDraft(state.activeChatId);
  return draft ? draft.text : "";
}

function officeDraftAttachmentId(item) { return item && (item.Id || item.id) || ""; }

function retainUnsentInputAttachments(drafts, sent) {
  return (drafts || []).filter(function (item) {
    var submitted = (sent || []).some(function (attachment) {
      return officeDraftAttachmentId(attachment) === officeDraftAttachmentId(item);
    });
    if (submitted && item.previewUrl) URL.revokeObjectURL(item.previewUrl);
    return !submitted;
  });
}

function saveOfficeDraft(chatIdValue, text, attachments, changed, nextAnnotationNumber) {
  var key = officeDraftKey(chatIdValue);
  if (!key) return null;
  var drafts = chatDraftStore();
  var previous = drafts[key];
  attachments = (attachments || []).slice();
  nextAnnotationNumber = nextAnnotationNumber || previous && previous.nextAnnotationNumber || 1;
  if (!text && !attachments.length && nextAnnotationNumber === 1) {
    delete drafts[key];
    persistDraft(key).catch(function () {});
    return null;
  }
  if (!changed && previous && previous.text === text &&
      (previous.attachments || []).length === attachments.length &&
      (previous.attachments || []).every(function (item, index) {
        return officeDraftAttachmentId(item) === officeDraftAttachmentId(attachments[index]);
      })) return previous;
  drafts[key] = { text: text, attachments: attachments,
    nextAnnotationNumber: nextAnnotationNumber, version: ++chatDraftVersion };
  persistDraft(key).catch(function () {});
  return drafts[key];
}

function acceptOfficeDraft(chatIdValue, text, version, sentAttachments) {
  var draft = readOfficeDraft(chatIdValue);
  if (!draft) return null;
  var remaining = retainUnsentInputAttachments(draft.attachments, sentAttachments);
  return saveOfficeDraft(chatIdValue, draft.version === version && draft.text.trim() === text ? "" : draft.text,
    remaining, false, remaining.length ? draft.nextAnnotationNumber : 1);
}

function reserveOfficeAnnotationNumber(chatIdValue) {
  var draft = readOfficeDraft(chatIdValue);
  var next = draft && draft.nextAnnotationNumber || 1;
  if (next > 1000000) throw new Error("Лимит номеров аннотаций достигнут.");
  saveOfficeDraft(chatIdValue, draft ? draft.text : "", draft ? draft.attachments : [], true, next + 1);
  return next;
}

window.RNAssistantChatDrafts.readOffice = readOfficeDraft;
window.RNAssistantChatDrafts.saveOffice = saveOfficeDraft;
window.RNAssistantChatDrafts.acceptOffice = acceptOfficeDraft;
window.RNAssistantChatDrafts.reserveOfficeAnnotationNumber = reserveOfficeAnnotationNumber;
window.RNAssistantChatDrafts.forgetOffice = forgetOfficeDraft;

function captureChatDraft(chatIdValue, changed) {
  if (!chatIdValue) return null;
  if (window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() !== "office") return readOfficeDraft(chatIdValue);
  var input = $("chatInput");
  var editing = typeof hasActiveMessageEdit === "function" && hasActiveMessageEdit();
  var text = editing && state.editingDraftCaptured
    ? (state.editingDraftText || "")
    : (input ? input.value : "");
  var attachments = (state.draftAttachments || []).slice();
  return saveOfficeDraft(chatIdValue, text, attachments, changed);
}

function restoreChatDraft(chatIdValue) {
  var draft = readOfficeDraft(chatIdValue);
  state.draftAttachments = draft && draft.attachments ? draft.attachments.slice() : [];
  if (typeof renderAttachmentDrafts === "function") {
    renderAttachmentDrafts();
  }
  if (window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() !== "office") return;
  if (typeof setChatInputText === "function") {
    setChatInputText(draft ? draft.text : "", false, false);
  } else if ($("chatInput")) {
    $("chatInput").value = draft ? draft.text : "";
  }
}

function applyChatStateForChat(response, expectedChatId) {
  if (!expectedChatId || state.activeChatId === expectedChatId) {
    return applyChatState(response);
  }
  applyChatCatalogState(response);
  return false;
}

function applyChatPreferenceStateForChat(response, expectedChatId) {
  response = response || {};
  var responseChatId = response.chatId || response.ChatId || "";
  if (!expectedChatId || responseChatId !== expectedChatId || state.activeChatId !== expectedChatId) return false;
  var revision = window.RNAssistantRunViewState.sessionRevision(response);
  if (!window.RNAssistantRunViewState.accept(state.chatProjectionRevisions, responseChatId, revision)) return false;
  state.activeChatModel = response.model || response.Model || "";
  state.activeChatMode = response.mode || response.Mode || "agent";
  state.mutationPolicy = response.mutationPolicy || "Ask";
  state.chatReadOnly = response.chatReadOnly === true;
  state.activeChatReasoning = !!(response.reasoningEnabled || response.ReasoningEnabled);
  if (response.contextUsage || response.ContextUsage) {
    state.contextUsage = response.contextUsage || response.ContextUsage;
    syncTokenEstimateCalibrationFromUsage();
  }
  renderChatSessions();
  renderContextMeter();
  renderModelControls();
  if (typeof renderSendControls === "function") renderSendControls();
  return true;
}

function applyPushedChatState(message) {
  message = message || {};
  var response = message.payload || message.Payload || {};
  var scope = String(message.scope || message.Scope || "catalog").toLowerCase();
  var responseChatId = response.activeChatId || response.ActiveChatId || "";
  if (scope === "selection") {
    var requestId = message.id || message.Id || "";
    var pending = (state.pending || {})[requestId];
    if (pending && pending.type === "selectChat" && pending.payload &&
        pending.payload.chatId === responseChatId && (response.detailDeferred || response.DetailDeferred))
      return applyChatState(response);
    return false;
  }
  if (scope === "full" && responseChatId && responseChatId === state.activeChatId) {
    return applyChatState(response);
  }
  applyChatCatalogState(response);
  return false;
}

function applyLibraryCatalogState(response) {
  var changed = false;
  response = response || {};
  if (response.tools !== undefined) {
    var selectedTool = state.tools[state.selectedToolIndex];
    var selectedToolId = selectedTool && selectedTool.Id || "";
    var responseTools = toolLibraryItemsFromContract(response.tools);
    if (state.toolLibraryDirty && typeof reconcileToolLibraryCatalog === "function") {
      reconcileToolLibraryCatalog(responseTools);
    } else {
      state.tools = responseTools;
      if (typeof acceptToolLibraryState === "function") acceptToolLibraryState();
    }
    if (selectedToolId) {
      state.selectedToolIndex = state.tools.findIndex(function (tool) {
        return String(tool && tool.Id || "").toLowerCase() === String(selectedToolId).toLowerCase();
      });
    }
    changed = true;
  }
  if (response.skills !== undefined) {
    var selectedSkill = state.skills[state.selectedSkillIndex];
    var selectedSkillId = selectedSkill && selectedSkill.Id || "";
    var responseSkills = skillLibraryItemsFromContract(response.skills);
    if (state.skillLibraryDirty && typeof reconcileSkillLibraryCatalog === "function") {
      reconcileSkillLibraryCatalog(responseSkills);
    } else {
      state.skills = typeof preserveSkillSourceState === "function"
        ? preserveSkillSourceState(responseSkills)
        : responseSkills;
      if (typeof acceptSkillLibraryState === "function") acceptSkillLibraryState();
    }
    if (selectedSkillId) {
      state.selectedSkillIndex = state.skills.findIndex(function (skill) {
        return String(skill && skill.Id || "").toLowerCase() === String(selectedSkillId).toLowerCase();
      });
    }
    changed = true;
  }
  return changed;
}

function retainHtmlWorkspaceSources(current, incoming) {
  if (!current || !incoming) return incoming;
  var currentRevision = current.revisionArtifactId || current.RevisionArtifactId || "";
  var incomingRevision = incoming.revisionArtifactId || incoming.RevisionArtifactId || "";
  if (!currentRevision || currentRevision !== incomingRevision) return incoming;
  var currentFiles = current.files || current.Files || [];
  var incomingFiles = incoming.files || incoming.Files || [];
  if (currentFiles.length !== incomingFiles.length) return incoming;
  for (var index = 0; index < currentFiles.length; index += 1) {
    var oldFile = currentFiles[index], newFile = incomingFiles[index];
    var oldSource = oldFile && oldFile.source, newSource = newFile && newFile.source;
    if (!oldFile || !newFile || oldFile.id !== newFile.id || oldFile.path !== newFile.path ||
        oldFile.kind !== newFile.kind || oldFile.characters !== newFile.characters ||
        oldFile.byteLength !== newFile.byteLength || oldFile.sha256 !== newFile.sha256 ||
        !oldSource || !newSource || oldSource.uri !== newSource.uri ||
        oldSource.revision !== newSource.revision) return incoming;
  }
  // Keep the same workspace and file objects so an in-flight exact read remains current.
  Object.assign(current, incoming);
  current.files = currentFiles;
  current.dataSources = incoming.dataSources || incoming.DataSources || [];
  current.history = incoming.history || incoming.History || [];
  current.redoHistory = incoming.redoHistory || incoming.RedoHistory || [];
  current.redoBranches = incoming.redoBranches || incoming.RedoBranches || current.redoHistory;
  current.activeFileId = incoming.activeFileId || incoming.ActiveFileId || "";
  current.recovery = incoming.recovery || incoming.Recovery || {};
  current.revisionArtifactId = incomingRevision;
  return current;
}

function applyChatState(response, skipDraftCapture) {
  response = response || {};
  var detailDeferred = !!(response.detailDeferred || response.DetailDeferred);
  var previousChatId = state.activeChatId || "";
  var hasResponseChatId = response.activeChatId !== undefined || response.ActiveChatId !== undefined;
  var nextChatId = hasResponseChatId
    ? (response.activeChatId || response.ActiveChatId || "")
    : previousChatId;
  var incomingRevision = window.RNAssistantRunViewState.sessionRevision(response);
  if (!window.RNAssistantRunViewState.accept(state.chatProjectionRevisions, nextChatId, incomingRevision)) {
    applyRevisionedChatSummaryState(response);
    return false;
  }
  if (response.inbox && typeof applyChatInbox === "function") applyChatInbox(response.inbox, true);
  state.chatStateApplyVersion = (state.chatStateApplyVersion || 0) + 1;
  var chatChanged = previousChatId !== nextChatId;
  if (chatChanged) {
    if (!skipDraftCapture) captureChatDraft(previousChatId);
    if (typeof cancelVbaModuleRead === "function") cancelVbaModuleRead();
    if (typeof cancelVbaModuleWrite === "function") cancelVbaModuleWrite();
    if (typeof cancelSkillSourceRead === "function") cancelSkillSourceRead();
    if (typeof cancelSkillSourceWrite === "function") cancelSkillSourceWrite();
    if (typeof cancelToolLibraryWrite === "function") cancelToolLibraryWrite();
    if (typeof cancelToolSourceRead === "function") cancelToolSourceRead();
    if (typeof cancelToolDocumentationRead === "function") cancelToolDocumentationRead();
    if (typeof releasePromptEditorContext === "function") releasePromptEditorContext();
    if (typeof window.cancelHtmlWorkspaceWrite === "function") window.cancelHtmlWorkspaceWrite();
    if (typeof window.cancelHtmlWorkspaceRead === "function") window.cancelHtmlWorkspaceRead();
    if (typeof window.closeArtifactViewerResources === "function") window.closeArtifactViewerResources();
    state.artifactViewerPages = {};
    state.artifactViewerThumbnails = { items: {}, order: [], queue: [], pending: 0 };
    state.artifactImageGalleryContext = null;
  }
  if (typeof resetMessageEditState === "function") {
    resetMessageEditState();
  }
  state.activeChatId = nextChatId;
  state.chatDetailPending = detailDeferred;
  state.chatDetailError = "";
  if (detailDeferred) {
    state.messages = [];
    state.messageStartIndex = 0;
    state.messageTotalCount = response.messageTotalCount || response.MessageTotalCount || 0;
    state.context = {};
    state.contextUsage = {};
    state.artifacts = [];
    state.artifactLibrary = { sessionRevision: incomingRevision || 0, heads: [] };
    state.htmlWorkspace = { files: [], dataSources: [], history: [], redoHistory: [], redoBranches: [],
      recovery: { status: "empty", canMutate: false } };
  }
  state.activeRunViewState = window.RNAssistantRunViewState.normalize(
    response.runViewState !== undefined ? response.runViewState : response.RunViewState);
  if (response.activeChatModel !== undefined || response.ActiveChatModel !== undefined) {
    state.activeChatModel = response.activeChatModel || response.ActiveChatModel || "";
  }
  if (response.activeChatMode !== undefined || response.ActiveChatMode !== undefined) {
    state.activeChatMode = response.activeChatMode || response.ActiveChatMode || "agent";
    state.mutationPolicy = response.mutationPolicy || "Ask";
    state.chatReadOnly = response.chatReadOnly === true;
  }
  if (response.activeChatReasoning !== undefined || response.ActiveChatReasoning !== undefined) {
    state.activeChatReasoning = !!(response.activeChatReasoning || response.ActiveChatReasoning);
  }
  if (response.chats !== undefined || response.Chats !== undefined) {
    state.chats = window.RNAssistantRunViewState.mergeCatalog(
      state.chats, response.chats || response.Chats || [], state.chatProjectionRevisions);
  }
  if (response.documents !== undefined || response.Documents !== undefined) {
    state.documents = response.documents || response.Documents || [];
  }
  var libraryChanged = applyLibraryCatalogState(response);
  if (response.context || response.Context) {
    state.context = response.context || response.Context || {};
  }
  if (response.messages || response.Messages) {
    state.liveActivity = null;
    state.liveAgentRun = null;
    state.liveStreamContent = null;
    if (typeof resetLiveReasoning === "function") resetLiveReasoning();
    state.messages = response.messages || response.Messages || [];
    state.messageStartIndex = response.messageStartIndex !== undefined
      ? response.messageStartIndex : (response.MessageStartIndex || 0);
    state.messageTotalCount = response.messageTotalCount !== undefined
      ? response.messageTotalCount : (response.MessageTotalCount || state.messages.length);
    if (typeof reconcileChatInputEchoes === "function") reconcileChatInputEchoes(nextChatId, state.messages);
  }
  if (response.artifacts !== undefined || response.Artifacts !== undefined) {
    state.artifacts = response.artifacts || response.Artifacts || [];
  }
  if (response.artifactLibrary !== undefined || response.ArtifactLibrary !== undefined) {
    state.artifactLibrary = response.artifactLibrary || response.ArtifactLibrary || { sessionRevision: incomingRevision || 0, heads: [] };
  }
  if (response.activeContextCheckpointId !== undefined || response.ActiveContextCheckpointId !== undefined) {
    state.activeContextCheckpointId = response.activeContextCheckpointId || response.ActiveContextCheckpointId || "";
  }
  if (response.activeHtmlArtifactId !== undefined || response.ActiveHtmlArtifactId !== undefined) {
    state.activeHtmlArtifactId = response.activeHtmlArtifactId || response.ActiveHtmlArtifactId || "";
  }
  if (response.activeTaskListArtifactId !== undefined || response.ActiveTaskListArtifactId !== undefined) {
    state.activeTaskListArtifactId = response.activeTaskListArtifactId || response.ActiveTaskListArtifactId || "";
  }
  if (response.activePlanDocumentArtifactId !== undefined || response.ActivePlanDocumentArtifactId !== undefined) {
    state.activePlanDocumentArtifactId = response.activePlanDocumentArtifactId || response.ActivePlanDocumentArtifactId || "";
  }
  if (response.contextUsage || response.ContextUsage) {
    state.contextUsage = response.contextUsage || response.ContextUsage || {};
    syncTokenEstimateCalibrationFromUsage();
  }
  if ((response.htmlWorkspace || response.HtmlWorkspace) && (chatChanged || !state.htmlWorkspaceDirty)) {
    var nextWorkspace = response.htmlWorkspace || response.HtmlWorkspace;
    state.htmlWorkspace = chatChanged ? nextWorkspace : retainHtmlWorkspaceSources(state.htmlWorkspace, nextWorkspace);
    state.htmlWorkspaceDirty = false;
  }
  if (chatChanged) {
    restoreChatDraft(state.activeChatId);
    if (typeof clearSendError === "function") clearSendError();
  }
  if (typeof restoreActiveChatRun === "function") restoreActiveChatRun(false);
  renderChatSessions();
  if (libraryChanged && typeof renderInstructions === "function") renderInstructions();
  renderMessages();
  renderContext();
  renderContextMeter();
  renderModelControls();
  if (typeof renderSendControls === "function") renderSendControls();
  if (typeof renderHtmlWorkspace === "function") {
    renderHtmlWorkspace();
  }
  if (typeof syncPromptContextInspectorState === "function") {
    syncPromptContextInspectorState();
  }
  return true;
}

function applyRevisionedChatSummaryState(response) {
  response = response || {};
  if (response.chats === undefined && response.Chats === undefined) return;
  state.chatStateApplyVersion = (state.chatStateApplyVersion || 0) + 1;
  state.chats = window.RNAssistantRunViewState.mergeCatalog(
    state.chats, response.chats || response.Chats || [], state.chatProjectionRevisions, true);
  renderChatSessions();
}

function applyChatCatalogState(response) {
  state.chatStateApplyVersion = (state.chatStateApplyVersion || 0) + 1;
  response = response || {};
  if (response.inbox && typeof applyChatInbox === "function") applyChatInbox(response.inbox, true);
  if (response.chats !== undefined || response.Chats !== undefined) {
    state.chats = window.RNAssistantRunViewState.mergeCatalog(
      state.chats, response.chats || response.Chats || [], state.chatProjectionRevisions, true);
  }
  if (response.documents !== undefined || response.Documents !== undefined) {
    state.documents = response.documents || response.Documents || [];
  }
  var libraryChanged = applyLibraryCatalogState(response);
  renderChatSessions();
  if (libraryChanged && typeof renderInstructions === "function") renderInstructions();
}

function renderChatSessionList(chats) {
  var list = $("chatSessionList");
  if (!list) {
    return;
  }

  var focused = document.activeElement && document.activeElement.closest("[data-tree-key]");
  var focusKey = focused && list.contains(focused) ? focused.dataset.treeKey : "";
  var previousTree = list.querySelector(".chat-navigation-tree");
  if (previousTree) window.RNAssistantNavigationTree.unmount(previousTree, "chat");
  var launchMount = $("chatOfficeLaunchMount") || list;
  var officeWasFocused = document.activeElement && launchMount.contains(document.activeElement) &&
    !!document.activeElement.closest(".chat-office-launch-list");
  var previousScroll = list.scrollTop;
  var selectionChanged = list.dataset.selectedChatId !== (state.activeChatId || "");
  list.dataset.selectedChatId = state.activeChatId || "";
  list.innerHTML = "";
  var query = (state.chatSearch || "").trim().toLowerCase();
  if (launchMount !== list) launchMount.innerHTML = "";
  renderOfficeHostLaunchRows(launchMount);
  if (officeWasFocused && !selectionChanged) {
    var launchHeading = launchMount.querySelector(".chat-office-launch-heading");
    if (launchHeading) launchHeading.focus();
  }
  if (!chats.length && !(state.documents || []).length) {
    list.classList.add("is-empty");
    var empty = document.createElement("div");
    empty.className = "tree-empty";
    empty.textContent = state.bridgeUnavailable ? "Панель недоступна." : "Чатов пока нет.";
    list.appendChild(empty);
    return;
  }
  list.classList.remove("is-empty");

  var documents = {};
  chats.forEach(function (chat) {
    if (query && [chatTitle(chat), chatDocumentTitle(chat), chatHost(chat)].join(" ").toLowerCase().indexOf(query) < 0) {
      return;
    }
    var key = chatHost(chat) + "|" + chatDocumentKey(chat);
    if (!documents[key]) {
      documents[key] = { key: key, documentKey: chatDocumentKey(chat), title: chatDocumentTitle(chat), host: chatHost(chat), chats: [], current: false, open: false, path: "" };
    }
    documents[key].chats.push(chat);
    documents[key].current = documents[key].current || chatIsCurrentDocument(chat);
    documents[key].open = documents[key].open || chatIsCurrentDocument(chat);
    documents[key].path = documents[key].path || chatDocumentPath(chat);
  });

  (state.documents || []).forEach(function (item) {
    var host = item.host || item.Host || state.host || "";
    var documentKey = item.documentKey || item.DocumentKey || "";
    var key = host + "|" + documentKey;
    if (!documents[key]) {
      documents[key] = {
        key: key,
        documentKey: documentKey,
        title: item.title || item.Title || "Документ",
        host: host,
        chats: [],
        current: !!(item.isActive || item.IsActive),
        open: true,
        path: item.path || item.Path || ""
      };
    } else {
      documents[key].documentKey = documentKey;
      documents[key].title = item.title || item.Title || documents[key].title;
      documents[key].current = !!(item.isActive || item.IsActive);
      documents[key].open = true;
      documents[key].path = item.path || item.Path || documents[key].path;
    }
  });

  var currentDocumentKey = "";
  Object.keys(documents).some(function (key) {
    if (!documents[key].current) {
      return false;
    }
    currentDocumentKey = key;
    return true;
  });
  if (currentDocumentKey && currentDocumentKey !== state.currentChatDocumentKey) {
    if (state.currentChatDocumentKey) {
      state.collapsedChatDocuments[state.currentChatDocumentKey] = true;
    }
    delete state.collapsedChatDocuments[currentDocumentKey];
    state.currentChatDocumentKey = currentDocumentKey;
  }
  Object.keys(documents).forEach(function (key) {
    if (state.initializedChatDocuments[key]) {
      return;
    }
    state.initializedChatDocuments[key] = true;
    if (!documents[key].current) {
      state.collapsedChatDocuments[key] = true;
    }
  });
  if (selectionChanged && state.activeChatId) {
    Object.keys(documents).forEach(function (key) {
      if (documents[key].chats.some(function (chat) { return chatId(chat) === state.activeChatId; })) {
        delete state.collapsedChatDocuments[key];
      }
    });
  }

  var tree = document.createElement("div");
  tree.className = "chat-navigation-tree";
  var nodes = Object.keys(documents).sort(function (left, right) {
    var a = documents[left];
    var b = documents[right];
    if (a.current !== b.current) {
      return a.current ? -1 : 1;
    }
    if (a.open !== b.open) {
      return a.open ? -1 : 1;
    }
    return a.title.localeCompare(b.title);
  }).map(function (key) { return chatDocumentTreeNode(documents[key], query); });
  list.appendChild(tree);
  window.RNAssistantNavigationTree.render(tree, nodes, {
    label: "Документы и чаты", positionKey: "chat",
    focusKey: selectionChanged && state.activeChatId ? "chat:" + state.activeChatId : focusKey,
    onToggle: function (node, expanded) { state.collapsedChatDocuments[node.documentKey] = !expanded; },
    onSelect: function (node) { if (node.chatId && !state.bridgeUnavailable) selectChat(node.chatId); }
  });
  list.scrollTop = previousScroll;
}

function renderOfficeHostLaunchRows(list) {
  var hosts = ["Excel", "Word", "PowerPoint", "Outlook"];
  var group = document.createElement("details");
  group.className = "chat-office-launch-list";
  group.open = !!state.officeHostLaunchOpen;
  group.addEventListener("toggle", function () {
    if (group.isConnected) state.officeHostLaunchOpen = group.open;
  });
  group.addEventListener("keydown", function (event) {
    if (event.key !== "Escape" || !group.open) return;
    event.preventDefault();
    group.open = false;
    state.officeHostLaunchOpen = false;
    heading.focus();
  });
  var heading = document.createElement("summary");
  heading.className = "chat-office-launch-heading";
  heading.innerHTML = window.RNAssistantIcons.render("plus") +
    "<span>Новый документ Office</span>" +
    window.RNAssistantIcons.render("down");
  group.appendChild(heading);

  var unavailable = officeHostChatUnavailableReason();
  var statusText = state.officeHostChatError || unavailable;
  var status = null;
  if (statusText) {
    status = document.createElement("div");
    status.className = "chat-office-launch-status" + (state.officeHostChatError ? " is-error" : "");
    status.setAttribute("role", state.officeHostChatError ? "alert" : "status");
    status.textContent = statusText;
  }

  var menu = document.createElement("div");
  menu.className = "chat-office-launch-menu";
  hosts.forEach(function (host) {
    var button = document.createElement("button");
    button.type = "button";
    button.className = "chat-office-launch-row" + documentHostClass(host);
    button.disabled = !!unavailable;
    button.title = unavailable || "Создать чат в " + host;
    button.setAttribute("aria-busy", String(!!state.officeHostChatPending && state.officeHostChatTarget === host));
    button.innerHTML = "<span class=\"chat-document-icon\">" + documentHostIcon(host) + "</span>" +
      "<span class=\"chat-office-launch-name\"></span>" +
      "<span class=\"chat-office-launch-action\">Новый чат</span>";
    button.querySelector(".chat-office-launch-name").textContent = host;
    button.addEventListener("click", function () {
      state.officeHostLaunchOpen = true;
      createOfficeHostChat(host);
    });
    menu.appendChild(button);
  });
  group.appendChild(menu);
  list.appendChild(group);
  if (status) list.appendChild(status);
}

function chatDocumentTreeNode(documentItem, query) {
  var tree = window.RNAssistantNavigationTree;
  var collapsed = !query && !!state.collapsedChatDocuments[documentItem.key];
  var status = documentItem.current ? "Активен" : documentItem.open ? "Открыт" : "Закрыт";
  var children = documentItem.chats.map(chatTreeNode);
  if (!children.length) children.push({
    key: "chat:empty:" + documentItem.key, kind: "leaf", title: "Нет чатов", disabled: true
  });
  return {
    key: "document:" + documentItem.key, kind: "parent", title: documentItem.title,
    documentKey: documentItem.key, expanded: !collapsed, iconHtml: documentHostIcon(documentItem.host),
    ariaLabel: documentItem.title + " · " + status,
    tooltip: [documentItem.title, documentItem.path, status].filter(Boolean).join(" · "),
    className: documentHostClass(documentItem.host).trim(),
    children: children,
    actions: [
      { title: "Новый чат для документа", iconHtml: tree.icon("plus"), onClick: function () { createDocumentChat(documentItem); } },
      { title: documentItem.open ? "Активировать документ" : "Открыть документ", iconHtml: tree.icon("open"),
        hidden: !documentItem.open && !documentItem.path,
        onClick: function () {
          if (documentItem.open) activateDocument(documentItem.documentKey);
          else if (documentItem.chats.length) openActiveDocument(chatId(documentItem.chats[0]));
        } },
      { title: "Удалить документ и все чаты", iconHtml: tree.icon("trash"),
        hidden: !documentItem.chats.length, danger: true,
        onClick: function () { deleteDocument(documentItem.host, documentItem.documentKey, documentItem.title); } }
    ]
  };
}

function chatTreeNode(chat) {
  var id = chatId(chat);
  var run = state.chatRuns[id] || state.activeSends[id];
  var persistedRun = chatSummaryRunViewState(chat);
  var hasActiveRun = !!run || !!(persistedRun && persistedRun.lifecycle === "running");
  var loading = id === state.chatNavigationTargetId && !!state.chatNavigationPending;
  var warning = chatStorageWarningLevel(chat);
  var badgeLevel = chatCasMissingBlobCount(chat) || chatCasReferenceIssueCount(chat) ? "critical" : warning;
  var storageTooltip = badgeLevel !== "none" ? buildChatStorageTooltip(chat, badgeLevel) : "";
  return {
    key: "chat:" + id, kind: "leaf", title: chatTitle(chat), chatId: id,
    selected: id === state.activeChatId, disabled: !!state.bridgeUnavailable,
    tooltip: chatTitle(chat),
    badge: badgeLevel !== "none" ? "!" : "", badgeKind: badgeLevel, badgeTitle: storageTooltip,
    status: hasActiveRun || loading ? "running" : "",
    statusLabel: loading ? "Подготовка истории чата" : state.activeSends[id] && state.activeSends[id].canceling
      ? "Запрос останавливается" : "Запрос выполняется",
    actions: [
      { title: "Переименовать чат", iconHtml: window.RNAssistantNavigationTree.icon("edit"),
        onClick: function () { renameChat(id); } },
      { title: hasActiveRun ? "Сначала остановите запрос" : "Удалить чат",
        iconHtml: window.RNAssistantNavigationTree.icon("trash"), danger: true, disabled: hasActiveRun,
        onClick: function () { deleteChat(id); } }
    ]
  };
}

function documentHostClass(host) {
  var values = { Excel: " is-host-excel", Word: " is-host-word", PowerPoint: " is-host-powerpoint", Outlook: " is-host-outlook" };
  return values[host] || " is-host-generic";
}

function documentHostIcon(host) {
  var letters = { Excel: "X", Word: "W", PowerPoint: "P", Outlook: "O" };
  if (letters[host]) {
    return "<span class=\"office-app-mark is-" + host.toLowerCase() + "\" aria-hidden=\"true\">" +
      "<span class=\"office-app-page\"></span>" +
      "<span class=\"office-app-tile\">" + letters[host] + "</span>" +
      "</span>";
  }
  return window.RNAssistantIcons.render("document", "office-generic-document");
}

function formatChatStorageBytes(value) {
  var bytes = Math.max(0, Number(value || 0));
  if (bytes < 1024) return Math.round(bytes) + " Б";
  var units = ["КБ", "МБ", "ГБ", "ТБ"];
  var scaled = bytes;
  var unit = units[0];
  for (var index = 0; index < units.length; index += 1) {
    scaled /= 1024;
    unit = units[index];
    if (scaled < 1024 || index === units.length - 1) break;
  }
  var digits = scaled < 10 ? 1 : 0;
  return scaled.toFixed(digits).replace(".", ",") + " " + unit;
}

function buildChatStorageTooltip(chat, warningLevel) {
  var lines = [];
  if (warningLevel === "critical") {
    lines.push("Хранилище чата требует внимания.");
  } else if (warningLevel === "warning") {
    lines.push("Размер истории чата приближается к порогу.");
  }
  lines.push("JSONL: " + formatChatStorageBytes(chatJsonlByteLength(chat)) + ".");
  lines.push("CAS: " + formatChatStorageBytes(chatCasStoredByteLength(chat)) + " на диске, " +
    formatChatStorageBytes(chatCasLogicalByteLength(chat)) + " логически, blobs: " +
    Math.round(chatCasBlobCount(chat)) + ".");
  if (chatCasMissingBlobCount(chat) > 0) {
    lines.push("Отсутствуют CAS blobs: " + Math.round(chatCasMissingBlobCount(chat)) + ".");
  }
  if (chatCasReferenceIssueCount(chat) > 0) {
    lines.push("Некорректные или конфликтующие CAS-ссылки: " + Math.round(chatCasReferenceIssueCount(chat)) + ".");
  }
  lines.push("Общие CAS blobs учитываются в каждом ссылающемся чате.");
  lines.push("Автоматическое удаление истории отключено.");
  return lines.join("\n");
}

function logToolResult(prefix, toolId, result) {
  var ok = result && (result.Success === true || result.success === true);
  var message = result ? (result.Message || result.message || "") : "";
  log(prefix + " " + (ok ? "OK" : "FAIL") + ": " + toolId + (message ? " - " + message : ""), ok ? "success" : "error");
}

function logToolResults(results) {
  (results || []).forEach(function (result, index) {
    logToolResult("Tool " + (index + 1), result.toolId || result.ToolId || "tool", result);
  });
}

function modelUsageFromContext(base) {
  base = base || {};
  var basePrompt = base.lastPromptTokens !== undefined ? base.lastPromptTokens : base.LastPromptTokens;
  var baseCompletion = base.lastCompletionTokens !== undefined ? base.lastCompletionTokens : base.LastCompletionTokens;
  var baseTotal = base.lastTotalTokens !== undefined ? base.lastTotalTokens : base.LastTotalTokens;
  if (basePrompt !== null && basePrompt !== undefined ||
      baseCompletion !== null && baseCompletion !== undefined || baseTotal !== null && baseTotal !== undefined)
    return { prompt: basePrompt == null ? null : basePrompt,
      completion: baseCompletion == null ? null : baseCompletion, total: baseTotal == null ? null : baseTotal };
  return null;
}

function lastModelUsage() {
  return modelUsageFromContext(state.contextUsage);
}

function renderContextMeter() {
  if (typeof isPanelActive === "function" && !isPanelActive("chat")) return;
  var summary = $("contextMeterSummary");
  var meter = $("contextMeter");
  if (!summary || !meter) {
    return;
  }
  var last = lastModelUsage();
  var usage = state.contextUsage || {};
  var windowTokens = Number(usage.contextWindowTokens || usage.ContextWindowTokens || 0);
  var used = last && last.prompt !== null ? Number(last.prompt) : null;
  var knownInput = used !== null && isFinite(used) && used >= 0;
  var knownWindow = isFinite(windowTokens) && windowTokens > 0;
  var percent = knownInput && knownWindow ? Math.min(100, used * 100 / windowTokens) : null;
  if ($("contextMeterWrap")) $("contextMeterWrap").hidden = percent === null;
  if (percent === null && $("contextMeterTooltip")) $("contextMeterTooltip").hidden = true;
  summary.textContent = !knownInput ? "API ещё не сообщил число входных токенов"
    : !knownWindow ? "Использовано " + formatNumber(used) + " токенов · размер окна неизвестен"
    : "Осталось " + Math.round(100 - percent) + "% · использовано " + formatNumber(used) +
      " / " + formatNumber(windowTokens) + " токенов";
  meter.dataset.level = percent === null ? "unknown" : percent >= 95 ? "danger" : percent >= 80 ? "warn" : "ok";
  meter.style.setProperty("--context-meter-offset", percent === null ? "100" : String(100 - percent));
  meter.setAttribute("aria-label", "Контекстное окно. " + summary.textContent + ". Открыть подробное использование");
}

function syncTokenEstimateCalibrationFromUsage() {
  var usage = state.contextUsage || {};
  var settings = state.settings || {};
  var model = String(usage.estimateModel || usage.EstimateModel || "").trim();
  var samples = Number(usage.calibrationSamples || usage.CalibrationSamples || 0);
  var relative = Number(usage.calibrationMultiplier || usage.CalibrationMultiplier || 0);
  if (!model || samples <= 0 || !isFinite(relative) || relative <= 0) return;

  var calibrations = settings.TokenEstimateCalibrations || settings.tokenEstimateCalibrations || {};
  var lowerModel = model.toLowerCase();
  var key = Object.keys(calibrations).filter(function (item) {
    return String(item || "").toLowerCase() === lowerModel;
  })[0] || model;
  var profile = usage.calibrationProfile || usage.CalibrationProfile;
  calibrations[key] = profile || {
    Multiplier: relative,
    InterceptTokens: Number(usage.calibrationInterceptTokens || usage.CalibrationInterceptTokens || 0),
    SampleCount: samples,
    LastEstimatedPromptTokens: Number(usage.calibrationLastEstimatedPromptTokens || usage.CalibrationLastEstimatedPromptTokens || 0),
    LastActualPromptTokens: Number(usage.calibrationLastActualPromptTokens || usage.CalibrationLastActualPromptTokens || 0),
    UpdatedUtc: usage.calibrationUpdatedUtc || usage.CalibrationUpdatedUtc || null
  };
  settings.TokenEstimateCalibrations = calibrations;
}

function showSendError(error, text) {
  state.failedSend = { text: text || "", error: error || "Unknown error" };
  if (typeof renderChatRunNotice === "function") renderChatRunNotice();
}

function clearSendError() {
  state.failedSend = null;
  if (typeof renderChatRunNotice === "function") renderChatRunNotice();
}

function markLocalMessage(text, values) {
  for (var i = state.messages.length - 1; i >= 0; i -= 1) {
    if (state.messages[i] && state.messages[i].Local && messageContent(state.messages[i]) === text) {
      Object.keys(values).forEach(function (key) {
        state.messages[i][key] = values[key];
      });
      return true;
    }
  }
  return false;
}
