function logToolResult(prefix, toolId, result) {
  var ok = result && (result.Success === true || result.success === true);
  var message = result ? (result.Message || result.message || "") : "";
  log(prefix + " " + (ok ? "OK" : "FAIL") + ": " + toolId + (message ? " - " + message : ""));
}

function logSkillResults(results) {
  (results || []).forEach(function (result, index) {
    logToolResult("Skill " + (index + 1), result.skillId || result.SkillId || "tool", result);
  });
}

function lastTokenUsageText() {
  for (var i = state.messages.length - 1; i >= 0; i -= 1) {
    var total = messageTotalTokens(state.messages[i]);
    if (total !== null && total !== undefined) {
      return " · last " + total + " tokens";
    }
  }
  return "";
}

function renderContextMeter() {
  var usage = state.contextUsage || {};
  var used = Number(usage.usedChars || usage.UsedChars || 0);
  var limit = Number(usage.limitChars || usage.LimitChars || 0);
  var percent = Number(usage.percent || usage.Percent || (limit ? Math.round(used * 100 / limit) : 0));
  var fill = $("contextMeterFill");
  var value = $("contextMeterValue");
  var detail = $("contextMeterDetail");
  if (!fill || !value || !detail) {
    return;
  }

  percent = Math.max(0, Math.min(100, percent));
  fill.style.width = percent + "%";
  fill.dataset.level = percent >= 90 ? "danger" : (percent >= 70 ? "warn" : "ok");
  value.textContent = percent + "%";
  detail.textContent = formatNumber(used) + " / " + formatNumber(limit) + " chars" + (usage.actual || usage.Actual ? "" : " est.") + lastTokenUsageText();
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

async function createChat() {
  setActivity("loading", "Создаю чат...");
  try {
    applyChatState(await send("createChat", { title: "New chat" }));
    clearSendError();
    log("Chat created.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    clearActivity();
  }
}

async function selectChat(id) {
  if (!id || id === state.activeChatId) {
    return;
  }

  setActivity("loading", "Открываю чат...");
  try {
    applyChatState(await send("selectChat", { chatId: id }));
    clearSendError();
    log("Chat selected.");
  } catch (error) {
    log(error.detail || error.message);
    renderChatSessions();
  } finally {
    clearActivity();
  }
}

async function renameChat() {
  if (!state.activeChatId) {
    return;
  }

  var current = "";
  (state.chats || []).forEach(function (chat) {
    if (chatId(chat) === state.activeChatId) {
      current = chatTitle(chat);
    }
  });

  var title = window.prompt("Chat name", current || "New chat");
  if (title === null || !title.trim()) {
    return;
  }

  try {
    applyChatState(await send("renameChat", { chatId: state.activeChatId, title: title.trim() }));
    log("Chat renamed.");
  } catch (error) {
    log(error.detail || error.message);
  }
}

async function clearChat() {
  if (!state.activeChatId || !window.confirm("Clear this chat?")) {
    return;
  }

  setActivity("clearing", "Очищаю чат...");
  try {
    applyChatState(await send("clearChat", { chatId: state.activeChatId }));
    clearSendError();
    log("Chat cleared.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    clearActivity();
  }
}

async function deleteChat() {
  if (!state.activeChatId || !window.confirm("Delete this chat?")) {
    return;
  }

  setActivity("clearing", "Удаляю чат...");
  try {
    applyChatState(await send("deleteChat", { chatId: state.activeChatId }));
    clearSendError();
    log("Chat deleted.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    clearActivity();
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
    log("Message deleted.");
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
    log("Chat branch created.");
  } catch (error) {
    log(error.detail || error.message);
  }
}

function applyInitState(init) {
  state.host = init.host;
  state.title = init.title;
  state.settings = init.settings || {};
  state.tools = init.tools || [];
  state.toolsPath = init.toolsPath || "";
  state.context = init.context || {};
  state.contextUsage = init.contextUsage || {};
  state.activeChatId = init.activeChatId || "";
  state.activeChatModel = init.activeChatModel || "";
  state.chats = init.chats || [];
  state.messages = init.messages || [];
  $("docLine").textContent = init.host + " - " + init.title;
  $("toolsPath").textContent = state.toolsPath ? "Storage: " + state.toolsPath : "";
  renderSettings();
  renderTools();
  renderContext(true);
  renderChatSessions();
  renderMessages();
  renderContextMeter();
  log("Initialized " + init.host);
  loadModelCatalog(false);
  if (init.quickAction) {
    runQuickAction(init.quickAction);
  }
}

async function initialize() {
  setActivity("loading", "Загружаю состояние...");
  try {
    var init = await send("init");
    applyInitState(init);
  } catch (error) {
    log(error.message);
  } finally {
    clearActivity();
  }
}

async function clearRuntimeData() {
  if (!window.confirm("Delete all local chats, chat context, VBA backups, and WebView cache for RNAssistant? Settings, API key, and custom tools will stay.")) {
    return;
  }

  setActivity("clearing", "Очищаю локальные данные...");
  try {
    var init = await send("clearRuntimeData", {});
    applyInitState(init);
    log("Runtime data cleared.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    clearActivity();
  }
}

async function sendChat(text) {
  setActivity("thinking", "Модель думает...");
  $("sendButton").disabled = true;
  $("chatInput").readOnly = true;
  if ($("chatModelSelect")) {
    $("chatModelSelect").disabled = true;
  }
  try {
    var response = await send("sendChat", { chatId: state.activeChatId, text: text });
    applyChatState(response);
    clearSendError();
    if (response.skillResults && response.skillResults.length) {
      logSkillResults(response.skillResults);
    }
  } catch (error) {
    markLocalMessage(text, { Pending: false, Failed: true });
    renderMessages();
    showSendError(error.detail || error.message, text);
    log(error.message);
    if (error.detail && error.detail !== error.message) {
      log(error.detail);
    }
  } finally {
    $("sendButton").disabled = false;
    $("chatInput").readOnly = false;
    state.liveActivity = null;
    renderMessages();
    renderModelControls();
    clearActivity();
  }
}

function submitChatInput() {
  if ($("sendButton").disabled || state.modelSaving) {
    return;
  }

  var text = $("chatInput").value.trim();
  if (!text) {
    return;
  }

  $("chatInput").value = "";
  clearSendError();
  state.messages.push({ Id: "local-" + Date.now(), Role: "user", Content: text, Local: true, Pending: true });
  updateEstimatedContextUsage();
  renderMessages();
  renderChatSessions();
  renderContextMeter();
  sendChat(text);
}

function retryFailedSend() {
  if (!state.failedSend || !state.failedSend.text) {
    return;
  }

  markLocalMessage(state.failedSend.text, { Pending: true, Failed: false });
  updateEstimatedContextUsage();
  renderMessages();
  renderChatSessions();
  renderContextMeter();
  var text = state.failedSend.text;
  clearSendError();
  sendChat(text);
}

async function runQuickAction(action) {
  var response = await send("quickAction", { action: action });
  if (response.prompt === "/open-settings") {
    switchTab("settings");
    return;
  }
  if (response.prompt === "/open-context") {
    switchTab("context");
    return;
  }
  $("chatInput").value = response.prompt || "";
  switchTab("chat");
}
