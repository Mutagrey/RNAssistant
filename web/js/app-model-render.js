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
  var catalog = modelSettingsFormContext().modelCatalog;
  var status = $("modelStatus");
  if (!status) {
    return;
  }

  status.hidden = false;

  if (catalog.loading) {
    status.textContent = "Загрузка каталога...";
    status.title = "";
    return;
  }
  if (catalog.error) {
    status.textContent = "Ошибка каталога: " + catalog.error;
    status.title = "";
    return;
  }
  if (catalog.loaded) {
    status.textContent = "Моделей: " + (catalog.models || []).length;
    status.title = "Моделей загружено: " + (catalog.models || []).length +
      (catalog.defaultModel ? ". По умолчанию: " + catalog.defaultModel : "") +
      (catalog.configUrl ? ". Источник: " + catalog.configUrl : "");
    return;
  }
  status.textContent = "";
  status.title = "";
  status.hidden = true;
}

function renderModelCapabilityBadges() {
  var root = $("modelCapabilityBadges");
  if (!root) return;
  var value = formModel();
  root.replaceChildren();
  root.hidden = !value;
  if (!value) return;

  [["Текст", true], ["Изображения", effectiveModelSupportsImages(value, modelSettingsFormContext())],
    ["Рассуждения", effectiveModelSupportsReasoning(value, modelSettingsFormContext())],
    ["Аудио", effectiveModelSupportsAudio(value, modelSettingsFormContext())]].forEach(function (entry, index) {
    var badge = document.createElement("span");
    var support = entry[1];
    badge.textContent = support === true ? entry[0] : entry[0] + (support === null ? " ?" : " нет");
    badge.className = support === true ? "" : "is-muted";
    if (index === 0) badge.textContent = "Текст";
    root.appendChild(badge);
  });
}

function renderModelControls() {
  var settingsVisible = typeof RNAssistantSettingsView !== "undefined" ? RNAssistantSettingsView.active :
    typeof isPanelActive !== "function" || isPanelActive("settings");
  var chatVisible = typeof isPanelActive !== "function" || isPanelActive("chat");
  if (!settingsVisible && !chatVisible) return;
  if (settingsVisible) {
    var context = modelSettingsFormContext();
    populateModelOptions($("modelOptions"), context);
    renderModelStatus();
    renderModelCapabilityList();
    renderModelCapabilityBadges();
    renderAttachmentModelPriority();
    if ($("loadModelsButton")) $("loadModelsButton").disabled = context.modelCatalog.loading;
    if (typeof renderTokenEstimateCalibrationStatus === "function") {
      renderTokenEstimateCalibrationStatus(context.settings);
    }
    if (typeof renderContextWindowSummary === "function") renderContextWindowSummary();
  }
  if (chatVisible) {
    renderActiveModelCapability();
    renderReasoningToggle();
    renderChatModelPicker();
  }
}

function renderReasoningToggle() {
  var button = $("chatReasoningToggle");
  if (!button) return;
  var selection = chatModelSelection();
  var support = effectiveModelSupportsReasoning(selection.runningModel || selection.selected || settingsModel());
  var active = !!selection.reasoning && support !== false;
  var disabled = selection.disabled || support === false;

  renderReasoningControl(button, active, disabled, support);
}

function renderReasoningControl(button, enabled, disabled, support) {
  var active = !!enabled && support !== false;
  button.classList.toggle("active", active);
  button.classList.toggle("is-unknown", support === null);
  button.disabled = !!disabled || support === false;
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
