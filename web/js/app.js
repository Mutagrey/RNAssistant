(function () {
  var state = {
    host: "",
    title: "",
    settings: {},
    tools: [],
    context: {},
    messages: [],
    selectedToolIndex: -1,
    toolsPath: "",
    vba: { modules: [], backups: [], selectedModule: "" },
    activity: { visible: false, phase: "", message: "" },
    pending: {},
    seq: 1,
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

  window.chrome.webview.addEventListener("message", function (event) {
    var response = event.data;
    if (typeof response === "string") {
      response = JSON.parse(response);
    }
    if (response && response.type === "progress") {
      var progress = response.payload || {};
      setActivity(progress.phase || "working", progress.message || "Working...");
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

  function renderMessages() {
    var box = $("messages");
    box.innerHTML = "";
    state.messages.forEach(function (message) {
      var node = document.createElement("article");
      node.className = "message " + (message.Role || message.role || "");
      var role = document.createElement("div");
      role.className = "role";
      role.textContent = message.Role || message.role || "assistant";
      var body = document.createElement("div");
      body.className = "markdown";
      body.innerHTML = markdown(message.Content || message.content || "");
      node.appendChild(role);
      node.appendChild(body);
      box.appendChild(node);
      enhanceMarkdown(body);
    });
    box.scrollTop = box.scrollHeight;
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
      var toggle = document.createElement("button");
      toggle.type = "button";
      toggle.textContent = "Hide code";
      toggle.addEventListener("click", function () {
        var hidden = pre.style.display === "none";
        pre.style.display = hidden ? "" : "none";
        toggle.textContent = hidden ? "Hide code" : "Show code";
      });
      var copy = document.createElement("button");
      copy.type = "button";
      copy.textContent = "Copy code";
      copy.addEventListener("click", function () {
        copyText(code ? code.textContent : pre.innerText);
      });
      tools.appendChild(toggle);
      tools.appendChild(copy);
      pre.parentNode.insertBefore(wrap, pre);
      wrap.appendChild(tools);
      wrap.appendChild(pre);
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
      toggle.textContent = "Hide table";
      toggle.addEventListener("click", function () {
        var hidden = table.style.display === "none";
        table.style.display = hidden ? "" : "none";
        toggle.textContent = hidden ? "Hide table" : "Show table";
      });
      var copy = document.createElement("button");
      copy.type = "button";
      copy.textContent = "Copy table";
      copy.addEventListener("click", function () {
        copyText(table.innerText);
      });
      tools.appendChild(toggle);
      tools.appendChild(copy);
      table.parentNode.insertBefore(wrap, table);
      wrap.appendChild(tools);
      wrap.appendChild(table);
    });
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
    $("temperatureInput").value = s.Temperature || s.temperature || 0.2;
    $("contextLimitInput").value = s.ContextCharLimit || s.contextCharLimit || 24000;
    $("streamInput").checked = !!(s.StreamResponses || s.streamResponses);
    $("autoRunToolsInput").checked = (s.AutoRunToolCalls !== false && s.autoRunToolCalls !== false);
    $("autoConfirmToolsInput").checked = !!(s.AutoConfirmToolActions || s.autoConfirmToolActions);
    $("includeVbaContextInput").checked = !!(s.IncludeVbaContext || s.includeVbaContext);
    $("vbaContextLimitInput").value = s.VbaContextCharLimit || s.vbaContextCharLimit || 30000;
    $("systemPromptInput").value = s.SystemPrompt || s.systemPrompt || "";
    $("headersInput").value = headersToText(s.CustomHeaders || s.customHeaders || {});
  }

  function readSettings() {
    return {
      BaseUrl: $("baseUrlInput").value.trim(),
      Model: $("modelInput").value.trim(),
      MaxTokens: Number($("maxTokensInput").value || 2048),
      Temperature: Number($("temperatureInput").value || 0.2),
      ContextCharLimit: Number($("contextLimitInput").value || 24000),
      StreamResponses: $("streamInput").checked,
      AutoRunToolCalls: $("autoRunToolsInput").checked,
      AutoConfirmToolActions: $("autoConfirmToolsInput").checked,
      IncludeVbaContext: $("includeVbaContextInput").checked,
      VbaContextCharLimit: Number($("vbaContextLimitInput").value || 30000),
      SystemPrompt: $("systemPromptInput").value,
      CustomHeaders: textToHeaders($("headersInput").value)
    };
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
      log((dryRun ? "Dry run finished: " : "Tool run finished: ") + skill.Id);
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
    var name = $("vbaModuleSelect").value;
    state.vba.selectedModule = name;
    var module = null;
    state.vba.modules.forEach(function (item) {
      if ((item.name || item.Name) === name) {
        module = item;
      }
    });

    $("vbaCodeInput").value = module ? (module.code || module.Code || "") : "";
    $("vbaMetaBox").textContent = module ? JSON.stringify({
      name: module.name || module.Name,
      type: module.type || module.Type,
      lineCount: module.lineCount || module.LineCount
    }, null, 2) : "";
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
    setActivity("vba", "Читаю VBA проект...");
    try {
      var response = await send("getVbaProject", { maxChars: Number($("vbaContextLimitInput").value || 30000) });
      readVbaResult(response);
    } catch (error) {
      $("vbaStatus").textContent = error.message;
      log(error.detail || error.message);
    } finally {
      clearActivity();
    }
  }

  async function saveVbaModule() {
    var moduleName = $("vbaModuleSelect").value;
    if (!moduleName) {
      return;
    }

    setActivity("vba", "Сохраняю VBA module...");
    try {
      var response = await send("saveVbaModule", { moduleName: moduleName, code: $("vbaCodeInput").value });
      $("vbaStatus").textContent = response.Message || response.message || "VBA module saved.";
      await refreshVbaProject();
    } catch (error) {
      $("vbaStatus").textContent = error.message;
      log(error.detail || error.message);
    } finally {
      clearActivity();
    }
  }

  async function restoreVbaBackup() {
    var backupId = $("vbaBackupSelect").value;
    var moduleName = $("vbaModuleSelect").value;
    setActivity("vba", "Восстанавливаю VBA backup...");
    try {
      var response = await send("restoreVbaBackup", { backupId: backupId, moduleName: moduleName });
      $("vbaStatus").textContent = response.Message || response.message || "VBA backup restored.";
      await refreshVbaProject();
    } catch (error) {
      $("vbaStatus").textContent = error.message;
      log(error.detail || error.message);
    } finally {
      clearActivity();
    }
  }

  function reviewVbaInChat() {
    var modules = state.vba.modules.map(function (module) {
      return "===== " + (module.name || module.Name) + " =====\n" + (module.code || module.Code || "");
    }).join("\n\n");
    $("chatInput").value = "Проверь мой VBA код: найди ошибки, риски, места для улучшения, предложи комментарии. Если нужны правки, верни конкретные обновленные модули и объясни, что меняешь.\\n\\n" + modules;
    switchTab("chat");
    $("chatInput").focus();
  }

  function renderContext() {
    $("contextBox").textContent = JSON.stringify(state.context || {}, null, 2);
  }

  async function initialize() {
    setActivity("loading", "Загружаю состояние...");
    try {
      var init = await send("init");
      state.host = init.host;
      state.title = init.title;
      state.settings = init.settings || {};
      state.tools = init.tools || [];
      state.toolsPath = init.toolsPath || "";
      state.context = init.context || {};
      state.messages = init.messages || [];
      $("docLine").textContent = init.host + " - " + init.title;
      $("toolsPath").textContent = state.toolsPath ? "Storage: " + state.toolsPath : "";
      renderSettings();
      renderTools();
      renderContext();
      renderMessages();
      log("Initialized " + init.host);
      if (init.quickAction) {
        runQuickAction(init.quickAction);
      }
    } catch (error) {
      log(error.message);
    } finally {
      clearActivity();
    }
  }

  async function sendChat(text) {
    setActivity("thinking", "Модель думает...");
    $("sendButton").disabled = true;
    $("chatInput").readOnly = true;
    try {
      var response = await send("sendChat", { text: text });
      state.messages = response.messages || state.messages;
      renderMessages();
      if (response.skillResults && response.skillResults.length) {
        log("Executed " + response.skillResults.length + " local skill command(s).");
      }
    } catch (error) {
      log(error.message);
      if (error.detail && error.detail !== error.message) {
        log(error.detail);
      }
    } finally {
      $("sendButton").disabled = false;
      $("chatInput").readOnly = false;
      clearActivity();
    }
  }

  function submitChatInput() {
    if ($("sendButton").disabled) {
      return;
    }

    var text = $("chatInput").value.trim();
    if (!text) {
      return;
    }

    $("chatInput").value = "";
    state.messages.push({ Role: "user", Content: text });
    renderMessages();
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
    $("refreshVbaButton").addEventListener("click", refreshVbaProject);
    $("vbaModuleSelect").addEventListener("change", renderSelectedVbaModule);
    $("saveVbaButton").addEventListener("click", saveVbaModule);
    $("restoreVbaButton").addEventListener("click", restoreVbaBackup);
    $("reviewVbaButton").addEventListener("click", reviewVbaInChat);
    $("clearInputButton").addEventListener("click", function () { $("chatInput").value = ""; });
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
        log("Settings saved.");
      } catch (error) {
        log(error.message);
      }
    });

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
      var context = selectedToolContext();
      if (!context) {
        return;
      }

      $("chatInput").value = "Отредактируй этот RNAssistant tool. Верни обновленные tool.json/pipeline/code блоки, не выполняй действия без подтверждения.\\n\\n" + context;
      switchTab("chat");
      $("chatInput").focus();
    });

    $("clearContextButton").addEventListener("click", async function () {
      setActivity("clearing", "Очищаю контекст...");
      try {
        state.context = await send("clearContext");
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
