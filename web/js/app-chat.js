function chatModeDefinition(mode) {
  return mode === "chat"
    ? { value: "chat", title: "Chat", icon: "○", description: "Прямой ответ модели без инструментов" }
    : { value: "agent", title: "Agent", icon: "✦", description: "Получает skills и tools, выполняет их по одному" };
}

function renderChatModePicker() {
  var picker = $("chatModePicker");
  var menu = $("chatModeMenu");
  var label = $("chatModeButtonLabel");
  var icon = $("chatModeButtonIcon");
  if (!picker || !menu || !label || !icon) return;

  var active = chatModeDefinition(state.activeChatMode || "agent");
  label.textContent = active.title;
  icon.textContent = active.icon;
  icon.dataset.mode = active.value;
  var disabled = !!currentActiveSend() || hasActiveMessageEdit() || state.reasoningSaving || state.bridgeUnavailable || !state.activeChatId;
  if (typeof setComposerPickerDisabled === "function") setComposerPickerDisabled(picker, disabled);

  menu.replaceChildren();
  [chatModeDefinition("agent"), chatModeDefinition("chat")].forEach(function (mode) {
    var button = document.createElement("button");
    button.type = "button";
    button.className = "composer-picker-item composer-mode-item" + (mode.value === active.value ? " is-selected" : "");
    button.setAttribute("role", "option");
    button.setAttribute("aria-selected", mode.value === active.value ? "true" : "false");

    var modeIcon = document.createElement("span");
    modeIcon.className = "composer-mode-item-icon";
    modeIcon.dataset.mode = mode.value;
    modeIcon.textContent = mode.icon;
    button.appendChild(modeIcon);

    var copy = document.createElement("span");
    copy.className = "composer-mode-item-copy";
    var title = document.createElement("strong");
    title.textContent = mode.title;
    copy.appendChild(title);
    var description = document.createElement("span");
    description.textContent = mode.description;
    copy.appendChild(description);
    button.appendChild(copy);

    if (mode.value === active.value) {
      var check = document.createElement("span");
      check.className = "composer-picker-check";
      check.setAttribute("aria-hidden", "true");
      check.textContent = "✓";
      button.appendChild(check);
    }
    button.addEventListener("click", function () {
      if (picker.classList.contains("is-disabled")) return;
      picker.open = false;
      $("chatModeSelect").value = mode.value;
      saveChatMode(mode.value);
    });
    menu.appendChild(button);
  });
}

