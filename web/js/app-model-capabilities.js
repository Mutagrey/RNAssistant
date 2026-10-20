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
  return select;
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
  input.step = "1";
  input.value = Number(value || 0) > 0 ? String(value) : "";
  input.placeholder = placeholder || "Авто";
  input.setAttribute("aria-label", label);
  input.title = label;
  input.addEventListener("change", function () {
    var parsed = Number(input.value || 0);
    var next = parsed > 0 && isFinite(parsed) ? Math.floor(parsed) : null;
    input.value = next === null ? "" : String(next);
    onChange(next);
  });
  holder.appendChild(input);
  row.appendChild(holder);
  return input;
}

function appendModelReasoningModeSelect(row, value, catalogMode, onChange) {
  var holder = document.createElement("label");
  holder.className = "model-capability-flag";
  var caption = document.createElement("span");
  caption.textContent = "Передача reasoning";
  holder.appendChild(caption);
  var select = document.createElement("select");
  select.className = "model-capability-select";
  select.setAttribute("aria-label", "Передача reasoning");
  [["", catalogMode ? "Из каталога: " + catalogMode : "Общий режим"], ["auto", "auto"], ["reasoning_effort", "reasoning_effort"],
    ["enable_thinking", "enable_thinking"], ["chat_template_kwargs", "chat_template_kwargs"],
    ["reasoning_enabled", "reasoning_enabled"], ["custom_json", "custom_json"]].forEach(function (item) {
    var option = document.createElement("option");
    option.value = item[0];
    option.textContent = item[1];
    select.appendChild(option);
  });
  select.value = value || "";
  select.addEventListener("change", function () { onChange(select.value || null); });
  holder.appendChild(select);
  row.appendChild(holder);
  return select;
}

function modelCapabilityEditorValue(value, field, catalogValue) {
  var camel = field.charAt(0).toLowerCase() + field.slice(1);
  var snake = field.replace(/([a-z])([A-Z])/g, "$1_$2").toLowerCase();
  var stored = storedModelCapabilityValue(value, field, camel, snake);
  return stored === null || stored === undefined || stored === "" || String(stored) === String(catalogValue)
    ? null : stored;
}

function modelCapabilityNumberText(value) {
  var number = Number(value);
  return number > 0 && isFinite(number) ? Math.floor(number).toLocaleString("ru-RU") : "?";
}

function modelCapabilityPlaceholder(value) {
  return modelCapabilityNumberText(value) === "?" ? "Не указано" : modelCapabilityNumberText(value) + " из каталога";
}

function updateModelCapabilitySummary(value, targetItem) {
  var list = $("modelCapabilityList");
  if (!list) return;
  var items = targetItem ? [targetItem] : Array.prototype.slice.call(list.querySelectorAll(".model-capability-item"));
  items.forEach(function (item) {
    if (item.dataset.modelValue.toLowerCase() !== value.toLowerCase()) return;
    var model = findModel(value) || {};
    var context = effectiveModelCapabilityValue(value, "MaxContextTokens", "maxContextTokens", model.maxContextTokens);
    var output = effectiveModelCapabilityValue(value, "MaxOutputTokens", "maxOutputTokens", model.maxOutputTokens);
    var meta = item.querySelector(".model-capability-meta");
    meta.textContent = "Контекст " + modelCapabilityNumberText(context) + " · Ответ " + modelCapabilityNumberText(output);
    var badges = item.querySelector(".model-capability-tags");
    badges.replaceChildren();
    var support = [["Vision", effectiveModelSupportsImages(value)], ["Audio", effectiveModelSupportsAudio(value)],
      ["Reasoning", effectiveModelSupportsReasoning(value)]];
    support.forEach(function (entry) {
      if (entry[1] !== true) return;
      var badge = document.createElement("span");
      badge.textContent = entry[0];
      badges.appendChild(badge);
    });
    if (!badges.children.length) {
      var unknown = document.createElement("span");
      unknown.className = "is-muted";
      unknown.textContent = support.every(function (entry) { return entry[1] === false; }) ? "Только текст" : "Возможности ?";
      badges.appendChild(unknown);
    }
  });
}

