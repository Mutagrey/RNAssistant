var settingsDirty = false;
var settingsFormRevision = 0;
var settingsSaveInProgress = false;
// Only non-DOM form fields. Copy-on-write keeps applied settings and submitted saves immutable.
var settingsModelDraft = {};
function modelSettingsFormContext() {
  return { settings: Object.assign({}, state.settings, settingsModelDraft),
    modelCatalog: modelCatalogPreview.modelCatalog || state.modelCatalog };
}
function setModelSettingsDraft(field, value) {
  settingsModelDraft[field] = value;
  markSettingsDirty();
}

var agentProtocolExampleViewers = {};

// Placement retains this form; its draft/save owner stays here, never in Dockview.
var RNAssistantSettingsView = {
  active: false,
  activate: function () {
    this.active = true;
    if (!settingsDirty && !settingsSaveInProgress) renderSettings();
    loadSettingsModelCatalogIfNeeded();
    RNAssistantDeploymentConfig.activate();
  },
  deactivate: function () {
    this.active = false;
    // Keep edited controls, but cancel form-derived catalog requests on leaving the view.
    // A later activation can reload that preview from the same unapplied inputs.
    discardModelCatalogPreview();
    RNAssistantDeploymentConfig.deactivate();
  },
  canClose: function () {
    if (settingsSaveInProgress) {
      RNAssistantShellLayout.notice("Настройки сохраняются. Дождитесь результата.");
      return false;
    }
    return !settingsDirty || window.confirm("Закрыть вкладку настроек?\n\nНесохранённые правки останутся до перезапуска приложения. Чтобы применить их сейчас, нажмите «Отмена», затем «Применить».");
  }
};

var modelSettingsDefaults = {
  model: "",
  reasoningRequestMode: "chat_template_kwargs",
  reasoningCustomJson: "{}",
  maxTokens: 3072,
  requestTimeoutSeconds: 1800,
  temperature: 0.2,
  topP: 1,
  contextWindowOverrideTokens: 0,
  attachmentHelperMaxTokens: 0,
  attachmentEvidenceMaxTokens: 0,
  tokenEstimateMultiplier: 1,
  autoCalibrateTokenEstimate: true,
  streamResponses: true
};

var agentSettingsDefaults = {
  systemPromptRole: "developer",
  agentResponseMode: "json_object",
  autoConfirmToolActions: false,
  enableAgentJavaScript: false,
  autoCompressContext: true,
  maxAgentIterations: 256,
  maxAgentFormatRetries: 3,
  maxConsecutiveReflections: 2,
  maxCollaborationAgents: 8,
  maxConcurrentAgentSteps: 4,
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
  if (window.RNAssistantAccents) {
    window.RNAssistantAccents.apply(document.documentElement.getAttribute("data-accent"), false);
  }
  return theme;
}

function renderUiAccentChoices(selected) {
  var host = $("uiAccentChoices");
  if (!host || !window.RNAssistantAccents) return;
  if (!host.firstChild) {
    window.RNAssistantAccents.choices.forEach(function (choice) {
      var label = document.createElement("label");
      label.className = "accent-choice";
      var input = document.createElement("input");
      input.type = "radio";
      input.name = "uiAccent";
      input.value = choice.id;
      var swatch = document.createElement("span");
      swatch.className = "accent-choice-swatch";
      swatch.style.setProperty("--swatch", choice.swatch);
      var text = document.createElement("span");
      label.appendChild(input);
      label.appendChild(swatch);
      label.appendChild(text);
      host.appendChild(label);
    });
  }
  var isDark = document.documentElement.getAttribute("data-theme") === "dark";
  Array.prototype.slice.call(host.querySelectorAll(".accent-choice")).forEach(function (label, index) {
    var choice = window.RNAssistantAccents.choices[index];
    var name = isDark ? choice.label : (choice.lightLabel || choice.label);
    label.querySelector("input").setAttribute("aria-label", name);
    label.lastChild.textContent = name;
  });
  Array.prototype.slice.call(host.querySelectorAll('input[name="uiAccent"]')).forEach(function (input) {
    input.checked = input.value === selected;
  });
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
  $("agentResponseModeNote").textContent = $("agentResponseModeInput").value === "json_schema"
    ? "Требует поддержки схемы сервером. Автоматического перехода на другой формат нет."
    : "Ответ и история сохраняют JSON. execute содержит непустой tool_calls; промежуточный message необязателен.";
  updateAgentProtocolExample();
}

