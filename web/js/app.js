(function () {
  var state = {
    host: "",
    title: "",
    settings: {},
    tools: [],
    context: {},
    contextUsage: {},
    chats: [],
    activeChatId: "",
    activeChatModel: "",
    messages: [],
    failedSend: null,
    modelCatalog: { configUrl: "", defaultModel: "", models: [], loaded: false, loading: false, error: "" },
    modelSaving: false,
    selectedToolIndex: -1,
    toolsPath: "",
    vba: { modules: [], backups: [], selectedModule: "" },
    activity: { visible: false, phase: "", message: "" },
    pending: {},
    seq: 1,
    focusReportTimer: null,
    highlightLog: {},
    highlightRetryScheduled: false,
    highlightRetryAttempts: 0,
    highlightLoadLogged: false
  };

  function $(id) {
    return document.getElementById(id);
  }

  window.RNAssistantHost = {
    blurComposer: function () {
      var active = document.activeElement;
      var chatInput = $("chatInput");
      if (chatInput) {
        chatInput.blur();
      }
      if (active && active !== document.body && typeof active.blur === "function") {
        active.blur();
      }
    },
    refreshContext: function () {
      refreshContext();
    },
    runQuickAction: function (action) {
      runQuickAction(action);
    }
  };

  function log(message) {
    var box = $("logBox");
    if (!box) {
      return;
    }
    var line = new Date().toISOString() + " " + message;
    box.textContent += line + "\n";
    box.scrollTop = box.scrollHeight;
  }

  function logOnce(message) {
    if (state.highlightLog[message]) {
      return;
    }
    state.highlightLog[message] = true;
    log(message);
  }

  function setActivity(phase, message) {
    var status = $("activityStatus");
    var text = $("activityText");
    if (!status || !text) {
      return;
    }

    state.activity = { visible: true, phase: phase || "working", message: message || "Working..." };
    status.classList.remove("hidden");
    status.dataset.phase = state.activity.phase;
    text.textContent = state.activity.message;
  }

  function clearActivity() {
    var status = $("activityStatus");
    if (!status) {
      return;
    }

    state.activity = { visible: false, phase: "", message: "" };
    status.classList.add("hidden");
    status.removeAttribute("data-phase");
  }

  function showHelp() {
    var modal = $("helpModal");
    if (modal) {
      modal.classList.remove("hidden");
    }
  }

  function hideHelp() {
    var modal = $("helpModal");
    if (modal) {
      modal.classList.add("hidden");
    }
  }

  function send(type, payload) {
    return new Promise(function (resolve, reject) {
      var id = String(state.seq++);
      state.pending[id] = { resolve: resolve, reject: reject };
      window.chrome.webview.postMessage({ id: id, type: type, payload: payload || {} });
    });
  }

  function isKeyboardElement(element) {
    if (!element) {
      return false;
    }

    var tag = (element.tagName || "").toLowerCase();
    if (element.isContentEditable || tag === "textarea" || tag === "select") {
      return true;
    }

    if (tag !== "input") {
      return false;
    }

    return ["button", "checkbox", "color", "file", "hidden", "image", "radio", "range", "reset", "submit"].indexOf((element.type || "text").toLowerCase()) === -1;
  }

  function reportFocusState() {
    if (!window.chrome || !window.chrome.webview) {
      return;
    }

    var selection = window.getSelection ? window.getSelection() : null;
    var hasSelection = !!(selection && !selection.isCollapsed && String(selection).length > 0);
    window.chrome.webview.postMessage({
      type: "focusState",
      payload: {
        wantsKeyboard: document.hasFocus() && (isKeyboardElement(document.activeElement) || hasSelection)
      }
    });
  }

  function scheduleFocusStateReport() {
    if (state.focusReportTimer) {
      window.clearTimeout(state.focusReportTimer);
    }

    state.focusReportTimer = window.setTimeout(reportFocusState, 0);
  }

  window.chrome.webview.addEventListener("message", function (event) {
    var response = event.data;
    if (typeof response === "string") {
      response = JSON.parse(response);
    }
    if (response && response.type === "progress") {
      var progress = response.payload || {};
      setActivity(progress.phase || "working", progress.message || "Working...");
      log("[" + (progress.phase || "working") + "] " + (progress.message || "Working..."));
      return;
    }
    var pending = state.pending[response.id];
    if (!pending) {
      return;
    }
    delete state.pending[response.id];
    if (response.ok) {
      pending.resolve(response.payload);
    } else {
      var error = new Error(response.error || "Bridge error");
      error.detail = response.errorDetail || response.error || "";
      pending.reject(error);
    }
  });

  function markdown(text) {
    return DOMPurify.sanitize(marked.parse(text || ""));
  }

  function messageValue(message, pascal, camel, fallback) {
    message = message || {};
    return message[pascal] !== undefined ? message[pascal] : (message[camel] !== undefined ? message[camel] : fallback);
  }

  function messageId(message) {
    return messageValue(message, "Id", "id", "");
  }

  function messageRole(message) {
    return messageValue(message, "Role", "role", "assistant") || "assistant";
  }

  function messageContent(message) {
    return messageValue(message, "Content", "content", "") || "";
  }

  function messageTotalTokens(message) {
    return messageValue(message, "TotalTokens", "totalTokens", null);
  }

  function messagePromptTokens(message) {
    return messageValue(message, "PromptTokens", "promptTokens", null);
  }

  function messageCompletionTokens(message) {
    return messageValue(message, "CompletionTokens", "completionTokens", null);
  }

  function chatValue(chat, pascal, camel, fallback) {
    chat = chat || {};
    return chat[pascal] !== undefined ? chat[pascal] : (chat[camel] !== undefined ? chat[camel] : fallback);
  }

  function chatId(chat) {
    return chatValue(chat, "Id", "id", "");
  }

  function chatTitle(chat) {
    return chatValue(chat, "Title", "title", "New chat") || "New chat";
  }

  function chatMessageCount(chat) {
    return Number(chatValue(chat, "MessageCount", "messageCount", 0) || 0);
  }

  function chatModel(chat) {
    return chatValue(chat, "Model", "model", "") || "";
  }

  function renderChatSessions() {
    var select = $("chatSessionSelect");
    if (!select) {
      return;
    }

    select.innerHTML = "";
    (state.chats || []).forEach(function (chat) {
      var option = document.createElement("option");
      option.value = chatId(chat);
      var model = chatModel(chat);
      option.textContent = chatTitle(chat) + " (" + chatMessageCount(chat) + ")" + (model ? " - " + model : "");
      select.appendChild(option);
    });
    select.value = state.activeChatId || "";

    var hasActive = !!state.activeChatId;
    $("renameChatButton").disabled = !hasActive;
    $("clearChatButton").disabled = !hasActive || !state.messages.length;
    $("deleteChatButton").disabled = !hasActive;
  }

  function applyChatState(response) {
    response = response || {};
    state.activeChatId = response.activeChatId || response.ActiveChatId || state.activeChatId || "";
    if (response.activeChatModel !== undefined || response.ActiveChatModel !== undefined) {
      state.activeChatModel = response.activeChatModel || response.ActiveChatModel || "";
    }
    state.chats = response.chats || response.Chats || state.chats || [];
    if (response.context || response.Context) {
      state.context = response.context || response.Context || {};
    }
    state.messages = response.messages || response.Messages || [];
    state.contextUsage = response.contextUsage || response.ContextUsage || state.contextUsage || {};
    renderChatSessions();
    renderMessages();
    renderContext(true);
    renderContextMeter();
    renderModelControls();
  }

  function iconSvg(name) {
    var icons = {
      copy: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><rect x=\"9\" y=\"9\" width=\"13\" height=\"13\" rx=\"2\"/><path d=\"M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1\"/></svg>",
      trash: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M3 6h18\"/><path d=\"M8 6V4h8v2\"/><path d=\"M19 6l-1 14H6L5 6\"/><path d=\"M10 11v5\"/><path d=\"M14 11v5\"/></svg>",
      branch: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><circle cx=\"6\" cy=\"6\" r=\"3\"/><circle cx=\"18\" cy=\"6\" r=\"3\"/><circle cx=\"18\" cy=\"18\" r=\"3\"/><path d=\"M9 6h3a6 6 0 0 1 6 6v3\"/><path d=\"M6 9v9\"/></svg>",
      retry: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M20 6v5h-5\"/><path d=\"M4 18v-5h5\"/><path d=\"M6.1 9A7 7 0 0 1 18.2 6.8L20 11\"/><path d=\"M17.9 15A7 7 0 0 1 5.8 17.2L4 13\"/></svg>",
      eye: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M2 12s3.5-6 10-6 10 6 10 6-3.5 6-10 6-10-6-10-6Z\"/><circle cx=\"12\" cy=\"12\" r=\"3\"/></svg>",
      eyeOff: "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M3 3l18 18\"/><path d=\"M10.6 10.6A3 3 0 0 0 13.4 13.4\"/><path d=\"M9.9 5.2A10.8 10.8 0 0 1 12 5c6.5 0 10 7 10 7a17.9 17.9 0 0 1-3.2 4.2\"/><path d=\"M6.1 6.6C3.4 8.4 2 12 2 12s3.5 7 10 7a10.6 10.6 0 0 0 4.1-.8\"/></svg>"
    };
    return icons[name] || "";
  }

  function smallIconButton(title, icon, onClick) {
    var button = document.createElement("button");
    button.type = "button";
    button.className = "message-action";
    button.title = title;
    button.setAttribute("aria-label", title);
    button.innerHTML = iconSvg(icon);
    button.addEventListener("click", onClick);
    return button;
  }

  function messageUsageText(message) {
    var total = messageTotalTokens(message);
    var prompt = messagePromptTokens(message);
    var completion = messageCompletionTokens(message);
    if (total === null && prompt === null && completion === null) {
      return "";
    }

    var parts = [];
    if (total !== null && total !== undefined) {
      parts.push(total + " tokens");
    }
    if (prompt !== null && prompt !== undefined) {
      parts.push("in " + prompt);
    }
    if (completion !== null && completion !== undefined) {
      parts.push("out " + completion);
    }
    return parts.join(" · ");
  }

  function renderLatex(root) {
    if (!window.renderMathInElement) {
      return;
    }

    try {
      window.renderMathInElement(root, {
        delimiters: [
          { left: "$$", right: "$$", display: true },
          { left: "\\[", right: "\\]", display: true },
          { left: "\\(", right: "\\)", display: false },
          { left: "$", right: "$", display: false }
        ],
        ignoredTags: ["script", "noscript", "style", "textarea", "pre", "code"],
        throwOnError: false
      });
    } catch (error) {
      logOnce("LaTeX render failed: " + (error.message || error));
    }
  }

  function renderMessages() {
    var box = $("messages");
    box.innerHTML = "";
    state.messages.forEach(function (message, index) {
      var node = document.createElement("article");
      node.className = "message " + messageRole(message) + (message.Pending ? " pending" : "") + (message.Failed ? " failed" : "");

      var body = document.createElement("div");
      body.className = "markdown";
      body.innerHTML = markdown(messageContent(message));
      node.appendChild(body);

      var footer = document.createElement("div");
      footer.className = "message-footer";

      var meta = document.createElement("div");
      meta.className = "message-footer-meta";

      var role = document.createElement("span");
      role.className = "role";
      role.textContent = messageRole(message);
      meta.appendChild(role);

      var usage = messageUsageText(message);
      if (usage || message.Pending || message.Failed) {
        var usageNode = document.createElement("span");
        usageNode.className = "message-usage";
        usageNode.textContent = message.Failed ? "Not sent" : (message.Pending ? "Sending..." : usage);
        meta.appendChild(usageNode);
      }

      var actions = document.createElement("div");
      actions.className = "message-actions";
      actions.appendChild(smallIconButton("Fork from this message", "branch", function () {
        forkChatAtMessage(message, index);
      }));
      actions.appendChild(smallIconButton("Copy message", "copy", function () {
        copyText(messageContent(message));
        log("Message copied.");
      }));
      actions.appendChild(smallIconButton("Delete message", "trash", function () {
        deleteMessage(message, index);
      }));

      footer.appendChild(meta);
      footer.appendChild(actions);
      node.appendChild(footer);

      box.appendChild(node);
      enhanceMarkdown(body);
    });
    box.scrollTop = box.scrollHeight;
  }

  function codePreviewText(code, pre) {
    var raw = code ? code.textContent : (pre ? pre.innerText : "");
    var lines = String(raw || "").replace(/\r\n/g, "\n").split("\n");
    var useful = [];
    lines.forEach(function (line) {
      var text = line.trim();
      if (text) {
        useful.push(text);
      }
    });

    if (!useful.length) {
      return "Code block";
    }

    var preview = useful[0];
    if ((preview === "{" || preview === "[" || preview.length < 8) && useful.length > 1) {
      preview += " " + useful[1];
    }
    if (preview.length > 180) {
      preview = preview.substring(0, 177) + "...";
    }
    return preview;
  }

  function enhanceMarkdown(root) {
    Array.prototype.slice.call(root.querySelectorAll("pre code")).forEach(function (code) {
      highlightCode(code);
    });

    Array.prototype.slice.call(root.querySelectorAll("pre")).forEach(function (pre) {
      if (pre.parentNode.classList.contains("code-wrap")) {
        return;
      }
      var wrap = document.createElement("div");
      wrap.className = "code-wrap";
      var tools = document.createElement("div");
      tools.className = "block-tools";
      var code = pre.querySelector("code");
      if (code && code.dataset.language) {
        var language = document.createElement("span");
        language.className = "code-lang";
        language.textContent = code.dataset.language;
        tools.appendChild(language);
      }
      var preview = document.createElement("div");
      preview.className = "code-preview";
      preview.textContent = codePreviewText(code, pre);
      var toggle = document.createElement("button");
      toggle.type = "button";
      toggle.className = "block-tool-button";
      toggle.innerHTML = iconSvg("eye") + "<span>Show</span>";
      toggle.addEventListener("click", function () {
        var hidden = pre.style.display === "none";
        pre.style.display = hidden ? "" : "none";
        preview.style.display = hidden ? "none" : "";
        toggle.innerHTML = iconSvg(hidden ? "eyeOff" : "eye") + "<span>" + (hidden ? "Hide" : "Show") + "</span>";
      });
      var copy = document.createElement("button");
      copy.type = "button";
      copy.className = "block-tool-button";
      copy.innerHTML = iconSvg("copy") + "<span>Copy</span>";
      copy.addEventListener("click", function () {
        copyText(code ? code.textContent : pre.innerText);
      });
      tools.appendChild(toggle);
      tools.appendChild(copy);
      pre.parentNode.insertBefore(wrap, pre);
      wrap.appendChild(tools);
      wrap.appendChild(preview);
      wrap.appendChild(pre);
      pre.style.display = "none";
    });

    Array.prototype.slice.call(root.querySelectorAll("table")).forEach(function (table) {
      if (table.parentNode.classList.contains("table-wrap")) {
        return;
      }
      var wrap = document.createElement("div");
      wrap.className = "table-wrap";
      var tools = document.createElement("div");
      tools.className = "block-tools";
      var toggle = document.createElement("button");
      toggle.type = "button";
      toggle.className = "block-tool-button";
      toggle.innerHTML = iconSvg("eyeOff") + "<span>Hide</span>";
      toggle.addEventListener("click", function () {
        var hidden = table.style.display === "none";
        table.style.display = hidden ? "" : "none";
        toggle.innerHTML = iconSvg(hidden ? "eyeOff" : "eye") + "<span>" + (hidden ? "Hide" : "Show") + "</span>";
      });
      var copy = document.createElement("button");
      copy.type = "button";
      copy.className = "block-tool-button";
      copy.innerHTML = iconSvg("copy") + "<span>Copy</span>";
      copy.addEventListener("click", function () {
        copyText(table.innerText);
      });
      tools.appendChild(toggle);
      tools.appendChild(copy);
      table.parentNode.insertBefore(wrap, table);
      wrap.appendChild(tools);
      wrap.appendChild(table);
    });

    renderLatex(root);
  }

  function detectCodeLanguage(code) {
    var classes = (code.className || "").split(/\s+/);
    for (var i = 0; i < classes.length; i++) {
      if (classes[i].indexOf("language-") === 0) {
        return classes[i].substring("language-".length);
      }
      if (classes[i].indexOf("lang-") === 0) {
        return classes[i].substring("lang-".length);
      }
    }
    return "";
  }

  function normalizeCodeLanguage(language) {
    var value = (language || "").toLowerCase();
    var aliases = {
      "c#": "csharp",
      "cs": "csharp",
      "js": "javascript",
      "ts": "typescript",
      "py": "python",
      "ps": "powershell",
      "ps1": "powershell",
      "vb": "vbnet",
      "vba": "vbnet"
    };
    return aliases[value] || value;
  }

  function highlightCode(code) {
    var text = code.textContent || "";
    var requestedLanguage = detectCodeLanguage(code);
    var language = normalizeCodeLanguage(requestedLanguage);

    code.classList.add("hljs");
    if (!window.hljs) {
      code.dataset.language = language || "plaintext";
      scheduleHighlightRetry();
      return;
    }

    state.highlightRetryAttempts = 0;
    try {
      var result;
      if (language && window.hljs.getLanguage && window.hljs.getLanguage(language)) {
        result = window.hljs.highlight(text, { language: language, ignoreIllegals: true });
      } else if (window.hljs.highlightAuto) {
        if (language) {
          logOnce("Highlight language is not bundled: " + requestedLanguage + "; using auto-detect.");
        }
        result = window.hljs.highlightAuto(text);
        language = result.language || "plaintext";
      }

      if (result && result.value) {
        code.innerHTML = result.value;
      }
      code.dataset.language = language || "plaintext";
      code.classList.add("language-" + code.dataset.language);
      logOnce("Highlighted code as " + code.dataset.language + (requestedLanguage ? " from " + requestedLanguage : " by auto-detect") + ".");
    } catch (error) {
      code.textContent = text;
      code.dataset.language = "plaintext";
      code.classList.add("language-plaintext");
      logOnce("Highlight failed: " + (error.message || error));
    }
  }

  function highlightAllCode() {
    Array.prototype.slice.call(document.querySelectorAll(".markdown pre code")).forEach(function (code) {
      highlightCode(code);
    });
  }

  function scheduleHighlightRetry() {
    if (state.highlightRetryScheduled || state.highlightLoadLogged) {
      return;
    }

    state.highlightRetryScheduled = true;
    window.setTimeout(function () {
      state.highlightRetryScheduled = false;
      if (window.hljs) {
        highlightAllCode();
        return;
      }

      state.highlightRetryAttempts += 1;
      if (state.highlightRetryAttempts < 3) {
        scheduleHighlightRetry();
        return;
      }

      state.highlightLoadLogged = true;
      logOnce("Highlight.js was not loaded from js/vendor/highlight.min.js; code is shown without syntax colors.");
    }, 150);
  }

  function copyText(text) {
    if (navigator.clipboard) {
      navigator.clipboard.writeText(text);
    } else {
      var input = document.createElement("textarea");
      input.value = text;
      document.body.appendChild(input);
      input.select();
      document.execCommand("copy");
      document.body.removeChild(input);
    }
  }

  function switchTab(name) {
    Array.prototype.slice.call(document.querySelectorAll(".tab")).forEach(function (tab) {
      tab.classList.toggle("active", tab.dataset.tab === name);
    });
    Array.prototype.slice.call(document.querySelectorAll(".panel")).forEach(function (panel) {
      panel.classList.toggle("active", panel.id === "tab-" + name);
    });
  }

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

  function headersToText(headers) {
    return Object.keys(headers).map(function (key) {
      return key + ": " + headers[key];
    }).join("\n");
  }

  function textToHeaders(text) {
    var headers = {};
    (text || "").split(/\r?\n/).forEach(function (line) {
      var index = line.indexOf(":");
      if (index > 0) {
        headers[line.slice(0, index).trim()] = line.slice(index + 1).trim();
      }
    });
    return headers;
  }

  function renderTools() {
    var list = $("toolsList");
    list.innerHTML = "";
    if (!state.tools.length) {
      state.selectedToolIndex = -1;
      renderToolEditor();
      return;
    }

    if (state.selectedToolIndex < 0 || state.selectedToolIndex >= state.tools.length) {
      state.selectedToolIndex = 0;
    }

    state.tools.forEach(function (skill, index) {
      var item = document.createElement("button");
      item.type = "button";
      item.className = "tool-list-item" + (index === state.selectedToolIndex ? " active" : "");
      item.innerHTML = "<div class=\"tool-list-title\"></div><div class=\"tool-list-meta\"></div>";
      item.querySelector(".tool-list-title").textContent = skill.Id || skill.Name || "tool";
      item.querySelector(".tool-list-meta").textContent = (skill.Host || "Common") + " - " + (skill.Executor || (skill.BuiltIn ? "builtin" : "pipeline"));
      item.addEventListener("click", function () {
        syncSelectedToolFromEditor();
        state.selectedToolIndex = index;
        renderTools();
      });
      list.appendChild(item);
    });

    renderToolEditor();
  }

  function renderToolEditor() {
    var skill = state.tools[state.selectedToolIndex] || null;
    var disabled = !skill;
    var builtIn = !!(skill && skill.BuiltIn);
    $("toolEditorTitle").textContent = skill ? (skill.Id || "tool") : "No tool selected";
    $("toolEditorMeta").textContent = skill ? (builtIn ? "Built-in tool" : (skill.StoragePath || "Custom tool")) : "";
    $("toolEnabledInput").checked = skill ? skill.Enabled !== false : false;
    $("toolIdInput").value = skill ? (skill.Id || "") : "";
    $("toolHostInput").value = skill ? (skill.Host || "Common") : "Common";
    $("toolExecutorInput").value = skill ? (skill.Executor || (builtIn ? "builtin" : "pipeline")) : "pipeline";
    $("toolConfirmInput").checked = skill ? !!skill.RequiresConfirmation : false;
    $("toolDescriptionInput").value = skill ? (skill.Description || "") : "";
    $("toolSchemaInput").value = skill ? (skill.ArgumentSchemaJson || "{}") : "{}";
    $("toolRunArgsInput").value = skill ? "{}" : "";
    $("toolPipelineInput").value = skill ? (skill.PipelineJson || "") : "";
    $("toolCodeInput").value = skill ? (skill.Code || "") : "";
    $("toolReadmeInput").value = skill ? (skill.Readme || "") : "";
    $("toolRunOutput").textContent = "";

    [
      "toolEnabledInput",
      "toolIdInput",
      "toolHostInput",
      "toolExecutorInput",
      "toolConfirmInput",
      "toolDescriptionInput",
      "toolSchemaInput",
      "toolRunArgsInput",
      "toolPipelineInput",
      "toolCodeInput",
      "toolReadmeInput"
    ].forEach(function (id) {
      $(id).disabled = disabled || builtIn;
    });
    $("toolRunArgsInput").disabled = disabled;

    $("deleteToolButton").disabled = disabled || builtIn;
    $("dryRunToolButton").disabled = disabled;
    $("runToolButton").disabled = disabled;
    $("cloneToolButton").disabled = disabled;
    $("copyToolContextButton").disabled = disabled;
    $("askToolBuilderButton").disabled = disabled;
  }

  function syncSelectedToolFromEditor() {
    var skill = state.tools[state.selectedToolIndex];
    if (!skill || skill.BuiltIn) {
      return;
    }

    skill.Id = $("toolIdInput").value.trim();
    skill.Host = $("toolHostInput").value;
    skill.Name = skill.Id;
    skill.Executor = $("toolExecutorInput").value;
    skill.RequiresConfirmation = $("toolConfirmInput").checked;
    skill.Description = $("toolDescriptionInput").value;
    skill.ArgumentSchemaJson = $("toolSchemaInput").value || "{}";
    skill.PipelineJson = $("toolPipelineInput").value;
    skill.Code = $("toolCodeInput").value;
    skill.Readme = $("toolReadmeInput").value;
    skill.Enabled = $("toolEnabledInput").checked;
    skill.BuiltIn = false;
  }

  function readTools() {
    syncSelectedToolFromEditor();
    return state.tools.map(function (skill) {
      return {
        Id: skill.Id || "",
        Host: skill.Host || "Common",
        Name: skill.Name || skill.Id || "",
        Description: skill.Description || "",
        ArgumentSchemaJson: skill.ArgumentSchemaJson || "{}",
        Executor: skill.Executor || (skill.BuiltIn ? "builtin" : "pipeline"),
        RequiresConfirmation: !!skill.RequiresConfirmation,
        PipelineJson: skill.PipelineJson || "",
        Code: skill.Code || "",
        Readme: skill.Readme || "",
        Enabled: skill.Enabled !== false,
        BuiltIn: !!skill.BuiltIn
      };
    });
  }

  function selectedToolContext() {
    syncSelectedToolFromEditor();
    var skill = state.tools[state.selectedToolIndex];
    if (!skill) {
      return "";
    }

    return [
      "# Tool",
      "id: " + (skill.Id || ""),
      "host: " + (skill.Host || "Common"),
      "executor: " + (skill.Executor || "pipeline"),
      "requiresConfirmation: " + (!!skill.RequiresConfirmation),
      "",
      "## Description",
      skill.Description || "",
      "",
      "## Argument schema",
      "```json",
      skill.ArgumentSchemaJson || "{}",
      "```",
      "",
      "## Pipeline",
      "```json",
      skill.PipelineJson || "",
      "```",
      "",
      "## Code",
      "```vba",
      skill.Code || "",
      "```",
      "",
      "## README",
      skill.Readme || ""
    ].join("\n");
  }

  function parseRunArguments() {
    var text = $("toolRunArgsInput").value.trim();
    if (!text) {
      return {};
    }

    return JSON.parse(text);
  }

  async function runSelectedTool(dryRun) {
    syncSelectedToolFromEditor();
    var skill = state.tools[state.selectedToolIndex];
    if (!skill) {
      return;
    }

    setActivity(dryRun ? "checking" : "executing", dryRun ? "Проверяю tool..." : "Исполняю tool...");
    $("toolRunOutput").textContent = dryRun ? "Dry run..." : "Running...";
    try {
      var response = await send("runTool", {
        toolId: skill.Id,
        arguments: parseRunArguments(),
        dryRun: !!dryRun
      });
      $("toolRunOutput").textContent = JSON.stringify(response, null, 2);
      logToolResult(dryRun ? "Dry run" : "Tool run", skill.Id, response);
    } catch (error) {
      $("toolRunOutput").textContent = error.detail || error.message;
      log(error.message);
    } finally {
      clearActivity();
    }
  }

  function renderVbaProject() {
    var moduleSelect = $("vbaModuleSelect");
    var backupSelect = $("vbaBackupSelect");
    moduleSelect.innerHTML = "";
    backupSelect.innerHTML = "";

    state.vba.modules.forEach(function (module) {
      var option = document.createElement("option");
      option.value = module.name || module.Name || "";
      option.textContent = option.value + " (" + (module.type || module.Type || "module") + ")";
      moduleSelect.appendChild(option);
    });

    state.vba.backups.forEach(function (backup) {
      var option = document.createElement("option");
      option.value = backup.BackupId || backup.backupId || "";
      option.textContent = (backup.ModuleName || backup.moduleName || "module") + " - " + (backup.CreatedUtc || backup.createdUtc || "");
      backupSelect.appendChild(option);
    });

    if (state.vba.selectedModule) {
      moduleSelect.value = state.vba.selectedModule;
    }
    renderSelectedVbaModule();
  }

  function renderSelectedVbaModule() {
    var module = selectedVbaModule();
    state.vba.selectedModule = vbaModuleName(module);
    $("vbaCodeInput").value = module ? vbaModuleCode(module) : "";
    renderVbaCodePreview();
    $("vbaMetaBox").textContent = module ? JSON.stringify({
      name: vbaModuleName(module),
      type: module.type || module.Type,
      lineCount: module.lineCount || module.LineCount
    }, null, 2) : "";
    $("vbaDiffOutput").textContent = "";
  }

  function selectedVbaModule() {
    var selectedName = $("vbaModuleSelect").value;
    var found = null;
    state.vba.modules.forEach(function (item) {
      if ((item.name || item.Name) === selectedName) {
        found = item;
      }
    });
    return found;
  }

  function vbaModuleName(module) {
    return module ? (module.name || module.Name || "") : "";
  }

  function vbaModuleCode(module) {
    return module ? (module.code || module.Code || "") : "";
  }

  function renderVbaCodePreview() {
    var preview = $("vbaCodePreview");
    if (!preview) {
      return;
    }

    preview.innerHTML = "";
    var codeText = $("vbaCodeInput").value || "";
    if (!codeText.trim()) {
      var empty = document.createElement("div");
      empty.className = "vba-code-empty";
      empty.textContent = "No VBA code loaded.";
      preview.appendChild(empty);
      return;
    }

    var tools = document.createElement("div");
    tools.className = "block-tools vba-preview-tools";
    var language = document.createElement("span");
    language.className = "code-lang";
    language.textContent = "vba";
    tools.appendChild(language);

    var pre = document.createElement("pre");
    var code = document.createElement("code");
    code.className = "language-vba";
    code.textContent = codeText;
    pre.appendChild(code);
    preview.appendChild(tools);
    preview.appendChild(pre);
    highlightCode(code);
  }

  function formatVbaDiff(before, after) {
    if (before === after) {
      return "No changes.";
    }

    var oldLines = String(before || "").replace(/\r\n/g, "\n").split("\n");
    var newLines = String(after || "").replace(/\r\n/g, "\n").split("\n");
    var start = 0;
    while (start < oldLines.length && start < newLines.length && oldLines[start] === newLines[start]) {
      start += 1;
    }

    var oldEnd = oldLines.length - 1;
    var newEnd = newLines.length - 1;
    while (oldEnd >= start && newEnd >= start && oldLines[oldEnd] === newLines[newEnd]) {
      oldEnd -= 1;
      newEnd -= 1;
    }

    var oldCount = Math.max(0, oldEnd - start + 1);
    var newCount = Math.max(0, newEnd - start + 1);
    var output = ["Changed lines: -" + oldCount + " +" + newCount, ""];
    var i;
    for (i = Math.max(0, start - 3); i < start; i += 1) {
      output.push("  " + oldLines[i]);
    }
    oldLines.slice(start, oldEnd + 1).slice(0, 200).forEach(function (line) {
      output.push("- " + line);
    });
    newLines.slice(start, newEnd + 1).slice(0, 200).forEach(function (line) {
      output.push("+ " + line);
    });
    if (oldCount > 200 || newCount > 200) {
      output.push("...diff truncated...");
    }
    for (i = oldEnd + 1; i < Math.min(oldLines.length, oldEnd + 4); i += 1) {
      output.push("  " + oldLines[i]);
    }
    return output.join("\n");
  }

  function previewVbaDiff() {
    var module = selectedVbaModule();
    if (!module) {
      $("vbaDiffOutput").textContent = "No module selected.";
      return;
    }

    $("vbaDiffOutput").textContent = formatVbaDiff(vbaModuleCode(module), $("vbaCodeInput").value);
    $("vbaStatus").textContent = "Diff preview ready.";
  }

  async function withVbaActivity(message, work) {
    setActivity("vba", message);
    try {
      await work();
      return true;
    } catch (error) {
      $("vbaStatus").textContent = error.message;
      log(error.detail || error.message);
      return false;
    } finally {
      clearActivity();
    }
  }

  function readVbaResult(response) {
    var result = response.result || response.Result || response;
    var dataJson = result.DataJson || result.dataJson || "";
    var data = dataJson ? JSON.parse(dataJson) : {};
    state.vba.modules = data.modules || data.Modules || [];
    state.vba.backups = response.backups || response.Backups || [];
    $("vbaStatus").textContent = result.Message || result.message || "VBA project loaded.";
    renderVbaProject();
  }

  async function refreshVbaProject() {
    await withVbaActivity("Читаю VBA проект...", async function () {
      var response = await send("getVbaProject", { maxChars: Number($("vbaContextLimitInput").value || 30000) });
      readVbaResult(response);
    });
  }

  async function saveVbaModule() {
    var moduleName = $("vbaModuleSelect").value;
    if (!moduleName) {
      return;
    }

    previewVbaDiff();
    if (await withVbaActivity("Сохраняю VBA module...", async function () {
      var response = await send("saveVbaModule", { moduleName: moduleName, code: $("vbaCodeInput").value });
      $("vbaStatus").textContent = response.Message || response.message || "VBA module saved.";
    })) {
      await refreshVbaProject();
    }
  }

  async function restoreVbaBackup() {
    var backupId = $("vbaBackupSelect").value;
    var moduleName = $("vbaModuleSelect").value;
    if (await withVbaActivity("Восстанавливаю VBA backup...", async function () {
      var response = await send("restoreVbaBackup", { backupId: backupId, moduleName: moduleName });
      $("vbaStatus").textContent = response.Message || response.message || "VBA backup restored.";
    })) {
      await refreshVbaProject();
    }
  }

  function reviewVbaInChat() {
    var patchTool = (state.host || "excel").toLowerCase() + ".vba_apply_patch";
    ensureVbaContextAttached().then(function () {
      $("chatInput").value = "Проверь VBA код из добавленного контекста: найди ошибки, риски и места для улучшения. Если нужны небольшие правки, используй " + patchTool + "; полную замену модуля предлагай только когда это реально нужно.";
      switchTab("chat");
      $("chatInput").focus();
    }).catch(function (error) {
      log(error.detail || error.message);
    });
  }

  function contextNotes() {
    var context = state.context || {};
    return (context.Notes || context.notes || []).filter(function (note) { return !!note; });
  }

  function vbaContextNotes() {
    return contextNotes().filter(function (note) {
      return noteKind(note) === "vba_project";
    });
  }

  function noteValue(note, pascal, camel, fallback) {
    note = note || {};
    return note[pascal] || note[camel] || fallback || "";
  }

  function noteTitle(note) {
    return noteValue(note, "Title", "title", noteValue(note, "Source", "source", "Context"));
  }

  function noteReference(note) {
    return noteValue(note, "Reference", "reference", noteValue(note, "Source", "source", ""));
  }

  function notePreview(note) {
    return noteValue(note, "Preview", "preview", noteValue(note, "Text", "text", ""));
  }

  function noteText(note) {
    return noteValue(note, "Text", "text", notePreview(note));
  }

  function noteKind(note) {
    return noteValue(note, "Kind", "kind", "context");
  }

  function noteDetails(note) {
    return noteValue(note, "DetailsJson", "detailsJson", "");
  }

  function noteHost(note) {
    return noteValue(note, "Host", "host", state.host || "");
  }

  function noteId(note) {
    return noteValue(note, "Id", "id", "");
  }

  function hostBadge(note) {
    var host = noteHost(note).toLowerCase();
    if (host.indexOf("excel") >= 0) {
      return "XL";
    }
    if (host.indexOf("word") >= 0) {
      return "W";
    }
    if (host.indexOf("powerpoint") >= 0) {
      return "PPT";
    }
    if (host.indexOf("outlook") >= 0) {
      return "Mail";
    }
    return "Ctx";
  }

  function createRemoveContextButton(note) {
    var button = document.createElement("button");
    button.type = "button";
    button.className = "context-chip-remove";
    button.title = "Remove context";
    button.setAttribute("aria-label", "Remove context");
    button.innerHTML = "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M18 6 6 18\"/><path d=\"m6 6 12 12\"/></svg>";
    button.addEventListener("click", function (event) {
      event.preventDefault();
      event.stopPropagation();
      removeContextItem(noteId(note));
    });
    return button;
  }

  function appendContextPopover(chip, note) {
    var popover = document.createElement("div");
    popover.className = "context-popover";

    var title = document.createElement("div");
    title.className = "context-popover-title";
    title.textContent = noteTitle(note);

    var meta = document.createElement("div");
    meta.className = "context-popover-meta";
    meta.textContent = noteHost(note) + " - " + noteKind(note) + (noteReference(note) ? " - " + noteReference(note) : "");

    var preview = document.createElement("div");
    preview.className = "context-popover-preview";
    preview.textContent = notePreview(note) || "No preview.";

    popover.appendChild(title);
    popover.appendChild(meta);
    popover.appendChild(preview);
    if (noteDetails(note)) {
      var details = document.createElement("div");
      details.className = "context-popover-details";
      details.textContent = noteDetails(note);
      popover.appendChild(details);
    }
    chip.appendChild(popover);
  }

  function renderContextChips(notes) {
    var strip = $("contextStrip");
    var chips = $("contextChips");
    chips.innerHTML = "";
    strip.classList.toggle("hidden", notes.length === 0);

    notes.forEach(function (note) {
      var chip = document.createElement("div");
      chip.className = "context-chip";
      chip.tabIndex = 0;

      var main = document.createElement("div");
      main.className = "context-chip-main";

      var badge = document.createElement("span");
      badge.className = "context-chip-badge";
      badge.textContent = hostBadge(note);

      var title = document.createElement("span");
      title.className = "context-chip-title";
      title.textContent = noteTitle(note);

      main.appendChild(badge);
      main.appendChild(title);
      chip.appendChild(main);
      chip.appendChild(createRemoveContextButton(note));
      appendContextPopover(chip, note);
      chips.appendChild(chip);
    });
  }

  function renderVbaContextToggle() {
    var button = $("toggleVbaContextButton");
    if (!button) {
      return;
    }

    var active = vbaContextNotes().length > 0;
    button.classList.toggle("active", active);
    button.setAttribute("aria-pressed", active ? "true" : "false");
    button.title = active ? "Detach VBA project context" : "Attach VBA project context";
  }

  function renderContextList(notes) {
    var list = $("contextList");
    var summary = $("contextSummary");
    list.innerHTML = "";
    summary.textContent = notes.length
      ? notes.length + " context attachment(s) belong to the active chat and will be included in its next model request."
      : "No context in this chat. Add a selection from the Office right-click menu or the composer button.";

    notes.forEach(function (note) {
      var card = document.createElement("article");
      card.className = "context-card";

      var head = document.createElement("div");
      head.className = "context-card-head";

      var text = document.createElement("div");
      var title = document.createElement("div");
      title.className = "context-card-title";
      title.textContent = noteTitle(note);

      var meta = document.createElement("div");
      meta.className = "context-card-meta";
      meta.textContent = noteHost(note) + " - " + noteKind(note) + (noteReference(note) ? " - " + noteReference(note) : "");

      text.appendChild(title);
      text.appendChild(meta);
      head.appendChild(text);

      var remove = createRemoveContextButton(note);
      remove.classList.add("secondary");
      head.appendChild(remove);

      var preview = document.createElement("div");
      preview.className = "context-card-preview";
      preview.textContent = notePreview(note) || "No preview.";

      card.appendChild(head);
      card.appendChild(preview);
      list.appendChild(card);
    });
  }

  function renderContext(skipUsageEstimate) {
    var notes = contextNotes();
    renderContextChips(notes);
    renderContextList(notes);
    $("contextBox").textContent = JSON.stringify(state.context || {}, null, 2);
    renderVbaContextToggle();
    if (!skipUsageEstimate) {
      updateEstimatedContextUsage();
    }
    renderContextMeter();
  }

  async function refreshContext() {
    try {
      state.context = await send("getContext", { chatId: state.activeChatId });
      renderContext();
    } catch (error) {
      log(error.detail || error.message);
    }
  }

  async function addSelectionContext(mode) {
    setActivity("context", "Добавляю выделение в контекст...");
    try {
      if (document.activeElement && typeof document.activeElement.blur === "function") {
        document.activeElement.blur();
      }
      reportFocusState();
      state.context = await send("addSelectionContext", { chatId: state.activeChatId, mode: mode || "full" });
      renderContext();
      log("Selection added to context.");
    } catch (error) {
      log(error.detail || error.message);
    } finally {
      clearActivity();
    }
  }

  async function addTextContext(kind, title, reference, text, details) {
    state.context = await send("addTextContext", {
      chatId: state.activeChatId,
      kind: kind,
      title: title,
      reference: reference,
      text: text,
      detailsJson: typeof details === "string" ? details : JSON.stringify(details || {})
    });
    renderContext();
  }

  async function addSelectedToolContextToContext() {
    syncSelectedToolFromEditor();
    var skill = state.tools[state.selectedToolIndex];
    var context = selectedToolContext();
    if (!skill || !context) {
      return false;
    }

    await addTextContext(
      "tool_definition",
      "Tool: " + (skill.Id || "tool"),
      "tool:" + (skill.Id || "tool"),
      context,
      {
        type: "tool_definition",
        id: skill.Id || ""
      });
    log("Tool context added to chat context.");
    return true;
  }

  async function ensureVbaContextAttached() {
    if (vbaContextNotes().length > 0) {
      return;
    }

    await addVbaContext();
  }

  async function addVbaContext() {
    setActivity("context", "Добавляю VBA в контекст...");
    try {
      state.context = await send("addVbaContext", {
        chatId: state.activeChatId,
        maxChars: Number($("vbaContextLimitInput").value || 30000)
      });
      renderContext();
      log("VBA context added.");
    } finally {
      clearActivity();
    }
  }

  async function toggleVbaContext() {
    var notes = vbaContextNotes();
    try {
      if (notes.length) {
        for (var i = 0; i < notes.length; i += 1) {
          state.context = await send("removeContextItem", { chatId: state.activeChatId, id: noteId(notes[i]) });
        }
        renderContext();
        log("VBA context removed.");
        return;
      }

      await addVbaContext();
    } catch (error) {
      log(error.detail || error.message);
    }
  }

  async function removeContextItem(id) {
    if (!id) {
      return;
    }

    try {
      state.context = await send("removeContextItem", { chatId: state.activeChatId, id: id });
      renderContext();
      log("Context item removed.");
    } catch (error) {
      log(error.detail || error.message);
    }
  }

  function logToolResult(prefix, toolId, result) {
    var ok = result && (result.Success === true || result.success === true);
    var message = result ? (result.Message || result.message || "") : "";
    log(prefix + " " + (ok ? "OK" : "FAIL") + ": " + toolId + (message ? " - " + message : ""));
  }

  function logSkillResults(results) {
    (results || []).forEach(function (result, index) {
      logToolResult("Skill " + (index + 1), result.skillId || result.SkillId || "tool", result);
    });
  }

  function formatNumber(value) {
    value = Number(value || 0);
    return value.toLocaleString ? value.toLocaleString() : String(value);
  }

  function lastTokenUsageText() {
    for (var i = state.messages.length - 1; i >= 0; i -= 1) {
      var total = messageTotalTokens(state.messages[i]);
      if (total !== null && total !== undefined) {
        return " · last " + total + " tokens";
      }
    }
    return "";
  }

  function renderContextMeter() {
    var usage = state.contextUsage || {};
    var used = Number(usage.usedChars || usage.UsedChars || 0);
    var limit = Number(usage.limitChars || usage.LimitChars || 0);
    var percent = Number(usage.percent || usage.Percent || (limit ? Math.round(used * 100 / limit) : 0));
    var fill = $("contextMeterFill");
    var value = $("contextMeterValue");
    var detail = $("contextMeterDetail");
    if (!fill || !value || !detail) {
      return;
    }

    percent = Math.max(0, Math.min(100, percent));
    fill.style.width = percent + "%";
    fill.dataset.level = percent >= 90 ? "danger" : (percent >= 70 ? "warn" : "ok");
    value.textContent = percent + "%";
    detail.textContent = formatNumber(used) + " / " + formatNumber(limit) + " chars" + (usage.actual || usage.Actual ? "" : " est.") + lastTokenUsageText();
  }

  function updateEstimatedContextUsage() {
    var used = 0;
    state.messages.forEach(function (message) {
      used += messageContent(message).length;
    });
    contextNotes().forEach(function (note) {
      used += noteText(note).length;
    });

    var limit = Number((state.settings && (state.settings.ContextCharLimit || state.settings.contextCharLimit)) || 24000);
    state.contextUsage = {
      usedChars: used,
      limitChars: limit,
      percent: limit ? Math.min(100, Math.round(used * 100 / limit)) : 0,
      actual: false
    };
  }

  function showSendError(error, text) {
    state.failedSend = { text: text || "", error: error || "Unknown error" };
    var box = $("sendError");
    var message = $("sendErrorText");
    if (box && message) {
      message.textContent = state.failedSend.error;
      box.classList.remove("hidden");
    }
  }

  function clearSendError() {
    state.failedSend = null;
    var box = $("sendError");
    if (box) {
      box.classList.add("hidden");
    }
  }

  function markLocalMessage(text, values) {
    for (var i = state.messages.length - 1; i >= 0; i -= 1) {
      if (state.messages[i] && state.messages[i].Local && messageContent(state.messages[i]) === text) {
        Object.keys(values).forEach(function (key) {
          state.messages[i][key] = values[key];
        });
        return true;
      }
    }
    return false;
  }

  async function createChat() {
    setActivity("loading", "Создаю чат...");
    try {
      applyChatState(await send("createChat", { title: "New chat" }));
      clearSendError();
      log("Chat created.");
    } catch (error) {
      log(error.detail || error.message);
    } finally {
      clearActivity();
    }
  }

  async function selectChat(id) {
    if (!id || id === state.activeChatId) {
      return;
    }

    setActivity("loading", "Открываю чат...");
    try {
      applyChatState(await send("selectChat", { chatId: id }));
      clearSendError();
      log("Chat selected.");
    } catch (error) {
      log(error.detail || error.message);
      renderChatSessions();
    } finally {
      clearActivity();
    }
  }

  async function renameChat() {
    if (!state.activeChatId) {
      return;
    }

    var current = "";
    (state.chats || []).forEach(function (chat) {
      if (chatId(chat) === state.activeChatId) {
        current = chatTitle(chat);
      }
    });

    var title = window.prompt("Chat name", current || "New chat");
    if (title === null || !title.trim()) {
      return;
    }

    try {
      applyChatState(await send("renameChat", { chatId: state.activeChatId, title: title.trim() }));
      log("Chat renamed.");
    } catch (error) {
      log(error.detail || error.message);
    }
  }

  async function clearChat() {
    if (!state.activeChatId || !window.confirm("Clear this chat?")) {
      return;
    }

    setActivity("clearing", "Очищаю чат...");
    try {
      applyChatState(await send("clearChat", { chatId: state.activeChatId }));
      clearSendError();
      log("Chat cleared.");
    } catch (error) {
      log(error.detail || error.message);
    } finally {
      clearActivity();
    }
  }

  async function deleteChat() {
    if (!state.activeChatId || !window.confirm("Delete this chat?")) {
      return;
    }

    setActivity("clearing", "Удаляю чат...");
    try {
      applyChatState(await send("deleteChat", { chatId: state.activeChatId }));
      clearSendError();
      log("Chat deleted.");
    } catch (error) {
      log(error.detail || error.message);
    } finally {
      clearActivity();
    }
  }

  async function deleteMessage(message, index) {
    if (message && message.Local) {
      state.messages.splice(index, 1);
      if (message.Failed) {
        clearSendError();
      }
      updateEstimatedContextUsage();
      renderMessages();
      renderChatSessions();
      renderContextMeter();
      return;
    }

    try {
      var response = await send("deleteMessage", { chatId: state.activeChatId, id: messageId(message), index: index });
      applyChatState(response);
      log("Message deleted.");
    } catch (error) {
      showSendError(error.detail || error.message, state.failedSend ? state.failedSend.text : "");
      log(error.detail || error.message);
    }
  }

  async function forkChatAtMessage(message, index) {
    if (!state.activeChatId) {
      return;
    }

    try {
      applyChatState(await send("forkChat", { chatId: state.activeChatId, id: messageId(message), index: index }));
      clearSendError();
      log("Chat branch created.");
    } catch (error) {
      log(error.detail || error.message);
    }
  }

  function applyInitState(init) {
    state.host = init.host;
    state.title = init.title;
    state.settings = init.settings || {};
    state.tools = init.tools || [];
    state.toolsPath = init.toolsPath || "";
    state.context = init.context || {};
    state.contextUsage = init.contextUsage || {};
    state.activeChatId = init.activeChatId || "";
    state.activeChatModel = init.activeChatModel || "";
    state.chats = init.chats || [];
    state.messages = init.messages || [];
    $("docLine").textContent = init.host + " - " + init.title;
    $("toolsPath").textContent = state.toolsPath ? "Storage: " + state.toolsPath : "";
    renderSettings();
    renderTools();
    renderContext(true);
    renderChatSessions();
    renderMessages();
    renderContextMeter();
    log("Initialized " + init.host);
    loadModelCatalog(false);
    if (init.quickAction) {
      runQuickAction(init.quickAction);
    }
  }

  async function initialize() {
    setActivity("loading", "Загружаю состояние...");
    try {
      var init = await send("init");
      applyInitState(init);
    } catch (error) {
      log(error.message);
    } finally {
      clearActivity();
    }
  }

  async function clearRuntimeData() {
    if (!window.confirm("Delete all local chats, chat context, VBA backups, and WebView cache for RNAssistant? Settings, API key, and custom tools will stay.")) {
      return;
    }

    setActivity("clearing", "Очищаю локальные данные...");
    try {
      var init = await send("clearRuntimeData", {});
      applyInitState(init);
      log("Runtime data cleared.");
    } catch (error) {
      log(error.detail || error.message);
    } finally {
      clearActivity();
    }
  }

  async function sendChat(text) {
    setActivity("thinking", "Модель думает...");
    $("sendButton").disabled = true;
    $("chatInput").readOnly = true;
    if ($("chatModelSelect")) {
      $("chatModelSelect").disabled = true;
    }
    try {
      var response = await send("sendChat", { chatId: state.activeChatId, text: text });
      applyChatState(response);
      clearSendError();
      if (response.skillResults && response.skillResults.length) {
        logSkillResults(response.skillResults);
      }
    } catch (error) {
      markLocalMessage(text, { Pending: false, Failed: true });
      renderMessages();
      showSendError(error.detail || error.message, text);
      log(error.message);
      if (error.detail && error.detail !== error.message) {
        log(error.detail);
      }
    } finally {
      $("sendButton").disabled = false;
      $("chatInput").readOnly = false;
      renderModelControls();
      clearActivity();
    }
  }

  function submitChatInput() {
    if ($("sendButton").disabled || state.modelSaving) {
      return;
    }

    var text = $("chatInput").value.trim();
    if (!text) {
      return;
    }

    $("chatInput").value = "";
    clearSendError();
    state.messages.push({ Id: "local-" + Date.now(), Role: "user", Content: text, Local: true, Pending: true });
    updateEstimatedContextUsage();
    renderMessages();
    renderChatSessions();
    renderContextMeter();
    sendChat(text);
  }

  function retryFailedSend() {
    if (!state.failedSend || !state.failedSend.text) {
      return;
    }

    markLocalMessage(state.failedSend.text, { Pending: true, Failed: false });
    updateEstimatedContextUsage();
    renderMessages();
    renderChatSessions();
    renderContextMeter();
    var text = state.failedSend.text;
    clearSendError();
    sendChat(text);
  }

  async function runQuickAction(action) {
    var response = await send("quickAction", { action: action });
    if (response.prompt === "/open-settings") {
      switchTab("settings");
      return;
    }
    if (response.prompt === "/open-context") {
      switchTab("context");
      return;
    }
    $("chatInput").value = response.prompt || "";
    switchTab("chat");
  }

  document.addEventListener("DOMContentLoaded", function () {
    ["focusin", "focusout", "selectionchange", "mouseup", "keyup"].forEach(function (name) {
      document.addEventListener(name, scheduleFocusStateReport);
    });
    window.addEventListener("focus", scheduleFocusStateReport);
    window.addEventListener("blur", scheduleFocusStateReport);
    scheduleFocusStateReport();

    Array.prototype.slice.call(document.querySelectorAll(".tab")).forEach(function (tab) {
      tab.addEventListener("click", function () { switchTab(tab.dataset.tab); });
    });

    $("helpButton").addEventListener("click", showHelp);
    $("closeHelpButton").addEventListener("click", hideHelp);
    $("helpModal").addEventListener("click", function (event) {
      if (event.target === $("helpModal")) {
        hideHelp();
      }
    });
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape") {
        hideHelp();
      }
    });

    $("refreshButton").addEventListener("click", initialize);
    $("chatSessionSelect").addEventListener("change", function () { selectChat($("chatSessionSelect").value); });
    $("newChatButton").addEventListener("click", createChat);
    $("renameChatButton").addEventListener("click", renameChat);
    $("clearChatButton").addEventListener("click", clearChat);
    $("deleteChatButton").addEventListener("click", deleteChat);
    $("openContextTabButton").addEventListener("click", function () { switchTab("context"); });
    $("addSelectionContextButton").addEventListener("click", function () { addSelectionContext("full"); });
    $("toggleVbaContextButton").addEventListener("click", toggleVbaContext);
    $("retrySendButton").addEventListener("click", retryFailedSend);
    $("refreshVbaButton").addEventListener("click", refreshVbaProject);
    $("vbaModuleSelect").addEventListener("change", renderSelectedVbaModule);
    $("vbaCodeInput").addEventListener("input", renderVbaCodePreview);
    $("previewVbaDiffButton").addEventListener("click", previewVbaDiff);
    $("saveVbaButton").addEventListener("click", saveVbaModule);
    $("restoreVbaButton").addEventListener("click", restoreVbaBackup);
    $("reviewVbaButton").addEventListener("click", reviewVbaInChat);
    $("clearInputButton").addEventListener("click", function () { $("chatInput").value = ""; });
    $("modelSelect").addEventListener("change", function () {
      if ($("modelSelect").value) {
        $("modelInput").value = $("modelSelect").value;
        applyModelDefaultsToForm(findModel($("modelSelect").value));
        renderModelControls();
      }
    });
    $("modelInput").addEventListener("input", renderModelControls);
    $("chatModelSelect").addEventListener("change", function () {
      saveChatModelSelection($("chatModelSelect").value);
    });
    $("loadModelsButton").addEventListener("click", function () {
      loadModelCatalog(true);
    });
    $("chatInput").addEventListener("keydown", function (event) {
      if (event.key === "Enter" && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
        event.preventDefault();
        submitChatInput();
      }
    });
    $("chatForm").addEventListener("submit", function (event) {
      event.preventDefault();
      submitChatInput();
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

    $("addToolButton").addEventListener("click", function () {
      syncSelectedToolFromEditor();
      state.tools.push({
        Id: (state.host || "common").toLowerCase() + ".new_tool",
        Host: state.host || "Common",
        Name: "new_tool",
        Description: "",
        ArgumentSchemaJson: "{}",
        Executor: "pipeline",
        RequiresConfirmation: true,
        PipelineJson: "{\n  \"version\": 1,\n  \"steps\": []\n}",
        Code: "",
        Readme: "",
        Enabled: true,
        BuiltIn: false
      });
      state.selectedToolIndex = state.tools.length - 1;
      renderTools();
    });

    $("cloneToolButton").addEventListener("click", function () {
      syncSelectedToolFromEditor();
      var source = state.tools[state.selectedToolIndex];
      if (!source) {
        return;
      }

      var id = (source.Id || "tool") + ".copy";
      state.tools.push({
        Id: id,
        Host: source.Host || state.host || "Common",
        Name: id,
        Description: source.Description || "",
        ArgumentSchemaJson: source.ArgumentSchemaJson || "{}",
        Executor: source.BuiltIn ? "pipeline" : (source.Executor || "pipeline"),
        RequiresConfirmation: source.BuiltIn ? true : !!source.RequiresConfirmation,
        PipelineJson: source.PipelineJson || "{\n  \"version\": 1,\n  \"steps\": []\n}",
        Code: source.Code || "",
        Readme: source.Readme || "",
        Enabled: true,
        BuiltIn: false
      });
      state.selectedToolIndex = state.tools.length - 1;
      renderTools();
    });

    $("saveToolsButton").addEventListener("click", async function () {
      try {
        var response = await send("saveTools", { tools: readTools() });
        state.tools = response || [];
        renderTools();
        log("Tools saved.");
      } catch (error) {
        log(error.message);
      }
    });

    $("deleteToolButton").addEventListener("click", function () {
      var skill = state.tools[state.selectedToolIndex];
      if (!skill || skill.BuiltIn) {
        return;
      }

      state.tools.splice(state.selectedToolIndex, 1);
      if (state.selectedToolIndex >= state.tools.length) {
        state.selectedToolIndex = state.tools.length - 1;
      }
      renderTools();
    });

    $("dryRunToolButton").addEventListener("click", function () {
      runSelectedTool(true);
    });

    $("runToolButton").addEventListener("click", function () {
      runSelectedTool(false);
    });

    $("copyToolContextButton").addEventListener("click", function () {
      copyText(selectedToolContext());
      log("Tool context copied.");
    });

    $("askToolBuilderButton").addEventListener("click", function () {
      addSelectedToolContextToContext().then(function (added) {
        if (!added) {
          return;
        }

        $("chatInput").value = "Отредактируй RNAssistant tool из добавленного контекста. Верни обновленные tool.json/pipeline/code блоки, не выполняй действия без подтверждения.";
        switchTab("chat");
        $("chatInput").focus();
      }).catch(function (error) {
        log(error.detail || error.message);
      });
    });

    $("clearContextButton").addEventListener("click", async function () {
      setActivity("clearing", "Очищаю контекст...");
      try {
        state.context = await send("clearContext", { chatId: state.activeChatId });
        renderContext();
        log("Context cleared.");
      } catch (error) {
        log(error.message);
      } finally {
        clearActivity();
      }
    });

    window.addEventListener("load", function () {
      if (window.hljs) {
        highlightAllCode();
      }
    });

    initialize();
  });
})();