function renderModelCapabilityEditor(item, value, model) {
  if (item.querySelector(".model-capability-body")) return;
  var body = document.createElement("div");
  body.className = "model-capability-body";
  var note = document.createElement("p");
  note.textContent = "Пустое поле берёт значение из каталога. Для неизвестного окна без общего лимита используется оценка 32 768 токенов.";
  body.appendChild(note);
  var fields = document.createElement("div");
  fields.className = "model-capability-fields";
  appendModelCapabilityNumber(fields, "Окно модели, токенов",
    modelCapabilityEditorValue(value, "MaxContextTokens", model.maxContextTokens),
    modelCapabilityPlaceholder(model.maxContextTokens), function (next) {
      setStoredModelCapabilityField(value, "MaxContextTokens", next);
    });
  appendModelCapabilityNumber(fields, "Макс. ответ, токенов",
    modelCapabilityEditorValue(value, "MaxOutputTokens", model.maxOutputTokens),
    modelCapabilityPlaceholder(model.maxOutputTokens), function (next) {
      setStoredModelCapabilityField(value, "MaxOutputTokens", next);
    });
  appendModelCapabilitySelect(fields, "Изображения", modelOverrideState(modelImageSupportOverrides(), value), function (mode) {
    setModelCapabilityOverride("image", value, mode);
  });
  appendModelCapabilitySelect(fields, "Аудио", modelOverrideState(modelAudioSupportOverrides(), value), function (mode) {
    setModelCapabilityOverride("audio", value, mode);
  });
  var reasoning = modelCapabilityEditorValue(value, "SupportsReasoning", model.supportsReasoning);
  appendModelCapabilitySelect(fields, "Рассуждения", reasoning === null ? "auto" : (reasoning ? "true" : "false"), function (mode) {
    setStoredModelCapabilityField(value, "SupportsReasoning", mode === "auto" ? null : mode === "true");
  });
  appendModelReasoningModeSelect(fields,
    modelCapabilityEditorValue(value, "ReasoningRequestMode", model.reasoningRequestMode),
    model.reasoningRequestMode, function (mode) {
      setStoredModelCapabilityField(value, "ReasoningRequestMode", mode);
    });
  appendModelCapabilityNumber(fields, "Изображений в запросе",
    modelCapabilityEditorValue(value, "MaxImagesPerPrompt", model.maxImagesPerPrompt),
    modelCapabilityPlaceholder(model.maxImagesPerPrompt), function (next) {
      setStoredModelCapabilityField(value, "MaxImagesPerPrompt", next);
    });
  body.appendChild(fields);
  item.appendChild(body);
}

function refreshModelCapabilityDependents(value) {
  updateModelCapabilitySummary(value);
  renderModelCapabilityBadges();
  renderAttachmentModelPriority();
  renderActiveModelCapability();
  if (typeof renderContextWindowSummary === "function") renderContextWindowSummary();
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
  if (nextValue === null || nextValue === undefined || nextValue === "") delete capability[field];
  else capability[field] = nextValue;
  if (!Object.keys(capability).length) delete capabilities[entry ? entry.key : value];
  settingsDirty = true;
  updateSettingsSaveButton();
  refreshModelCapabilityDependents(value);
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
  refreshModelCapabilityDependents(value);
}

function renderModelCapabilityList() {
  var list = $("modelCapabilityList");
  if (!list) return;
  var search = $("modelCapabilitySearch");
  var query = search ? search.value.trim().toLocaleLowerCase("ru") : "";
  var open = {};
  Array.prototype.slice.call(list.querySelectorAll(".model-capability-item[open]")).forEach(function (item) {
    open[item.dataset.modelValue.toLowerCase()] = true;
  });
  list.replaceChildren();
  var values = allKnownModelValues().filter(function (value) {
    var model = findModel(value);
    return !query || (value + " " + (model ? model.title : "")).toLocaleLowerCase("ru").includes(query);
  });
  if (!values.length) {
    var empty = document.createElement("div");
    empty.className = "model-capability-empty";
    empty.textContent = query ? "Модели не найдены." :
      (state.modelCatalog.loading ? "Загрузка моделей…" : "Обновите каталог или введите ID модели.");
    list.appendChild(empty);
    return;
  }
  values.forEach(function (value) {
    var model = findModel(value) || { value: value, title: value };
    var item = document.createElement("details");
    item.className = "model-capability-item";
    item.dataset.modelValue = value;
    item.open = !!open[value.toLowerCase()];
    if (value.toLowerCase() === formModel().toLowerCase()) item.classList.add("is-selected");
    var summary = document.createElement("summary");
    var text = document.createElement("span");
    text.className = "model-capability-text";
    var title = document.createElement("strong");
    title.className = "model-capability-title";
    title.textContent = model.title || value;
    text.appendChild(title);
    if (model.title && model.title !== value) {
      var id = document.createElement("small");
      id.className = "model-capability-id";
      id.textContent = value;
      text.appendChild(id);
    }
    summary.appendChild(text);
    var meta = document.createElement("span");
    meta.className = "model-capability-meta";
    summary.appendChild(meta);
    var badges = document.createElement("span");
    badges.className = "model-capability-tags";
    summary.appendChild(badges);
    item.appendChild(summary);
    item.addEventListener("toggle", function () {
      if (item.open) renderModelCapabilityEditor(item, value, model);
      else {
        var body = item.querySelector(".model-capability-body");
        if (body) body.remove();
      }
    });
    list.appendChild(item);
    if (item.open) renderModelCapabilityEditor(item, value, model);
    updateModelCapabilitySummary(value, item);
  });
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
