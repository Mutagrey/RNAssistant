function chatModeDefinition(mode) {
  if (mode === "plan") return { value: "plan", title: "Plan", icon: "◇", description: "Исследует, задаёт вопросы и создаёт Markdown-план без изменений" };
  return mode === "chat"
    ? { value: "chat", title: "Chat", icon: "○", description: "Ответ модели с безопасным чтением ресурсов" }
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
  var disabled = !!currentActiveSend() || isPendingChatSubmit(state.activeChatId) ||
    hasActiveMessageEdit() || state.modeSaving || state.reasoningSaving || state.bridgeUnavailable || !state.activeChatId;
  if (typeof setComposerPickerDisabled === "function") setComposerPickerDisabled(picker, disabled);

  menu.replaceChildren();
  [chatModeDefinition("agent"), chatModeDefinition("plan"), chatModeDefinition("chat")].forEach(function (mode) {
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
  var navigating = !!(state.chatNavigationPending || state.initializePromise);
  var activeSend = currentActiveSend();
  var isEditing = hasActiveMessageEdit();
  var isSending = !!activeSend;
  var isPreparingAttachments = !isSending && isPendingChatSubmit(state.activeChatId);
  var isCanceling = isSending && !!activeSend.canceling;
  var approvalPending = !isEditing && typeof pendingAgentApprovalActivity === "function" && !!pendingAgentApprovalActivity();
  var sendButton = $("sendButton");
  var stopButton = $("stopButton");
  var input = $("chatInput");
  var clearButton = $("clearInputButton");
  var modeSelect = $("chatModeSelect");
  var form = $("chatForm");
  var editBar = $("messageEditBar");
  var cancelEditButton = $("cancelMessageEditButton");
  var currentDocumentAvailable = typeof activeChatUsesCurrentDocument !== "function" || activeChatUsesCurrentDocument();

  if ($("newChatButton")) $("newChatButton").disabled = navigating || !!state.bridgeUnavailable;

  if (form) {
    form.classList.toggle("is-message-editing", isEditing);
    form.classList.toggle("is-preparing-attachments", isPreparingAttachments);
  }
  if (editBar) {
    editBar.classList.toggle("hidden", !isEditing);
    editBar.setAttribute("aria-hidden", isEditing ? "false" : "true");
  }
  if (cancelEditButton) {
    cancelEditButton.disabled = !isEditing || state.editingBusy || isSending;
  }

  if (sendButton) {
    sendButton.classList.remove("hidden");
    sendButton.title = isPreparingAttachments ? "Подготовка вложения" : (isEditing ? "Отправить заново" : (isSending ? (runningMessageDelivery() === "Queue" ? "В очередь" : "Отправить сразу") : "Отправить"));
    sendButton.setAttribute("aria-label", sendButton.title);
  }
  if (stopButton) {
    stopButton.classList.toggle("hidden", !isSending);
    stopButton.disabled = isCanceling;
    stopButton.title = isCanceling ? "Останавливаю запрос" : "Остановить запрос";
    stopButton.setAttribute("aria-label", stopButton.title);
  }
  if (input) {
    input.readOnly = navigating || isPreparingAttachments || state.modeSaving || state.reasoningSaving ||
      state.bridgeUnavailable;
    input.placeholder = navigating ? "Обновляю чат…" : isEditing
      ? "Измените сообщение или отправьте его заново..."
      : (state.bridgeUnavailable
        ? "Откройте RNAssistant внутри Office, чтобы начать чат..."
        : (isSending || approvalPending
          ? "Уточните задачу или добавьте сообщение в очередь…"
          : (currentDocumentAvailable ? "Спросите про текущий документ..." : "Обсудите сохранённый контекст...")));
  }
  if (clearButton) {
    clearButton.disabled = isPreparingAttachments || state.editingBusy;
  }
  if (modeSelect) {
    modeSelect.disabled = navigating || isSending || isPreparingAttachments || isEditing || state.modeSaving || state.reasoningSaving || state.bridgeUnavailable || !state.activeChatId;
  }
  renderChatModePicker();
  if (typeof renderChatModelPicker === "function") {
    renderChatModelPicker();
  }
  if (typeof renderReasoningToggle === "function") {
    renderReasoningToggle();
  }
  if (navigating || isPreparingAttachments) {
    if (typeof setComposerPickerDisabled === "function") {
      setComposerPickerDisabled($("chatModePicker"), true);
      setComposerPickerDisabled($("chatModelPicker"), true);
    }
    if ($("chatReasoningToggle")) $("chatReasoningToggle").disabled = true;
  }
  if ($("addSelectionContextButton")) {
    $("addSelectionContextButton").disabled = navigating || isPreparingAttachments || isEditing || state.bridgeUnavailable || !currentDocumentAvailable;
  }
  if ($("attachFileButton")) {
    $("attachFileButton").disabled = navigating || isPreparingAttachments || isEditing || state.bridgeUnavailable || !state.activeChatId;
  }
  if (typeof renderPromptContextInspectorAvailability === "function") {
    renderPromptContextInspectorAvailability();
  }
  if ($("sendDeliveryMenu")) $("sendDeliveryMenu").hidden = isEditing;
  updateComposerInputState();
}

function updateComposerInputState() {
  var input = $("chatInput");
  var form = $("chatForm");
  var clearButton = $("clearInputButton");
  var hasText = !!(input && input.value.trim());
  var hasAttachments = !!(state.draftAttachments && state.draftAttachments.length);
  var hasPendingAttachments = !!(state.chatResourceIngestions && state.activeChatId &&
    state.chatResourceIngestions[state.activeChatId]);

  if (hasActiveMessageEdit() && input) {
    state.editingText = input.value;
  }

  if (form) {
    form.classList.toggle("has-input", hasText || hasAttachments || hasPendingAttachments);
  }
  if (clearButton) {
    clearButton.hidden = !hasText;
  }
  updateSendButtonAvailability(hasText || hasAttachments || hasPendingAttachments);
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
    !!state.chatNavigationPending || !!state.initializePromise ||
    isPendingChatSubmit(state.activeChatId) ||
    state.modelSaving ||
    state.modeSaving ||
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