function updateAgentProtocolExample() {
  var mode = $("agentResponseModeInput").value;
  var instructionRole = $("systemPromptRoleInput").value;
  var saved = state.settings || {};
  var changed = mode !== compatibilityValue(saved, "AgentResponseMode", "agentResponseMode", agentSettingsDefaults.agentResponseMode) ||
    instructionRole !== compatibilityValue(saved, "SystemPromptRole", "systemPromptRole", agentSettingsDefaults.systemPromptRole);
  $("agentProtocolExampleSummary").textContent = mode + " · JSON-история";
  $("agentProtocolApplyNote").textContent = changed
    ? "Показан черновик настроек. Нажмите «Применить», чтобы использовать его в следующем запуске."
    : "Сохранённый формат для следующего запуска. Уже отправленные запросы и их снимки не меняются.";
  if (!$("agentProtocolExample").open) return;
  var call = { name: "core.tools.search", arguments: { query: "VBA" } };
  var decision = { action: "execute", reason: "Найти инструменты.", tool_calls: [call] };
  var response = { role: "assistant", content: JSON.stringify(mode === "json_schema" ? { response: decision } : decision) };
  var assistant = { role: "assistant", content: JSON.stringify(decision) };
  var result = { role: "user", content: JSON.stringify({ type: "tool_results", results: [
    { call_index: 0, name: call.name, status: "ok", message: "Найдены инструменты.", data: { items: ["…"] } }
  ] }) };
  var format = { type: mode };
  if (mode === "json_schema") format.json_schema = { name: "rnassistant_conversation_response_v8", strict: true, schema: "…" };
  var request = { response_format: format, messages: [{ role: instructionRole, content: "… инструкции выбранного формата …" }, assistant, result] };
  [["agentResponseExample", response], ["agentHistoryExample", request]].forEach(function (entry) {
    var text = JSON.stringify(entry[1], null, 2), viewer = agentProtocolExampleViewers[entry[0]];
    if (viewer) viewer.setSource(text);
    else {
      viewer = window.RNAssistantJsonViewer.create({ text: text, compact: true, expandedPaths: [
        '$["content"]', '$["content"]::<json>["response"]', '$["content"]::<json>["tool_calls"]',
        '$["content"]::<json>["response"]["tool_calls"]', '$["messages"]', '$["messages"][1]', '$["messages"][2]'],
        onCopy: window.copyTextResult });
      agentProtocolExampleViewers[entry[0]] = viewer;
      $(entry[0]).replaceChildren(viewer.element);
    }
  });
}