function renderSendControls() {
  var activeSend = currentActiveSend();
  var isEditing = hasActiveMessageEdit();
  var isSending = !!activeSend;
  var isCanceling = isSending && !!activeSend.canceling;
  var sendButton = $("sendButton");
  var stopButton = $("stopButton");
  var input = $("chatInput");
  var clearButton = $("clearInputButton");
  var modelSelect = $("chatModelSelect");
  var modeSelect = $("chatModeSelect");
  var form = $("chatForm");
  var editBar = $("messageEditBar");
  var cancelEditButton = $("cancelMessageEditButton");
  var currentDocumentAvailable = typeof activeChatUsesCurrentDocument !== "function" || activeChatUsesCurrentDocument();

  if (form) {
    form.classList.toggle("is-message-editing", isEditing);
  }
  if (editBar) {
    editBar.classList.toggle("hidden", !isEditing);
    editBar.setAttribute("aria-hidden", isEditing ? "false" : "true");
  }
  if (cancelEditButton) {
    cancelEditButton.disabled = !isEditing || state.editingBusy || isSending;
  }

  if (sendButton) {
    sendButton.classList.toggle("hidden", isSending);
    sendButton.title = isEditing ? "Сохранить изменения" : "Отправить";
    sendButton.setAttribute("aria-label", sendButton.title);
  }
  if (stopButton) {
    stopButton.classList.toggle("hidden", !isSending);
    stopButton.disabled = isCanceling;
    stopButton.title = isCanceling ? "Останавливаю запрос" : "Остановить запрос";
    stopButton.setAttribute("aria-label", stopButton.title);
  }
  if (input) {
    input.readOnly = isSending || state.reasoningSaving || state.bridgeUnavailable;
    input.placeholder = isEditing
      ? "Измените сообщение..."
      : (state.bridgeUnavailable
        ? "Откройте RNAssistant внутри Office, чтобы начать чат..."
        : (currentDocumentAvailable ? "Спросите про текущий документ..." : "Обсудите сохранённый контекст..."));
  }
  if (clearButton) {
    clearButton.disabled = isSending || state.editingBusy;
  }
  if (modelSelect) {
    modelSelect.disabled = isSending || isEditing || state.modelCatalog.loading || state.modelSaving || state.reasoningSaving || state.bridgeUnavailable || !state.activeChatId;
  }
  if (modeSelect) {
    modeSelect.disabled = isSending || isEditing || state.reasoningSaving || state.bridgeUnavailable || !state.activeChatId;
  }
  renderChatModePicker();
  if (typeof renderChatModelPicker === "function") {
    renderChatModelPicker();
  }
  if (typeof renderReasoningToggle === "function") {
    renderReasoningToggle();
  }
  if ($("addSelectionContextButton")) {
    $("addSelectionContextButton").disabled = isSending || isEditing || state.bridgeUnavailable || !currentDocumentAvailable;
  }
  if ($("toggleHtmlModeButton")) {
    $("toggleHtmlModeButton").disabled = isSending || isEditing || state.bridgeUnavailable || !state.activeChatId;
  }
  if ($("attachFileButton")) {
    $("attachFileButton").disabled = isSending || isEditing || state.bridgeUnavailable || !state.activeChatId;
  }
  var optionsMenu = $("composerOptionsMenu");
  if (optionsMenu) {
    var optionsDisabled = isSending || isEditing || state.bridgeUnavailable || !state.activeChatId;
    optionsMenu.classList.toggle("is-disabled", optionsDisabled);
    if (optionsDisabled) {
      optionsMenu.open = false;
    }
    var optionsSummary = optionsMenu.querySelector("summary");
    if (optionsSummary) {
      optionsSummary.setAttribute("aria-disabled", optionsDisabled ? "true" : "false");
      optionsSummary.tabIndex = optionsDisabled ? -1 : 0;
    }
  }
  if (typeof renderHtmlModeToggle === "function") {
    renderHtmlModeToggle();
  }
  updateComposerInputState();
}

function updateComposerInputState() {
  var input = $("chatInput");
  var form = $("chatForm");
  var clearButton = $("clearInputButton");
  var hasText = !!(input && input.value.trim());
  var hasAttachments = !!(state.draftAttachments && state.draftAttachments.length);

  if (hasActiveMessageEdit() && input) {
    state.editingText = input.value;
  }

  if (form) {
    form.classList.toggle("has-input", hasText || hasAttachments);
  }
  if (clearButton) {
    clearButton.hidden = !hasText;
  }
  updateSendButtonAvailability(hasText || hasAttachments);
  resizeChatInput();
}

function updateSendButtonAvailability(hasContent) {
  var sendButton = $("sendButton");
  if (!sendButton) {
    return;
  }

  var editingTarget = hasActiveMessageEdit() ? findEditingMessage() : null;
  var canSaveEdit = !!editingTarget && canSaveMessageEdit(editingTarget.message, editingTarget.index);
  sendButton.disabled =
    !!currentActiveSend() ||
    state.modelSaving ||
    state.reasoningSaving ||
    state.bridgeUnavailable ||
    !state.activeChatId ||
    (hasActiveMessageEdit() ? !canSaveEdit : !hasContent);
}

function resizeChatInput() {
  var input = $("chatInput");
  if (!input) {
    return;
  }

  input.style.height = "auto";
  var styles = window.getComputedStyle(input);
  var fontSize = parseFloat(styles.fontSize) || 14;
  var lineHeight = parseFloat(styles.lineHeight) || (fontSize * 1.45);
  var verticalChrome =
    (parseFloat(styles.paddingTop) || 0) +
    (parseFloat(styles.paddingBottom) || 0) +
    (parseFloat(styles.borderTopWidth) || 0) +
    (parseFloat(styles.borderBottomWidth) || 0);
  var minHeight = Math.ceil((lineHeight * 2) + verticalChrome);
  var maxHeight = Math.ceil((lineHeight * 6) + verticalChrome);
  var contentHeight = Math.max(input.scrollHeight, minHeight);

  input.style.height = Math.min(contentHeight, maxHeight) + "px";
  input.style.overflowY = contentHeight > maxHeight ? "auto" : "hidden";
}

