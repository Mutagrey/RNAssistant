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
  $("modelInput").value = s.Model || s.model || "";
  $("maxTokensInput").value = s.MaxTokens || s.maxTokens || 2048;
  $("requestTimeoutInput").value = s.RequestTimeoutSeconds || s.requestTimeoutSeconds || 300;
  $("temperatureInput").value = s.Temperature || s.temperature || 0.2;
  $("topPInput").value = s.TopP || s.topP || 1;
  $("uiFontScaleInput").value = Math.round(clampUiFontScale(s.UiFontScale || s.uiFontScale || 1) * 100);
  $("contextLimitInput").value = s.ContextCharLimit || s.contextCharLimit || 24000;
  $("streamInput").checked = !!(s.StreamResponses || s.streamResponses);
  $("agentModeInput").checked = (s.AgentModeEnabled !== false && s.agentModeEnabled !== false);
  $("autoRunToolsInput").checked = (s.AutoRunToolCalls !== false && s.autoRunToolCalls !== false);
  $("autoConfirmToolsInput").checked = !!(s.AutoConfirmToolActions || s.autoConfirmToolActions);
  $("autoRetryToolsInput").checked = (s.AutoRetryToolErrors !== false && s.autoRetryToolErrors !== false);
  $("smartChatTitlesInput").checked = (s.SmartChatTitles !== false && s.smartChatTitles !== false);
  $("includeVbaContextInput").checked = !!(s.IncludeVbaContext || s.includeVbaContext);
  $("vbaContextLimitInput").value = s.VbaContextCharLimit || s.vbaContextCharLimit || 30000;
  $("systemPromptInput").value = s.SystemPrompt || s.systemPrompt || "";
  $("agentPromptInput").value = s.AgentPrompt || s.agentPrompt || "";
  $("headersInput").value = headersToText(s.CustomHeaders || s.customHeaders || {});
  renderModelControls();
}

function readSettings() {
  return {
    BaseUrl: $("baseUrlInput").value.trim(),
    Model: $("modelInput").value.trim(),
    MaxTokens: Number($("maxTokensInput").value || 2048),
    RequestTimeoutSeconds: Number($("requestTimeoutInput").value || 300),
    Temperature: Number($("temperatureInput").value || 0.2),
    TopP: Number($("topPInput").value || 1),
    UiFontScale: clampUiFontScale(Number($("uiFontScaleInput").value || 100) / 100),
    ContextCharLimit: Number($("contextLimitInput").value || 24000),
    StreamResponses: $("streamInput").checked,
    AgentModeEnabled: $("agentModeInput").checked,
    AutoRunToolCalls: $("autoRunToolsInput").checked,
    AutoConfirmToolActions: $("autoConfirmToolsInput").checked,
    AutoRetryToolErrors: $("autoRetryToolsInput").checked,
    SmartChatTitles: $("smartChatTitlesInput").checked,
    IncludeVbaContext: $("includeVbaContextInput").checked,
    VbaContextCharLimit: Number($("vbaContextLimitInput").value || 30000),
    SystemPrompt: $("systemPromptInput").value,
    AgentPrompt: $("agentPromptInput").value,
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
    });
  });

  $("uiFontScaleInput").addEventListener("input", function () {
    applyUiFontScale({ UiFontScale: Number($("uiFontScaleInput").value || 100) / 100 });
  });

  $("saveSettingsButton").addEventListener("click", async function () {
    try {
      var apiKey = $("apiKeyInput").value;
      var response = await send("saveSettings", { settings: readSettings(), apiKey: apiKey || null });
      state.settings = response.settings;
      $("apiKeyInput").value = "";
      renderSettings();
      updateEstimatedContextUsage();
      renderContextMeter();
      await loadModelCatalog(false);
      log("Settings saved.");
    } catch (error) {
      log(error.message);
    }
  });
  $("clearRuntimeDataButton").addEventListener("click", clearRuntimeData);
}
