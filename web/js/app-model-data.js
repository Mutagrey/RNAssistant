// One presentation contract; domain adapters provide only their current selection and availability.
function chatModelSelection() {
  if (window.RNAssistantWorkspaceChat && window.RNAssistantWorkspaceChat.owner() !== "office")
    return window.RNAssistantWorkspaceChat.modelSelection();
  return { selected: activeChatModel(),
    disabled: !!(state.modelSaving || state.reasoningSaving || state.chatNavigationPending || state.initializePromise ||
      state.chatDetailPending || state.chatDetailError || state.chatReadOnly || currentActiveSend() || hasActiveMessageEdit() ||
      state.bridgeUnavailable || !state.activeChatId || state.activeRunViewState && state.activeRunViewState.lifecycle === "awaiting_confirmation") };
}

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

function nullableModelBoolean(value) {
  if (value === null || value === undefined || value === "") return null;
  if (typeof value === "string") {
    if (value.toLowerCase() === "true") return true;
    if (value.toLowerCase() === "false") return false;
  }
  return !!value;
}

function modelFieldWithDefaults(model, pascal, snake, camel) {
  var value = modelField(model, pascal, snake, camel, null);
  if (value !== null && value !== undefined) return value;
  var defaults = model.default_params || model.defaultParams || model.DefaultParams || {};
  return modelField(defaults, pascal, snake, camel, null);
}

function normalizeModelCatalog(payload, target) {
  payload = payload || {};
  var catalog = payload.catalog || payload.Catalog || payload;
  var rawModels = Array.isArray(catalog)
    ? catalog
    : (catalog.models || catalog.Models || catalog.data || catalog.Data || []);
  var seen = {};
  var models = [];

  rawModels.forEach(function (item) {
    var rawId = typeof item === "string"
      ? item
      : (item.id !== undefined ? item.id : (item.Id !== undefined ? item.Id : modelField(item, "Value", "value", "value", "")));
    var value = String(rawId || "").trim();
    var fallbackTitle = modelField(item, "Title", "title", "title", value);
    var title = String(modelField(item, "DisplayName", "display_name", "displayName", fallbackTitle) || value).trim();
    if (!value || seen[value.toLowerCase()]) {
      return;
    }
    seen[value.toLowerCase()] = true;
    var supportsImages = modelField(item, "SupportsImages", "supports_images", "supportsImages", null);
    if (supportsImages === null || supportsImages === undefined) {
      supportsImages = modelField(item, "SupportsVision", "supports_vision", "supportsVision", null);
    }
    models.push({
      value: value,
      title: title || value,
      description: modelField(item, "Description", "description", "description", "") || "",
      maxContextTokens: modelField(item, "MaxContextTokens", "max_context_tokens", "maxContextTokens",
        modelField(item, "ContextWindow", "context_window", "contextWindow",
          modelField(item, "ContextLength", "context_length", "contextLength", null))),
      maxOutputTokens: modelField(item, "MaxOutputTokens", "max_output_tokens", "maxOutputTokens",
        modelField(item, "MaxCompletionTokens", "max_completion_tokens", "maxCompletionTokens",
          modelField(item, "OutputTokenLimit", "output_token_limit", "outputTokenLimit", null))),
      maxTokens: modelFieldWithDefaults(item, "MaxTokens", "max_tokens", "maxTokens"),
      systemPrompt: modelField(item, "SystemPrompt", "system_prompt", "systemPrompt", "") || "",
      temperature: modelFieldWithDefaults(item, "Temperature", "temperature", "temperature"),
      topP: modelFieldWithDefaults(item, "TopP", "top_p", "topP"),
      topK: modelFieldWithDefaults(item, "TopK", "top_k", "topK"),
      presencePenalty: modelFieldWithDefaults(item, "PresencePenalty", "presence_penalty", "presencePenalty"),
      frequencyPenalty: modelFieldWithDefaults(item, "FrequencyPenalty", "frequency_penalty", "frequencyPenalty"),
      seed: modelFieldWithDefaults(item, "Seed", "seed", "seed"),
      supportsReasoning: nullableModelBoolean(modelField(item, "SupportsReasoning", "supports_reasoning", "supportsReasoning", null)),
      reasoningRequestMode: String(modelField(item, "ReasoningRequestMode", "reasoning_request_mode", "reasoningRequestMode",
        modelField(item, "ReasoningTransport", "reasoning_transport", "reasoningTransport", "")) || "").trim(),
      supportsImages: nullableModelBoolean(supportsImages),
      supportsAudio: nullableModelBoolean(modelField(item, "SupportsAudio", "supports_audio", "supportsAudio", null)),
      isDefault: nullableModelBoolean(modelField(item, "IsDefault", "is_default", "isDefault", false)) === true,
      maxImagesPerPrompt: modelField(item, "MaxImagesPerPrompt", "max_images_per_prompt", "maxImagesPerPrompt", null),
      inputModalities: modelField(item, "InputModalities", "input_modalities", "inputModalities", []) || []
    });
  });

  var configuredDefault = Array.isArray(catalog)
    ? ""
    : (catalog.default_model || catalog.defaultModel || catalog.DefaultModel || "");
  if (!configuredDefault) {
    for (var defaultIndex = 0; defaultIndex < models.length; defaultIndex += 1) {
      if (models[defaultIndex].isDefault) {
        configuredDefault = models[defaultIndex].value;
        break;
      }
    }
  }

  (target || state).modelCatalog = {
    configUrl: payload.configUrl || payload.ConfigUrl || "",
    defaultModel: configuredDefault,
    models: models,
    loaded: true,
    loading: false,
    error: ""
  };
}

