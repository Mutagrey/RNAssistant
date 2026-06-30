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
      topP: modelField(item, "TopP", "top_p", "topP", null),
      supportsImages: modelField(item, "SupportsImages", "supports_images", "supportsImages", null),
      inputModalities: modelField(item, "InputModalities", "input_modalities", "inputModalities", []) || []
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

function effectiveModelSupportsImages(value) {
  value = String(value || "").trim();
  var overrides = modelImageSupportOverrides();
  if (Object.prototype.hasOwnProperty.call(overrides, value) && overrides[value] !== null) {
    return !!overrides[value];
  }
  return catalogModelSupportsImages(findModel(value));
}
