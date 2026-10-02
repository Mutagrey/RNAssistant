function hasModelSettingValue(value) {
  return value !== null && value !== undefined && value !== "";
}

function setInputIfPresent(id, value) {
  if (hasModelSettingValue(value) && $(id)) {
    $(id).value = value;
  }
}

function applyModelDefaultsToForm(model) {
  if (!model) {
    return;
  }

  setInputIfPresent("maxTokensInput", model.maxTokens);
  setInputIfPresent("temperatureInput", model.temperature);
  setInputIfPresent("topPInput", model.topP);
}

function renderModelStatus() {
  var status = $("modelStatus");
  if (!status) {
    return;
  }

  if (state.modelCatalog.loading) {
    status.textContent = "Загрузка каталога...";
    status.title = "";
    return;
  }
  if (state.modelCatalog.error) {
    status.textContent = "Ошибка каталога: " + state.modelCatalog.error;
    status.title = "";
    return;
  }
  if (state.modelCatalog.loaded) {
    status.textContent = "Моделей: " + (state.modelCatalog.models || []).length;
    status.title = "Моделей загружено: " + (state.modelCatalog.models || []).length +
      (state.modelCatalog.defaultModel ? ". По умолчанию: " + state.modelCatalog.defaultModel : "") +
      (state.modelCatalog.configUrl ? ". Источник: " + state.modelCatalog.configUrl : "");
    return;
  }
  status.textContent = "Каталог не загружен.";
  status.title = "";
}

function renderModelControls() {
  if (typeof isPanelActive === "function" && !isPanelActive("chat") && !isPanelActive("settings")) return;
  if (typeof isPanelActive !== "function" || isPanelActive("settings")) {
    populateModelSelect($("modelSelect"), formModel());
    renderModelStatus();
    renderModelCapabilityList();
    renderAttachmentModelPriority();
    renderActiveModelCapability();
    if ($("loadModelsButton")) $("loadModelsButton").disabled = state.modelCatalog.loading;
    if (typeof renderTokenEstimateCalibrationStatus === "function") {
      renderTokenEstimateCalibrationStatus(state.settings);
    }
  }
  if (typeof isPanelActive !== "function" || isPanelActive("chat")) {
    renderReasoningToggle();
    renderChatModelPicker(true);
  }
}

function renderReasoningToggle() {
  var button = $("chatReasoningToggle");
  if (!button) return;
  var model = activeChatModel() || settingsModel();
  var support = effectiveModelSupportsReasoning(model);
  var active = !!state.activeChatReasoning && support !== false;
  var disabled = !!currentActiveSend() || state.modelSaving || state.reasoningSaving ||
    !!state.chatNavigationPending || !!state.initializePromise ||
    hasActiveMessageEdit() || state.bridgeUnavailable || !state.activeChatId || support === false;

  button.classList.toggle("active", active);
  button.classList.toggle("is-unknown", support === null);
  button.disabled = disabled;
  button.setAttribute("aria-pressed", active ? "true" : "false");
  if (support === false) {
    button.title = "Выбранная модель не поддерживает reasoning";
  } else if (active) {
    button.title = "Reasoning включен";
  } else {
    button.title = support === null ? "Включить reasoning · поддержка модели не определена" : "Включить reasoning";
  }
  button.setAttribute("aria-label", active ? "Выключить reasoning" : "Включить reasoning");
}
