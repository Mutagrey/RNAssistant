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

function switchTab(name) {
  Array.prototype.slice.call(document.querySelectorAll(".tab")).forEach(function (tab) {
    tab.classList.toggle("active", tab.dataset.tab === name);
  });
  Array.prototype.slice.call(document.querySelectorAll(".panel")).forEach(function (panel) {
    panel.classList.toggle("active", panel.id === "tab-" + name);
  });
}

document.addEventListener("DOMContentLoaded", function () {
  ["focusin", "focusout", "selectionchange", "mouseup", "keyup"].forEach(function (name) {
    document.addEventListener(name, scheduleFocusStateReport);
  });
  window.addEventListener("focus", scheduleFocusStateReport);
  window.addEventListener("blur", scheduleFocusStateReport);
  scheduleFocusStateReport();

  Array.prototype.slice.call(document.querySelectorAll(".tab")).forEach(function (tab) {
    tab.addEventListener("click", function () { switchTab(tab.dataset.tab); });
  });

  $("helpButton").addEventListener("click", showHelp);
  $("closeHelpButton").addEventListener("click", hideHelp);
  $("helpModal").addEventListener("click", function (event) {
    if (event.target === $("helpModal")) {
      hideHelp();
    }
  });
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape") {
      hideHelp();
    }
  });

  $("refreshButton").addEventListener("click", initialize);
  $("chatSessionSelect").addEventListener("change", function () { selectChat($("chatSessionSelect").value); });
  $("newChatButton").addEventListener("click", createChat);
  $("renameChatButton").addEventListener("click", renameChat);
  $("clearChatButton").addEventListener("click", clearChat);
  $("deleteChatButton").addEventListener("click", deleteChat);
  $("openContextTabButton").addEventListener("click", function () { switchTab("context"); });
  $("addSelectionContextButton").addEventListener("click", function () { addSelectionContext("full"); });
  $("toggleVbaContextButton").addEventListener("click", toggleVbaContext);
  $("retrySendButton").addEventListener("click", retryFailedSend);
  $("refreshVbaButton").addEventListener("click", refreshVbaProject);
  $("vbaModuleSelect").addEventListener("change", renderSelectedVbaModule);
  $("vbaCodeInput").addEventListener("input", renderVbaCodePreview);
  $("previewVbaDiffButton").addEventListener("click", previewVbaDiff);
  $("saveVbaButton").addEventListener("click", saveVbaModule);
  $("restoreVbaButton").addEventListener("click", restoreVbaBackup);
  $("reviewVbaButton").addEventListener("click", reviewVbaInChat);
  $("clearInputButton").addEventListener("click", function () { $("chatInput").value = ""; });
  $("modelSelect").addEventListener("change", function () {
    if ($("modelSelect").value) {
      $("modelInput").value = $("modelSelect").value;
      applyModelDefaultsToForm(findModel($("modelSelect").value));
      renderModelControls();
    }
  });
  $("modelInput").addEventListener("input", renderModelControls);
  $("chatModelSelect").addEventListener("change", function () {
    saveChatModelSelection($("chatModelSelect").value);
  });
  $("loadModelsButton").addEventListener("click", function () {
    loadModelCatalog(true);
  });
  $("chatInput").addEventListener("keydown", function (event) {
    if (event.key === "Enter" && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
      event.preventDefault();
      submitChatInput();
    }
  });
  $("chatForm").addEventListener("submit", function (event) {
    event.preventDefault();
    submitChatInput();
  });

  $("saveSettingsButton").addEventListener("click", async function () {
    try {
      var apiKey = $("apiKeyInput").value;
      var response = await send("saveSettings", { settings: readSettings(), apiKey: apiKey || null });
      state.settings = response.settings;
      $("apiKeyInput").value = "";
      renderSettings();
      updateEstimatedContextUsage();
      renderContextMeter();
      await loadModelCatalog(false);
      log("Settings saved.");
    } catch (error) {
      log(error.message);
    }
  });
  $("clearRuntimeDataButton").addEventListener("click", clearRuntimeData);

  $("addToolButton").addEventListener("click", function () {
    syncSelectedToolFromEditor();
    state.tools.push({
      Id: (state.host || "common").toLowerCase() + ".new_tool",
      Host: state.host || "Common",
      Name: "new_tool",
      Description: "",
      ArgumentSchemaJson: "{}",
      Executor: "pipeline",
      RequiresConfirmation: true,
      PipelineJson: "{\n  \"version\": 1,\n  \"steps\": []\n}",
      Code: "",
      Readme: "",
      Enabled: true,
      BuiltIn: false
    });
    state.selectedToolIndex = state.tools.length - 1;
    renderTools();
  });

  $("cloneToolButton").addEventListener("click", function () {
    syncSelectedToolFromEditor();
    var source = state.tools[state.selectedToolIndex];
    if (!source) {
      return;
    }

    var id = (source.Id || "tool") + ".copy";
    state.tools.push({
      Id: id,
      Host: source.Host || state.host || "Common",
      Name: id,
      Description: source.Description || "",
      ArgumentSchemaJson: source.ArgumentSchemaJson || "{}",
      Executor: source.BuiltIn ? "pipeline" : (source.Executor || "pipeline"),
      RequiresConfirmation: source.BuiltIn ? true : !!source.RequiresConfirmation,
      PipelineJson: source.PipelineJson || "{\n  \"version\": 1,\n  \"steps\": []\n}",
      Code: source.Code || "",
      Readme: source.Readme || "",
      Enabled: true,
      BuiltIn: false
    });
    state.selectedToolIndex = state.tools.length - 1;
    renderTools();
  });

  $("saveToolsButton").addEventListener("click", async function () {
    try {
      var response = await send("saveTools", { tools: readTools() });
      state.tools = response || [];
      renderTools();
      log("Tools saved.");
    } catch (error) {
      log(error.message);
    }
  });

  $("deleteToolButton").addEventListener("click", function () {
    var skill = state.tools[state.selectedToolIndex];
    if (!skill || skill.BuiltIn) {
      return;
    }

    state.tools.splice(state.selectedToolIndex, 1);
    if (state.selectedToolIndex >= state.tools.length) {
      state.selectedToolIndex = state.tools.length - 1;
    }
    renderTools();
  });

  $("dryRunToolButton").addEventListener("click", function () {
    runSelectedTool(true);
  });

  $("runToolButton").addEventListener("click", function () {
    runSelectedTool(false);
  });

  $("copyToolContextButton").addEventListener("click", function () {
    copyText(selectedToolContext());
    log("Tool context copied.");
  });

  $("askToolBuilderButton").addEventListener("click", function () {
    addSelectedToolContextToContext().then(function (added) {
      if (!added) {
        return;
      }

      $("chatInput").value = "Отредактируй RNAssistant tool из добавленного контекста. Верни обновленные tool.json/pipeline/code блоки, не выполняй действия без подтверждения.";
      switchTab("chat");
      $("chatInput").focus();
    }).catch(function (error) {
      log(error.detail || error.message);
    });
  });

  $("clearContextButton").addEventListener("click", async function () {
    setActivity("clearing", "Очищаю контекст...");
    try {
      state.context = await send("clearContext", { chatId: state.activeChatId });
      renderContext();
      log("Context cleared.");
    } catch (error) {
      log(error.message);
    } finally {
      clearActivity();
    }
  });

  window.addEventListener("load", function () {
    if (window.hljs) {
      highlightAllCode();
    }
  });

  initialize();
});
