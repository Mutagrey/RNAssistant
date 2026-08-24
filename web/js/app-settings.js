var settingsDirty = false;

var modelSettingsDefaults = {
  model: "gpt-4o-mini",
  reasoningRequestMode: "chat_template_kwargs",
  reasoningCustomJson: "{}",
  maxTokens: 3072,
  requestTimeoutSeconds: 1800,
  temperature: 0.2,
  topP: 1,
  contextWindowOverrideTokens: 0,
  tokenEstimateMultiplier: 1,
  autoCalibrateTokenEstimate: true,
  streamResponses: true
};

var agentSettingsDefaults = {
  systemPromptRole: "developer",
  agentResponseMode: "json_object",
  toolResultRole: "user",
  fallbackToJsonObject: true,
  autoConfirmToolActions: false,
  autoCompressContext: true,
  maxAgentIterations: 256,
  maxAgentFormatRetries: 10,
  maxAgentToolSteps: 4096
};

function clampUiFontScale(value) {
  value = Number(value || 1);
  if (!isFinite(value) || value <= 0) {
    value = 1;
  }
  return Math.max(0.85, Math.min(1.3, value));
}

function applyUiFontScale(settings) {
  var scale = clampUiFontScale((settings || {}).UiFontScale || (settings || {}).uiFontScale || 1);
  document.documentElement.style.setProperty("--ui-font-scale", String(scale));
  document.body.setAttribute("data-ui-font-scale", String(scale));
}

function normalizeUiTheme(value) {
  return String(value || "").toLowerCase() === "dark" ? "dark" : "light";
}

function applyUiTheme(settings, persist) {
  var theme = normalizeUiTheme((settings || {}).UiTheme || (settings || {}).uiTheme);
  document.documentElement.setAttribute("data-theme", theme);
  document.documentElement.style.colorScheme = theme;
  if (document.body) {
    document.body.setAttribute("data-theme", theme);
  }
  if (persist !== false) {
    try {
      localStorage.setItem("rnassistant.ui.theme", theme);
    } catch (error) {
      // WebView storage may be unavailable while its profile is being reset.
    }
  }
  return theme;
}

function updateReasoningCustomJsonVisibility() {
  var field = $("reasoningCustomJsonField");
  if (!field) {
    return;
  }
  var visible = $("reasoningRequestModeInput").value === "custom_json";
  field.classList.toggle("hidden", !visible);
  field.setAttribute("aria-hidden", visible ? "false" : "true");
}

function updateAgentProtocolControls() {
  var field = $("agentJsonFallbackField");
  if (!field) {
    return;
  }
  var visible = $("agentResponseModeInput").value === "json_schema";
  field.classList.toggle("hidden", !visible);
  field.setAttribute("aria-hidden", visible ? "false" : "true");
}

function tokenEstimateCalibrationFor(settings, model) {
  var calibrations = compatibilityValue(settings, "TokenEstimateCalibrations", "tokenEstimateCalibrations", {}) || {};
  var target = String(model || "").toLowerCase();
  var key = Object.keys(calibrations).filter(function (item) {
    return String(item || "").toLowerCase() === target;
  })[0];
  return key ? calibrations[key] : null;
}

function renderTokenEstimateCalibrationStatus(settings) {
  var status = $("tokenEstimateCalibrationStatus");
  if (!status) return;
  settings = settings || state.settings || {};
  var manual = Number($("tokenEstimateMultiplierInput").value || modelSettingsDefaults.tokenEstimateMultiplier);
  var automatic = $("autoCalibrateTokenEstimateInput").checked;
  var model = $("modelInput").value.trim() || settings.Model || settings.model || "";
  var calibration = tokenEstimateCalibrationFor(settings, model);
  var relative = Number(compatibilityValue(calibration, "Multiplier", "multiplier", 1) || 1);
  var intercept = Number(compatibilityValue(calibration, "InterceptTokens", "interceptTokens", 0) || 0);
  var samples = Number(compatibilityValue(calibration, "SampleCount", "sampleCount", 0) || 0);
  var effective = Math.max(0.25, Math.min(4, automatic && samples > 0 ? relative : manual));
  if (automatic && samples > 0) {
    var estimated = Number(compatibilityValue(calibration, "LastEstimatedPromptTokens", "lastEstimatedPromptTokens", 0) || 0);
    var actual = Number(compatibilityValue(calibration, "LastActualPromptTokens", "lastActualPromptTokens", 0) || 0);
    status.textContent = "Авто: ×" + effective.toFixed(2).replace(".", ",") +
      (intercept > 0 ? " + " + formatNumber(Math.ceil(intercept)) : "") + " · " + samples +
      " API usage" +
      (estimated && actual ? " · последнее: ≈" + formatNumber(estimated) + " → " + formatNumber(actual) : "");
    return;
  }
  status.textContent = "×" + effective.toFixed(2).replace(".", ",") + " · UTF-8/4" +
    (automatic ? " · авто после первого API usage" : "");
}

