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

async function createOfficeHostChat(host) {
  if (state.officeHostChatPending) return;
  var unavailable = officeHostChatUnavailableReason();
  if (unavailable) {
    state.officeHostChatError = unavailable;
    renderChatSessions();
    return;
  }
  if (typeof confirmDiscardHtmlWorkspaceChanges === "function" &&
      !confirmDiscardHtmlWorkspaceChanges("Создать новый чат")) return;
  state.officeHostChatPending = true;
  state.officeHostChatTarget = host;
  state.officeHostChatError = "";
  renderChatSessions();
  try {
    var result = await navigateChat("createOfficeHostChat", { host: host });
    state.officeHostLaunchOpen = false;
    log("Чат создан: " + host + (result.documentTitle ? " — " + result.documentTitle : "") + ".");
  } catch (error) {
    state.officeHostChatError = "Не удалось открыть чат " + host + ": " +
      (error.detail || error.message || "неизвестная ошибка");
    log(state.officeHostChatError, "error");
  } finally {
    state.officeHostChatPending = false;
    renderChatSessions();
  }
}

function officeHostChatUnavailableReason() {
  if (state.bridgeUnavailable) return "Нет связи с Office. Повторно откройте панель RN Assistant.";
  if (!state.officeHostChatAvailable) return "Создание Office-чата недоступно в этом окне.";
  if (state.officeHostChatPending) return "Создаю чат в " + state.officeHostChatTarget + "…";
  if (currentActiveSend()) return "Дождитесь завершения ответа или остановите текущую задачу.";
  if (hasActiveMessageEdit()) return "Завершите редактирование сообщения перед созданием чата.";
  return "";
}

async function saveChatMode(mode) {
  mode = mode || "agent";
  if (state.chatReadOnly || !state.activeChatId || state.bridgeUnavailable || state.modeSaving || state.chatNavigationPending || state.initializePromise ||
      state.chatDetailPending || state.chatDetailError || currentActiveSend() || hasActiveMessageEdit()) {
    return false;
  }
  if (mode === state.activeChatMode) {
    return true;
  }
  var targetChatId = state.activeChatId;
  var previousMode = state.activeChatMode || "agent";
  state.modeSaving = true;
  state.activeChatMode = mode;
  renderChatSessions();
  try {
    var applied = applyChatStateForChat(await sendChatPreference("setChatMode", {
      chatId: targetChatId,
      mode: mode
    }), targetChatId);
    if (applied) log("Режим чата: " + state.activeChatMode + ".");
    return applied && state.activeChatMode === mode;
  } catch (error) {
    if (state.activeChatId === targetChatId) state.activeChatMode = previousMode;
    log(error.detail || error.message, "error");
    return false;
  } finally {
    state.modeSaving = false;
    renderChatSessions();
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
  wrap.closest(".chat-sidebar-head").classList.toggle("searching", !!open);
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
  if (typeof bindModelContextActions === "function") bindModelContextActions();
  if (typeof bindContextInspectorActions === "function") bindContextInspectorActions();
  $("chatSessionSelect").addEventListener("change", function () { selectChat($("chatSessionSelect").value); });
  $("newChatButton").addEventListener("click", createChat);
  $("toggleChatSearchButton").addEventListener("click", function () {
    var wrap = $("chatSearchWrap");
    setChatSearchOpen(!wrap.classList.contains("is-open"), true);
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
  bindComposerPickers();
  $("compactContextButton").addEventListener("click", compactChatContext);
  $("stopButton").addEventListener("click", stopActiveSend);
  $("clearInputButton").addEventListener("click", function () { setChatInputText("", true); });
  $("focusReplyButton").addEventListener("click", function () { $("chatInput").focus(); });
  $("cancelMessageEditButton").addEventListener("click", cancelMessageEdit);
  $("chatInput").addEventListener("input", updateComposerInputState);
  window.addEventListener("resize", resizeChatInput);
  $("chatInput").addEventListener("keydown", function (event) {
    if (event.key === "Escape" && hasActiveMessageEdit() && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
      event.preventDefault();
      cancelMessageEdit();
      return;
    }
    if (event.key === "Enter" && !event.isComposing && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
      event.preventDefault();
      submitChatInput();
    }
  });
  $("chatForm").addEventListener("submit", function (event) {
    event.preventDefault();
    submitChatInput();
  });
  if (typeof initChatInbox === "function") initChatInbox();
  updateComposerInputState();
}
