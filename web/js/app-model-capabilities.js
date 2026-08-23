function renderActiveModelCapability() {
  var indicator = $("modelCapabilityIndicator");
  if (!indicator) return;
  var value = activeChatModel() || settingsModel();
  var vision = effectiveModelSupportsImages(value);
  var reasoning = effectiveModelSupportsReasoning(value);
  var audio = effectiveModelSupportsAudio(value);
  var labels = [];
  if (reasoning === true) labels.push("Reasoning");
  if (vision === true) labels.push("Vision");
  if (audio === true) labels.push("Audio");
  var known = reasoning !== null || vision !== null || audio !== null;
  indicator.textContent = labels.length ? labels.join(" · ") : (known ? "Только текст" : "Возможности ?");
  indicator.className = "model-capability-indicator " +
    (labels.length ? "is-enabled" : (known ? "is-disabled" : "is-unknown"));
  indicator.title = "Модель: " + (value || "не выбрана") +
    ". Reasoning: " + (reasoning === null ? "?" : (reasoning ? "да" : "нет")) +
    ". Vision: " + (vision === null ? "?" : (vision ? "да" : "нет")) +
    ". Audio: " + (audio === null ? "?" : (audio ? "да" : "нет")) + ".";
}

function modelOverrideState(overrides, value) {
  var override = modelSupportOverride(overrides, value);
  return override === null ? "auto" : (override ? "true" : "false");
}

function appendModelCapabilitySelect(row, label, mode, onChange) {
  var holder = document.createElement("label");
  holder.className = "model-capability-flag";
  var select = document.createElement("select");
  select.className = "model-capability-select";
  select.setAttribute("aria-label", label);
  [["auto", "Авто"], ["true", "Да"], ["false", "Нет"]].forEach(function (item) {
    var option = document.createElement("option");
    option.value = item[0];
    option.textContent = item[1];
    select.appendChild(option);
  });
  select.value = mode;
  select.addEventListener("change", function () { onChange(select.value); });
  holder.appendChild(select);
  row.appendChild(holder);
}

function appendModelCapabilityNumber(row, label, value, placeholder, onChange) {
  var holder = document.createElement("label");
  holder.className = "model-capability-flag";
  var input = document.createElement("input");
  input.className = "model-capability-number";
  input.type = "number";
  input.min = "1";
  input.value = Number(value || 0) > 0 ? String(value) : "";
  input.placeholder = placeholder || "Авто";
  input.setAttribute("aria-label", label);
  input.title = label;
  input.addEventListener("change", function () {
    var parsed = Number(input.value || 0);
    onChange(parsed > 0 && isFinite(parsed) ? Math.floor(parsed) : null);
  });
  holder.appendChild(input);
  row.appendChild(holder);
}

function appendReasoningModeSelect(row, value, onChange) {
  var holder = document.createElement("label");
  holder.className = "model-capability-flag";
  var select = document.createElement("select");
  select.className = "model-capability-mode-select";
  select.setAttribute("aria-label", "Режим reasoning API");
  [["", "Общий"], ["auto", "Auto"], ["reasoning_effort", "effort"], ["enable_thinking", "thinking"],
    ["chat_template_kwargs", "kwargs"], ["reasoning_enabled", "enabled"], ["custom_json", "custom"]].forEach(function (item) {
    var option = document.createElement("option");
    option.value = item[0];
    option.textContent = item[1];
    select.appendChild(option);
  });
  select.value = value || "";
  select.addEventListener("change", function () { onChange(select.value || null); });
  holder.appendChild(select);
  row.appendChild(holder);
}

function setStoredModelCapabilityField(value, field, nextValue) {
  value = String(value || "").trim();
  if (!value) return;
  var settings = state.settings || (state.settings = {});
  var capabilities = settings.ModelCapabilities || settings.modelCapabilities || {};
  settings.ModelCapabilities = capabilities;
  var entry = storedModelCapabilityEntry(value);
  var capability = entry ? entry.value : {};
  if (!entry) capabilities[value] = capability;
  capability[field] = nextValue;
  settingsDirty = true;
  updateSettingsSaveButton();
  renderModelControls();
}

