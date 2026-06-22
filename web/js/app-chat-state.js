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

  var hasActive = !!state.activeChatId;
  var hasMessages = !!(state.messages && state.messages.length);
  $("newChatButton").disabled = !!state.bridgeUnavailable;
  $("renameChatButton").disabled = !hasActive;
  $("clearChatButton").disabled = !hasActive || !hasMessages;
  $("clearChatButton").hidden = !hasActive || !hasMessages;
  $("deleteChatButton").disabled = !hasActive;
  renderHtmlModeToggle();
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
  if (response.chats || response.Chats) {
    state.chats = response.chats || response.Chats || [];
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
  if (!chats.length) {
    list.classList.add("is-empty");
    return;
  }
  list.classList.remove("is-empty");

  chats.forEach(function (chat) {
    var id = chatId(chat);
    var button = document.createElement("button");
    button.type = "button";
    button.className = "chat-session-row" + (id === state.activeChatId ? " active" : "");
    button.disabled = !!state.bridgeUnavailable;
    button.addEventListener("click", function () { selectChat(id); });

    var title = document.createElement("span");
    title.className = "chat-session-title";
    title.textContent = chatTitle(chat);
    button.appendChild(title);

    var meta = document.createElement("span");
    meta.className = "chat-session-meta";
    meta.textContent = chatMessageCount(chat) + " msg" + (chatModel(chat) ? " · " + chatModel(chat) : "");
    button.appendChild(meta);

    if (chatHtmlModeEnabled(chat)) {
      button.appendChild(createChatSessionBadge("HTML mode", "mode"));
    } else if (chatHasHtml(chat)) {
      button.appendChild(createChatSessionBadge("HTML " + chatHtmlFileCount(chat) + "/" + chatHtmlDataSourceCount(chat), "workspace"));
    }
    list.appendChild(button);
  });
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
