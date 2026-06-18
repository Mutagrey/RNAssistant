function settingsModel() {
  var s = state.settings || {};
  return s.Model || s.model || "";
}

function formModel() {
  var input = $("modelInput");
  return input ? input.value.trim() : settingsModel();
}

function activeChatModel() {
  return state.activeChatModel || "";
}

function modelField(model, pascal, snake, camel, fallback) {
  model = model || {};
  if (model[pascal] !== undefined) {
    return model[pascal];
  }
  if (model[snake] !== undefined) {
    return model[snake];
  }
  if (model[camel] !== undefined) {
    return model[camel];
  }
  return fallback;
}

function normalizeModelCatalog(payload) {
  payload = payload || {};
  var catalog = payload.catalog || payload.Catalog || payload;
  var rawModels = catalog.models || catalog.Models || [];
  var seen = {};
  var models = [];

  rawModels.forEach(function (item) {
    var value = String(modelField(item, "Value", "value", "value", "") || "").trim();
    var title = String(modelField(item, "Title", "title", "title", value) || value).trim();
    if (!value || seen[value.toLowerCase()]) {
      return;
    }
    seen[value.toLowerCase()] = true;
    models.push({
      value: value,
      title: title || value,
      description: modelField(item, "Description", "description", "description", "") || "",
      maxContextTokens: modelField(item, "MaxContextTokens", "max_context_tokens", "maxContextTokens", null),
      maxTokens: modelField(item, "MaxTokens", "max_tokens", "maxTokens", null),
      systemPrompt: modelField(item, "SystemPrompt", "system_prompt", "systemPrompt", "") || "",
      temperature: modelField(item, "Temperature", "temperature", "temperature", null),
      topP: modelField(item, "TopP", "top_p", "topP", null)
    });
  });

  state.modelCatalog = {
    configUrl: payload.configUrl || payload.ConfigUrl || "",
    defaultModel: catalog.default_model || catalog.defaultModel || catalog.DefaultModel || "",
    models: models,
    loaded: true,
    loading: false,
    error: ""
  };
}

function findModel(value) {
  value = String(value || "").toLowerCase();
  var models = state.modelCatalog.models || [];
  for (var i = 0; i < models.length; i += 1) {
    if (String(models[i].value || "").toLowerCase() === value) {
      return models[i];
    }
  }
  return null;
}

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
    parts.push("Context: " + model.maxContextTokens);
  }
  if (model.maxTokens) {
    parts.push("Output: " + model.maxTokens);
  }
  if (model.temperature !== null && model.temperature !== undefined) {
    parts.push("Temperature: " + model.temperature);
  }
  if (model.topP !== null && model.topP !== undefined) {
    parts.push("Top P: " + model.topP);
  }
  return parts.join("\n");
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
    fallback.textContent = selected + " (fallback)";
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
    empty.textContent = state.modelCatalog.loading ? "Loading models..." : "No model list";
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
  defaultOption.textContent = "Default: " + (defaultModel || "not set");
  select.appendChild(defaultOption);

  if (selected && !findModel(selected)) {
    var fallback = document.createElement("option");
    fallback.value = selected;
    fallback.textContent = selected + " (chat)";
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
  select.title = selected ? ("Chat model: " + selected) : ("Using default model: " + (defaultModel || ""));
  select.disabled = state.modelCatalog.loading || state.modelSaving || !state.activeChatId;
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
  titleText.textContent = model ? model.title : "Default model fallback";
  title.appendChild(titleText);

  if (model && state.modelCatalog.defaultModel &&
      String(model.value).toLowerCase() === String(state.modelCatalog.defaultModel).toLowerCase()) {
    var badge = document.createElement("span");
    badge.className = "model-default-badge";
    badge.textContent = "Default";
    title.appendChild(badge);
  }
  box.appendChild(title);

  var value = document.createElement("div");
  value.className = "model-info-value";
  value.textContent = model ? model.value : (selected || "No model selected");
  box.appendChild(value);

  var description = document.createElement("div");
  description.className = "model-info-description";
  description.textContent = model
    ? (model.description || "No description.")
    : "Typed default model will be used for new chats and chats without their own model.";
  box.appendChild(description);

  if (!model) {
    return;
  }

  var metrics = document.createElement("div");
  metrics.className = "model-info-metrics";
  appendModelMetric(metrics, "Context", model.maxContextTokens);
  appendModelMetric(metrics, "Output", model.maxTokens);
  appendModelMetric(metrics, "Temp", model.temperature);
  appendModelMetric(metrics, "Top P", model.topP);
  box.appendChild(metrics);

  if (model.systemPrompt) {
    var prompt = document.createElement("div");
    prompt.className = "model-info-prompt";
    prompt.textContent = "System prompt: " + model.systemPrompt;
    box.appendChild(prompt);
  }
}

function renderModelStatus() {
  var status = $("modelStatus");
  if (!status) {
    return;
  }

  if (state.modelCatalog.loading) {
    status.textContent = "Loading models...";
    return;
  }
  if (state.modelCatalog.error) {
    status.textContent = "Model list error: " + state.modelCatalog.error;
    return;
  }
  if (state.modelCatalog.loaded) {
    status.textContent = "Models loaded: " + (state.modelCatalog.models || []).length +
      (state.modelCatalog.defaultModel ? ". Default: " + state.modelCatalog.defaultModel : "") +
      (state.modelCatalog.configUrl ? ". Source: " + state.modelCatalog.configUrl : "");
    return;
  }
  status.textContent = "Model list is not loaded.";
}

function renderModelControls() {
  populateModelSelect($("modelSelect"), formModel());
  populateChatModelSelect($("chatModelSelect"));
  renderModelInfo(formModel());
  renderModelStatus();
}

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
