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

// Exact view source; the common composer owns pending and stale delivery guards.
function officePreferenceService(component) {
  var id = component.target.sessionId;
  function snapshot() { return component.snapshot(); }
  function accept(value) { return component.accept(value); }
  return {
    write: function (change) {
      if (change.name === "model") return send("setChatModel", { chatId: id, model: change.value });
      if (change.name === "mode") return send("setChatMode", { chatId: id, mode: change.value });
      return send("setChatReasoning", { chatId: id, enabled: change.value });
    }, read: function () { return loadChatState(id); },
    apply: function (response, change) {
      if (!component.isCurrent()) return false;
      if (!change || change.name === "mode") return accept(response);
      if ((response.chatId || response.ChatId) !== id) throw new Error("Настройка получена для другого чата.");
      var revision = RNAssistantRunViewState.sessionRevision(response), data = snapshot();
      if (!Number.isSafeInteger(revision) || revision < data.sessionRevision) return false;
      data.sessionRevision = revision;
      data.activeChatModel = response.model || response.Model || "";
      data.activeChatMode = response.mode || response.Mode || "agent";
      data.activeChatReasoning = !!(response.reasoningEnabled || response.ReasoningEnabled);
      return data[change.name === "model" ? "activeChatModel" : "activeChatReasoning"] === change.value;
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
  $("loadModelsButton").addEventListener("click", function () {
    loadModelCatalog(true);
  });
}