function readReasoningCustomJson(mode) {
  var text = $("reasoningCustomJsonInput").value.trim();
  if (!text) {
    return "{}";
  }
  if (mode !== "custom_json") {
    return text;
  }

  var value;
  try {
    value = JSON.parse(text);
  } catch (error) {
    throw new Error("Кастомный reasoning JSON содержит ошибку: " + error.message);
  }
  if (!value || Array.isArray(value) || typeof value !== "object") {
    throw new Error("Кастомный reasoning JSON должен быть JSON-объектом.");
  }

  var reserved = {
    model: true,
    messages: true,
    max_tokens: true,
    temperature: true,
    top_p: true,
    stream: true,
    stream_options: true,
    response_format: true,
    tools: true,
    tool_choice: true,
    parallel_tool_calls: true
  };
  Object.keys(value).forEach(function (key) {
    if (reserved[String(key).toLowerCase()]) {
      throw new Error("Кастомный reasoning JSON не может переопределять системное поле " + key + ".");
    }
  });
  return JSON.stringify(value, null, 2);
}

function renderSettings() {
  var s = state.settings || {};
  applyUiFontScale(s);
  var uiTheme = applyUiTheme(s);
  var appVersion = $("appVersion");
  if (appVersion) {
    var appVersionText = String(state.appVersion || "").trim();
    appVersion.textContent = appVersionText ? "v" + appVersionText.replace(/^v/i, "") : "—";
  }
  $("baseUrlInput").value = s.BaseUrl || s.baseUrl || "";
  $("modelsConfigUrlInput").value = s.ModelsConfigUrl || s.modelsConfigUrl || "";
  $("modelInput").value = s.Model || s.model || "";
  var instructionRole = String(s.SystemPromptRole || s.systemPromptRole || agentSettingsDefaults.systemPromptRole).toLowerCase();
  $("systemPromptRoleInput").value = instructionRole === "system" || instructionRole === "user" ? instructionRole : agentSettingsDefaults.systemPromptRole;
  var responseMode = String(s.AgentResponseMode || s.agentResponseMode || agentSettingsDefaults.agentResponseMode).toLowerCase();
  $("agentResponseModeInput").value = responseMode === "json_schema" ? "json_schema" : "json_object";
  var toolResultRole = String(s.ToolResultRole || s.toolResultRole || agentSettingsDefaults.toolResultRole).toLowerCase();
  $("toolResultRoleInput").value = toolResultRole === "developer" || toolResultRole === "tool" ? toolResultRole : agentSettingsDefaults.toolResultRole;
  $("fallbackJsonObjectInput").checked = compatibilityValue(s, "FallbackToJsonObject", "fallbackToJsonObject", agentSettingsDefaults.fallbackToJsonObject) !== false;
  updateAgentProtocolControls();
  var reasoningRequestMode = String(s.ReasoningRequestMode || s.reasoningRequestMode || modelSettingsDefaults.reasoningRequestMode).toLowerCase();
  var reasoningModes = ["auto", "reasoning_effort", "enable_thinking", "chat_template_kwargs", "reasoning_enabled", "custom_json"];
  $("reasoningRequestModeInput").value = reasoningModes.indexOf(reasoningRequestMode) >= 0 ? reasoningRequestMode : modelSettingsDefaults.reasoningRequestMode;
  $("reasoningCustomJsonInput").value = s.ReasoningCustomJson || s.reasoningCustomJson || modelSettingsDefaults.reasoningCustomJson;
  updateReasoningCustomJsonVisibility();
  $("maxTokensInput").value = compatibilityValue(s, "MaxTokens", "maxTokens", modelSettingsDefaults.maxTokens);
  $("requestTimeoutInput").value = compatibilityValue(s, "RequestTimeoutSeconds", "requestTimeoutSeconds", modelSettingsDefaults.requestTimeoutSeconds);
  $("temperatureInput").value = compatibilityValue(s, "Temperature", "temperature", modelSettingsDefaults.temperature);
  $("topPInput").value = compatibilityValue(s, "TopP", "topP", modelSettingsDefaults.topP);
  $("uiFontScaleInput").value = Math.round(clampUiFontScale(s.UiFontScale || s.uiFontScale || 1) * 100);
  Array.prototype.slice.call(document.querySelectorAll('input[name="uiTheme"]')).forEach(function (input) {
    input.checked = input.value === uiTheme;
  });
  $("contextLimitInput").value = compatibilityValue(s, "ContextWindowOverrideTokens", "contextWindowOverrideTokens", modelSettingsDefaults.contextWindowOverrideTokens);
  $("tokenEstimateMultiplierInput").value = compatibilityValue(s, "TokenEstimateMultiplier", "tokenEstimateMultiplier", modelSettingsDefaults.tokenEstimateMultiplier);
  $("autoCalibrateTokenEstimateInput").checked = compatibilityValue(s, "AutoCalibrateTokenEstimate", "autoCalibrateTokenEstimate", modelSettingsDefaults.autoCalibrateTokenEstimate) !== false;
  renderTokenEstimateCalibrationStatus(s);
  $("streamInput").checked = compatibilityValue(s, "StreamResponses", "streamResponses", modelSettingsDefaults.streamResponses) !== false;
  $("autoConfirmToolsInput").checked = compatibilityValue(s, "AutoConfirmToolActions", "autoConfirmToolActions", agentSettingsDefaults.autoConfirmToolActions) === true;
  $("autoCompressContextInput").checked = compatibilityValue(s, "AutoCompressContext", "autoCompressContext", agentSettingsDefaults.autoCompressContext) !== false;
  $("debugModelTrafficInput").checked = !!(s.DebugModelTraffic || s.debugModelTraffic);
  $("smartChatTitlesInput").checked = (s.SmartChatTitles !== false && s.smartChatTitles !== false);
  $("maxAgentIterationsInput").value = s.MaxAgentIterations || s.maxAgentIterations || agentSettingsDefaults.maxAgentIterations;
  $("maxAgentFormatRetriesInput").value = s.MaxAgentFormatRetries || s.maxAgentFormatRetries || agentSettingsDefaults.maxAgentFormatRetries;
  $("maxAgentToolStepsInput").value = s.MaxAgentToolSteps || s.maxAgentToolSteps || agentSettingsDefaults.maxAgentToolSteps;
  if (typeof renderPromptSettings === "function") {
    renderPromptSettings(s);
  }
  $("headersInput").value = headersToText(s.CustomHeaders || s.customHeaders || {});
  $("htmlNetworkOriginsInput").value = (s.HtmlNetworkAllowedOrigins || s.htmlNetworkAllowedOrigins || []).join("\n");
  var apiKeyStatus = $("apiKeyStatus");
  $("apiKeyInput").placeholder = state.hasApiKey
    ? "Ключ сохранён; оставьте пустым, чтобы не менять"
    : "Введите API-ключ";
  if (apiKeyStatus) {
    apiKeyStatus.textContent = state.hasApiKey
      ? "Ключ сохранён локально и не отображается повторно."
      : "Ключ пока не сохранён.";
  }
  renderModelControls();
  settingsDirty = false;
  updateSettingsSaveButton();
}

