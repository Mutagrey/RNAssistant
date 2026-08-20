var settingsDirty = false;

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
  $("baseUrlInput").value = s.BaseUrl || s.baseUrl || "";
  $("modelsConfigUrlInput").value = s.ModelsConfigUrl || s.modelsConfigUrl || "";
  $("modelInput").value = s.Model || s.model || "";
  var instructionRole = String(s.SystemPromptRole || s.systemPromptRole || "developer").toLowerCase();
  $("systemPromptRoleInput").value = instructionRole === "system" || instructionRole === "user" ? instructionRole : "developer";
  var responseMode = String(s.AgentResponseMode || s.agentResponseMode || "json_schema").toLowerCase();
  $("agentResponseModeInput").value = responseMode === "native_tool_calls" || responseMode === "json_object" ? responseMode : "json_schema";
  var reasoningRequestMode = String(s.ReasoningRequestMode || s.reasoningRequestMode || "auto").toLowerCase();
  var reasoningModes = ["auto", "reasoning_effort", "enable_thinking", "chat_template_kwargs", "reasoning_enabled", "custom_json"];
  $("reasoningRequestModeInput").value = reasoningModes.indexOf(reasoningRequestMode) >= 0 ? reasoningRequestMode : "auto";
  $("reasoningCustomJsonInput").value = s.ReasoningCustomJson || s.reasoningCustomJson || "{}";
  updateReasoningCustomJsonVisibility();
  $("fallbackJsonObjectInput").checked = (s.FallbackToJsonObject !== false && s.fallbackToJsonObject !== false);
  var toolResultRole = String(s.ToolResultRole || s.toolResultRole || "tool").toLowerCase();
  $("toolResultRoleInput").value = toolResultRole === "developer" || toolResultRole === "user" ? toolResultRole : "tool";
  $("maxTokensInput").value = compatibilityValue(s, "MaxTokens", "maxTokens", 2048);
  $("requestTimeoutInput").value = compatibilityValue(s, "RequestTimeoutSeconds", "requestTimeoutSeconds", 300);
  $("temperatureInput").value = compatibilityValue(s, "Temperature", "temperature", 0.2);
  $("topPInput").value = compatibilityValue(s, "TopP", "topP", 1);
  $("uiFontScaleInput").value = Math.round(clampUiFontScale(s.UiFontScale || s.uiFontScale || 1) * 100);
  Array.prototype.slice.call(document.querySelectorAll('input[name="uiTheme"]')).forEach(function (input) {
    input.checked = input.value === uiTheme;
  });
  $("contextLimitInput").value = s.ContextWindowOverrideTokens || s.contextWindowOverrideTokens || 0;
  $("streamInput").checked = !!(s.StreamResponses || s.streamResponses);
  $("autoRunToolsInput").checked = (s.AutoRunToolCalls !== false && s.autoRunToolCalls !== false);
  $("autoConfirmToolsInput").checked = !!(s.AutoConfirmToolActions || s.autoConfirmToolActions);
  $("autoRetryToolsInput").checked = (s.AutoRetryToolErrors !== false && s.autoRetryToolErrors !== false);
  $("requireVerificationInput").checked = (s.RequireVerificationForMutations !== false && s.requireVerificationForMutations !== false);
  $("autoContinueAfterConfirmationInput").checked = (s.AutoContinueAfterConfirmation !== false && s.autoContinueAfterConfirmation !== false);
  $("autoCompressContextInput").checked = (s.AutoCompressContext !== false && s.autoCompressContext !== false);
  $("debugModelTrafficInput").checked = !!(s.DebugModelTraffic || s.debugModelTraffic);
  $("smartChatTitlesInput").checked = (s.SmartChatTitles !== false && s.smartChatTitles !== false);
  $("maxAgentIterationsInput").value = s.MaxAgentIterations || s.maxAgentIterations || 8;
  $("maxAgentFormatRetriesInput").value = s.MaxAgentFormatRetries || s.maxAgentFormatRetries || 2;
  $("maxAgentToolStepsInput").value = s.MaxAgentToolSteps || s.maxAgentToolSteps || 40;
  $("maxAgentToolsPerRequestInput").value = s.MaxAgentToolsPerRequest || s.maxAgentToolsPerRequest || 24;
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
    : { SystemPrompt: "", ChatSystemPrompt: "", AgentPrompts: {} };
  var reasoningRequestMode = $("reasoningRequestModeInput").value;
  var reasoningCustomJson = readReasoningCustomJson(reasoningRequestMode);
  return {
    BaseUrl: $("baseUrlInput").value.trim(),
    ModelsConfigUrl: $("modelsConfigUrlInput").value.trim(),
    Model: $("modelInput").value.trim(),
    AgentResponseMode: $("agentResponseModeInput").value,
    ReasoningRequestMode: reasoningRequestMode,
    ReasoningCustomJson: reasoningCustomJson,
    FallbackToJsonObject: $("fallbackJsonObjectInput").checked,
    ToolResultRole: $("toolResultRoleInput").value,
    MaxTokens: Number($("maxTokensInput").value || 2048),
    RequestTimeoutSeconds: Number($("requestTimeoutInput").value || 300),
    Temperature: Number($("temperatureInput").value || 0.2),
    TopP: Number($("topPInput").value || 1),
    UiFontScale: clampUiFontScale(Number($("uiFontScaleInput").value || 100) / 100),
    UiTheme: normalizeUiTheme((document.querySelector('input[name="uiTheme"]:checked') || {}).value),
    ContextWindowOverrideTokens: Number($("contextLimitInput").value || 0),
    StreamResponses: $("streamInput").checked,
    AutoRunToolCalls: $("autoRunToolsInput").checked,
    AutoConfirmToolActions: $("autoConfirmToolsInput").checked,
    AutoRetryToolErrors: $("autoRetryToolsInput").checked,
    RequireVerificationForMutations: $("requireVerificationInput").checked,
    AutoContinueAfterConfirmation: $("autoContinueAfterConfirmationInput").checked,
    AutoCompressContext: $("autoCompressContextInput").checked,
    DebugModelTraffic: $("debugModelTrafficInput").checked,
    SmartChatTitles: $("smartChatTitlesInput").checked,
    MaxAgentIterations: Number($("maxAgentIterationsInput").value || 8),
    MaxAgentFormatRetries: Number($("maxAgentFormatRetriesInput").value || 2),
    MaxAgentToolSteps: Number($("maxAgentToolStepsInput").value || 40),
    MaxAgentToolsPerRequest: Number($("maxAgentToolsPerRequestInput").value || 24),
    SystemPrompt: promptSettings.SystemPrompt,
    ChatSystemPrompt: promptSettings.ChatSystemPrompt,
    SystemPromptRole: $("systemPromptRoleInput").value,
    AgentPrompts: promptSettings.AgentPrompts,
    ModelImageSupportOverrides: modelImageSupportOverrides(),
    ModelAudioSupportOverrides: modelAudioSupportOverrides(),
    ModelCapabilities: modelCapabilitiesForSettings(),
    AttachmentModelPriority: attachmentModelPriorityForSettings(),
    HtmlNetworkAllowedOrigins: $("htmlNetworkOriginsInput").value.split(/\r?\n/).map(function (value) { return value.trim(); }).filter(Boolean),
    CustomHeaders: textToHeaders($("headersInput").value)
  };
}

async function persistSettingsFromForm() {
  var apiKey = $("apiKeyInput").value;
  var nextSettings = readSettings();
  var response = await send("saveSettings", { settings: nextSettings, apiKey: apiKey || null });
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
    compatibilityValue(result, "ResponseMode", "responseMode", ""),
    "instruction: " + compatibilityValue(result, "InstructionRole", "instructionRole", ""),
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
      log(error.message);
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
      log(error.message);
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