function setChatInputText(text, shouldFocus) {
  var input = $("chatInput");
  if (!input) {
    return;
  }

  input.value = text || "";
  updateComposerInputState();
  window.requestAnimationFrame(resizeChatInput);
  if (shouldFocus) {
    input.focus();
  }
}

function removeLocalMessage(text) {
  for (var i = state.messages.length - 1; i >= 0; i -= 1) {
    if (state.messages[i] && state.messages[i].Local && messageContent(state.messages[i]) === text) {
      state.messages.splice(i, 1);
      return true;
    }
  }
  return false;
}

async function sendChat(text, attachments) {
  attachments = attachments || [];
  var sentChatId = state.activeChatId;
  var request = send("sendChat", {
    chatId: state.activeChatId,
    text: text,
    attachmentIds: attachments.map(attachmentId)
  });
  state.activeSends[sentChatId] = { requestId: request.requestId, text: text, attachments: attachments, canceling: false };
  beginChatRunTracking(sentChatId);
  renderSendControls();
  renderChatSessions();
  try {
    var response = await request;
    if (state.activeChatId === sentChatId) applyChatState(response);
    else applyChatCatalogState(response);
    if (state.activeChatId === sentChatId) clearSendError();
    if (response.toolResults && response.toolResults.length) {
      logToolResults(response.toolResults);
    }
  } catch (error) {
    if (error.cancelled) {
      if (state.activeChatId === sentChatId) {
        removeLocalMessage(text);
        if (!$("chatInput").value.trim()) setChatInputText(text, false);
        state.draftAttachments = attachments.slice();
        renderDraftAttachments();
        updateEstimatedContextUsage();
        renderContextMeter();
        clearSendError();
      }
      renderChatSessions();
      log("Chat request cancelled.");
    } else {
      if (state.activeChatId === sentChatId) {
        markLocalMessage(text, { Pending: false, Failed: true });
        renderMessages();
        showSendError(error.detail || error.message, text);
        state.failedSend.attachments = attachments;
      }
      log(error.message);
      if (error.detail && error.detail !== error.message) {
        log(error.detail);
      }
    }
  } finally {
    delete state.activeSends[sentChatId];
    endChatRunTracking(sentChatId);
    renderSendControls();
    if (state.activeChatId === sentChatId) renderMessages();
    renderChatSessions();
    renderModelControls();
    renderSendControls();
  }
}

async function submitChatInput() {
  if (hasActiveMessageEdit()) {
    if (!currentActiveSend() && !state.modelSaving && !state.reasoningSaving) {
      state.editingText = $("chatInput").value;
      saveMessageEdit();
    } else {
      focusMessageEditComposer();
    }
    return;
  }
  if (currentActiveSend() || state.modelSaving || state.reasoningSaving) {
    return;
  }

  var text = $("chatInput").value.trim();
  var attachments = (state.draftAttachments || []).slice();
  if (!text && !attachments.length) {
    return;
  }

  setChatInputText("", false);
  clearSendError();
  state.messages.push({ Id: "local-" + Date.now(), Role: "user", Content: text, Attachments: attachments, Local: true, Pending: true });
  clearDraftAttachments();
  updateEstimatedContextUsage();
  renderMessages({ forceScroll: true });
  renderChatSessions();
  renderContextMeter();
  sendChat(text, attachments);
}

function retryFailedSend() {
  if (currentActiveSend() || hasActiveMessageEdit() || !state.failedSend || (!state.failedSend.text && !(state.failedSend.attachments || []).length)) {
    return;
  }

  markLocalMessage(state.failedSend.text, { Pending: true, Failed: false });
  updateEstimatedContextUsage();
  renderMessages({ forceScroll: true });
  renderChatSessions();
  renderContextMeter();
  var text = state.failedSend.text;
  var attachments = state.failedSend.attachments || [];
  clearSendError();
  sendChat(text, attachments);
}

function stopActiveSend() {
  var activeSend = currentActiveSend();
  if (!activeSend || activeSend.canceling) {
    return;
  }

  activeSend.canceling = true;
  renderSendControls();
  var run = state.chatRuns[state.activeChatId] || {};
  var cancellation = run.runId
    ? cancelChatRun(state.activeChatId, run.runId)
    : cancelBridgeRequest(activeSend.requestId);
  cancellation.catch(function (error) {
    log(error.detail || error.message);
  });
}

function currentActiveSend() {
  return state.activeSends[state.activeChatId] || null;
}