function modelCapabilitiesForSettings(context) {
  var settings = (context || state).settings || {};
  var existing = settings.ModelCapabilities || settings.modelCapabilities || {};
  var result = {};
  var storedKeys = {};
  Object.keys(existing).forEach(function (key) {
    result[key] = Object.assign({}, existing[key]);
    storedKeys[key.toLowerCase()] = key;
  });
  ((context || state).modelCatalog.models || []).forEach(function (model) {
    var storedKey = storedKeys[String(model.value).toLowerCase()];
    var stored = storedKey ? existing[storedKey] || {} : {};
    result[storedKey || model.value] = {
      MaxContextTokens: preferredCapabilityValue(stored, "MaxContextTokens", "maxContextTokens", model.maxContextTokens),
      MaxOutputTokens: preferredCapabilityValue(stored, "MaxOutputTokens", "maxOutputTokens", model.maxOutputTokens),
      SupportsImages: preferredCapabilityValue(stored, "SupportsImages", "supportsImages", catalogModelSupportsImages(model)),
      SupportsReasoning: preferredCapabilityValue(stored, "SupportsReasoning", "supportsReasoning", model.supportsReasoning),
      ReasoningRequestMode: preferredCapabilityValue(stored, "ReasoningRequestMode", "reasoningRequestMode", model.reasoningRequestMode),
      SupportsAudio: preferredCapabilityValue(stored, "SupportsAudio", "supportsAudio", catalogModelSupportsAudio(model)),
      MaxImagesPerPrompt: preferredCapabilityValue(stored, "MaxImagesPerPrompt", "maxImagesPerPrompt", model.maxImagesPerPrompt)
    };
  });
  return result;
}

function preferredCapabilityValue(stored, pascal, camel, fallback) {
  stored = stored || {};
  var value = stored[pascal] !== undefined ? stored[pascal] : stored[camel];
  return value !== null && value !== undefined && value !== "" ? value : (fallback === undefined ? null : fallback);
}

function findModel(value, context) {
  value = String(value || "").toLowerCase();
  var models = (context || state).modelCatalog.models || [];
  for (var i = 0; i < models.length; i += 1) {
    if (String(models[i].value || "").toLowerCase() === value) {
      return models[i];
    }
  }
  return null;
}

function modelImageSupportOverrides(context) {
  var settings = (context || state).settings || {};
  return settings.ModelImageSupportOverrides || settings.modelImageSupportOverrides || {};
}

function modelAudioSupportOverrides(context) {
  var settings = (context || state).settings || {};
  return settings.ModelAudioSupportOverrides || settings.modelAudioSupportOverrides || {};
}

function storedAttachmentModelPriority(context) {
  var settings = (context || state).settings || {};
  return settings.AttachmentModelPriority || settings.attachmentModelPriority || [];
}

function catalogModelSupportsImages(model) {
  if (!model) return null;
  if (model.supportsImages !== null && model.supportsImages !== undefined) {
    if (typeof model.supportsImages === "string") return model.supportsImages.toLowerCase() === "true";
    return !!model.supportsImages;
  }
  var modalities = model.inputModalities || [];
  return modalities.length
    ? modalities.some(function (item) { return String(item || "").toLowerCase() === "image"; })
    : null;
}

