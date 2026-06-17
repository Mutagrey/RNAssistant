(function () {
  var state = {
    host: "",
    title: "",
    settings: {},
    skills: [],
    context: {},
    messages: [],
    pending: {},
    seq: 1,
    highlightLog: {},
    webViewFocused: true
  };

  function $(id) {
    return document.getElementById(id);
  }

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
      var copy = document.createElement("button");
      copy.type = "button";
      copy.textContent = "Copy code";
      copy.addEventListener("click", function () {
        copyText(pre.innerText);
      });
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
      logOnce("Highlight.js is not loaded; code is shown without syntax colors.");
      return;
    }

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

  function renderSkills() {
    var list = $("skillsList");
    list.innerHTML = "";
    state.skills.forEach(function (skill, index) {
      var item = document.createElement("div");
      item.className = "skill-item";
      item.innerHTML =
        "<div class=\"skill-head\">" +
        "<div><div class=\"skill-title\"></div><div class=\"skill-meta\"></div></div>" +
        "<label class=\"checkline\"><input type=\"checkbox\" class=\"skill-enabled\"> Enabled</label>" +
        "</div>" +
        "<input class=\"skill-id\" placeholder=\"skill.id\">" +
        "<select class=\"skill-host\"><option>Common</option><option>Excel</option><option>Word</option><option>PowerPoint</option><option>Outlook</option></select>" +
        "<textarea class=\"skill-description\" rows=\"3\" placeholder=\"Description for LLM\"></textarea>" +
        "<textarea class=\"skill-schema\" rows=\"3\" placeholder=\"Argument schema JSON\"></textarea>" +
        "<div class=\"toolbar\"><button class=\"secondary skill-delete\" type=\"button\">Delete</button></div>";
      item.querySelector(".skill-title").textContent = skill.Name || skill.Id || "Skill";
      item.querySelector(".skill-meta").textContent = skill.BuiltIn ? "Built-in" : "Custom";
      item.querySelector(".skill-enabled").checked = skill.Enabled !== false;
      item.querySelector(".skill-id").value = skill.Id || "";
      item.querySelector(".skill-id").disabled = !!skill.BuiltIn;
      item.querySelector(".skill-host").value = skill.Host || "Common";
      item.querySelector(".skill-host").disabled = !!skill.BuiltIn;
      item.querySelector(".skill-description").value = skill.Description || "";
      item.querySelector(".skill-description").disabled = !!skill.BuiltIn;
      item.querySelector(".skill-schema").value = skill.ArgumentSchemaJson || "{}";
      item.querySelector(".skill-schema").disabled = !!skill.BuiltIn;
      item.querySelector(".skill-delete").disabled = !!skill.BuiltIn;
      item.querySelector(".skill-delete").addEventListener("click", function () {
        state.skills.splice(index, 1);
        renderSkills();
      });
      list.appendChild(item);
    });
  }

  function readSkills() {
    return Array.prototype.slice.call(document.querySelectorAll(".skill-item")).map(function (item, index) {
      var original = state.skills[index] || {};
      return {
        Id: item.querySelector(".skill-id").value.trim(),
        Host: item.querySelector(".skill-host").value,
        Name: item.querySelector(".skill-id").value.trim(),
        Description: item.querySelector(".skill-description").value,
        ArgumentSchemaJson: item.querySelector(".skill-schema").value || "{}",
        Enabled: item.querySelector(".skill-enabled").checked,
        BuiltIn: !!original.BuiltIn
      };
    });
  }

  function renderContext() {
    $("contextBox").textContent = JSON.stringify(state.context || {}, null, 2);
  }

  async function initialize() {
    try {
      var init = await send("init");
      state.host = init.host;
      state.title = init.title;
      state.settings = init.settings || {};
      state.skills = init.skills || [];
      state.context = init.context || {};
      state.messages = init.messages || [];
      $("docLine").textContent = init.host + " - " + init.title;
      renderSettings();
      renderSkills();
      renderContext();
      renderMessages();
      log("Initialized " + init.host);
      if (init.quickAction) {
        runQuickAction(init.quickAction);
      }
    } catch (error) {
      log(error.message);
    }
  }

  async function sendChat(text, restoreComposerFocus) {
    $("sendButton").disabled = true;
    $("chatInput").disabled = true;
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
      $("chatInput").disabled = false;
      if (restoreComposerFocus && state.webViewFocused && document.hasFocus()) {
        $("chatInput").focus();
      }
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

    var active = document.activeElement;
    var restoreComposerFocus = active === $("chatInput") || active === $("sendButton");

    $("chatInput").value = "";
    state.messages.push({ Role: "user", Content: text });
    renderMessages();
    sendChat(text, restoreComposerFocus);
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
    $("chatInput").focus();
  }

  document.addEventListener("DOMContentLoaded", function () {
    window.addEventListener("focus", function () { state.webViewFocused = true; });
    window.addEventListener("blur", function () { state.webViewFocused = false; });

    Array.prototype.slice.call(document.querySelectorAll(".tab")).forEach(function (tab) {
      tab.addEventListener("click", function () { switchTab(tab.dataset.tab); });
    });

    $("refreshButton").addEventListener("click", initialize);
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

    $("addSkillButton").addEventListener("click", function () {
      state.skills.push({ Id: "custom.skill", Host: state.host || "Common", Name: "custom.skill", Description: "", ArgumentSchemaJson: "{}", Enabled: true, BuiltIn: false });
      renderSkills();
    });

    $("saveSkillsButton").addEventListener("click", async function () {
      try {
        var response = await send("saveSkills", { skills: readSkills() });
        state.skills = response || [];
        renderSkills();
        log("Skills saved.");
      } catch (error) {
        log(error.message);
      }
    });

    $("clearContextButton").addEventListener("click", async function () {
      try {
        state.context = await send("clearContext");
        renderContext();
        log("Context cleared.");
      } catch (error) {
        log(error.message);
      }
    });

    initialize();
  });
})();
