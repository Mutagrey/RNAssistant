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

// The service captures an exact address; the common composer owns pending/delivery.
function officePreferenceService(scope) {
  var chatId = scope.target.sessionId;
  return {
    write: function (change) {
      if (change.name === "model") return sendChatPreference("setChatModel", { chatId: chatId, model: change.value });
      if (change.name === "mode") return sendChatPreference("setChatMode", { chatId: chatId, mode: change.value });
      return sendChatPreference("setChatReasoning", { chatId: chatId, enabled: change.value });
    },
    read: function () { return loadChatState(chatId); },
    apply: function (response, change) {
      var full = !change || change.name === "mode";
      var applied = full ? applyChatStateForChat(response, chatId) :
        applyChatPreferenceStateForChat(response, chatId);
      return applied && (!change || officeChatPreferences()[change.name === "model" ? "selected" : change.name] === change.value);
    }
  };
}
function saveChatModelSelection(value) { return chatComposer().setPreference("model", String(value || "").trim()); }
function saveChatReasoningSelection(enabled) {
  var selection = chatModelSelection();
  if (effectiveModelSupportsReasoning(selection.runningModel || selection.selected || settingsModel()) === false)
    return Promise.resolve(false);
  return chatComposer().setPreference("reasoning", !!enabled);
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
    if (!this.disabled) saveChatReasoningSelection(!chatComposer().preferences().reasoning);
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