function renderChatRunControls() {
  renderMessages();
  renderChatSessions();
  renderModelControls();
  renderSendControls();
}

function beginChatRunTracking(chatId) {
  if (!chatId) {
    return;
  }

  state.chatRuns[chatId] = { activities: [], stream: "", reasoning: "", reasoningComplete: false };
  if (state.activeChatId !== chatId) {
    return;
  }

  state.liveActivity = null;
  state.liveAgentRun = [];
  state.liveStreamContent = "";
  resetLiveReasoning();
}

function endChatRunTracking(chatId) {
  if (!chatId) {
    return;
  }

  delete state.chatRuns[chatId];
  if (state.activeChatId !== chatId) {
    return;
  }

  state.liveActivity = null;
  state.liveAgentRun = null;
  state.liveStreamContent = null;
  resetLiveReasoning();
}

function restoreActiveChatRun() {
  var run = state.chatRuns[state.activeChatId];
  state.liveAgentRun = run && run.activities ? run.activities : null;
  state.liveStreamContent = run && run.stream ? run.stream : null;
  state.liveReasoning = run && run.reasoning ? run.reasoning : "";
  state.liveReasoningComplete = !!(run && run.reasoningComplete);
  state.liveActivity = state.liveAgentRun && state.liveAgentRun.length ? state.liveAgentRun[state.liveAgentRun.length - 1] : null;
  renderMessages();
  renderSendControls();
}

async function confirmAgentTool(pendingId) {
  if (!pendingId || currentActiveSend()) {
    return;
  }

  var chatId = state.activeChatId;
  var request = send("confirmAgentTool", { chatId: chatId, pendingId: pendingId });
  state.activeSends[chatId] = {
    requestId: request.requestId,
    text: "",
    attachments: [],
    canceling: false,
    confirming: true
  };
  beginChatRunTracking(chatId);
  renderChatRunControls();
  try {
    var response = await request;
    if (state.activeChatId === chatId) applyChatState(response);
    else applyChatCatalogState(response);
    log("Agent tool confirmed.");
  } catch (error) {
    log(error.detail || error.message);
  } finally {
    delete state.activeSends[chatId];
    endChatRunTracking(chatId);
    renderChatRunControls();
  }
}

async function cancelAgentTool(pendingId) {
  if (!pendingId) {
    return;
  }

  var approvalDock = $("agentApprovalDock");
  if (approvalDock) {
    Array.prototype.slice.call(approvalDock.querySelectorAll("button")).forEach(function (button) {
      button.disabled = true;
    });
  }
  try {
    applyChatState(await send("cancelAgentTool", { chatId: state.activeChatId, pendingId: pendingId }));
    log("Agent tool cancelled.");
  } catch (error) {
    log(error.detail || error.message);
    if (typeof renderAgentApprovalDock === "function") renderAgentApprovalDock();
  }
}

async function runQuickAction(action) {
  var response = await send("quickAction", { action: action });
  if (response.prompt === "/open-settings") {
    switchTab("settings");
    return;
  }
  if (response.prompt === "/open-context") {
    switchTab("chat");
    if (typeof setContextManagerOpen === "function") {
      setContextManagerOpen(true);
    }
    return;
  }
  setChatInputText(response.prompt || "", false);
  switchTab("chat");
}

async function toggleChatHtmlMode() {
  if (!state.activeChatId || state.bridgeUnavailable || currentActiveSend() || hasActiveMessageEdit()) {
    return;
  }

  try {
    applyChatState(await send("setChatHtmlMode", {
      chatId: state.activeChatId,
      enabled: !state.activeChatHtmlMode
    }));
    log(state.activeChatHtmlMode ? "HTML mode включен." : "HTML mode выключен.");
  } catch (error) {
    log(error.detail || error.message);
  }
}

async function saveChatMode(mode) {
  if (!state.activeChatId || state.bridgeUnavailable || currentActiveSend() || hasActiveMessageEdit()) {
    return;
  }
  try {
    applyChatState(await send("setChatMode", {
      chatId: state.activeChatId,
      mode: mode || "agent"
    }));
    log("Режим чата: " + state.activeChatMode + ".");
  } catch (error) {
    $("chatModeSelect").value = state.activeChatMode || "agent";
    log(error.detail || error.message);
  }
}

