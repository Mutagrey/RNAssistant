function officeCanMessageAction(action) { return chatComposer().dispatch("canMessageAction", action); }
function officeMessageAction(action, scope) { return (scope || chatComposer().capture()).invoke("messageAction", action); }

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

function saveChatMode(mode) { return chatComposer().setPreference("mode", mode || "agent"); }

function bindChatActions() {
  if (typeof bindModelContextActions === "function") bindModelContextActions();
  $("chatSessionSelect").addEventListener("change", function () { selectChat($("chatSessionSelect").value); });
  $("newChatButton").addEventListener("click", createChat);
  $("chatSearchInput").addEventListener("input", function () {
    state.chatSearch = $("chatSearchInput").value || ""; renderChatSessionList(state.chats || []);
  });
}
