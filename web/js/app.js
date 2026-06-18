function markdown(text) {
  return DOMPurify.sanitize(marked.parse(text || ""));
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