function readSettings() {
  if (typeof syncSelectedPromptFromEditor === "function") {
    syncSelectedPromptFromEditor();
  }
  var promptSettings = typeof readPromptSettings === "function"
    ? readPromptSettings()
    : { SystemPrompt: "", ChatSystemPrompt: "", ContextCompactionPrompt: "", ChatTitlePrompt: "" };
  var reasoningRequestMode = $("reasoningRequestModeInput").value;
  var reasoningCustomJson = readReasoningCustomJson(reasoningRequestMode);
  return {
    BaseUrl: $("baseUrlInput").value.trim(),
    ModelsConfigUrl: $("modelsConfigUrlInput").value.trim(),
    Model: $("modelInput").value.trim(),
    ReasoningRequestMode: reasoningRequestMode,
    ReasoningCustomJson: reasoningCustomJson,
    MaxTokens: Number($("maxTokensInput").value || modelSettingsDefaults.maxTokens),
    RequestTimeoutSeconds: Number($("requestTimeoutInput").value || modelSettingsDefaults.requestTimeoutSeconds),
    Temperature: Number($("temperatureInput").value || modelSettingsDefaults.temperature),
    TopP: Number($("topPInput").value || modelSettingsDefaults.topP),
    UiFontScale: clampUiFontScale(Number($("uiFontScaleInput").value || 100) / 100),
    UiTheme: normalizeUiTheme((document.querySelector('input[name="uiTheme"]:checked') || {}).value),
    ContextWindowOverrideTokens: Number($("contextLimitInput").value || 0),
    TokenEstimateMultiplier: Number($("tokenEstimateMultiplierInput").value || modelSettingsDefaults.tokenEstimateMultiplier),
    AutoCalibrateTokenEstimate: $("autoCalibrateTokenEstimateInput").checked,
    TokenEstimateCalibrations: compatibilityValue(state.settings, "TokenEstimateCalibrations", "tokenEstimateCalibrations", {}) || {},
    StreamResponses: $("streamInput").checked,
    AutoConfirmToolActions: $("autoConfirmToolsInput").checked,
    AutoCompressContext: $("autoCompressContextInput").checked,
    DebugModelTraffic: $("debugModelTrafficInput").checked,
    SmartChatTitles: $("smartChatTitlesInput").checked,
    MaxAgentIterations: Number($("maxAgentIterationsInput").value || agentSettingsDefaults.maxAgentIterations),
    MaxAgentFormatRetries: Math.max(1, Math.min(20, Number($("maxAgentFormatRetriesInput").value || agentSettingsDefaults.maxAgentFormatRetries))),
    MaxAgentToolSteps: Number($("maxAgentToolStepsInput").value || agentSettingsDefaults.maxAgentToolSteps),
    AgentResponseMode: $("agentResponseModeInput").value,
    ToolResultRole: $("toolResultRoleInput").value,
    FallbackToJsonObject: $("fallbackJsonObjectInput").checked,
    SystemPrompt: promptSettings.SystemPrompt,
    ChatSystemPrompt: promptSettings.ChatSystemPrompt,
    SystemPromptRole: $("systemPromptRoleInput").value,
    ContextCompactionPrompt: promptSettings.ContextCompactionPrompt,
    ChatTitlePrompt: promptSettings.ChatTitlePrompt,
    ModelImageSupportOverrides: modelImageSupportOverrides(),
    ModelAudioSupportOverrides: modelAudioSupportOverrides(),
    ModelCapabilities: modelCapabilitiesForSettings(),
    HtmlNetworkAllowedOrigins: $("htmlNetworkOriginsInput").value.split(/\r?\n/).map(function (value) { return value.trim(); }).filter(Boolean),
    CustomHeaders: textToHeaders($("headersInput").value)
  };
}

