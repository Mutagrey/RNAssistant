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
    var model = chatModel(chat);
    option.textContent = (chatHasHtml(chat) ? "[HTML] " : "") + chatTitle(chat) + " (" + chatMessageCount(chat) + ", " + chatMode(chat) + ")" + (model ? " - " + model : "");
    select.appendChild(option);
  });
  select.value = state.activeChatId || "";
  select.disabled = !chats.length || state.bridgeUnavailable;
  renderChatSessionList(chats);

  var activeChat = activeChatSummary();
  var isCurrentDocument = !activeChat || chatIsCurrentDocument(activeChat);
  $("activeChatTitle").textContent = activeChat ? chatTitle(activeChat) : "Новый чат";
  $("activeChatDocument").textContent = activeChat
    ? [chatDocumentTitle(activeChat), chatHost(activeChat)].filter(Boolean).join(" · ")
    : "";
  $("offlineNotice").classList.toggle("hidden", isCurrentDocument);
  $("openDocumentButton").hidden = isCurrentDocument || !chatDocumentPath(activeChat);

  var hasActive = !!state.activeChatId;
  var hasMessages = !!(state.messages && state.messages.length);
  $("newChatButton").disabled = !!state.bridgeUnavailable;
  $("clearChatButton").disabled = !hasActive || !hasMessages || !!currentActiveSend();
  $("clearChatButton").hidden = !hasActive || !hasMessages;
  if ($("chatModeSelect")) {
    $("chatModeSelect").value = state.activeChatMode || "chat";
  }
  renderHtmlModeToggle();
  renderSendControls();
}

function activeChatSummary() {
  return (state.chats || []).filter(function (chat) {
    return chatId(chat) === state.activeChatId;
  })[0] || null;
}

function activeChatUsesCurrentDocument() {
  var active = activeChatSummary();
  return !active || chatIsCurrentDocument(active);
}

function applyChatState(response) {
  response = response || {};
  if (typeof resetMessageEditState === "function") {
    resetMessageEditState();
  }
  state.activeChatId = response.activeChatId || response.ActiveChatId || state.activeChatId || "";
  if (response.activeChatModel !== undefined || response.ActiveChatModel !== undefined) {
    state.activeChatModel = response.activeChatModel || response.ActiveChatModel || "";
  }
  if (response.activeChatMode !== undefined || response.ActiveChatMode !== undefined) {
    state.activeChatMode = response.activeChatMode || response.ActiveChatMode || "chat";
  }
  if (response.activeChatHtmlMode !== undefined || response.ActiveChatHtmlMode !== undefined) {
    state.activeChatHtmlMode = !!(response.activeChatHtmlMode || response.ActiveChatHtmlMode);
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
    state.liveStreamContent = null;
    state.messages = response.messages || response.Messages || [];
  }
  if (response.contextUsage || response.ContextUsage) {
    state.contextUsage = response.contextUsage || response.ContextUsage || {};
  }
  if (response.htmlWorkspace || response.HtmlWorkspace) {
    state.htmlWorkspace = response.htmlWorkspace || response.HtmlWorkspace || { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [] };
    state.htmlWorkspaceDirty = false;
  }
  renderChatSessions();
  renderMessages();
  renderContext(true);
  renderContextMeter();
  renderModelControls();
  if ($("chatModeSelect")) {
    $("chatModeSelect").value = state.activeChatMode || "chat";
  }
  if (typeof renderHtmlWorkspace === "function") {
    renderHtmlWorkspace();
  }
}

