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

function modelOptionText(model) {
  if (!model) {
    return "";
  }
  return model.title === model.value ? model.value : model.title + " - " + model.value;
}

function modelOptionTitle(model) {
  var parts = [];
  if (model.description) {
    parts.push(model.description);
  }
  var maxContextTokens = effectiveModelCapabilityValue(model.value, "MaxContextTokens", "maxContextTokens", model.maxContextTokens);
  var maxOutputTokens = effectiveModelCapabilityValue(model.value, "MaxOutputTokens", "maxOutputTokens", model.maxOutputTokens);
  if (maxContextTokens) {
    parts.push("Контекст: " + maxContextTokens);
  }
  if (maxOutputTokens) {
    parts.push("Лимит ответа: " + maxOutputTokens);
  } else if (model.maxTokens) {
    parts.push("Ответ по умолчанию: " + model.maxTokens);
  }
  if (model.temperature !== null && model.temperature !== undefined) {
    parts.push("Temperature: " + model.temperature);
  }
  if (model.topP !== null && model.topP !== undefined) {
    parts.push("Top P: " + model.topP);
  }
  var reasoning = effectiveModelSupportsReasoning(model.value);
  var vision = effectiveModelSupportsImages(model.value);
  var audio = effectiveModelSupportsAudio(model.value);
  if (reasoning !== null) parts.push("Reasoning: " + (reasoning ? "да" : "нет"));
  if (vision !== null) parts.push("Vision: " + (vision ? "да" : "нет"));
  if (audio !== null) parts.push("Audio: " + (audio ? "да" : "нет"));
  return parts.join("\n");
}

function compactModelTokenCount(value) {
  value = Number(value || 0);
  if (!value) return "";
  if (value >= 1000000) return (Math.round(value / 100000) / 10) + "M";
  if (value >= 1000) return (Math.round(value / 100) / 10) + "K";
  return String(value);
}

function setComposerPickerDisabled(picker, disabled) {
  if (!picker) return;
  var summary = picker.querySelector("summary");
  picker.classList.toggle("is-disabled", !!disabled);
  if (disabled) picker.open = false;
  if (summary) {
    summary.setAttribute("aria-disabled", disabled ? "true" : "false");
    summary.tabIndex = disabled ? -1 : 0;
  }
}

function appendModelPickerBadge(parent, className, text) {
  if (!text) return;
  var badge = document.createElement("span");
  badge.className = "composer-model-badge " + className;
  badge.textContent = text;
  parent.appendChild(badge);
}

function createChatModelPickerItem(value, model, isDefault, selected) {
  var button = document.createElement("button");
  button.type = "button";
  button.className = "composer-picker-item composer-model-item" + (selected ? " is-selected" : "");
  button.setAttribute("role", "option");
  button.setAttribute("aria-selected", selected ? "true" : "false");
  button.dataset.value = value;

  var header = document.createElement("span");
  header.className = "composer-model-item-head";
  var title = document.createElement("strong");
  title.textContent = isDefault ? "По умолчанию" : ((model && model.title) || value || "Модель");
  header.appendChild(title);
  if (selected) {
    var check = document.createElement("span");
    check.className = "composer-picker-check";
    check.setAttribute("aria-hidden", "true");
    check.textContent = "✓";
    header.appendChild(check);
  }
  button.appendChild(header);

  var modelValue = model ? model.value : value;
  var subtitle = document.createElement("span");
  subtitle.className = "composer-model-item-id";
  subtitle.textContent = isDefault
    ? ((model && model.title && model.title !== modelValue ? model.title + " · " : "") + (modelValue || settingsModel() || "не задана"))
    : (model && model.title !== modelValue ? modelValue : "");
  if (subtitle.textContent) button.appendChild(subtitle);

  var descriptionText = isDefault
    ? "Использовать модель из общих настроек"
    : String((model && model.description) || "").trim();
  if (descriptionText) {
    var description = document.createElement("span");
    description.className = "composer-model-item-description";
    description.textContent = descriptionText;
    button.appendChild(description);
  }

  if (model) {
    var badges = document.createElement("span");
    badges.className = "composer-model-badges";
    if (effectiveModelSupportsReasoning(model.value) === true) appendModelPickerBadge(badges, "is-reasoning", "Reasoning");
    if (effectiveModelSupportsImages(model.value) === true) appendModelPickerBadge(badges, "is-vision", "Vision");
    if (effectiveModelSupportsAudio(model.value) === true) appendModelPickerBadge(badges, "is-audio", "Audio");
    var contextTokens = effectiveModelCapabilityValue(model.value, "MaxContextTokens", "maxContextTokens", model.maxContextTokens);
    var outputTokens = effectiveModelCapabilityValue(model.value, "MaxOutputTokens", "maxOutputTokens", model.maxOutputTokens);
    appendModelPickerBadge(badges, "", compactModelTokenCount(contextTokens) ? compactModelTokenCount(contextTokens) + " контекст" : "");
    appendModelPickerBadge(badges, "", compactModelTokenCount(outputTokens) ? compactModelTokenCount(outputTokens) + " ответ" : "");
    if (badges.childNodes.length) button.appendChild(badges);
  }
  return button;
}