async function persistSettingsFromForm() {
  var apiKey = $("apiKeyInput").value;
  var nextSettings = readSettings();
  var response = await send("saveSettings", { settings: nextSettings, apiKey: apiKey || null });
  state.appVersion = response.appVersion || response.AppVersion || state.appVersion;
  state.settings = response.settings || response.Settings || nextSettings;
  state.hasApiKey = !!(response.hasApiKey || response.HasApiKey);
  $("apiKeyInput").value = "";
  renderSettings();
  updateEstimatedContextUsage();
  renderContextMeter();
  return state.settings;
}

function compatibilityValue(source, pascal, camel, fallback) {
  source = source || {};
  return source[pascal] !== undefined ? source[pascal] : (source[camel] !== undefined ? source[camel] : fallback);
}

function markSettingsDirty() {
  settingsDirty = true;
  updateSettingsSaveButton();
}

function resetModelSettingsToDefaults() {
  var settings = state.settings || (state.settings = {});
  settings.ModelCapabilities = {};
  settings.ModelImageSupportOverrides = {};
  settings.ModelAudioSupportOverrides = {};
  $("modelInput").value = modelSettingsDefaults.model;
  $("reasoningRequestModeInput").value = modelSettingsDefaults.reasoningRequestMode;
  $("reasoningCustomJsonInput").value = modelSettingsDefaults.reasoningCustomJson;
  $("maxTokensInput").value = modelSettingsDefaults.maxTokens;
  $("requestTimeoutInput").value = modelSettingsDefaults.requestTimeoutSeconds;
  $("temperatureInput").value = modelSettingsDefaults.temperature;
  $("topPInput").value = modelSettingsDefaults.topP;
  $("contextLimitInput").value = modelSettingsDefaults.contextWindowOverrideTokens;
  $("tokenEstimateMultiplierInput").value = modelSettingsDefaults.tokenEstimateMultiplier;
  $("autoCalibrateTokenEstimateInput").checked = modelSettingsDefaults.autoCalibrateTokenEstimate;
  settings.TokenEstimateCalibrations = {};
  $("streamInput").checked = modelSettingsDefaults.streamResponses;
  updateReasoningCustomJsonVisibility();
  renderModelControls();
  renderTokenEstimateCalibrationStatus(settings);
  markSettingsDirty();
}