function applyChatCatalogState(response) {
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

function chatDocumentTreeKeys() {
  var keys = {};
  (state.chats || []).forEach(function (chat) {
    keys[chatHost(chat) + "|" + chatDocumentKey(chat)] = true;
  });
  (state.documents || []).forEach(function (item) {
    var host = item.host || item.Host || state.host || "";
    var documentKey = item.documentKey || item.DocumentKey || "";
    keys[host + "|" + documentKey] = true;
  });
  return Object.keys(keys);
}

function allChatDocumentsCollapsed() {
  var keys = chatDocumentTreeKeys();
  return keys.length > 0 && keys.every(function (key) {
    return !!state.collapsedChatDocuments[key];
  });
}

function setAllChatDocumentsCollapsed(collapsed) {
  chatDocumentTreeKeys().forEach(function (key) {
    if (collapsed) {
      state.collapsedChatDocuments[key] = true;
    } else {
      delete state.collapsedChatDocuments[key];
    }
  });
  state.chatTreeCollapsedAll = !!collapsed;
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
    "<span class=\"chat-document-caret\">›</span>" +
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
    state.chatTreeCollapsedAll = allChatDocumentsCollapsed();
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
  var treeButton = $("toggleChatTreeButton");
  if (treeButton) {
    state.chatTreeCollapsedAll = allChatDocumentsCollapsed();
    var collapse = !state.chatTreeCollapsedAll;
    treeButton.title = collapse ? "Свернуть всё дерево" : "Развернуть всё дерево";
    treeButton.setAttribute("aria-label", treeButton.title);
    treeButton.setAttribute("aria-pressed", state.chatTreeCollapsedAll ? "true" : "false");
    treeButton.innerHTML = state.chatTreeCollapsedAll
      ? "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"m8 8 4 4 4-4\"/><path d=\"m8 14 4 4 4-4\"/></svg>"
      : "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"m8 10 4-4 4 4\"/><path d=\"m8 16 4-4 4 4\"/></svg>";
  }

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
  var persistedRunStatus = chat.RunStatus || chat.runStatus || "";
  if (run || persistedRunStatus === "running") {
    var spinner = document.createElement("span");
    spinner.className = "chat-run-spinner";
    spinner.title = "Запрос выполняется";
    title.appendChild(spinner);
  }
  button.appendChild(title);
  var meta = document.createElement("span");
  meta.className = "chat-session-meta";
  meta.textContent = chatMessageCount(chat) + " сообщ. · " + chatMode(chat);
  button.appendChild(meta);
  row.appendChild(button);
  var actions = document.createElement("span");
  actions.className = "chat-row-actions";
  actions.innerHTML = "<button type=\"button\" class=\"chat-row-action chat-edit\" title=\"Переименовать\" aria-label=\"Переименовать чат\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M12 20h9\"/><path d=\"M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z\"/></svg></button><button type=\"button\" class=\"chat-row-action chat-delete\" title=\"Удалить\" aria-label=\"Удалить чат\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M3 6h18\"/><path d=\"M8 6V4h8v2\"/><path d=\"m19 6-1 14H6L5 6\"/><path d=\"M10 11v5\"/><path d=\"M14 11v5\"/></svg></button>";
  actions.querySelector(".chat-edit").addEventListener("click", function () { renameChat(id); });
  actions.querySelector(".chat-delete").addEventListener("click", function () { deleteChat(id); });
  if (run || persistedRunStatus === "running") {
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
  if (host === "Excel") {
    return "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><rect x=\"5\" y=\"4\" width=\"14\" height=\"16\" rx=\"1\"/><path d=\"M5 9h14M10 4v16M14.5 9v11\"/></svg>";
  }
  if (host === "Word") {
    return "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M6 4h12v16H6zM9 9h6M9 12h6M9 15h4\"/></svg>";
  }
  if (host === "PowerPoint") {
    return "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><rect x=\"5\" y=\"4\" width=\"14\" height=\"16\" rx=\"1\"/><path d=\"M9 15V9h3a2 2 0 0 1 0 4H9\"/></svg>";
  }
  if (host === "Outlook") {
    return "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><rect x=\"4\" y=\"6\" width=\"16\" height=\"12\" rx=\"1\"/><path d=\"m5 8 7 5 7-5\"/></svg>";
  }
  return "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M6 3h9l3 3v15H6zM15 3v4h4\"/></svg>";
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
    ? "HTML mode включен: агент будет работать через HTML workspace"
    : "Вести этот чат как HTML workspace";
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
  log(prefix + " " + (ok ? "OK" : "FAIL") + ": " + toolId + (message ? " - " + message : ""));
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
  var detailText = formatNumber(used) + " / " + formatNumber(limit) + " вход";
  if (windowTokens) detailText += " · окно " + formatNumber(windowTokens);
  if (reservedOutput) detailText += " · ответ до " + formatNumber(reservedOutput);
  detailText += (usage.actual || usage.Actual ? " · API usage" : " · оценка") + lastTokenUsageText();
  var level = percent >= 90 ? "danger" : (percent >= 70 ? "warn" : "ok");
  meter.dataset.level = level;
  meter.style.setProperty("--context-meter-percent", percent + "%");
  meter.style.setProperty("--context-meter-color", level === "danger" ? "var(--danger)" : (level === "warn" ? "#b7791f" : "var(--success)"));
  value.textContent = percent + "%";
  detail.textContent = detailText;
  meter.title = "Контекст: " + percent + "%\n" + detailText;
  meter.setAttribute("aria-label", meter.title);
}

function updateEstimatedContextUsage() {
  var used = 0;
  state.messages.forEach(function (message) {
    if (messageActivity(message)) return;
    var role = messageRole(message).toLowerCase();
    if (role !== "user" && role !== "assistant") return;
    var content = messageContent(message);
    var pending = message.Local || message.local || message.Pending || message.pending;
    if (!content.trim() && !pending) return;
    if (content.trim()) used += 4 + estimateTextTokens(content);
    if (pending) {
      messageAttachments(message).forEach(function (attachment) {
        var chars = Number(attachment.ExtractedCharCount || attachment.extractedCharCount || 0);
        used += Math.ceil(chars / 2);
        if (attachmentKind(attachment) === "image") used += 4096;
      });
    }
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

  var settings = state.settings || {};
  var override = Number(settings.ContextWindowOverrideTokens || settings.contextWindowOverrideTokens || 0);
  var modelName = activeChatModel() || settingsModel();
  var model = typeof findModel === "function" ? findModel(modelName) : null;
  var capabilities = settings.ModelCapabilities || settings.modelCapabilities || {};
  var capability = capabilities[modelName] || {};
  var windowTokens = override || Number((model && model.maxContextTokens) || capability.MaxContextTokens || capability.maxContextTokens || 32768);
  var requestedOutput = Number(settings.MaxTokens || settings.maxTokens || 2048);
  var modelOutputLimit = Number((model && model.maxOutputTokens) || capability.MaxOutputTokens || capability.maxOutputTokens || 0);
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
    availableOutputTokens: Math.max(0, windowTokens - safety - used)
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
  return Math.max(1, Math.ceil(bytes / 3));
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