function updateHistoryProtectionControls() {
  var hmac = $("historyHmacInput").checked;
  var encrypted = $("historyEncryptionInput").checked;
  var enabled = hmac || encrypted;
  var source = $("historyKeySourceInput");
  source.disabled = !enabled;
  var custom = enabled && source.value === "custom_secret";
  var field = $("historySecretField");
  field.classList.toggle("hidden", !custom);
  field.setAttribute("aria-hidden", custom ? "false" : "true");
  $("historySecretInput").disabled = !custom;
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

function renderContextWindowSummary() {
  var summary = $("contextWindowSummary");
  if (!summary) return;
  var model = formModel();
  var catalog = findModel(model, modelSettingsFormContext()) || {};
  var declared = Number(effectiveModelCapabilityValue(model, "MaxContextTokens", "maxContextTokens", catalog.maxContextTokens, modelSettingsFormContext()) || 0);
  var limit = Number($("contextLimitInput").value || 0);
  var effective = limit > 0 ? (declared > 0 ? Math.min(limit, declared) : limit) :
    (declared > 0 ? declared : 32768);
  var format = function (value) { return Math.floor(value).toLocaleString("ru-RU"); };
  summary.textContent = (model || "Модель не выбрана") + ": " +
    (declared > 0 ? "окно модели " + format(declared) : "окно модели неизвестно") +
    " · бюджет RNAssistant " + format(effective) + " токенов" +
    (declared <= 0 && limit <= 0 ? " (резервная оценка)" : "") + ".";
}

function loadSettingsModelCatalogIfNeeded() {
  var page = document.querySelector('.settings-page[data-settings-page="model"].active');
  var catalog = modelSettingsFormContext().modelCatalog;
  if (page && !catalog.loaded && !catalog.loading && !catalog.error &&
      !state.bridgeUnavailable && typeof loadModelCatalog === "function") {
    loadModelCatalog(true);
  }
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

function formatAppVersionLabel(value) {
  var raw = String(value || "").trim().replace(/^v/i, "");
  if (!raw) {
    return "—";
  }

  var identified = raw.match(/^(.+)\+g([0-9a-f]{7,64})(?:\.(dirty|unknown))?$/i);
  if (!identified) {
    return "v" + raw;
  }

  var label = "v" + identified[1] + " · " + identified[2].substring(0, 7);
  return identified[3] ? label + " · " + identified[3].toLowerCase() : label;
}

function renderSettings(options) {
  // Host/chat refreshes may arrive while this global view has an edited form.
  if ((settingsDirty || settingsSaveInProgress) && !(options && options.committed)) return;
  settingsModelDraft = {};
  var s = state.settings || {};
  applyUiFontScale(s);
  var uiTheme = applyUiTheme(s);
  var uiAccent = window.RNAssistantAccents.apply(s.UiAccent || s.uiAccent);
  renderUiAccentChoices(uiAccent);
  if ($("runningMessageDelivery")) $("runningMessageDelivery").value = String(s.RunningMessageDelivery || "Steer").toLowerCase() === "queue" ? "Queue" : "Steer";
  $("baseUrlInput").value = s.BaseUrl || s.baseUrl || "";
  $("modelsConfigUrlInput").value = s.ModelsConfigUrl || s.modelsConfigUrl || "";
  $("modelInput").value = s.Model || s.model || "";
  var instructionRole = String(s.SystemPromptRole || s.systemPromptRole || agentSettingsDefaults.systemPromptRole).toLowerCase();
  $("systemPromptRoleInput").value = instructionRole === "system" || instructionRole === "user" ? instructionRole : agentSettingsDefaults.systemPromptRole;
  var responseMode = String(s.AgentResponseMode || s.agentResponseMode || agentSettingsDefaults.agentResponseMode).toLowerCase();
  $("agentResponseModeInput").value = responseMode === "json_schema" ? "json_schema" : "json_object";
  var migration = $("agentProtocolMigrationNote");
  migration.textContent = s.ProtocolMigrationNotice || s.protocolMigrationNotice || "";
  migration.hidden = !migration.textContent;
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
  $("desktopWindowWidthInput").value = compatibilityValue(s, "DesktopWindowWidth", "desktopWindowWidth", 1600);
  Array.prototype.slice.call(document.querySelectorAll('input[name="uiTheme"]')).forEach(function (input) {
    input.checked = input.value === uiTheme;
  });
  $("contextLimitInput").value = compatibilityValue(s, "ContextWindowOverrideTokens", "contextWindowOverrideTokens", modelSettingsDefaults.contextWindowOverrideTokens);
  renderContextWindowSummary();
  $("attachmentHelperMaxTokensInput").value = compatibilityValue(s, "AttachmentHelperMaxTokens", "attachmentHelperMaxTokens", modelSettingsDefaults.attachmentHelperMaxTokens);
  $("attachmentEvidenceMaxTokensInput").value = compatibilityValue(s, "AttachmentEvidenceMaxTokens", "attachmentEvidenceMaxTokens", modelSettingsDefaults.attachmentEvidenceMaxTokens);
  $("tokenEstimateMultiplierInput").value = compatibilityValue(s, "TokenEstimateMultiplier", "tokenEstimateMultiplier", modelSettingsDefaults.tokenEstimateMultiplier);
  $("autoCalibrateTokenEstimateInput").checked = compatibilityValue(s, "AutoCalibrateTokenEstimate", "autoCalibrateTokenEstimate", modelSettingsDefaults.autoCalibrateTokenEstimate) !== false;
  renderTokenEstimateCalibrationStatus(s);
  $("streamInput").checked = compatibilityValue(s, "StreamResponses", "streamResponses", modelSettingsDefaults.streamResponses) !== false;
  $("autoConfirmToolActionsInput").checked = compatibilityValue(s, "AutoConfirmToolActions", "autoConfirmToolActions", agentSettingsDefaults.autoConfirmToolActions) === true;
  $("enableAgentJavaScriptInput").checked = compatibilityValue(s, "EnableAgentJavaScript", "enableAgentJavaScript", agentSettingsDefaults.enableAgentJavaScript) === true;
  $("autoCompressContextInput").checked = compatibilityValue(s, "AutoCompressContext", "autoCompressContext", agentSettingsDefaults.autoCompressContext) !== false;
  $("debugModelTrafficInput").checked = !!(s.DebugModelTraffic || s.debugModelTraffic);
  $("screenCaptureProtectionInput").checked = compatibilityValue(s, "ScreenCaptureProtectionEnabled", "screenCaptureProtectionEnabled", true) !== false;
  $("historyHmacInput").checked = String(compatibilityValue(s, "HistoryIntegrityMode", "historyIntegrityMode", "sha256")).toLowerCase() === "hmac_sha256";
  $("historyEncryptionInput").checked = String(compatibilityValue(s, "HistoryEncryptionMode", "historyEncryptionMode", "none")).toLowerCase() === "aes256_cbc_hmac_sha256";
  $("historyKeySourceInput").value = String(compatibilityValue(s, "HistoryKeySource", "historyKeySource", "api_key")).toLowerCase() === "custom_secret"
    ? "custom_secret"
    : "api_key";
  var historySecretStatus = $("historySecretStatus");
  $("historySecretInput").placeholder = state.hasHistorySecret
    ? "Секрет сохранён; оставьте пустым, чтобы не менять"
    : "Минимум 12 символов";
  if (historySecretStatus) {
    historySecretStatus.textContent = state.hasHistorySecret
      ? "Секрет сохранён через DPAPI и повторно не отображается."
      : "Отдельный секрет пока не сохранён.";
  }
  updateHistoryProtectionControls();
  $("smartChatTitlesInput").checked = (s.SmartChatTitles !== false && s.smartChatTitles !== false);
  $("maxAgentIterationsInput").value = s.MaxAgentIterations || s.maxAgentIterations || agentSettingsDefaults.maxAgentIterations;
  $("maxConsecutiveReflectionsInput").value = compatibilityValue(s, "MaxConsecutiveReflections", "maxConsecutiveReflections", agentSettingsDefaults.maxConsecutiveReflections);
  $("maxAgentFormatRetriesInput").value = s.MaxAgentFormatRetries || s.maxAgentFormatRetries || agentSettingsDefaults.maxAgentFormatRetries;
  $("maxCollaborationAgentsInput").value = compatibilityValue(s, "MaxCollaborationAgents", "maxCollaborationAgents", agentSettingsDefaults.maxCollaborationAgents);
  $("maxConcurrentAgentStepsInput").value = compatibilityValue(s, "MaxConcurrentAgentSteps", "maxConcurrentAgentSteps", agentSettingsDefaults.maxConcurrentAgentSteps);
  $("maxAgentToolStepsInput").value = s.MaxAgentToolSteps || s.maxAgentToolSteps || agentSettingsDefaults.maxAgentToolSteps;
  if (typeof renderPromptSettings === "function") {
    renderPromptSettings(state.prompts);
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
  var modelContext = modelSettingsFormContext();
  if (typeof syncSelectedPromptFromEditor === "function") {
    syncSelectedPromptFromEditor();
  }
  var reasoningRequestMode = $("reasoningRequestModeInput").value;
  var reasoningCustomJson = readReasoningCustomJson(reasoningRequestMode);
  return {
    BaseUrl: $("baseUrlInput").value.trim(),
    ModelsConfigUrl: $("modelsConfigUrlInput").value.trim(),
    Model: $("modelInput").value.trim(),
    AgentPromptSchemaVersion: Number(compatibilityValue(state.settings, "AgentPromptSchemaVersion", "agentPromptSchemaVersion", 0)),
    ReasoningRequestMode: reasoningRequestMode,
    ReasoningCustomJson: reasoningCustomJson,
    MaxTokens: Number($("maxTokensInput").value || modelSettingsDefaults.maxTokens),
    RequestTimeoutSeconds: Number($("requestTimeoutInput").value || modelSettingsDefaults.requestTimeoutSeconds),
    Temperature: Number($("temperatureInput").value || modelSettingsDefaults.temperature),
    TopP: Number($("topPInput").value || modelSettingsDefaults.topP),
    UiFontScale: clampUiFontScale(Number($("uiFontScaleInput").value || 100) / 100),
    DesktopWindowWidth: Number($("desktopWindowWidthInput").value || 1600),
    RunningMessageDelivery: $("runningMessageDelivery") ? $("runningMessageDelivery").value : "Steer",
    UiTheme: normalizeUiTheme((document.querySelector('input[name="uiTheme"]:checked') || {}).value),
    UiAccent: window.RNAssistantAccents.normalize((document.querySelector('input[name="uiAccent"]:checked') || {}).value),
    ContextWindowOverrideTokens: Number($("contextLimitInput").value || 0),
    AttachmentHelperMaxTokens: Math.max(0, Math.floor(Number($("attachmentHelperMaxTokensInput").value || 0))),
    AttachmentEvidenceMaxTokens: Math.max(0, Math.floor(Number($("attachmentEvidenceMaxTokensInput").value || 0))),
    TokenEstimateMultiplier: Number($("tokenEstimateMultiplierInput").value || modelSettingsDefaults.tokenEstimateMultiplier),
    AutoCalibrateTokenEstimate: $("autoCalibrateTokenEstimateInput").checked,
    TokenEstimateCalibrations: compatibilityValue(modelContext.settings, "TokenEstimateCalibrations", "tokenEstimateCalibrations", {}) || {},
    StreamResponses: $("streamInput").checked,
    AutoConfirmToolActions: $("autoConfirmToolActionsInput").checked,
    EnableAgentJavaScript: $("enableAgentJavaScriptInput").checked,
    AutoCompressContext: $("autoCompressContextInput").checked,
    DebugModelTraffic: $("debugModelTrafficInput").checked,
    ScreenCaptureProtectionEnabled: $("screenCaptureProtectionInput").checked,
    HistoryIntegrityMode: $("historyHmacInput").checked ? "hmac_sha256" : "sha256",
    HistoryEncryptionMode: $("historyEncryptionInput").checked ? "aes256_cbc_hmac_sha256" : "none",
    HistoryKeySource: $("historyKeySourceInput").value,
    SmartChatTitles: $("smartChatTitlesInput").checked,
    MaxAgentIterations: Number($("maxAgentIterationsInput").value || agentSettingsDefaults.maxAgentIterations),
    MaxConsecutiveReflections: Math.max(0, Math.min(20, Math.trunc(Number($("maxConsecutiveReflectionsInput").value)))),
    MaxAgentFormatRetries: Math.max(1, Math.min(20, Number($("maxAgentFormatRetriesInput").value || agentSettingsDefaults.maxAgentFormatRetries))),
    MaxCollaborationAgents: Number($("maxCollaborationAgentsInput").value || agentSettingsDefaults.maxCollaborationAgents),
    MaxConcurrentAgentSteps: Number($("maxConcurrentAgentStepsInput").value || agentSettingsDefaults.maxConcurrentAgentSteps),
    MaxAgentToolSteps: Number($("maxAgentToolStepsInput").value || agentSettingsDefaults.maxAgentToolSteps),
    AgentResponseMode: $("agentResponseModeInput").value,
    SystemPromptRole: $("systemPromptRoleInput").value,
    ModelImageSupportOverrides: Object.assign({}, modelImageSupportOverrides(modelContext)),
    ModelAudioSupportOverrides: Object.assign({}, modelAudioSupportOverrides(modelContext)),
    ModelCapabilities: modelCapabilitiesForSettings(modelContext),
    AttachmentModelPriority: attachmentModelPriorityForSettings(modelContext),
    HtmlNetworkAllowedOrigins: $("htmlNetworkOriginsInput").value.split(/\r?\n/).map(function (value) { return value.trim(); }).filter(Boolean),
    CustomHeaders: textToHeaders($("headersInput").value)
  };
}

async function persistSettingsFromForm() {
  if (settingsSaveInProgress) throw new Error("Сохранение настроек уже выполняется.");
  var apiKey = $("apiKeyInput").value;
  var historySecret = $("historySecretInput").value;
  var nextSettings = readSettings();
  var submittedRevision = settingsFormRevision;
  var submittedModelDraft = Object.assign({}, settingsModelDraft);
  var javascriptWasEnabled = compatibilityValue(state.settings, "EnableAgentJavaScript", "enableAgentJavaScript", false) === true;
  var messagingWasEnabled = compatibilityValue(state.settings, "MaxCollaborationAgents", "maxCollaborationAgents", agentSettingsDefaults.maxCollaborationAgents) > 0;
  settingsSaveInProgress = true;
  updateSettingsSaveButton();
  try {
    var response = await saveSettingsWithPromptChanges(nextSettings, apiKey, historySecret);
    state.appVersion = response.appVersion || response.AppVersion || state.appVersion;
    state.settings = response.settings;
    if (window.RNAssistantAgentCommunication) RNAssistantAgentCommunication.settingsChanged();
    Object.keys(submittedModelDraft).forEach(function (field) {
      if (settingsModelDraft[field] === submittedModelDraft[field]) delete settingsModelDraft[field];
    });
    if (typeof resetModelCatalog === "function") resetModelCatalog();
    state.prompts = response.prompts;
    state.hasApiKey = !!(response.hasApiKey || response.HasApiKey);
    state.hasHistorySecret = !!(response.hasHistorySecret || response.HasHistorySecret);
    if ($("apiKeyInput").value === apiKey) $("apiKeyInput").value = "";
    if ($("historySecretInput").value === historySecret) $("historySecretInput").value = "";
    if (submittedRevision === settingsFormRevision) renderSettings({ committed: true });
    else {
      // A confirmed save applies its captured values, not edits made while it was in flight.
      if (typeof renderPromptSettings === "function") renderPromptSettings(state.prompts);
      settingsDirty = true;
      updateAgentProtocolExample();
    }
    if (typeof refreshModelDiagnosticsSettings === "function") refreshModelDiagnosticsSettings();
    if (typeof renderChatModePicker === "function") renderChatModePicker();
    renderModelControls();
    if ((javascriptWasEnabled !== nextSettings.EnableAgentJavaScript || messagingWasEnabled !== (nextSettings.MaxCollaborationAgents > 0)) &&
        typeof applyLibraryCatalogState === "function") {
      try {
        applyLibraryCatalogState({ tools: await send("getTools", {}) });
      } catch (error) {
        log("Настройка сохранена, но список инструментов не обновился: " + error.message, "error");
      }
    }
    renderContextMeter();
    return state.settings;
  } finally {
    settingsSaveInProgress = false;
    updateSettingsSaveButton();
  }
}

function compatibilityValue(source, pascal, camel, fallback) {
  source = source || {};
  return source[pascal] !== undefined ? source[pascal] : (source[camel] !== undefined ? source[camel] : fallback);
}

function markSettingsDirty() {
  settingsFormRevision++;
  settingsDirty = true;
  updateSettingsSaveButton();
}

function resetModelSettingsToDefaults() {
  var settings = settingsModelDraft;
  settings.ModelCapabilities = {};
  settings.ModelImageSupportOverrides = {};
  settings.ModelAudioSupportOverrides = {};
  settings.AttachmentModelPriority = [];
  $("modelInput").value = modelSettingsDefaults.model;
  $("reasoningRequestModeInput").value = modelSettingsDefaults.reasoningRequestMode;
  $("reasoningCustomJsonInput").value = modelSettingsDefaults.reasoningCustomJson;
  $("maxTokensInput").value = modelSettingsDefaults.maxTokens;
  $("requestTimeoutInput").value = modelSettingsDefaults.requestTimeoutSeconds;
  $("temperatureInput").value = modelSettingsDefaults.temperature;
  $("topPInput").value = modelSettingsDefaults.topP;
  $("contextLimitInput").value = modelSettingsDefaults.contextWindowOverrideTokens;
  renderContextWindowSummary();
  $("attachmentHelperMaxTokensInput").value = modelSettingsDefaults.attachmentHelperMaxTokens;
  $("attachmentEvidenceMaxTokensInput").value = modelSettingsDefaults.attachmentEvidenceMaxTokens;
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
  $("autoConfirmToolActionsInput").checked = agentSettingsDefaults.autoConfirmToolActions;
  $("enableAgentJavaScriptInput").checked = agentSettingsDefaults.enableAgentJavaScript;
  $("autoCompressContextInput").checked = agentSettingsDefaults.autoCompressContext;
  $("maxAgentIterationsInput").value = agentSettingsDefaults.maxAgentIterations;
  $("maxAgentFormatRetriesInput").value = agentSettingsDefaults.maxAgentFormatRetries;
  $("maxConsecutiveReflectionsInput").value = agentSettingsDefaults.maxConsecutiveReflections;
  $("maxCollaborationAgentsInput").value = agentSettingsDefaults.maxCollaborationAgents;
  $("maxConcurrentAgentStepsInput").value = agentSettingsDefaults.maxConcurrentAgentSteps;
  $("maxAgentToolStepsInput").value = agentSettingsDefaults.maxAgentToolSteps;
  updateAgentProtocolControls();
  markSettingsDirty();
}


function bindSettingsActions() {
  RNAssistantDeploymentConfig.bind();
  var navSearch = $("settingsNavSearch");
  var navRoot = $("settingsNavTree");
  var navItems = [
    ["connection", "Подключение"], ["model", "Модель"], ["context", "Контекст и вложения"],
    ["agent", "Агент"], ["interface", "Интерфейс"], ["protection", "Защита"]
  ];

  function selectSettingsPage(page) {
    if (page !== "connection") RNAssistantDeploymentConfig.lock();
    Array.prototype.slice.call(document.querySelectorAll(".settings-page")).forEach(function (item) {
      item.classList.toggle("active", item.getAttribute("data-settings-page") === page);
    });
    var pages = document.querySelector("#settingsView .settings-pages");
    if (pages) pages.scrollTop = 0;
    renderSettingsNavigation();
    updateSettingsSaveButton();
    loadSettingsModelCatalogIfNeeded();
  }

  function renderSettingsNavigation() {
    var query = navSearch.value.trim().toLocaleLowerCase("ru");
    var active = document.querySelector(".settings-page.active");
    var selectedPage = active && active.getAttribute("data-settings-page");
    $("settingsPageSelect").value = selectedPage;
    var nodes = navItems.filter(function (item) { return !query || item[1].toLocaleLowerCase("ru").includes(query); })
      .map(function (item) {
        return {
          key: "settings:" + item[0], kind: "leaf", title: item[1],
          iconHtml: window.RNAssistantNavigationTree.icon(item[0]), selected: item[0] === selectedPage,
          page: item[0]
        };
      });
    window.RNAssistantNavigationTree.render(navRoot, nodes, {
      label: "Настройки", onSelect: function (node) { selectSettingsPage(node.page); }
    });
  }

  navItems.forEach(function (item) {
    var option = document.createElement("option"); option.value = item[0]; option.textContent = item[1];
    $("settingsPageSelect").appendChild(option);
  });
  $("settingsPageSelect").addEventListener("change", function () { selectSettingsPage(this.value); });

  var railPopovers = window.RNAssistantRailPopovers;
  if (railPopovers) {
    railPopovers.register("settings-menu", function (_anchor, body) {
      var version = document.createElement("span");
      var fullVersion = String(state.appVersion || "").trim();
      version.className = "rail-hover-version";
      version.textContent = "Версия " + formatAppVersionLabel(fullVersion);
      if (fullVersion) version.title = "Сборка: " + fullVersion;
      navItems.forEach(function (item) {
        body.appendChild(railPopovers.action(item[1], window.RNAssistantNavigationTree.icon(item[0]), function () {
          railPopovers.hide();
          switchTab("settings");
          selectSettingsPage(item[0]);
        }));
      });
      var separator = document.createElement("span");
      separator.className = "rail-hover-separator";
      separator.setAttribute("role", "separator");
      body.appendChild(separator);
      body.appendChild(version);
    });
  }

  navSearch.addEventListener("input", renderSettingsNavigation);

  renderSettingsNavigation();

  $("modelCapabilitySearch").addEventListener("input", renderModelCapabilityList);
  $("contextLimitInput").addEventListener("input", renderContextWindowSummary);

  $("uiFontScaleInput").addEventListener("input", function () {
    applyUiFontScale({ UiFontScale: Number($("uiFontScaleInput").value || 100) / 100 });
  });

  $("reasoningRequestModeInput").addEventListener("change", updateReasoningCustomJsonVisibility);
  $("agentResponseModeInput").addEventListener("change", updateAgentProtocolControls);
  $("systemPromptRoleInput").addEventListener("change", updateAgentProtocolExample);
  $("agentProtocolExample").addEventListener("toggle", updateAgentProtocolExample);
  $("historyHmacInput").addEventListener("change", updateHistoryProtectionControls);
  $("historyEncryptionInput").addEventListener("change", updateHistoryProtectionControls);
  $("historyKeySourceInput").addEventListener("change", updateHistoryProtectionControls);
  $("tokenEstimateMultiplierInput").addEventListener("input", function () {
    renderTokenEstimateCalibrationStatus(modelSettingsFormContext().settings);
  });
  $("autoCalibrateTokenEstimateInput").addEventListener("change", function () {
    renderTokenEstimateCalibrationStatus(modelSettingsFormContext().settings);
  });
  $("modelInput").addEventListener("input", function () {
    renderTokenEstimateCalibrationStatus(modelSettingsFormContext().settings);
  });
  $("resetModelSettingsButton").addEventListener("click", resetModelSettingsToDefaults);
  $("resetAgentSettingsButton").addEventListener("click", resetAgentSettingsToDefaults);

  Array.prototype.slice.call(document.querySelectorAll('input[name="uiTheme"]')).forEach(function (input) {
    input.addEventListener("change", function () {
      if (input.checked) {
        applyUiTheme({ UiTheme: input.value }, false);
        renderUiAccentChoices(document.documentElement.getAttribute("data-accent"));
      }
    });
  });

  $("uiAccentChoices").addEventListener("change", function (event) {
    if (event.target && event.target.name === "uiAccent") {
      window.RNAssistantAccents.apply(event.target.value, false);
      markSettingsDirty();
    }
  });

  Array.prototype.slice.call(document.querySelectorAll("#settingsView input, #settingsView textarea, #settingsView select")).forEach(function (control) {
    control.addEventListener(control.type === "checkbox" || control.tagName === "SELECT" ? "change" : "input", function () {
      if (control.closest(".diagnostics-tab-panel, [data-deployment-config]")) return;
      if (control.id === "promptSearchInput" || control.id === "settingsNavSearch" || control.id === "modelCapabilitySearch" || control.id === "settingsPageSelect") {
        return;
      }
      markSettingsDirty();
    });
  });

  $("saveSettingsButton").addEventListener("click", async function () {
    var button = $("saveSettingsButton");
    try {
      button.disabled = true;
      await persistSettingsFromForm();
      log("Настройки сохранены.");
      showChatCopyFeedback(settingsDirty ? "Сохранено. В форме остались новые неприменённые правки." :
        "Настройки сохранены. Следующий запуск использует их без перезапуска приложения.");
      loadModelCatalog(false);
    } catch (error) {
      settingsDirty = true;
      updateSettingsSaveButton();
      log(error.message, "error");
      showChatCopyFeedback("Настройки не сохранены: " + error.message, true);
    }
  });
  $("openModelLabFromSettings").addEventListener("click", function () { RNAssistantDiagnostics.open("model-lab"); });
  $("clearRuntimeDataButton").addEventListener("click", clearRuntimeData);
  if (typeof bindPromptSettingsActions === "function") {
    bindPromptSettingsActions();
  }
}

function updateSettingsSaveButton() {
  var row = document.querySelector(".settings-actions-row");
  var button = $("saveSettingsButton");
  var visible = !!settingsDirty;
  if (!row || !button) {
    return;
  }

  $("settingsSaveStatus").textContent = settingsSaveInProgress ? "Применение настроек…" : "Есть неприменённые изменения";
  row.hidden = !visible;
  button.hidden = !visible;
  button.disabled = !visible || settingsSaveInProgress;
}