function resetAgentSettingsToDefaults() {
  $("systemPromptRoleInput").value = agentSettingsDefaults.systemPromptRole;
  $("agentResponseModeInput").value = agentSettingsDefaults.agentResponseMode;
  $("toolResultRoleInput").value = agentSettingsDefaults.toolResultRole;
  $("fallbackJsonObjectInput").checked = agentSettingsDefaults.fallbackToJsonObject;
  $("autoConfirmToolsInput").checked = agentSettingsDefaults.autoConfirmToolActions;
  $("autoCompressContextInput").checked = agentSettingsDefaults.autoCompressContext;
  $("maxAgentIterationsInput").value = agentSettingsDefaults.maxAgentIterations;
  $("maxAgentFormatRetriesInput").value = agentSettingsDefaults.maxAgentFormatRetries;
  $("maxAgentToolStepsInput").value = agentSettingsDefaults.maxAgentToolSteps;
  updateAgentProtocolControls();
  markSettingsDirty();
}

function renderModelCompatibilityResult(result) {
  var root = $("modelCompatibilityResults");
  if (!root) return;
  root.replaceChildren();

  var compatible = !!compatibilityValue(result, "Compatible", "compatible", false);
  var summary = document.createElement("div");
  summary.className = "model-compatibility-summary " + (compatible ? "passed" : "failed");
  summary.textContent = compatibilityValue(result, "Summary", "summary", compatible ? "Совместимо." : "Есть несовместимости.");
  root.appendChild(summary);

  var meta = document.createElement("div");
  meta.className = "model-compatibility-meta";
  meta.textContent = [
    compatibilityValue(result, "Model", "model", ""),
    "instruction: " + compatibilityValue(result, "InstructionRole", "instructionRole", ""),
    "agent: " + compatibilityValue(result, "ResponseMode", "responseMode", ""),
    "tool result: " + compatibilityValue(result, "ToolResultRole", "toolResultRole", "")
  ].filter(Boolean).join(" · ");
  root.appendChild(meta);

  var list = document.createElement("div");
  list.className = "model-compatibility-list";
  (compatibilityValue(result, "Checks", "checks", []) || []).forEach(function (check) {
    var passed = !!compatibilityValue(check, "Passed", "passed", false);
    var required = !!compatibilityValue(check, "Required", "required", false);
    var row = document.createElement("div");
    row.className = "model-compatibility-check " + (passed ? "passed" : "failed") + (required ? " required" : " optional");

    var mark = document.createElement("span");
    mark.className = "model-compatibility-mark";
    mark.textContent = passed ? "✓" : "×";
    row.appendChild(mark);

    var copy = document.createElement("div");
    var title = document.createElement("div");
    title.className = "model-compatibility-check-title";
    title.textContent = compatibilityValue(check, "Title", "title", "Проверка") + (required ? " · используется сейчас" : " · необязательно");
    copy.appendChild(title);
    var message = document.createElement("div");
    message.className = "model-compatibility-check-message";
    message.textContent = compatibilityValue(check, "Message", "message", "") + " · " + Number(compatibilityValue(check, "DurationMs", "durationMs", 0) || 0) + " мс";
    copy.appendChild(message);
    row.appendChild(copy);
    list.appendChild(row);
  });
  root.appendChild(list);
}