function setChatSearchOpen(open, clearQuery) {
  var wrap = $("chatSearchWrap");
  var button = $("toggleChatSearchButton");
  var input = $("chatSearchInput");
  if (!wrap || !button || !input) {
    return;
  }

  wrap.classList.toggle("is-open", !!open);
  wrap.setAttribute("aria-hidden", open ? "false" : "true");
  button.classList.toggle("active", !!open);
  button.setAttribute("aria-expanded", open ? "true" : "false");

  if (open) {
    input.focus();
    return;
  }

  if (clearQuery && (input.value || state.chatSearch)) {
    input.value = "";
    state.chatSearch = "";
    renderChatSessionList(state.chats || []);
  }
}

function bindChatActions() {
  bindMessageScrollControls();
  bindAttachmentActions();
  $("chatSessionSelect").addEventListener("change", function () { selectChat($("chatSessionSelect").value); });
  $("newChatButton").addEventListener("click", createChat);
  $("toggleChatSearchButton").addEventListener("click", function () {
    var wrap = $("chatSearchWrap");
    setChatSearchOpen(!wrap.classList.contains("is-open"), true);
  });
  $("toggleChatSidebarButton").addEventListener("click", function () {
    state.chatSidebarHidden = !state.chatSidebarHidden;
    try {
      window.localStorage.setItem("rnassistant.chat.sidebar.hidden", state.chatSidebarHidden ? "1" : "0");
    } catch (error) {
    }
    renderChatTreeControls();
    if (typeof refreshCodeEditors === "function") {
      refreshCodeEditors();
    }
  });
  $("openDocumentButton").addEventListener("click", openActiveDocument);
  $("chatSearchInput").addEventListener("input", function () {
    state.chatSearch = $("chatSearchInput").value || "";
    renderChatSessionList(state.chats || []);
  });
  $("chatSearchInput").addEventListener("keydown", function (event) {
    if (event.key === "Escape") {
      event.preventDefault();
      setChatSearchOpen(false, true);
      $("toggleChatSearchButton").focus();
    }
  });
  $("toggleHtmlModeButton").addEventListener("click", toggleChatHtmlMode);
  $("chatModeSelect").addEventListener("change", function () {
    saveChatMode($("chatModeSelect").value);
  });
  var optionsMenu = $("composerOptionsMenu");
  var composerPickers = [$("chatModePicker"), $("chatModelPicker")].filter(Boolean);
  composerPickers.forEach(function (picker) {
    var summary = picker.querySelector("summary");
    if (summary) {
      summary.addEventListener("click", function (event) {
        if (picker.classList.contains("is-disabled")) event.preventDefault();
      });
    }
    picker.addEventListener("toggle", function () {
      if (!picker.open) return;
      if (optionsMenu) optionsMenu.open = false;
      composerPickers.forEach(function (other) {
        if (other !== picker) other.open = false;
      });
    });
  });
  document.addEventListener("pointerdown", function (event) {
    if (optionsMenu && optionsMenu.open && !optionsMenu.contains(event.target)) {
      optionsMenu.open = false;
    }
    composerPickers.forEach(function (picker) {
      if (picker.open && !picker.contains(event.target)) picker.open = false;
    });
  });
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape") {
      var opened = composerPickers.filter(function (picker) { return picker.open; })[0] ||
        (optionsMenu && optionsMenu.open ? optionsMenu : null);
      if (opened) {
        opened.open = false;
        var summary = opened.querySelector("summary");
        if (summary) summary.focus();
      }
    }
  });
  $("clearChatButton").addEventListener("click", clearChat);
  $("compactContextButton").addEventListener("click", compactChatContext);
  $("stopButton").addEventListener("click", stopActiveSend);
  $("clearInputButton").addEventListener("click", function () { setChatInputText("", true); });
  $("cancelMessageEditButton").addEventListener("click", cancelMessageEdit);
  $("chatInput").addEventListener("input", updateComposerInputState);
  window.addEventListener("resize", resizeChatInput);
  $("chatInput").addEventListener("keydown", function (event) {
    if (event.key === "Escape" && hasActiveMessageEdit() && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
      event.preventDefault();
      cancelMessageEdit();
      return;
    }
    if (event.key === "Enter" && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
      event.preventDefault();
      submitChatInput();
    }
  });
  $("chatForm").addEventListener("submit", function (event) {
    event.preventDefault();
    submitChatInput();
  });
  updateComposerInputState();
}
