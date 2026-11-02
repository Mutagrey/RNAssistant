var modelCatalogPreview = { modelCatalog: null };

function loadModelCatalog(useFormSettings) {
  if (state.chatNavigationPending || state.initializePromise || state.bridgeUnavailable) return Promise.resolve(false);
  var previous = state.modelCatalogRequest;
  if (previous) {
    if (previous.cancelled || previous.useFormSettings !== !!useFormSettings) {
      return cancelModelCatalogLoad().then(function () { return loadModelCatalog(useFormSettings); });
    }
    return previous.completion;
  }
  var operation = { cancelled: false, settings: state.settings, bridgeToken: state.bridgeToken, useFormSettings: !!useFormSettings };
  operation.target = useFormSettings ? modelCatalogPreview : state;
  operation.formRevision = typeof settingsFormRevision === "number" ? settingsFormRevision : 0;
  operation.target.modelCatalog = Object.assign({}, operation.target.modelCatalog || state.modelCatalog,
    { loading: true, error: "" });
  state.modelCatalogRequest = operation;
  renderModelControls();
  operation.completion = (async function () {
    try {
      var apiKey = useFormSettings && $("apiKeyInput") ? $("apiKeyInput").value : "";
      var settings = useFormSettings ? readSettings() : (state.settings || {});
      operation.request = send("getModelCatalog", { settings: settings, apiKey: apiKey || null });
      var response = await operation.request;
      if (operation.cancelled || operation.bridgeToken !== state.bridgeToken || operation.settings !== state.settings) return false;
      if (useFormSettings && typeof settingsFormRevision === "number" && operation.formRevision !== settingsFormRevision) return false;
      normalizeModelCatalog(response, operation.target);
      log("Models loaded: " + operation.target.modelCatalog.models.length);
      return true;
    } catch (error) {
      if (!operation.cancelled && !error.cancelled && operation.bridgeToken === state.bridgeToken && operation.settings === state.settings) {
        if (operation.target.modelCatalog) operation.target.modelCatalog.error = error.message || "Unknown error";
        var message = error.message || "Неизвестная ошибка";
        log(/^Каталог моделей не загружен:/i.test(message) ? message : ("Каталог моделей не загружен: " + message), "warning");
      }
      return false;
    } finally {
      if (!operation.cancelled) finishModelCatalogLoad(operation);
    }
  })();
  return operation.completion;
}

function finishModelCatalogLoad(operation) {
  if (state.modelCatalogRequest !== operation) return;
  state.modelCatalogRequest = null;
  if (operation.target.modelCatalog) operation.target.modelCatalog.loading = false;
  renderModelControls();
}

function cancelModelCatalogLoad() {
  var operation = state.modelCatalogRequest;
  if (!operation) return Promise.resolve();
  operation.cancelled = true;
  if (!operation.cancellation) {
    var cancellation = cancelBridgeRequest(operation.request && operation.request.requestId).catch(function () {});
    // Both bridge handlers must drain before an exclusive host switch can start.
    operation.cancellation = Promise.all([cancellation, operation.completion]).then(function () {
      finishModelCatalogLoad(operation);
    });
  }
  return operation.cancellation;
}

function resetModelCatalog() {
  cancelModelCatalogLoad();
  state.modelCatalog = { configUrl: "", defaultModel: "", models: [], loaded: false, loading: false, error: "" };
  modelCatalogPreview.modelCatalog = null;
}

function discardModelCatalogPreview() {
  if (state.modelCatalogRequest && state.modelCatalogRequest.useFormSettings) cancelModelCatalogLoad();
  modelCatalogPreview.modelCatalog = null;
}

// An uncertain preference write is never retried. Re-read the selected chat before allowing another send.
async function refreshUncertainChatPreference(error, isCurrent, refresh) {
  if (!error.resultUncertain || !isCurrent()) return false;
  try { await refresh(); return isCurrent(); }
  catch (refreshError) {
    if (isCurrent()) failBridgeTransport(null, refreshError);
    return false;
  }
}

function refreshOfficeChatPreference(error, chatId) {
  function selected() {
    return state.activeChatId === chatId && (!window.RNAssistantWorkspaceChat || window.RNAssistantWorkspaceChat.owner() === "office");
  }
  return refreshUncertainChatPreference(error, selected, async function () {
    var response = await send("getChatState", { chatId: chatId });
    if (selected()) applyChatStateForChat(response, chatId);
  });
}

async function saveChatModelSelection(value) {
  if (window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() !== "office")
    return window.RNAssistantWorkspaceChat.selectModel(value);
  value = String(value || "").trim();
  if (chatModelSelection().disabled) {
    return false;
  }
  if (value === activeChatModel()) {
    return true;
  }

  state.modelSaving = true;
  if ($("sendButton") && (!window.RNAssistantWorkspaceChat || window.RNAssistantWorkspaceChat.owner() === "office")) {
    $("sendButton").disabled = true;
  }
  var targetChatId = state.activeChatId;
  try {
    var response = await sendChatPreference("setChatModel", { chatId: targetChatId, model: value });
    if (!applyChatPreferenceStateForChat(response, targetChatId)) return false;
    renderContextMeter();
    log(value ? ("Chat model selected: " + value) : "Chat model uses default.");
    return activeChatModel() === value;
  } catch (error) {
    await refreshOfficeChatPreference(error, targetChatId);
    renderModelControls();
    log(error.detail || error.message, "error");
    return false;
  } finally {
    state.modelSaving = false;
    renderModelControls();
    if (typeof updateComposerInputState === "function") {
      updateComposerInputState();
    }
  }
}

async function saveChatReasoningSelection(enabled) {
  if (chatModelSelection().disabled) {
    return false;
  }
  state.reasoningSaving = true;
  renderReasoningToggle();
  renderSendControls();
  var targetChatId = state.activeChatId;
  try {
    var response = await sendChatPreference("setChatReasoning", { chatId: targetChatId, enabled: !!enabled });
    if (!applyChatPreferenceStateForChat(response, targetChatId)) return false;
    log(enabled ? "Reasoning enabled." : "Reasoning disabled.");
    return state.activeChatReasoning === !!enabled;
  } catch (error) {
    await refreshOfficeChatPreference(error, targetChatId);
    log(error.detail || error.message, "error");
    return false;
  } finally {
    state.reasoningSaving = false;
    renderModelControls();
    renderSendControls();
  }
}

function bindModelActions() {
  $("modelInput").addEventListener("input", function () {
    renderModelCapabilityBadges();
    renderContextWindowSummary();
  });
  $("modelInput").addEventListener("change", function () {
    applyModelDefaultsToForm(findModel($("modelInput").value, modelSettingsFormContext()));
    renderModelControls();
  });
  $("chatReasoningToggle").addEventListener("click", function () {
    if (window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() !== "office") {
      window.RNAssistantWorkspaceChat.composerAction("toggleReasoning"); return;
    }
    if (effectiveModelSupportsReasoning(activeChatModel() || settingsModel()) === false) return;
    saveChatReasoningSelection(!state.activeChatReasoning);
  });
  $("loadModelsButton").addEventListener("click", function () {
    loadModelCatalog(true);
  });
  $("chatModelPicker").addEventListener("toggle", function () {
    if (!$("chatModelPicker").open) return;
    renderChatModelPicker(true);
    if (!state.modelCatalog.loaded && !state.modelCatalog.loading) {
      loadModelCatalog(false);
    }
  });
}
