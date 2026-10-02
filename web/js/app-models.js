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
  state.modelCatalogRequest = operation;
  state.modelCatalog.loading = true;
  state.modelCatalog.error = "";
  renderModelControls();
  operation.completion = (async function () {
    try {
      var apiKey = useFormSettings && $("apiKeyInput") ? $("apiKeyInput").value : "";
      var settings = useFormSettings ? readSettings() : (state.settings || {});
      operation.request = send("getModelCatalog", { settings: settings, apiKey: apiKey || null });
      var response = await operation.request;
      if (operation.cancelled || operation.bridgeToken !== state.bridgeToken || operation.settings !== state.settings) return false;
      normalizeModelCatalog(response);
      state.modelCatalog.fromSettingsForm = operation.useFormSettings;
      log("Models loaded: " + state.modelCatalog.models.length);
      return true;
    } catch (error) {
      if (!operation.cancelled && !error.cancelled && operation.bridgeToken === state.bridgeToken && operation.settings === state.settings) {
        state.modelCatalog.error = error.message || "Unknown error";
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
  state.modelCatalog.loading = false;
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
}

function discardModelCatalogPreview() {
  if (state.modelCatalog.fromSettingsForm || state.modelCatalogRequest && state.modelCatalogRequest.useFormSettings) {
    resetModelCatalog();
  }
}

async function saveChatModelSelection(value) {
  value = String(value || "").trim();
  if (!state.activeChatId || state.modelSaving || state.reasoningSaving || state.chatNavigationPending || state.initializePromise || hasActiveMessageEdit() || !!currentActiveSend()) {
    return false;
  }
  if (value === activeChatModel()) {
    return true;
  }

  state.modelSaving = true;
  if ($("sendButton")) {
    $("sendButton").disabled = true;
  }
  var targetChatId = state.activeChatId;
  try {
    var response = await sendChatPreference("setChatModel", { chatId: targetChatId, model: value });
    if (!applyChatStateForChat(response, targetChatId)) return false;
    renderContextMeter();
    log(value ? ("Chat model selected: " + value) : "Chat model uses default.");
    return activeChatModel() === value;
  } catch (error) {
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
  if (!state.activeChatId || state.modelSaving || state.reasoningSaving || state.chatNavigationPending || state.initializePromise || hasActiveMessageEdit() || !!currentActiveSend()) {
    return false;
  }
  state.reasoningSaving = true;
  renderReasoningToggle();
  renderSendControls();
  var targetChatId = state.activeChatId;
  try {
    var response = await sendChatPreference("setChatReasoning", { chatId: targetChatId, enabled: !!enabled });
    if (!applyChatStateForChat(response, targetChatId)) return false;
    log(enabled ? "Reasoning enabled." : "Reasoning disabled.");
    return state.activeChatReasoning === !!enabled;
  } catch (error) {
    log(error.detail || error.message, "error");
    return false;
  } finally {
    state.reasoningSaving = false;
    renderModelControls();
    renderSendControls();
  }
}

function bindModelActions() {
  $("modelSelect").addEventListener("change", function () {
    if ($("modelSelect").value) {
      $("modelInput").value = $("modelSelect").value;
      applyModelDefaultsToForm(findModel($("modelSelect").value));
      renderModelControls();
    }
  });
  $("modelInput").addEventListener("input", renderModelControls);
  $("chatReasoningToggle").addEventListener("click", function () {
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
