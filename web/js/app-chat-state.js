function renderChatSessions() {
  var select = $("chatSessionSelect");
  if (!select) {
    return;
  }

  select.innerHTML = "";
  var chats = state.chats || [];
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
    subtitle.push(formatChatMessageCount((state.messages || []).filter(function (message) { return !messageProtocolMessage(message); }).length));
  }
  if (activeChat) {
    subtitle = subtitle.concat([chatDocumentTitle(activeChat), chatHost(activeChat)].filter(Boolean));
  }
  $("activeChatSubtitle").textContent = subtitle.join(" · ");
  $("documentNotice").classList.toggle("hidden", isCurrentDocument);
  $("openDocumentButton").hidden = isCurrentDocument || !chatDocumentPath(activeChat);

  var hasActive = !!state.activeChatId;
  var hasMessages = !!(state.messages && state.messages.length);
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
  renderHtmlModeToggle();
  renderSendControls();
}

function activeChatSummary() {
  return (state.chats || []).filter(function (chat) {
    return chatId(chat) === state.activeChatId;
  })[0] || null;
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
    applyChatState(response);
    return true;
  }
  applyChatCatalogState(response);
  return false;
}

function applyChatState(response) {
  state.chatStateApplyVersion = (state.chatStateApplyVersion || 0) + 1;
  response = response || {};
  var previousChatId = state.activeChatId || "";
  var hasResponseChatId = response.activeChatId !== undefined || response.ActiveChatId !== undefined;
  var nextChatId = hasResponseChatId
    ? (response.activeChatId || response.ActiveChatId || "")
    : previousChatId;
  var chatChanged = previousChatId !== nextChatId;
  if (chatChanged) {
    captureChatDraft(previousChatId);
  }
  if (typeof resetMessageEditState === "function") {
    resetMessageEditState();
  }
  state.activeChatId = nextChatId;
  if (response.activeChatModel !== undefined || response.ActiveChatModel !== undefined) {
    state.activeChatModel = response.activeChatModel || response.ActiveChatModel || "";
  }
  if (response.activeChatMode !== undefined || response.ActiveChatMode !== undefined) {
    state.activeChatMode = response.activeChatMode || response.ActiveChatMode || "agent";
  }
  if (response.activeChatHtmlMode !== undefined || response.ActiveChatHtmlMode !== undefined) {
    state.activeChatHtmlMode = !!(response.activeChatHtmlMode || response.ActiveChatHtmlMode);
  }
  if (response.activeChatReasoning !== undefined || response.ActiveChatReasoning !== undefined) {
    state.activeChatReasoning = !!(response.activeChatReasoning || response.ActiveChatReasoning);
  }
  if (response.chats !== undefined || response.Chats !== undefined) {
    state.chats = response.chats || response.Chats || [];
  }
  if (response.documents !== undefined || response.Documents !== undefined) {
    state.documents = response.documents || response.Documents || [];
  }
  if (response.context || response.Context) {
    state.context = response.context || response.Context || {};
  }
  if (response.messages || response.Messages) {
    state.liveActivity = null;
    state.liveAgentRun = null;
    state.liveStreamContent = null;
    if (typeof resetLiveReasoning === "function") resetLiveReasoning();
    state.messages = response.messages || response.Messages || [];
  }
  if (response.artifacts !== undefined || response.Artifacts !== undefined) {
    state.artifacts = response.artifacts || response.Artifacts || [];
  }
  if (response.activeContextCheckpointId !== undefined || response.ActiveContextCheckpointId !== undefined) {
    state.activeContextCheckpointId = response.activeContextCheckpointId || response.ActiveContextCheckpointId || "";
  }
  if (response.activeHtmlArtifactId !== undefined || response.ActiveHtmlArtifactId !== undefined) {
    state.activeHtmlArtifactId = response.activeHtmlArtifactId || response.ActiveHtmlArtifactId || "";
  }
  if (response.activePlanArtifactId !== undefined || response.ActivePlanArtifactId !== undefined) {
    state.activePlanArtifactId = response.activePlanArtifactId || response.ActivePlanArtifactId || "";
  }
  if (response.contextUsage || response.ContextUsage) {
    state.contextUsage = response.contextUsage || response.ContextUsage || {};
    syncTokenEstimateCalibrationFromUsage();
  }
  if (response.htmlWorkspace || response.HtmlWorkspace) {
    state.htmlWorkspace = response.htmlWorkspace || response.HtmlWorkspace || { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [], redoBranches: [], recovery: { status: "empty", canMutate: true, candidates: [] } };
    state.htmlWorkspaceDirty = false;
  }
  if (chatChanged) {
    restoreChatDraft(state.activeChatId);
    if (typeof clearSendError === "function") clearSendError();
  }
  renderChatSessions();
  renderMessages();
  renderContext(true);
  renderContextMeter();
  renderModelControls();
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
}

