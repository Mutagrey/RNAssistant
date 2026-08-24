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
    log(error.detail || error.message, "error");
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
    log(error.detail || error.message, "error");
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
