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

function appendModelMetric(box, label, value) {
  if (value === null || value === undefined || value === "") {
    return;
  }
  var item = document.createElement("span");
  item.textContent = label + ": " + (typeof value === "number" ? formatNumber(value) : value);
  box.appendChild(item);
}

function renderModelInfo(selectedValue) {
  var box = $("modelInfo");
  if (!box) {
    return;
  }

  var selected = String(selectedValue || "").trim();
  var model = findModel(selected);
  box.innerHTML = "";

  var title = document.createElement("div");
  title.className = "model-info-title";
  var titleText = document.createElement("span");
  titleText.textContent = model ? model.title : "Модель, заданная вручную";
  title.appendChild(titleText);

  if (model && state.modelCatalog.defaultModel &&
      String(model.value).toLowerCase() === String(state.modelCatalog.defaultModel).toLowerCase()) {
    var badge = document.createElement("span");
    badge.className = "model-default-badge";
    badge.textContent = "По умолчанию";
    title.appendChild(badge);
  }
  box.appendChild(title);

  var value = document.createElement("div");
  value.className = "model-info-value";
  value.textContent = model ? model.value : (selected || "Модель не выбрана");
  box.appendChild(value);

  var description = document.createElement("div");
  description.className = "model-info-description";
  description.textContent = model
    ? (model.description || "Описание отсутствует.")
    : "Введенная модель по умолчанию будет использоваться для новых чатов и чатов без собственной модели.";
  box.appendChild(description);

  if (!model) {
    return;
  }

  var metrics = document.createElement("div");
  metrics.className = "model-info-metrics";
  appendModelMetric(metrics, "Контекст", effectiveModelCapabilityValue(model.value, "MaxContextTokens", "maxContextTokens", model.maxContextTokens));
  appendModelMetric(metrics, "Лимит ответа", effectiveModelCapabilityValue(model.value, "MaxOutputTokens", "maxOutputTokens", model.maxOutputTokens));
  appendModelMetric(metrics, "Ответ по умолчанию", model.maxTokens);
  appendModelMetric(metrics, "Temp", model.temperature);
  appendModelMetric(metrics, "Top P", model.topP);
  appendModelMetric(metrics, "Top K", model.topK);
  appendModelMetric(metrics, "Presence penalty", model.presencePenalty);
  appendModelMetric(metrics, "Frequency penalty", model.frequencyPenalty);
  var reasoning = effectiveModelSupportsReasoning(model.value);
  var vision = effectiveModelSupportsImages(model.value);
  var audio = effectiveModelSupportsAudio(model.value);
  appendModelMetric(metrics, "Reasoning", reasoning === null ? "?" : (reasoning ? "да" : "нет"));
  appendModelMetric(metrics, "Vision", vision === null ? "?" : (vision ? "да" : "нет"));
  appendModelMetric(metrics, "Audio", audio === null ? "?" : (audio ? "да" : "нет"));
  box.appendChild(metrics);

  if (model.systemPrompt) {
    var prompt = document.createElement("div");
    prompt.className = "model-info-prompt";
    prompt.textContent = "Системный промпт: " + model.systemPrompt;
    box.appendChild(prompt);
  }
}

function renderModelStatus() {
  var status = $("modelStatus");
  if (!status) {
    return;
  }

  if (state.modelCatalog.loading) {
    status.textContent = "Загрузка моделей...";
    return;
  }
  if (state.modelCatalog.error) {
    status.textContent = "Ошибка списка моделей: " + state.modelCatalog.error;
    return;
  }
  if (state.modelCatalog.loaded) {
    status.textContent = "Моделей загружено: " + (state.modelCatalog.models || []).length +
      (state.modelCatalog.defaultModel ? ". По умолчанию: " + state.modelCatalog.defaultModel : "") +
      (state.modelCatalog.configUrl ? ". Источник: " + state.modelCatalog.configUrl : "");
    return;
  }
  status.textContent = "Список моделей не загружен.";
}

function renderModelControls() {
  if (typeof isPanelActive === "function" && !isPanelActive("chat") && !isPanelActive("settings")) return;
  populateModelSelect($("modelSelect"), formModel());
  populateChatModelSelect($("chatModelSelect"));
  renderModelInfo(formModel());
  renderModelStatus();
  renderModelCapabilityList();
  renderAttachmentModelPriority();
  renderActiveModelCapability();
  renderReasoningToggle();
  renderChatModelPicker();
  if (typeof renderTokenEstimateCalibrationStatus === "function") {
    renderTokenEstimateCalibrationStatus(state.settings);
  }
}

function renderReasoningToggle() {
  var button = $("chatReasoningToggle");
  if (!button) return;
  var model = activeChatModel() || settingsModel();
  var support = effectiveModelSupportsReasoning(model);
  var active = !!state.activeChatReasoning && support !== false;
  var disabled = !!currentActiveSend() || state.modelSaving || state.reasoningSaving ||
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
