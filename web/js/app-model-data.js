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

function normalizeModelCatalog(payload) {
  payload = payload || {};
  var catalog = payload.catalog || payload.Catalog || payload;
  var rawModels = Array.isArray(catalog)
    ? catalog
    : (catalog.models || catalog.Models || catalog.data || catalog.Data || []);
  var seen = {};
  var models = [];

  rawModels.forEach(function (item) {
    var value = String(modelField(item, "Value", "value", "value", item.id || item.Id || "") || "").trim();
    var legacyTitle = modelField(item, "Title", "title", "title", value);
    var title = String(modelField(item, "DisplayName", "display_name", "displayName", legacyTitle) || value).trim();
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

  state.modelCatalog = {
    configUrl: payload.configUrl || payload.ConfigUrl || "",
    defaultModel: configuredDefault,
    models: models,
    loaded: true,
    loading: false,
    error: ""
  };
}

function modelCapabilitiesForSettings() {
  var settings = state.settings || {};
  var existing = settings.ModelCapabilities || settings.modelCapabilities || {};
  var result = {};
  Object.keys(existing).forEach(function (key) {
    result[key] = existing[key];
  });
  (state.modelCatalog.models || []).forEach(function (model) {
    result[model.value] = {
      MaxContextTokens: model.maxContextTokens || null,
      MaxOutputTokens: model.maxOutputTokens || null,
      SupportsImages: catalogModelSupportsImages(model),
      SupportsReasoning: model.supportsReasoning,
      SupportsAudio: model.supportsAudio,
      MaxImagesPerPrompt: model.maxImagesPerPrompt || null
    };
  });
  return result;
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

function modelImageSupportOverrides() {
  var settings = state.settings || {};
  return settings.ModelImageSupportOverrides || settings.modelImageSupportOverrides || {};
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

function storedModelCapability(value, pascal, camel, snake) {
  value = String(value || "").trim().toLowerCase();
  var settings = state.settings || {};
  var capabilities = settings.ModelCapabilities || settings.modelCapabilities || {};
  var keys = Object.keys(capabilities);
  for (var index = 0; index < keys.length; index += 1) {
    if (keys[index].toLowerCase() !== value) continue;
    var capability = capabilities[keys[index]] || {};
    var support = modelField(capability, pascal, snake, camel, null);
    return nullableModelBoolean(support);
  }
  return null;
}

function effectiveModelSupportsReasoning(value) {
  var model = findModel(value);
  return model && model.supportsReasoning !== null && model.supportsReasoning !== undefined
    ? nullableModelBoolean(model.supportsReasoning)
    : storedModelCapability(value, "SupportsReasoning", "supportsReasoning", "supports_reasoning");
}

function effectiveModelSupportsAudio(value) {
  var model = findModel(value);
  return model && model.supportsAudio !== null && model.supportsAudio !== undefined
    ? nullableModelBoolean(model.supportsAudio)
    : storedModelCapability(value, "SupportsAudio", "supportsAudio", "supports_audio");
}

function effectiveModelSupportsImages(value) {
  value = String(value || "").trim();
  var overrides = modelImageSupportOverrides();
  if (Object.prototype.hasOwnProperty.call(overrides, value) && overrides[value] !== null) {
    return !!overrides[value];
  }
  var catalogSupport = catalogModelSupportsImages(findModel(value));
  if (catalogSupport !== null) return catalogSupport;

  return storedModelCapability(value, "SupportsImages", "supportsImages", "supports_images");
}
