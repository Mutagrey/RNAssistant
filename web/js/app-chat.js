function officeCanMessageAction(action) {
  if (!action.message || state.chatNavigationPending || !state.activeChatId) return false;
  if (action.name === "edit") return messageSupportsEdit(action.message, action.index, messageActivity(action.message));
  return (action.name === "fork" || action.name === "delete") && !currentActiveSend() && !hasActiveMessageEdit() &&
    !(typeof pendingAgentApprovalActivity === "function" && pendingAgentApprovalActivity());
}
function officeMessageAction(action) {
  if (!officeCanMessageAction(action)) return;
  if (action.name === "fork") return forkChatAtMessage(action.message, action.index);
  if (action.name === "edit") return startMessageEdit(action.message, action.index);
  if (action.name === "delete") return deleteMessage(action.message, action.index);
}

async function runQuickAction(action) {
  var scope = chatComposer().capture();
  var response = await send("quickAction", { action: action });
  if (!scope.isCurrent()) return;
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
    // The bridge keeps the exception message in `error`; its generic detail is
    // intended for other requests and would hide the Office creation failure.
    var reason = error.code === "bridge_request_failed" && error.message
      ? error.message : (error.detail || error.message || "неизвестная ошибка");
    state.officeHostChatError = "Не удалось открыть чат " + host + ": " +
      reason;
    log(state.officeHostChatError, "error");
  } finally {
    state.officeHostChatPending = false;
    renderChatSessions();
  }
}

function officeHostChatUnavailableReason() {
  if (state.bridgeUnavailable) return state.bridgeFailure && state.bridgeFailure.message || "Панель RN Assistant недоступна.";
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

function bindChatActions() {
  bindMessageScrollControls();
  bindAttachmentActions();
  if (typeof bindModelContextActions === "function") bindModelContextActions();
  if (typeof bindContextMeterActions === "function") bindContextMeterActions();
  $("chatSessionSelect").addEventListener("change", function () { selectChat($("chatSessionSelect").value); });
  $("newChatButton").addEventListener("click", createChat);
  $("openDocumentButton").addEventListener("click", openActiveDocument);
  $("chatSearchInput").addEventListener("input", function () {
    state.chatSearch = $("chatSearchInput").value || "";
    renderChatSessionList(state.chats || []);
  });
  bindComposerPickers();
  $("compactContextButton").addEventListener("click", compactChatContext);
  $("focusReplyButton").addEventListener("click", function () { $("chatInput").focus(); });
  window.addEventListener("resize", resizeChatInput);
  chatComposer().register("office", {
    target: function () { return officeComposerTarget(); },
    canMessageAction: officeCanMessageAction,
    messageAction: officeMessageAction,
    loadPrevious: loadPreviousChatMessages,
    loadLatest: jumpToLatestChatMessages,
    refresh: function () { renderMessages(); renderSendControls(); },
    submit: submitChatInput,
    stop: stopActiveSend,
    clear: function () { setChatInputText("", true); },
    cancelEdit: cancelMessageEdit,
    input: function () {
      updateComposerInputState();
      if (!hasActiveMessageEdit()) captureChatDraft(state.activeChatId, true);
    },
    escape: function (event) {
      if (hasActiveMessageEdit() && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
        event.preventDefault(); cancelMessageEdit();
      }
    }
  });
  chatComposer().bind();
  if (typeof initChatInbox === "function") initChatInbox();
  updateComposerInputState();
}