function setModelCapabilityOverride(kind, value, mode) {
  value = String(value || "").trim();
  if (!value) return;
  var settings = state.settings || (state.settings = {});
  var overrides = kind === "audio" ? modelAudioSupportOverrides() : modelImageSupportOverrides();
  if (kind === "audio") settings.ModelAudioSupportOverrides = overrides;
  else settings.ModelImageSupportOverrides = overrides;
  Object.keys(overrides).forEach(function (key) {
    if (key.toLowerCase() === value.toLowerCase()) delete overrides[key];
  });
  if (mode !== "auto") {
    overrides[value] = mode === "true";
  }
  settingsDirty = true;
  updateSettingsSaveButton();
  renderModelControls();
}

function renderModelCapabilityList() {
  var list = $("modelCapabilityList");
  if (!list) return;

  var models = allKnownModelValues().map(function (value) {
    return findModel(value) || { value: value, title: value, supportsReasoning: null, supportsImages: null, supportsAudio: null, inputModalities: [] };
  });
  list.innerHTML = "";

  if (!models.length) {
    var empty = document.createElement("div");
    empty.className = "model-capability-empty";
    empty.textContent = state.modelCatalog.loading ? "Загрузка моделей..." : "Загрузите каталог моделей.";
    list.appendChild(empty);
    return;
  }

  var imageOverrides = modelImageSupportOverrides();
  var audioOverrides = modelAudioSupportOverrides();
  var header = document.createElement("div");
  header.className = "model-capability-row model-capability-header";
  ["Модель", "Контекст", "Ответ", "Reasoning", "Reasoning API", "Vision", "Audio", "Изобр."].forEach(function (label) {
    var cell = document.createElement("span");
    cell.textContent = label;
    header.appendChild(cell);
  });
  list.appendChild(header);
  models.forEach(function (model) {
    var value = model.value;
    var imageMode = modelOverrideState(imageOverrides, value);
    var audioMode = modelOverrideState(audioOverrides, value);
    var reasoningValue = effectiveModelSupportsReasoning(value);
    var reasoningMode = reasoningValue === null ? "auto" : (reasoningValue ? "true" : "false");
    var maxContextTokens = effectiveModelCapabilityValue(value, "MaxContextTokens", "maxContextTokens", model.maxContextTokens);
    var maxOutputTokens = effectiveModelCapabilityValue(value, "MaxOutputTokens", "maxOutputTokens", model.maxOutputTokens);
    var maxImages = effectiveModelCapabilityValue(value, "MaxImagesPerPrompt", "maxImagesPerPrompt", model.maxImagesPerPrompt);
    var requestMode = effectiveModelCapabilityValue(value, "ReasoningRequestMode", "reasoningRequestMode", model.reasoningRequestMode);
    var hasOverride = imageMode !== "auto" || audioMode !== "auto";

    var row = document.createElement("div");
    row.className = "model-capability-row";

    var text = document.createElement("div");
    text.className = "model-capability-text";
    var title = document.createElement("div");
    title.className = "model-capability-title";
    title.textContent = model.title || value;
    text.appendChild(title);
    if ((model.title && model.title !== value) || hasOverride) {
      var id = document.createElement("div");
      id.className = "model-capability-id";
      id.textContent = value + (hasOverride ? " · возможности вручную" : "");
      text.appendChild(id);
    }
    row.appendChild(text);

    appendModelCapabilityNumber(row, "Контекст модели: " + value, maxContextTokens, "32768", function (next) {
      setStoredModelCapabilityField(value, "MaxContextTokens", next);
    });
    appendModelCapabilityNumber(row, "Лимит ответа модели: " + value, maxOutputTokens, "Нет", function (next) {
      setStoredModelCapabilityField(value, "MaxOutputTokens", next);
    });
    appendModelCapabilitySelect(row, "Reasoning: " + value, reasoningMode, function (mode) {
      setStoredModelCapabilityField(value, "SupportsReasoning", mode === "auto" ? null : mode === "true");
    });
    appendReasoningModeSelect(row, requestMode, function (mode) {
      setStoredModelCapabilityField(value, "ReasoningRequestMode", mode);
    });
    appendModelCapabilitySelect(row, "Vision: " + value, imageMode, function (mode) {
      setModelCapabilityOverride("image", value, mode);
    });
    appendModelCapabilitySelect(row, "Audio: " + value, audioMode, function (mode) {
      setModelCapabilityOverride("audio", value, mode);
    });
    appendModelCapabilityNumber(row, "Изображений в запросе: " + value, maxImages, "3", function (next) {
      setStoredModelCapabilityField(value, "MaxImagesPerPrompt", next);
    });
    list.appendChild(row);
  });
}
