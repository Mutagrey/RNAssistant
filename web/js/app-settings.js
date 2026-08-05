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

function renderSettings() {
  var s = state.settings || {};
  applyUiFontScale(s);
  $("baseUrlInput").value = s.BaseUrl || s.baseUrl || "";
  $("modelsConfigUrlInput").value = s.ModelsConfigUrl || s.modelsConfigUrl || "";
  $("modelInput").value = s.Model || s.model || "";
  var instructionRole = String(s.SystemPromptRole || s.systemPromptRole || "developer").toLowerCase();
  $("systemPromptRoleInput").value = instructionRole === "system" || instructionRole === "user" ? instructionRole : "developer";
  var responseMode = String(s.AgentResponseMode || s.agentResponseMode || "json_schema").toLowerCase();
  $("agentResponseModeInput").value = responseMode === "native_tool_calls" || responseMode === "json_object" ? responseMode : "json_schema";
  $("fallbackJsonObjectInput").checked = (s.FallbackToJsonObject !== false && s.fallbackToJsonObject !== false);
  var toolResultRole = String(s.ToolResultRole || s.toolResultRole || "tool").toLowerCase();
  $("toolResultRoleInput").value = toolResultRole === "developer" || toolResultRole === "user" ? toolResultRole : "tool";
  $("maxTokensInput").value = s.MaxTokens || s.maxTokens || 2048;
  $("requestTimeoutInput").value = s.RequestTimeoutSeconds || s.requestTimeoutSeconds || 300;
  $("temperatureInput").value = s.Temperature || s.temperature || 0.2;
  $("topPInput").value = s.TopP || s.topP || 1;
  $("uiFontScaleInput").value = Math.round(clampUiFontScale(s.UiFontScale || s.uiFontScale || 1) * 100);
  $("contextLimitInput").value = s.ContextWindowOverrideTokens || s.contextWindowOverrideTokens || 0;
  $("streamInput").checked = !!(s.StreamResponses || s.streamResponses);
  $("autoRunToolsInput").checked = (s.AutoRunToolCalls !== false && s.autoRunToolCalls !== false);
  $("autoConfirmToolsInput").checked = !!(s.AutoConfirmToolActions || s.autoConfirmToolActions);
  $("autoRetryToolsInput").checked = (s.AutoRetryToolErrors !== false && s.autoRetryToolErrors !== false);
  $("requireVerificationInput").checked = (s.RequireVerificationForMutations !== false && s.requireVerificationForMutations !== false);
  $("autoContinueAfterConfirmationInput").checked = (s.AutoContinueAfterConfirmation !== false && s.autoContinueAfterConfirmation !== false);
  $("allowAgentToolAuthoringInput").checked = !!(s.AllowAgentToolAuthoring || s.allowAgentToolAuthoring);
  $("autoCompressContextInput").checked = (s.AutoCompressContext !== false && s.autoCompressContext !== false);
  $("smartChatTitlesInput").checked = (s.SmartChatTitles !== false && s.smartChatTitles !== false);
  $("includeVbaContextInput").checked = !!(s.IncludeVbaContext || s.includeVbaContext);
  $("maxAgentIterationsInput").value = s.MaxAgentIterations || s.maxAgentIterations || 8;
  $("maxAgentToolStepsInput").value = s.MaxAgentToolSteps || s.maxAgentToolSteps || 40;
  $("maxAgentToolsPerRequestInput").value = s.MaxAgentToolsPerRequest || s.maxAgentToolsPerRequest || 24;
  $("vbaContextLimitInput").value = s.VbaContextCharLimit || s.vbaContextCharLimit || 30000;
  if (typeof renderPromptSettings === "function") {
    renderPromptSettings(s);
  }
  $("headersInput").value = headersToText(s.CustomHeaders || s.customHeaders || {});
  $("htmlNetworkOriginsInput").value = (s.HtmlNetworkAllowedOrigins || s.htmlNetworkAllowedOrigins || []).join("\n");
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
  return {
    BaseUrl: $("baseUrlInput").value.trim(),
    ModelsConfigUrl: $("modelsConfigUrlInput").value.trim(),
    Model: $("modelInput").value.trim(),
    AgentResponseMode: $("agentResponseModeInput").value,
    FallbackToJsonObject: $("fallbackJsonObjectInput").checked,
    ToolResultRole: $("toolResultRoleInput").value,
    MaxTokens: Number($("maxTokensInput").value || 2048),
    RequestTimeoutSeconds: Number($("requestTimeoutInput").value || 300),
    Temperature: Number($("temperatureInput").value || 0.2),
    TopP: Number($("topPInput").value || 1),
    UiFontScale: clampUiFontScale(Number($("uiFontScaleInput").value || 100) / 100),
    ContextWindowOverrideTokens: Number($("contextLimitInput").value || 0),
    StreamResponses: $("streamInput").checked,
    AutoRunToolCalls: $("autoRunToolsInput").checked,
    AutoConfirmToolActions: $("autoConfirmToolsInput").checked,
    AutoRetryToolErrors: $("autoRetryToolsInput").checked,
    RequireVerificationForMutations: $("requireVerificationInput").checked,
    AutoContinueAfterConfirmation: $("autoContinueAfterConfirmationInput").checked,
    AllowAgentToolAuthoring: $("allowAgentToolAuthoringInput").checked,
    AutoCompressContext: $("autoCompressContextInput").checked,
    SmartChatTitles: $("smartChatTitlesInput").checked,
    IncludeVbaContext: $("includeVbaContextInput").checked,
    MaxAgentIterations: Number($("maxAgentIterationsInput").value || 8),
    MaxAgentToolSteps: Number($("maxAgentToolStepsInput").value || 40),
    MaxAgentToolsPerRequest: Number($("maxAgentToolsPerRequestInput").value || 24),
    VbaContextCharLimit: Number($("vbaContextLimitInput").value || 30000),
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
      var apiKey = $("apiKeyInput").value;
      var nextSettings = readSettings();
      var response = await send("saveSettings", { settings: nextSettings, apiKey: apiKey || null });
      state.settings = response.settings || response.Settings || nextSettings;
      $("apiKeyInput").value = "";
      renderSettings();
      updateEstimatedContextUsage();
      renderContextMeter();
      await loadModelCatalog(false);
      log("Настройки сохранены.");
    } catch (error) {
      settingsDirty = true;
      updateSettingsSaveButton();
      log(error.message);
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
  var visible = !!settingsDirty && activeSettingsPage() !== "service";
  if (!row || !button) {
    return;
  }

  row.hidden = !visible;
  button.hidden = !visible;
  button.disabled = !visible;
}
