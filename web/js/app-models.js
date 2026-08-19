async function loadModelCatalog(useFormSettings) {
  state.modelCatalog.loading = true;
  state.modelCatalog.error = "";
  renderModelControls();
  try {
    var apiKey = $("apiKeyInput") ? $("apiKeyInput").value : "";
    var settings = useFormSettings ? readSettings() : (state.settings || {});
    var response = await send("getModelCatalog", { settings: settings, apiKey: apiKey || null });
    normalizeModelCatalog(response);
    renderModelControls();
    log("Models loaded: " + state.modelCatalog.models.length);
    return true;
  } catch (error) {
    state.modelCatalog.loading = false;
    state.modelCatalog.error = error.message || "Unknown error";
    renderModelControls();
    var message = error.message || "Неизвестная ошибка";
    log(/^Каталог моделей не загружен:/i.test(message) ? message : ("Каталог моделей не загружен: " + message));
    return false;
  }
}

async function saveChatModelSelection(value) {
  value = String(value || "").trim();
  if (!state.activeChatId || state.reasoningSaving || hasActiveMessageEdit() || !!currentActiveSend()) {
    return false;
  }
  if (value === activeChatModel()) {
    return true;
  }

  state.modelSaving = true;
  if ($("sendButton")) {
    $("sendButton").disabled = true;
  }
  try {
    var response = await send("setChatModel", { chatId: state.activeChatId, model: value });
    applyChatState(response);
    updateEstimatedContextUsage();
    renderContextMeter();
    log(value ? ("Chat model selected: " + value) : "Chat model uses default.");
    return activeChatModel() === value;
  } catch (error) {
    renderModelControls();
    log(error.detail || error.message);
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
  if (!state.activeChatId || state.modelSaving || state.reasoningSaving || hasActiveMessageEdit() || !!currentActiveSend()) {
    return false;
  }
  state.reasoningSaving = true;
  renderReasoningToggle();
  renderSendControls();
  try {
    var response = await send("setChatReasoning", { chatId: state.activeChatId, enabled: !!enabled });
    applyChatState(response);
    log(enabled ? "Reasoning enabled." : "Reasoning disabled.");
    return state.activeChatReasoning === !!enabled;
  } catch (error) {
    log(error.detail || error.message);
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
  $("chatModelSelect").addEventListener("change", function () {
    setChatModelSelectWidth($("chatModelSelect"));
    saveChatModelSelection($("chatModelSelect").value);
  });
  $("chatReasoningToggle").addEventListener("click", function () {
    if (effectiveModelSupportsReasoning(activeChatModel() || settingsModel()) === false) return;
    saveChatReasoningSelection(!state.activeChatReasoning);
  });
  $("loadModelsButton").addEventListener("click", function () {
    loadModelCatalog(true);
  });
}
