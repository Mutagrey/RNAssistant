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
    option.textContent = (chatHasHtml(chat) ? "[HTML] " : "") + chatTitle(chat) + " (" + chatMessageCount(chat) + ")" + (model ? " - " + model : "");
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
  $("clearChatButton").disabled = !hasActive || !hasMessages;
  $("clearChatButton").hidden = !hasActive || !hasMessages;
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
  state.activeChatId = response.activeChatId || response.ActiveChatId || state.activeChatId || "";
  if (response.activeChatModel !== undefined || response.ActiveChatModel !== undefined) {
    state.activeChatModel = response.activeChatModel || response.ActiveChatModel || "";
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
  if (typeof renderHtmlWorkspace === "function") {
    renderHtmlWorkspace();
  }
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
  group.className = "chat-document" + (documentItem.current ? " is-current" : (documentItem.open ? " is-open" : " is-closed"));
  var collapsed = !query && (state.chatTreeCollapsedAll || !!state.collapsedChatDocuments[documentItem.key]);

  var header = document.createElement("div");
  header.className = "chat-document-row";
  header.setAttribute("aria-expanded", collapsed ? "false" : "true");
  header.innerHTML =
    "<button type=\"button\" class=\"chat-document-toggle\" aria-label=\"Свернуть или развернуть документ\">" +
    "<span class=\"chat-document-caret\">›</span>" +
    "<span class=\"chat-document-icon\">" + documentHostInitial(documentItem.host) + "</span>" +
    "<span class=\"chat-document-name\"></span>" +
    "<span class=\"chat-document-state\">" + (documentItem.current ? "Активен" : (documentItem.open ? "Открыт" : "Закрыт")) + "</span></button>" +
    "<button type=\"button\" class=\"chat-row-action chat-document-open\" title=\"" + (documentItem.open ? "Активировать документ" : "Открыть документ") + "\" aria-label=\"" + (documentItem.open ? "Активировать документ" : "Открыть документ") + "\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><rect x=\"4\" y=\"7\" width=\"11\" height=\"11\" rx=\"1.5\"/><path d=\"M9 7V5.5A1.5 1.5 0 0 1 10.5 4H19a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1h-4\"/><path d=\"M11 12h8\"/><path d=\"m16 9 3 3-3 3\"/></svg></button>";
  header.querySelector(".chat-document-name").textContent = documentItem.title;
  header.querySelector(".chat-document-toggle").addEventListener("click", function () {
    state.chatTreeCollapsedAll = false;
    state.collapsedChatDocuments[documentItem.key] = !children.hidden;
    renderChatSessionList(state.chats || []);
  });
  header.querySelector(".chat-document-open").addEventListener("click", function () {
    if (documentItem.open) {
      activateDocument(documentItem.documentKey);
    } else if (documentItem.chats.length) {
      openActiveDocument(chatId(documentItem.chats[0]));
    }
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
  button.appendChild(title);
  var meta = document.createElement("span");
  meta.className = "chat-session-meta";
  meta.textContent = chatMessageCount(chat) + " сообщ.";
  button.appendChild(meta);
  row.appendChild(button);
  var actions = document.createElement("span");
  actions.className = "chat-row-actions";
  actions.innerHTML = "<button type=\"button\" class=\"chat-row-action chat-edit\" title=\"Переименовать\" aria-label=\"Переименовать чат\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M12 20h9\"/><path d=\"M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z\"/></svg></button><button type=\"button\" class=\"chat-row-action chat-delete\" title=\"Удалить\" aria-label=\"Удалить чат\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M3 6h18\"/><path d=\"M8 6V4h8v2\"/><path d=\"m19 6-1 14H6L5 6\"/><path d=\"M10 11v5\"/><path d=\"M14 11v5\"/></svg></button>";
  actions.querySelector(".chat-edit").addEventListener("click", function () { renameChat(id); });
  actions.querySelector(".chat-delete").addEventListener("click", function () { deleteChat(id); });
  row.appendChild(actions);
  return row;
}

function documentHostInitial(host) {
  var values = { Excel: "X", Word: "W", PowerPoint: "P", Outlook: "O" };
  return values[host] || (host || "?").charAt(0).toUpperCase();
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
  var used = Number(usage.usedChars || usage.UsedChars || 0);
  var limit = Number(usage.limitChars || usage.LimitChars || 0);
  var percent = Number(usage.percent || usage.Percent || (limit ? Math.round(used * 100 / limit) : 0));
  var value = $("contextMeterValue");
  var detail = $("contextMeterDetail");
  var meter = $("contextMeter");
  if (!value || !detail || !meter) {
    return;
  }

  percent = Math.max(0, Math.min(100, percent));
  var detailText = formatNumber(used) + " / " + formatNumber(limit) + " символов" + (usage.actual || usage.Actual ? "" : " · оценка") + lastTokenUsageText();
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
    used += messageContent(message).length;
  });
  contextNotes().forEach(function (note) {
    used += noteText(note).length;
  });

  var limit = Number((state.settings && (state.settings.ContextCharLimit || state.settings.contextCharLimit)) || 24000);
  state.contextUsage = {
    usedChars: used,
    limitChars: limit,
    percent: limit ? Math.min(100, Math.round(used * 100 / limit)) : 0,
    actual: false
  };
}

function showSendError(error, text) {
  state.failedSend = { text: text || "", error: error || "Unknown error" };
  var box = $("sendError");
  var message = $("sendErrorText");
  if (box && message) {
    message.textContent = state.failedSend.error;
    box.classList.remove("hidden");
  }
}

function clearSendError() {
  state.failedSend = null;
  var box = $("sendError");
  if (box) {
    box.classList.add("hidden");
  }
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
