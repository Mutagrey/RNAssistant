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
  if (model.maxContextTokens) {
    parts.push("Контекст: " + model.maxContextTokens);
  }
  if (model.maxTokens) {
    parts.push("Ответ: " + model.maxTokens);
  }
  if (model.temperature !== null && model.temperature !== undefined) {
    parts.push("Temperature: " + model.temperature);
  }
  if (model.topP !== null && model.topP !== undefined) {
    parts.push("Top P: " + model.topP);
  }
  return parts.join("\n");
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
  select.disabled = state.modelCatalog.loading || state.modelSaving || !!state.activeSend || !state.activeChatId;
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
  appendModelMetric(metrics, "Контекст", model.maxContextTokens);
  appendModelMetric(metrics, "Ответ", model.maxTokens);
  appendModelMetric(metrics, "Temp", model.temperature);
  appendModelMetric(metrics, "Top P", model.topP);
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
}

function renderActiveModelCapability() {
  var indicator = $("modelCapabilityIndicator");
  if (!indicator) return;
  var value = activeChatModel() || settingsModel();
  var support = effectiveModelSupportsImages(value);
  indicator.textContent = support === true ? "Изображения" : (support === false ? "Только текст" : "Модальность ?");
  indicator.className = "model-capability-indicator " +
    (support === true ? "is-enabled" : (support === false ? "is-disabled" : "is-unknown"));
  indicator.title = "Модель: " + (value || "не выбрана") + ". " +
    (support === true ? "Изображения поддерживаются." : (support === false ? "Изображения отключены." : "Поддержка изображений не определена."));
}

function setModelImageSupportOverride(value, enabled) {
  value = String(value || "").trim();
  if (!value) return;
  var settings = state.settings || (state.settings = {});
  var overrides = modelImageSupportOverrides();
  settings.ModelImageSupportOverrides = overrides;
  if (enabled === null) {
    delete overrides[value];
  } else {
    overrides[value] = !!enabled;
  }
  settingsDirty = true;
  updateSettingsSaveButton();
  renderModelControls();
}

function renderModelCapabilityList() {
  var list = $("modelCapabilityList");
  if (!list) return;

  var models = (state.modelCatalog.models || []).slice();
  var manualValue = formModel();
  if (manualValue && !findModel(manualValue)) {
    models.unshift({ value: manualValue, title: manualValue, supportsImages: null, inputModalities: [] });
  }
  list.innerHTML = "";

  if (!models.length) {
    var empty = document.createElement("div");
    empty.className = "model-capability-empty";
    empty.textContent = state.modelCatalog.loading ? "Загрузка моделей..." : "Загрузите каталог моделей.";
    list.appendChild(empty);
    return;
  }

  var overrides = modelImageSupportOverrides();
  models.forEach(function (model) {
    var value = model.value;
    var hasOverride = Object.prototype.hasOwnProperty.call(overrides, value) && overrides[value] !== null;
    var catalogSupport = catalogModelSupportsImages(model);
    var effective = hasOverride ? !!overrides[value] : catalogSupport;

    var row = document.createElement("div");
    row.className = "model-capability-row";

    var toggle = document.createElement("input");
    toggle.type = "checkbox";
    toggle.checked = effective === true;
    toggle.indeterminate = effective === null;
    toggle.setAttribute("aria-label", "Поддержка изображений: " + value);
    toggle.addEventListener("change", function () {
      setModelImageSupportOverride(value, toggle.checked);
    });
    row.appendChild(toggle);

    var text = document.createElement("div");
    text.className = "model-capability-text";
    var title = document.createElement("div");
    title.className = "model-capability-title";
    title.textContent = model.title || value;
    text.appendChild(title);
    if (model.title && model.title !== value) {
      var id = document.createElement("div");
      id.className = "model-capability-id";
      id.textContent = value;
      text.appendChild(id);
    }
    row.appendChild(text);

    var source = document.createElement("span");
    source.className = "model-capability-source " + (hasOverride ? "is-manual" : "");
    source.textContent = hasOverride
      ? "Вручную"
      : (catalogSupport === true ? "Каталог: да" : (catalogSupport === false ? "Каталог: нет" : "Не определено"));
    row.appendChild(source);

    var reset = document.createElement("button");
    reset.type = "button";
    reset.className = "model-capability-reset";
    reset.textContent = "Авто";
    reset.disabled = !hasOverride;
    reset.title = hasOverride ? "Использовать статус из каталога" : "Уже используется статус из каталога";
    reset.addEventListener("click", function () {
      setModelImageSupportOverride(value, null);
    });
    row.appendChild(reset);
    list.appendChild(row);
  });
}
