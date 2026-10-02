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
    empty.textContent = state.bridgeUnavailable ? "Office bridge недоступен" : "Нет чатов";
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
  var hasMessages = state.messageTotalCount > 0 || !!(state.messages && state.messages.length);
  var compactableMessages = (state.messages || []).filter(function (message) {
    return !messageActivity(message);
  }).length;
  $("newChatButton").disabled = !!state.bridgeUnavailable;
  $("clearChatButton").disabled = !hasActive || !hasMessages || !!currentActiveSend();
  $("clearChatButton").hidden = !hasActive || !hasMessages;
  if ($("compactContextButton")) {
    $("compactContextButton").disabled = !hasActive || compactableMessages < 3 || !!currentActiveSend();
    $("compactContextButton").hidden = !hasActive || compactableMessages < 3;
  }
  if ($("chatModeSelect")) {
    $("chatModeSelect").value = state.activeChatMode || "agent";
  }
  renderSendControls();
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

function captureChatDraft(chatIdValue) {
  if (!chatIdValue) return;
  var input = $("chatInput");
  var editing = typeof hasActiveMessageEdit === "function" && hasActiveMessageEdit();
  var text = editing && state.editingDraftCaptured
    ? (state.editingDraftText || "")
    : (input ? input.value : "");
  var attachments = (state.draftAttachments || []).slice();
  var drafts = chatDraftStore();
  if (!text && !attachments.length) {
    delete drafts[chatIdValue];
    return;
  }
  drafts[chatIdValue] = { text: text, attachments: attachments };
}

