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
  var detail = "Модель: " + (value || "не выбрана") +
    ". Reasoning: " + (reasoning === null ? "?" : (reasoning ? "да" : "нет")) +
    ". Vision: " + (vision === null ? "?" : (vision ? "да" : "нет")) +
    ". Audio: " + (audio === null ? "?" : (audio ? "да" : "нет")) + ".";
  indicator.title = detail;
  indicator.setAttribute("aria-label", detail);
}

function modelOverrideState(overrides, value) {
  var override = modelSupportOverride(overrides, value);
  return override === null ? "auto" : (override ? "true" : "false");
}

function appendModelCapabilitySelect(row, label, mode, onChange) {
  var holder = document.createElement("label");
  holder.className = "model-capability-flag";
  var caption = document.createElement("span");
  caption.textContent = label;
  holder.appendChild(caption);
  var select = document.createElement("select");
  select.className = "model-capability-select";
  select.setAttribute("aria-label", label);
  [["auto", "Из каталога"], ["true", "Поддерживает"], ["false", "Не поддерживает"]].forEach(function (item) {
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
  var caption = document.createElement("span");
  caption.textContent = label;
  holder.appendChild(caption);
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
  settings.AttachmentModelPriority = attachmentModelPriorityForSettings();
  settingsDirty = true;
  updateSettingsSaveButton();
  renderModelControls();
}

function renderModelCapabilityList() {
  var list = $("modelCapabilityList");
  if (!list) return;

  var value = formModel();
  list.replaceChildren();
  if (!value) {
    var empty = document.createElement("div");
    empty.className = "model-capability-empty";
    empty.textContent = "Выберите модель.";
    list.appendChild(empty);
    return;
  }
  var model = findModel(value) || { value: value, supportsReasoning: null, supportsImages: null, supportsAudio: null, inputModalities: [] };
  var imageOverrides = modelImageSupportOverrides();
  var audioOverrides = modelAudioSupportOverrides();
  var reasoningValue = effectiveModelSupportsReasoning(value);
  var reasoningMode = reasoningValue === null ? "auto" : (reasoningValue ? "true" : "false");
  var row = document.createElement("div");
  row.className = "model-capability-fields";
  appendModelCapabilitySelect(row, "Изображения", modelOverrideState(imageOverrides, value), function (mode) {
    setModelCapabilityOverride("image", value, mode);
  });
  appendModelCapabilitySelect(row, "Аудио", modelOverrideState(audioOverrides, value), function (mode) {
    setModelCapabilityOverride("audio", value, mode);
  });
  appendModelCapabilitySelect(row, "Рассуждения", reasoningMode, function (mode) {
    setStoredModelCapabilityField(value, "SupportsReasoning", mode === "auto" ? null : mode === "true");
  });
  appendModelCapabilityNumber(row, "Контекст, токенов", effectiveModelCapabilityValue(value, "MaxContextTokens", "maxContextTokens", model.maxContextTokens), "Из каталога", function (next) {
    setStoredModelCapabilityField(value, "MaxContextTokens", next);
  });
  appendModelCapabilityNumber(row, "Ответ, токенов", effectiveModelCapabilityValue(value, "MaxOutputTokens", "maxOutputTokens", model.maxOutputTokens), "Из каталога", function (next) {
    setStoredModelCapabilityField(value, "MaxOutputTokens", next);
  });
  appendModelCapabilityNumber(row, "Изображений в запросе", effectiveModelCapabilityValue(value, "MaxImagesPerPrompt", "maxImagesPerPrompt", model.maxImagesPerPrompt), "Из каталога", function (next) {
    setStoredModelCapabilityField(value, "MaxImagesPerPrompt", next);
  });
  list.appendChild(row);
}

function setAttachmentModelPriority(values) {
  var settings = state.settings || (state.settings = {});
  settings.AttachmentModelPriority = values.slice();
  settingsDirty = true;
  updateSettingsSaveButton();
  renderAttachmentModelPriority();
}

function renderAttachmentModelPriority() {
  var list = $("attachmentModelPriorityList");
  if (!list) return;
  var values = attachmentModelPriorityForSettings();
  list.innerHTML = "";
  if (!values.length) {
    var empty = document.createElement("div");
    empty.className = "model-capability-empty";
    empty.textContent = "Нет моделей с подтверждённой поддержкой Vision или Audio.";
    list.appendChild(empty);
    return;
  }
  values.forEach(function (value, index) {
    var row = document.createElement("div");
    row.className = "attachment-model-priority-row";
    var rank = document.createElement("span");
    rank.className = "attachment-model-priority-rank";
    rank.textContent = String(index + 1);
    row.appendChild(rank);

    var copy = document.createElement("div");
    copy.className = "model-capability-text";
    var model = findModel(value);
    var title = document.createElement("div");
    title.className = "model-capability-title";
    title.textContent = model ? model.title : value;
    copy.appendChild(title);
    if (model && model.title !== value) {
      var id = document.createElement("div");
      id.className = "model-capability-id";
      id.textContent = value;
      copy.appendChild(id);
    }
    row.appendChild(copy);

    var badges = document.createElement("div");
    badges.className = "attachment-model-priority-badges";
    if (effectiveModelSupportsImages(value) === true) {
      var vision = document.createElement("span");
      vision.textContent = "Vision";
      badges.appendChild(vision);
    }
    if (effectiveModelSupportsAudio(value) === true) {
      var audio = document.createElement("span");
      audio.textContent = "Audio";
      badges.appendChild(audio);
    }
    row.appendChild(badges);

    var actions = document.createElement("div");
    actions.className = "attachment-model-priority-actions";
    [["↑", -1, "Поднять приоритет"], ["↓", 1, "Понизить приоритет"]].forEach(function (action) {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "secondary";
      button.textContent = action[0];
      button.title = action[2];
      button.disabled = index + action[1] < 0 || index + action[1] >= values.length;
      button.addEventListener("click", function () {
        var target = index + action[1];
        var reordered = values.slice();
        var moved = reordered[index];
        reordered[index] = reordered[target];
        reordered[target] = moved;
        setAttachmentModelPriority(reordered);
      });
      actions.appendChild(button);
    });
    row.appendChild(actions);
    list.appendChild(row);
  });
}
