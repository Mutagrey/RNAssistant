function renderSettings() {
  var s = state.settings || {};
  $("baseUrlInput").value = s.BaseUrl || s.baseUrl || "";
  $("modelInput").value = s.Model || s.model || "";
  $("maxTokensInput").value = s.MaxTokens || s.maxTokens || 2048;
  $("requestTimeoutInput").value = s.RequestTimeoutSeconds || s.requestTimeoutSeconds || 300;
  $("temperatureInput").value = s.Temperature || s.temperature || 0.2;
  $("topPInput").value = s.TopP || s.topP || 1;
  $("contextLimitInput").value = s.ContextCharLimit || s.contextCharLimit || 24000;
  $("streamInput").checked = !!(s.StreamResponses || s.streamResponses);
  $("agentModeInput").checked = (s.AgentModeEnabled !== false && s.agentModeEnabled !== false);
  $("autoRunToolsInput").checked = (s.AutoRunToolCalls !== false && s.autoRunToolCalls !== false);
  $("autoConfirmToolsInput").checked = !!(s.AutoConfirmToolActions || s.autoConfirmToolActions);
  $("autoRetryToolsInput").checked = (s.AutoRetryToolErrors !== false && s.autoRetryToolErrors !== false);
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
    ContextCharLimit: Number($("contextLimitInput").value || 24000),
    StreamResponses: $("streamInput").checked,
    AgentModeEnabled: $("agentModeInput").checked,
    AutoRunToolCalls: $("autoRunToolsInput").checked,
    AutoConfirmToolActions: $("autoConfirmToolsInput").checked,
    AutoRetryToolErrors: $("autoRetryToolsInput").checked,
    IncludeVbaContext: $("includeVbaContextInput").checked,
    VbaContextCharLimit: Number($("vbaContextLimitInput").value || 30000),
    SystemPrompt: $("systemPromptInput").value,
    AgentPrompt: $("agentPromptInput").value,
    CustomHeaders: textToHeaders($("headersInput").value)
  };
}