function restoreChatDraft(chatIdValue) {
  var draft = chatIdValue ? chatDraftStore()[chatIdValue] : null;
  state.draftAttachments = draft && draft.attachments ? draft.attachments.slice() : [];
  if (typeof renderAttachmentDrafts === "function") {
    renderAttachmentDrafts();
  }
  if (typeof setChatInputText === "function") {
    setChatInputText(draft ? draft.text : "", false);
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

function applyChatState(response) {
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
  if (response.inbox && typeof applyChatInbox === "function") applyChatInbox(response.inbox);
  state.chatStateApplyVersion = (state.chatStateApplyVersion || 0) + 1;
  var chatChanged = previousChatId !== nextChatId;
  if (chatChanged) {
    captureChatDraft(previousChatId);
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
  if (typeof renderChatInbox === "function") renderChatInbox();
  state.activeRunViewState = window.RNAssistantRunViewState.normalize(
    response.runViewState !== undefined ? response.runViewState : response.RunViewState);
  if (response.activeChatModel !== undefined || response.ActiveChatModel !== undefined) {
    state.activeChatModel = response.activeChatModel || response.ActiveChatModel || "";
  }
  if (response.activeChatMode !== undefined || response.ActiveChatMode !== undefined) {
    state.activeChatMode = response.activeChatMode || response.ActiveChatMode || "agent";
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
  renderChatSessions();
  if (libraryChanged && typeof renderInstructions === "function") renderInstructions();
  renderMessages();
  renderContext();
  renderContextMeter();
  renderModelControls();
  if (typeof renderSendControls === "function") renderSendControls();
  if ($("chatModeSelect")) {
    $("chatModeSelect").value = state.activeChatMode || "agent";
  }
  if (typeof renderHtmlWorkspace === "function") {
    renderHtmlWorkspace();
  }
  if (typeof syncPromptContextInspectorState === "function") {
    syncPromptContextInspectorState();
  }
  if (chatChanged && typeof restoreActiveChatRun === "function") {
    restoreActiveChatRun();
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

  list.innerHTML = "";
  renderChatTreeControls();
  var query = (state.chatSearch || "").trim().toLowerCase();
  renderOfficeHostLaunchRows(list, query);
  if (!chats.length && !(state.documents || []).length) {
    list.classList.add("is-empty");
    var empty = document.createElement("div");
    empty.className = "chat-tree-empty";
    empty.textContent = state.bridgeUnavailable ? "Office bridge недоступен." : "Чатов пока нет.";
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

  Object.keys(documents).sort(function (left, right) {
    var a = documents[left];
    var b = documents[right];
    if (a.current !== b.current) {
      return a.current ? -1 : 1;
    }
    if (a.open !== b.open) {
      return a.open ? -1 : 1;
    }
    return a.title.localeCompare(b.title);
  }).forEach(function (key) {
    list.appendChild(renderChatDocumentNode(documents[key], query));
  });
}

function renderOfficeHostLaunchRows(list, query) {
  var hosts = ["Excel", "Word", "PowerPoint", "Outlook"].filter(function (host) {
    return !query || host.toLowerCase().indexOf(query) >= 0;
  });
  if (!hosts.length) return;

  var group = document.createElement("section");
  group.className = "chat-office-launch-list";
  var heading = document.createElement("div");
  heading.className = "chat-office-launch-heading";
  heading.textContent = "Приложения Office";
  group.appendChild(heading);

  hosts.forEach(function (host) {
    var button = document.createElement("button");
    button.type = "button";
    button.className = "chat-office-launch-row" + documentHostClass(host);
    button.disabled = !!state.bridgeUnavailable || !state.officeHostChatAvailable ||
      !!state.officeHostChatPending || !!currentActiveSend() || !!hasActiveMessageEdit();
    button.title = button.disabled ? "Создание чата недоступно" : "Создать чат в " + host;
    button.innerHTML = "<span class=\"chat-document-icon\">" + documentHostIcon(host) + "</span>" +
      "<span class=\"chat-office-launch-name\"></span>" +
      "<span class=\"chat-office-launch-action\">Новый чат</span>";
    button.querySelector(".chat-office-launch-name").textContent = host;
    button.addEventListener("click", function () { createOfficeHostChat(host); });
    group.appendChild(button);
  });
  list.appendChild(group);
}

function renderChatDocumentNode(documentItem, query) {
  var group = document.createElement("section");
  group.className = "chat-document" + documentHostClass(documentItem.host) + (documentItem.current ? " is-current" : (documentItem.open ? " is-open" : " is-closed"));
  var collapsed = !query && !!state.collapsedChatDocuments[documentItem.key];

  var header = document.createElement("div");
  header.className = "chat-document-row";
  header.setAttribute("aria-expanded", collapsed ? "false" : "true");
  header.innerHTML =
    "<button type=\"button\" class=\"chat-document-toggle\" aria-label=\"Свернуть или развернуть документ\">" +
    "<span class=\"chat-document-icon\">" + documentHostIcon(documentItem.host) + "</span>" +
    "<span class=\"chat-document-name\"></span>" +
    "<span class=\"chat-document-state\">" + (documentItem.current ? "Активен" : (documentItem.open ? "Открыт" : "Закрыт")) + "</span></button>" +
    "<span class=\"chat-document-actions\">" +
    "<button type=\"button\" class=\"chat-row-action chat-document-new\" title=\"Новый чат для документа\" aria-label=\"Новый чат для документа\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M12 5v14\"/><path d=\"M5 12h14\"/></svg></button>" +
    "<button type=\"button\" class=\"chat-row-action chat-document-open\" title=\"" + (documentItem.open ? "Активировать документ" : "Открыть документ") + "\" aria-label=\"" + (documentItem.open ? "Активировать документ" : "Открыть документ") + "\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M14 5h5v5\"/><path d=\"m19 5-8 8\"/><path d=\"M18 13v5a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5\"/></svg></button>" +
    "<button type=\"button\" class=\"chat-row-action chat-document-delete\" title=\"Удалить документ и все чаты\" aria-label=\"Удалить документ и все чаты\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M3 6h18\"/><path d=\"M8 6V4h8v2\"/><path d=\"m19 6-1 14H6L5 6\"/><path d=\"M10 11v5\"/><path d=\"M14 11v5\"/></svg></button>" +
    "</span>";
  header.querySelector(".chat-document-name").textContent = documentItem.title;
  header.querySelector(".chat-document-toggle").addEventListener("click", function () {
    state.collapsedChatDocuments[documentItem.key] = !children.hidden;
    renderChatSessionList(state.chats || []);
  });
  header.querySelector(".chat-document-new").addEventListener("click", function () {
    createDocumentChat(documentItem);
  });
  var openButton = header.querySelector(".chat-document-open");
  openButton.hidden = !documentItem.open && !documentItem.path;
  openButton.addEventListener("click", function () {
    if (documentItem.open) {
      activateDocument(documentItem.documentKey);
    } else if (documentItem.chats.length) {
      openActiveDocument(chatId(documentItem.chats[0]));
    }
  });
  var deleteButton = header.querySelector(".chat-document-delete");
  deleteButton.hidden = documentItem.chats.length === 0;
  deleteButton.addEventListener("click", function () {
    deleteDocument(documentItem.host, documentItem.documentKey, documentItem.title);
  });
  group.appendChild(header);

  var children = document.createElement("div");
  children.className = "chat-document-children";
  children.hidden = collapsed;
  documentItem.chats.forEach(function (chat) {
    children.appendChild(renderChatTreeRow(chat));
  });
  if (!documentItem.chats.length) {
    var empty = document.createElement("div");
    empty.className = "chat-document-empty";
    empty.textContent = "Нет чатов";
    children.appendChild(empty);
  }
  group.appendChild(children);
  return group;
}

function renderChatTreeControls() {
  var layout = $("chatLayout");
  var sidebarButton = $("toggleChatSidebarButton");
  if (layout) {
    layout.classList.toggle("is-sidebar-hidden", !!state.chatSidebarHidden);
  }
  if (sidebarButton) {
    var label = state.chatSidebarHidden ? "Показать список" : "Скрыть список";
    sidebarButton.title = label;
    sidebarButton.setAttribute("aria-label", label);
    sidebarButton.setAttribute("aria-pressed", state.chatSidebarHidden ? "true" : "false");
    sidebarButton.innerHTML = state.chatSidebarHidden
      ? "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"m10 6 6 6-6 6\"/></svg>"
      : "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"m14 6-6 6 6 6\"/></svg>";
  }
}

function renderChatTreeRow(chat) {
  var row = document.createElement("div");
  var id = chatId(chat);
  row.className = "chat-session-row" + (id === state.activeChatId ? " active" : "");
  var button = document.createElement("button");
  button.type = "button";
  button.className = "chat-session-select";
  button.disabled = !!state.bridgeUnavailable;
  button.addEventListener("click", function () { selectChat(id); });

  var title = document.createElement("span");
  title.className = "chat-session-title";
  title.textContent = chatTitle(chat);
  var storageWarningLevel = chatStorageWarningLevel(chat);
  var storedFootprint = chatJsonlByteLength(chat) + chatCasStoredByteLength(chat);
  if (storedFootprint > 0 || chatCasMissingBlobCount(chat) > 0 || chatCasReferenceIssueCount(chat) > 0) {
    var storageTooltip = buildChatStorageTooltip(chat, storageWarningLevel);
    var storageBadge = createChatSessionBadge(
      (storageWarningLevel === "none" ? "" : "! ") + formatChatStorageBytes(storedFootprint),
      storageWarningLevel === "none" ? "storage" : storageWarningLevel
    );
    storageBadge.title = storageTooltip;
    storageBadge.setAttribute("aria-label", storageTooltip);
    button.classList.add("has-storage-badge");
    button.title = storageTooltip;
  }
  var run = state.chatRuns[id] || state.activeSends[id];
  var persistedRun = chatSummaryRunViewState(chat);
  var persistedRunStatus = persistedRun ? persistedRun.lifecycle : "";
  var hasActiveRun = !!run || persistedRunStatus === "running";
  var loading = id === state.chatNavigationTargetId && !!state.chatNavigationPending;
  if (hasActiveRun) {
    row.classList.add("has-active-run");
  }
  if (loading) row.classList.add("is-loading");
  button.appendChild(title);
  if (storageBadge) {
    button.appendChild(storageBadge);
  }
  row.appendChild(button);
  if (hasActiveRun || loading) {
    var status = document.createElement("span");
    status.className = "chat-row-status";
    status.title = loading ? "Подготовка истории чата" :
      state.activeSends[id] && state.activeSends[id].canceling
        ? "Запрос останавливается" : "Запрос выполняется";
    status.setAttribute("aria-label", status.title);
    var spinner = document.createElement("span");
    spinner.className = "chat-run-spinner";
    spinner.setAttribute("aria-hidden", "true");
    status.appendChild(spinner);
    row.appendChild(status);
  }
  var actions = document.createElement("span");
  actions.className = "chat-row-actions";
  actions.innerHTML = "<button type=\"button\" class=\"chat-row-action chat-edit\" title=\"Переименовать\" aria-label=\"Переименовать чат\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M12 20h9\"/><path d=\"M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z\"/></svg></button><button type=\"button\" class=\"chat-row-action chat-delete\" title=\"Удалить\" aria-label=\"Удалить чат\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M3 6h18\"/><path d=\"M8 6V4h8v2\"/><path d=\"m19 6-1 14H6L5 6\"/><path d=\"M10 11v5\"/><path d=\"M14 11v5\"/></svg></button>";
  actions.querySelector(".chat-edit").addEventListener("click", function () { renameChat(id); });
  actions.querySelector(".chat-delete").addEventListener("click", function () { deleteChat(id); });
  if (hasActiveRun) {
    actions.querySelector(".chat-delete").disabled = true;
    actions.querySelector(".chat-delete").title = "Сначала остановите запрос";
  }
  row.appendChild(actions);
  return row;
}

function documentHostClass(host) {
  var values = { Excel: " is-host-excel", Word: " is-host-word", PowerPoint: " is-host-powerpoint", Outlook: " is-host-outlook" };
  return values[host] || " is-host-generic";
}

function documentHostIcon(host) {
  var letters = { Excel: "X", Word: "W", PowerPoint: "P", Outlook: "O" };
  if (letters[host]) {
    return "<span class=\"office-app-mark\" aria-hidden=\"true\">" +
      "<span class=\"office-app-page\"></span>" +
      "<span class=\"office-app-tile\">" + letters[host] + "</span>" +
      "</span>";
  }
  return "<svg class=\"office-generic-document\" viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M6 3h9l3 3v15H6zM15 3v4h4\"/></svg>";
}

function createChatSessionBadge(text, kind) {
  var badge = document.createElement("span");
  badge.className = "chat-session-badge is-" + kind;
  badge.textContent = text;
  return badge;
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
  var value = $("contextMeterValue");
  var detail = $("contextMeterDetail");
  var meter = $("contextMeter");
  if (!value || !detail || !meter) {
    return;
  }
  var last = lastModelUsage();
  if (last) {
    var parts = [];
    if (last.prompt !== null) parts.push("↑" + formatNumber(last.prompt));
    if (last.completion !== null) parts.push("↓" + formatNumber(last.completion));
    if (!parts.length && last.total !== null) parts.push(formatNumber(last.total) + " всего");
    value.textContent = "API";
    detail.textContent = parts.join(" · ");
    meter.title = "Последние данные API usage: " +
      (last.prompt === null ? "" : "вход " + formatNumber(last.prompt) + " · ") +
      (last.completion === null ? "" : "выход " + formatNumber(last.completion) + " · ") +
      (last.total === null ? "" : "всего " + formatNumber(last.total) + " · ") +
      "токенов. Откройте данные usage и состав контекста.";
  } else {
    value.textContent = "API";
    detail.textContent = "нет usage";
    meter.title = "Точное число токенов не получено от API. Откройте состав контекста.";
  }
  meter.dataset.level = "ok";
  meter.style.setProperty("--context-meter-percent", "0%");
  meter.setAttribute("aria-label", meter.title);
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
}

function clearSendError() {
  state.failedSend = null;
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