function bindSettingsActions() {
  Array.prototype.slice.call(document.querySelectorAll(".settings-nav-button")).forEach(function (button) {
    button.addEventListener("click", function () {
      var page = button.getAttribute("data-settings-page");
      Array.prototype.slice.call(document.querySelectorAll(".settings-nav-button")).forEach(function (item) {
        item.classList.toggle("active", item === button);
      });
      Array.prototype.slice.call(document.querySelectorAll(".settings-page")).forEach(function (item) {
        item.classList.toggle("active", item.getAttribute("data-settings-page") === page);
      });
      updateSettingsSaveButton();
    });
  });

  $("uiFontScaleInput").addEventListener("input", function () {
    applyUiFontScale({ UiFontScale: Number($("uiFontScaleInput").value || 100) / 100 });
  });

  $("reasoningRequestModeInput").addEventListener("change", updateReasoningCustomJsonVisibility);
  $("agentResponseModeInput").addEventListener("change", updateAgentProtocolControls);
  $("tokenEstimateMultiplierInput").addEventListener("input", function () {
    renderTokenEstimateCalibrationStatus(state.settings);
  });
  $("autoCalibrateTokenEstimateInput").addEventListener("change", function () {
    renderTokenEstimateCalibrationStatus(state.settings);
  });
  $("modelInput").addEventListener("input", function () {
    renderTokenEstimateCalibrationStatus(state.settings);
  });
  $("resetModelSettingsButton").addEventListener("click", resetModelSettingsToDefaults);
  $("resetAgentSettingsButton").addEventListener("click", resetAgentSettingsToDefaults);

  Array.prototype.slice.call(document.querySelectorAll('input[name="uiTheme"]')).forEach(function (input) {
    input.addEventListener("change", function () {
      if (input.checked) {
        applyUiTheme({ UiTheme: input.value }, false);
      }
    });
  });

  Array.prototype.slice.call(document.querySelectorAll("#tab-settings input, #tab-settings textarea, #tab-settings select")).forEach(function (control) {
    control.addEventListener(control.type === "checkbox" || control.tagName === "SELECT" ? "change" : "input", function () {
      if (control.id === "promptSearchInput") {
        return;
      }
      settingsDirty = true;
      updateSettingsSaveButton();
    });
  });

  $("saveSettingsButton").addEventListener("click", async function () {
    var button = $("saveSettingsButton");
    try {
      button.disabled = true;
      await persistSettingsFromForm();
      log("Настройки сохранены.");
      loadModelCatalog(false);
    } catch (error) {
      settingsDirty = true;
      updateSettingsSaveButton();
      log(error.message, "error");
    }
  });
  $("testModelCompatibilityButton").addEventListener("click", async function () {
    var button = $("testModelCompatibilityButton");
    var root = $("modelCompatibilityResults");
    try {
      button.disabled = true;
      button.textContent = "Проверяю…";
      root.textContent = "Выполняются безопасные запросы к endpoint…";
      await persistSettingsFromForm();
      var response = await send("testModelCompatibility", {});
      renderModelCompatibilityResult(response);
      log("Проверка совместимости модели завершена.");
    } catch (error) {
      root.textContent = "Тест не выполнен: " + error.message;
      log(error.message, "error");
    } finally {
      button.disabled = false;
      button.textContent = "Запустить тест";
    }
  });
  $("clearRuntimeDataButton").addEventListener("click", clearRuntimeData);
  if (typeof bindPromptSettingsActions === "function") {
    bindPromptSettingsActions();
  }
}

function activeSettingsPage() {
  var active = document.querySelector(".settings-nav-button.active");
  return active ? active.getAttribute("data-settings-page") : "connection";
}

function updateSettingsSaveButton() {
  var row = document.querySelector(".settings-actions-row");
  var button = $("saveSettingsButton");
  var visible = !!settingsDirty;
  if (!row || !button) {
    return;
  }

  row.hidden = !visible;
  button.hidden = !visible;
  button.disabled = !visible;
}
