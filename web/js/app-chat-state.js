function renderChatSessions() {
  var select = $("chatSessionSelect");
  if (!select) {
    return;
  }

  select.innerHTML = "";
  (state.chats || []).forEach(function (chat) {
    var option = document.createElement("option");
    option.value = chatId(chat);
    var model = chatModel(chat);
    option.textContent = chatTitle(chat) + " (" + chatMessageCount(chat) + ")" + (model ? " - " + model : "");
    select.appendChild(option);
  });
  select.value = state.activeChatId || "";

  var hasActive = !!state.activeChatId;
  $("renameChatButton").disabled = !hasActive;
  $("clearChatButton").disabled = !hasActive || !state.messages.length;
  $("deleteChatButton").disabled = !hasActive;
}

function applyChatState(response) {
  response = response || {};
  state.activeChatId = response.activeChatId || response.ActiveChatId || state.activeChatId || "";
  if (response.activeChatModel !== undefined || response.ActiveChatModel !== undefined) {
    state.activeChatModel = response.activeChatModel || response.ActiveChatModel || "";
  }
  state.chats = response.chats || response.Chats || state.chats || [];
  if (response.context || response.Context) {
    state.context = response.context || response.Context || {};
  }
  if (response.messages || response.Messages) {
    state.liveActivity = null;
  }
  state.messages = response.messages || response.Messages || [];
  state.contextUsage = response.contextUsage || response.ContextUsage || state.contextUsage || {};
  renderChatSessions();
  renderMessages();
  renderContext(true);
  renderContextMeter();
  renderModelControls();
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