function applyChatCatalogState(response) {
  state.chatStateApplyVersion = (state.chatStateApplyVersion || 0) + 1;
  response = response || {};
  if (response.chats !== undefined || response.Chats !== undefined) {
    state.chats = response.chats || response.Chats || [];
  }
  if (response.documents !== undefined || response.Documents !== undefined) {
    state.documents = response.documents || response.Documents || [];
  }
  renderChatSessions();
}

function renderChatSessionList(chats) {
  var list = $("chatSessionList");
  if (!list) {
    return;
  }

  list.innerHTML = "";
  renderChatTreeControls();
  if (!chats.length && !(state.documents || []).length) {
    list.classList.add("is-empty");
    var empty = document.createElement("div");
    empty.className = "chat-tree-empty";
    empty.textContent = state.bridgeUnavailable ? "Office bridge недоступен." : "Чатов пока нет.";
    list.appendChild(empty);
    return;
  }
  list.classList.remove("is-empty");

  var query = (state.chatSearch || "").trim().toLowerCase();
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
  var run = state.chatRuns[id] || state.activeSends[id];
  var persistedRunStatus = String(chat.RunStatus || chat.runStatus || "").toLowerCase();
  var hasActiveRun = !!run || persistedRunStatus === "running" || persistedRunStatus === "cancelling";
  if (hasActiveRun) {
    row.classList.add("has-active-run");
  }
  button.appendChild(title);
  row.appendChild(button);
  if (hasActiveRun) {
    var status = document.createElement("span");
    status.className = "chat-row-status";
    status.title = persistedRunStatus === "cancelling" ? "Запрос останавливается" : "Запрос выполняется";
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

function renderHtmlModeToggle() {
  var button = $("toggleHtmlModeButton");
  if (!button) {
    return;
  }

  button.classList.toggle("active", !!state.activeChatHtmlMode);
  button.setAttribute("aria-pressed", state.activeChatHtmlMode ? "true" : "false");
  button.title = state.activeChatHtmlMode
    ? "HTML-предпочтение включено: агент будет чаще создавать отчёты и визуализации в workspace"
    : "Предпочитать HTML для отчётов и визуализаций в этом чате";
}

function chatHasHtml(chat) {
  return !!(chat && (chat.hasHtmlWorkspace || chat.HasHtmlWorkspace || chatHtmlModeEnabled(chat)));
}

function chatHtmlModeEnabled(chat) {
  return !!(chat && (chat.htmlModeEnabled || chat.HtmlModeEnabled));
}

function chatHtmlFileCount(chat) {
  return Number((chat && (chat.htmlFileCount || chat.HtmlFileCount)) || 0);
}

function chatHtmlDataSourceCount(chat) {
  return Number((chat && (chat.htmlDataSourceCount || chat.HtmlDataSourceCount)) || 0);
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

function lastTokenUsageText() {
  for (var i = state.messages.length - 1; i >= 0; i -= 1) {
    var total = messageTotalTokens(state.messages[i]);
    if (total !== null && total !== undefined) {
      return " · последнее " + total + " токенов";
    }
  }
  return "";
}

function formatCompactTokenCount(value) {
  value = Number(value || 0);
  if (value >= 1000000) return (Math.round(value / 100000) / 10) + "M";
  if (value >= 1000) return (Math.round(value / 100) / 10) + "K";
  return formatNumber(value);
}

function renderContextMeter() {
  var usage = state.contextUsage || {};
  var used = Number(usage.usedTokens || usage.UsedTokens || 0);
  var limit = Number(usage.limitTokens || usage.LimitTokens || 0);
  var windowTokens = Number(usage.contextWindowTokens || usage.ContextWindowTokens || 0);
  var reservedOutput = Number(usage.reservedOutputTokens || usage.ReservedOutputTokens || 0);
  var percent = Number(usage.percent || usage.Percent || (limit ? Math.round(used * 100 / limit) : 0));
  var value = $("contextMeterValue");
  var detail = $("contextMeterDetail");
  var meter = $("contextMeter");
  if (!value || !detail || !meter) {
    return;
  }

  percent = Math.max(0, Math.min(100, percent));
  var actual = !!(usage.actual || usage.Actual);
  var compactDetail = (actual ? "" : "≈") + formatCompactTokenCount(used) + " / " + formatCompactTokenCount(limit);
  var detailText = (actual ? "" : "≈") + formatNumber(used) + " / " + formatNumber(limit) + " вход";
  if (windowTokens) detailText += " · окно " + formatNumber(windowTokens);
  if (reservedOutput) detailText += " · ответ до " + formatNumber(reservedOutput);
  detailText += (actual ? " · API usage" : "") + lastTokenUsageText();
  var level = percent >= 90 ? "danger" : (percent >= 70 ? "warn" : "ok");
  meter.dataset.level = level;
  meter.style.setProperty("--context-meter-percent", percent + "%");
  meter.style.setProperty("--context-meter-color", level === "danger" ? "var(--danger)" : (level === "warn" ? "#b7791f" : "var(--success)"));
  value.textContent = percent + "%";
  detail.textContent = compactDetail;
  meter.title = "Контекст: " + percent + "%\n" + detailText + "\nНажмите, чтобы увидеть состав.";
  meter.setAttribute("aria-label", meter.title);
}

function updateEstimatedContextUsage() {
  var used = 0;
  state.messages.forEach(function (message) {
    if (messageActivity(message) || message.ExcludeFromModelContext || message.excludeFromModelContext) return;
    var role = messageRole(message).toLowerCase();
    var protocol = !!(message.ProtocolMessage || message.protocolMessage);
    if (role !== "user" && role !== "assistant" &&
        !(protocol && (role === "tool" || role === "developer"))) return;
    var content = messageContent(message);
    var pending = message.Local || message.local || message.Pending || message.pending;
    var toolCalls = message.ToolCalls || message.toolCalls || [];
    if (!content.trim() && !pending && !toolCalls.length && role !== "tool") return;
    used += 4 + estimateTextTokens(role) + estimateTextTokens(content);
    if (toolCalls.length) {
      used += 8;
      toolCalls.forEach(function (call) {
        call = call || {};
        used += 4 + estimateTextTokens(call.Id || call.id || "") +
          estimateTextTokens(call.Name || call.name || "") +
          estimateTextTokens(call.ArgumentsJson || call.argumentsJson || "");
      });
    }
    if (role === "tool") {
      used += 2 + estimateTextTokens(message.ToolCallId || message.toolCallId || "") +
        estimateTextTokens(message.ToolName || message.toolName || "");
    }
    messageAttachments(message).forEach(function (attachment) {
      var extracted = String(attachment.ExtractedText || attachment.extractedText || "");
      var chars = Math.max(Number(attachment.ExtractedCharCount || attachment.extractedCharCount || 0), extracted.length);
      used += estimateCharacterCountTokens(chars);
      if (attachmentKind(attachment) === "image") used += 4096;
      if (attachmentKind(attachment) === "audio") {
        used += Math.ceil(Number(attachment.Size || attachment.size || 0) / 512);
      }
    });
  });
  var includedContext = {};
  contextNotes().forEach(function (note) {
    var text = noteText(note);
    var reference = noteReference(note);
    var identity = reference
      ? [noteHost(note), noteKind(note), reference].join("|").toLowerCase()
      : noteId(note);
    if (!text.trim() || includedContext[identity]) return;
    includedContext[identity] = true;
    used += estimateTextTokens(text);
  });
  if (used > 0) used += effectiveTokenEstimateIntercept();

  var settings = state.settings || {};
  var override = Number(settings.ContextWindowOverrideTokens || settings.contextWindowOverrideTokens || 0);
  var modelName = activeChatModel() || settingsModel();
  var model = typeof findModel === "function" ? findModel(modelName) : null;
  var configuredWindow = typeof effectiveModelCapabilityValue === "function"
    ? effectiveModelCapabilityValue(modelName, "MaxContextTokens", "maxContextTokens", model && model.maxContextTokens)
    : (model && model.maxContextTokens);
  var windowTokens = override || Number(configuredWindow || 32768);
  var requestedOutput = Number(settings.MaxTokens || settings.maxTokens || 3072);
  var configuredOutput = typeof effectiveModelCapabilityValue === "function"
    ? effectiveModelCapabilityValue(modelName, "MaxOutputTokens", "maxOutputTokens", model && model.maxOutputTokens)
    : (model && model.maxOutputTokens);
  var modelOutputLimit = Number(configuredOutput || 0);
  var maxOutput = modelOutputLimit > 0 ? Math.min(requestedOutput, modelOutputLimit) : requestedOutput;
  var safety = Math.max(1024, Math.min(16384, Math.ceil(windowTokens * 0.02)));
  var reservedOutput = Math.min(Math.max(1, maxOutput), Math.max(1, windowTokens - safety - 1024));
  var limit = Math.max(1024, windowTokens - reservedOutput - safety);
  state.contextUsage = {
    usedTokens: used,
    limitTokens: limit,
    percent: limit ? Math.min(100, Math.round(used * 100 / limit)) : 0,
    actual: false,
    contextWindowTokens: windowTokens,
    reservedOutputTokens: reservedOutput,
    maxOutputTokens: maxOutput,
    safetyTokens: safety,
    availableOutputTokens: Math.max(0, windowTokens - safety - used),
    estimateMultiplier: effectiveTokenEstimateMultiplier(),
    estimateInterceptTokens: effectiveTokenEstimateIntercept(),
    estimateModel: modelName,
    manualEstimateMultiplier: Number(settings.TokenEstimateMultiplier || settings.tokenEstimateMultiplier || 1),
    autoCalibrateEstimate: settings.AutoCalibrateTokenEstimate !== false && settings.autoCalibrateTokenEstimate !== false
  };
}

function estimateTextTokens(text) {
  text = String(text || "");
  if (!text) return 0;
  var bytes;
  if (window.TextEncoder) {
    bytes = new TextEncoder().encode(text).length;
  } else {
    bytes = unescape(encodeURIComponent(text)).length;
  }
  return Math.max(1, Math.ceil(Math.ceil(bytes / 4) * effectiveTokenEstimateMultiplier()));
}

function estimateCharacterCountTokens(characters) {
  characters = Number(characters || 0);
  if (characters <= 0) return 0;
  return Math.max(1, Math.ceil(Math.ceil(characters / 2) * effectiveTokenEstimateMultiplier()));
}

function effectiveTokenEstimateMultiplier() {
  var settings = state.settings || {};
  var manual = Number(settings.TokenEstimateMultiplier || settings.tokenEstimateMultiplier || 1);
  if (!isFinite(manual) || manual <= 0) manual = 1;
  manual = Math.max(0.25, Math.min(4, manual));
  var automatic = settings.AutoCalibrateTokenEstimate !== false && settings.autoCalibrateTokenEstimate !== false;
  var usage = state.contextUsage || {};
  var modelName = String(activeChatModel() || settingsModel() || "");
  var usageModel = String(usage.estimateModel || usage.EstimateModel || "");
  var usageMultiplier = Number(usage.estimateMultiplier || usage.EstimateMultiplier || 0);
  var usageManual = Number(usage.manualEstimateMultiplier || usage.ManualEstimateMultiplier || 0);
  var usageAutomatic = usage.autoCalibrateEstimate !== false && usage.AutoCalibrateEstimate !== false;
  if (usageMultiplier > 0 && usageModel.toLowerCase() === modelName.toLowerCase() &&
      Math.abs(usageManual - manual) < 0.0001 && usageAutomatic === automatic) {
    return Math.max(0.25, Math.min(4, usageMultiplier));
  }
  if (!automatic) return manual;
  var model = modelName.toLowerCase();
  var calibrations = settings.TokenEstimateCalibrations || settings.tokenEstimateCalibrations || {};
  var key = Object.keys(calibrations).filter(function (item) {
    return String(item || "").toLowerCase() === model;
  })[0];
  var calibration = key ? calibrations[key] : null;
  var samples = Number((calibration && (calibration.SampleCount || calibration.sampleCount)) || 0);
  var relative = Number((calibration && (calibration.Multiplier || calibration.multiplier)) || 1);
  if (!samples || !isFinite(relative) || relative <= 0) return manual;
  return Math.max(0.25, Math.min(4, relative));
}

function effectiveTokenEstimateIntercept() {
  var settings = state.settings || {};
  var automatic = settings.AutoCalibrateTokenEstimate !== false && settings.autoCalibrateTokenEstimate !== false;
  if (!automatic) return 0;
  var usage = state.contextUsage || {};
  var modelName = String(activeChatModel() || settingsModel() || "");
  var usageModel = String(usage.estimateModel || usage.EstimateModel || "");
  var usageIntercept = Number(usage.estimateInterceptTokens || usage.EstimateInterceptTokens || 0);
  if (usageModel.toLowerCase() === modelName.toLowerCase() && isFinite(usageIntercept) && usageIntercept >= 0) {
    return Math.min(65536, Math.ceil(usageIntercept));
  }
  var calibrations = settings.TokenEstimateCalibrations || settings.tokenEstimateCalibrations || {};
  var model = modelName.toLowerCase();
  var key = Object.keys(calibrations).filter(function (item) {
    return String(item || "").toLowerCase() === model;
  })[0];
  var calibration = key ? calibrations[key] : null;
  var samples = Number((calibration && (calibration.SampleCount || calibration.sampleCount)) || 0);
  var intercept = Number((calibration && (calibration.InterceptTokens || calibration.interceptTokens)) || 0);
  return samples > 0 && isFinite(intercept) && intercept > 0 ? Math.min(65536, Math.ceil(intercept)) : 0;
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
