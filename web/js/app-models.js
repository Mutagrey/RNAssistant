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
  } catch (error) {
    state.modelCatalog.loading = false;
    state.modelCatalog.error = error.message || "Unknown error";
    renderModelControls();
    log(error.detail || error.message);
  }
}

async function saveChatModelSelection(value) {
  value = String(value || "").trim();
  if (value === activeChatModel()) {
    return;
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
  } catch (error) {
    renderModelControls();
    log(error.detail || error.message);
  } finally {
    state.modelSaving = false;
    if ($("sendButton")) {
      $("sendButton").disabled = false;
    }
    renderModelControls();
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
  $("loadModelsButton").addEventListener("click", function () {
    loadModelCatalog(true);
  });
}