function renderChatModelPicker() {
  var picker = $("chatModelPicker");
  var menu = $("chatModelMenu");
  var label = $("chatModelButtonLabel");
  var select = $("chatModelSelect");
  if (!picker || !menu || !label) return;

  var selected = activeChatModel();
  var defaultValue = settingsModel();
  var effectiveValue = selected || defaultValue;
  var effectiveModel = findModel(effectiveValue);
  label.textContent = (effectiveModel && effectiveModel.title) || effectiveValue || "Модель";
  picker.title = effectiveValue ? "Модель чата: " + effectiveValue : "Модель чата не выбрана";
  var disabled = state.modelCatalog.loading || state.modelSaving || state.reasoningSaving ||
    !!currentActiveSend() || hasActiveMessageEdit() || state.bridgeUnavailable || !state.activeChatId;
  setComposerPickerDisabled(picker, disabled);

  menu.replaceChildren();
  var defaultModel = findModel(defaultValue);
  var defaultItem = createChatModelPickerItem("", defaultModel || (defaultValue ? { value: defaultValue, title: defaultValue } : null), true, !selected);
  menu.appendChild(defaultItem);

  if (selected && !findModel(selected)) {
    menu.appendChild(createChatModelPickerItem(selected, { value: selected, title: selected, description: "Модель этого чата отсутствует в текущем каталоге" }, false, true));
  }
  (state.modelCatalog.models || []).forEach(function (model) {
    menu.appendChild(createChatModelPickerItem(model.value, model, false,
      String(model.value).toLowerCase() === String(selected).toLowerCase()));
  });

  Array.prototype.slice.call(menu.querySelectorAll(".composer-model-item")).forEach(function (item) {
    item.addEventListener("click", function () {
      if (picker.classList.contains("is-disabled")) return;
      var value = item.dataset.value || "";
      picker.open = false;
      if (select) select.value = value;
      saveChatModelSelection(value);
    });
  });
}

function setChatModelSelectWidth(select) {
  if (!select) {
    return;
  }

  var option = select.options[select.selectedIndex];
  var text = option ? String(option.textContent || "") : "";
  var width = Math.max(48, Math.min(228, text.length * 8 + 6));

  if (typeof window !== "undefined" && window.document && window.document.createElement) {
    var canvas = setChatModelSelectWidth.canvas || (setChatModelSelectWidth.canvas = window.document.createElement("canvas"));
    var context = canvas.getContext && canvas.getContext("2d");
    if (context && window.getComputedStyle) {
      var styles = window.getComputedStyle(select);
      context.font = styles.font || [styles.fontStyle, styles.fontVariant, styles.fontWeight, styles.fontSize, styles.fontFamily].join(" ");
      width = Math.max(48, Math.min(228, Math.ceil(context.measureText(text).width) + 6));
    }
  }

  select.style.setProperty("--chat-model-select-width", width + "px");
}

function populateModelSelect(select, selectedValue) {
  if (!select) {
    return;
  }

  var models = state.modelCatalog.models || [];
  var selected = String(selectedValue || "").trim();
  select.innerHTML = "";

  if (selected && !findModel(selected)) {
    var fallback = document.createElement("option");
    fallback.value = selected;
    fallback.textContent = selected + " (резерв)";
    select.appendChild(fallback);
  }

  models.forEach(function (model) {
    var option = document.createElement("option");
    option.value = model.value;
    option.textContent = modelOptionText(model);
    option.title = modelOptionTitle(model);
    select.appendChild(option);
  });

  if (!select.options.length) {
    var empty = document.createElement("option");
    empty.value = "";
    empty.textContent = state.modelCatalog.loading ? "Загрузка моделей..." : "Список моделей пуст";
    select.appendChild(empty);
  }

  select.value = selected || state.modelCatalog.defaultModel || "";
  select.disabled = state.modelCatalog.loading || state.modelSaving || models.length === 0;
}

function populateChatModelSelect(select) {
  if (!select) {
    return;
  }

  var models = state.modelCatalog.models || [];
  var selected = activeChatModel();
  var defaultModel = settingsModel();
  select.innerHTML = "";

  var defaultOption = document.createElement("option");
  defaultOption.value = "";
  defaultOption.textContent = defaultModel || "Не выбрана";
  select.appendChild(defaultOption);

  if (selected && !findModel(selected)) {
    var fallback = document.createElement("option");
    fallback.value = selected;
    fallback.textContent = selected + " (чат)";
    select.appendChild(fallback);
  }

  models.forEach(function (model) {
    var option = document.createElement("option");
    option.value = model.value;
    option.textContent = modelOptionText(model);
    option.title = modelOptionTitle(model);
    select.appendChild(option);
  });

  select.value = selected;
  select.title = selected || defaultModel ? ("Модель чата: " + (selected || defaultModel)) : "Модель чата не выбрана";
  select.disabled = state.modelCatalog.loading || state.modelSaving || !!currentActiveSend() || !state.activeChatId;
  setChatModelSelectWidth(select);
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
  populateModelSelect($("modelSelect"), formModel());
  populateChatModelSelect($("chatModelSelect"));
  renderModelInfo(formModel());
  renderModelStatus();
  renderModelCapabilityList();
  renderActiveModelCapability();
  renderReasoningToggle();
  renderChatModelPicker();
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