function catalogModelSupportsAudio(model) {
  if (!model) return null;
  if (model.supportsAudio !== null && model.supportsAudio !== undefined) {
    return nullableModelBoolean(model.supportsAudio);
  }
  var modalities = model.inputModalities || [];
  return modalities.length
    ? modalities.some(function (item) { return String(item || "").toLowerCase() === "audio"; })
    : null;
}

function storedModelCapabilityEntry(value, context) {
  value = String(value || "").trim().toLowerCase();
  var settings = (context || state).settings || {};
  var capabilities = settings.ModelCapabilities || settings.modelCapabilities || {};
  var keys = Object.keys(capabilities);
  for (var index = 0; index < keys.length; index += 1) {
    if (keys[index].toLowerCase() !== value) continue;
    return { key: keys[index], value: capabilities[keys[index]] || {} };
  }
  return null;
}

function storedModelCapabilityValue(value, pascal, camel, snake, context) {
  var entry = storedModelCapabilityEntry(value, context);
  return entry ? modelField(entry.value, pascal, snake, camel, null) : null;
}

function storedModelCapability(value, pascal, camel, snake, context) {
  return nullableModelBoolean(storedModelCapabilityValue(value, pascal, camel, snake, context));
}

function effectiveModelCapabilityValue(value, pascal, camel, catalogValue, context) {
  var stored = storedModelCapabilityValue(value, pascal, camel, camel, context);
  return stored !== null && stored !== undefined && stored !== "" ? stored : catalogValue;
}

function modelSupportOverride(overrides, value) {
  value = String(value || "").trim().toLowerCase();
  var keys = Object.keys(overrides || {});
  for (var index = 0; index < keys.length; index += 1) {
    if (keys[index].toLowerCase() === value && overrides[keys[index]] !== null && overrides[keys[index]] !== undefined) {
      return !!overrides[keys[index]];
    }
  }
  return null;
}

function effectiveModelSupportsReasoning(value, context) {
  var stored = storedModelCapability(value, "SupportsReasoning", "supportsReasoning", "supports_reasoning", context);
  if (stored !== null) return stored;
  var model = findModel(value, context);
  return model && model.supportsReasoning !== null && model.supportsReasoning !== undefined
    ? nullableModelBoolean(model.supportsReasoning)
    : null;
}

function effectiveModelSupportsAudio(value, context) {
  value = String(value || "").trim();
  var override = modelSupportOverride(modelAudioSupportOverrides(context), value);
  if (override !== null) return override;
  var model = findModel(value, context);
  var catalogSupport = catalogModelSupportsAudio(model);
  return catalogSupport !== null
    ? catalogSupport
    : storedModelCapability(value, "SupportsAudio", "supportsAudio", "supports_audio", context);
}

function effectiveModelSupportsImages(value, context) {
  value = String(value || "").trim();
  var override = modelSupportOverride(modelImageSupportOverrides(context), value);
  if (override !== null) return override;
  var catalogSupport = catalogModelSupportsImages(findModel(value, context));
  if (catalogSupport !== null) return catalogSupport;

  return storedModelCapability(value, "SupportsImages", "supportsImages", "supports_images", context);
}

function allKnownModelValues(context) {
  var values = [];
  var seen = {};
  function add(value) {
    value = String(value || "").trim();
    var key = value.toLowerCase();
    if (!value || seen[key]) return;
    seen[key] = true;
    values.push(value);
  }
  ((context || state).modelCatalog.models || []).forEach(function (model) { add(model.value); });
  add(formModel());
  storedAttachmentModelPriority(context).forEach(add);
  Object.keys(modelImageSupportOverrides(context)).forEach(add);
  Object.keys(modelAudioSupportOverrides(context)).forEach(add);
  var settings = (context || state).settings || {};
  var capabilities = settings.ModelCapabilities || settings.modelCapabilities || {};
  Object.keys(capabilities).forEach(add);
  return values;
}

function attachmentModelPriorityForSettings(context) {
  var result = [];
  var seen = {};
  function append(value) {
    value = String(value || "").trim();
    var key = value.toLowerCase();
    if (!value || seen[key]) return;
    if (effectiveModelSupportsImages(value, context) !== true && effectiveModelSupportsAudio(value, context) !== true) return;
    seen[key] = true;
    result.push(value);
  }
  storedAttachmentModelPriority(context).forEach(append);
  allKnownModelValues(context).forEach(append);
  return result;
}
