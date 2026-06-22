(function () {
  var promptDefinitions = [
    { key: "systemPrompt", label: "Системный промпт", group: "Base", source: "root", field: "SystemPrompt", description: "Стиль и общий контекст ассистента." },
    { key: "agentPrompt", label: "Промпт агента", group: "Base", source: "root", field: "AgentPrompt", description: "Общее поведение агента при Office-действиях." },
    { key: "toolProtocolPrompt", label: "Протокол tool-вызовов", group: "Runtime", source: "agent", field: "ToolProtocolPrompt", description: "Формат rnassistant-agent блока и аргументов." },
    { key: "toolRoutingPrompt", label: "Routing tools", group: "Runtime", source: "agent", field: "ToolRoutingPrompt", description: "Правила выбора tool, VBA, chart/html artifacts и проверок." },
    { key: "forceToolUsePrompt", label: "Force tool use", group: "Recovery", source: "agent", field: "ForceToolUsePrompt", description: "Follow-up, когда модель ответила текстом на явное действие." },
    { key: "repairMalformedToolBlockPrompt", label: "Repair malformed block", group: "Recovery", source: "agent", field: "RepairMalformedToolBlockPrompt", description: "Follow-up для битого rnassistant-agent JSON." },
    { key: "afterToolResultsPrompt", label: "After tool results", group: "Loop", source: "agent", field: "AfterToolResultsPrompt", description: "Продолжение после успешных локальных tool results." },
    { key: "verifyMutationPrompt", label: "Verify mutation", group: "Loop", source: "agent", field: "VerifyMutationPrompt", description: "Проверка после мутации документа или VBA." },
    { key: "confirmedToolContinuationPrompt", label: "After confirmation", group: "Loop", source: "agent", field: "ConfirmedToolContinuationPrompt", description: "Продолжение после ручного подтверждения tool." },
    { key: "retryFailedToolPrompt", label: "Retry failed tool", group: "Recovery", source: "agent", field: "RetryFailedToolPrompt", description: "Шаблон авторемонта упавшего tool с placeholders." }
  ];

  function promptValue(settings, def) {
    settings = settings || {};
    if (def.source === "root") {
      return settings[def.field] !== undefined ? settings[def.field] : (settings[lowerFirst(def.field)] || "");
    }

    var prompts = settings.AgentPrompts || settings.agentPrompts || {};
    return prompts[def.field] !== undefined ? prompts[def.field] : (prompts[lowerFirst(def.field)] || "");
  }

  function lowerFirst(value) {
    return value ? value.charAt(0).toLowerCase() + value.slice(1) : value;
  }

  function selectedPromptDefinition() {
    return promptDefinitions[state.selectedPromptIndex] || null;
  }

  function promptText(def) {
    return def ? (state.promptDrafts[def.key] || "") : "";
  }

  function promptMatchesSearch(def, query) {
    var text = [
      def.label,
      def.group,
      def.field,
      def.description,
      state.promptDrafts[def.key] || ""
    ].join(" ").toLowerCase();
    return text.indexOf(query) >= 0;
  }

  function renderPromptPreview(def) {
    var preview = $("promptPreview");
    if (!preview) {
      return;
    }

    var value = promptText(def);
    preview.innerHTML = markdown(value || "_Промпт пуст. При сохранении будет использован встроенный дефолт._");
    if (typeof enhanceMarkdown === "function") {
      enhanceMarkdown(preview);
    }
  }

  function renderPromptSettings(settings) {
    state.promptDrafts = {};
    promptDefinitions.forEach(function (def) {
      state.promptDrafts[def.key] = promptValue(settings, def);
    });
    renderPromptList();
  }

  function renderPromptList() {
    renderResourceList({
      listId: "promptList",
      searchInputId: "promptSearchInput",
      items: promptDefinitions,
      emptyText: "Промпты не найдены.",
      getSelectedIndex: function () { return state.selectedPromptIndex; },
      setSelectedIndex: function (index) { state.selectedPromptIndex = index; },
      matches: promptMatchesSearch,
      title: function (def) { return def.label; },
      enabled: function () { return null; },
      meta: function (def) { return def.group + " - " + def.field; },
      description: function (def) { return def.description; },
      syncEditor: syncSelectedPromptFromEditor,
      renderEditor: renderPromptEditor,
      renderList: renderPromptList
    });
  }

  function renderPromptEditor() {
    var def = selectedPromptDefinition();
    var disabled = !def;
    var panel = $("promptEditorPanel");
    var content = $("promptEditorContent");
    var empty = $("promptEditorEmpty");
    if (panel) {
      panel.classList.toggle("is-empty", disabled);
    }
    if (content) {
      content.hidden = disabled;
    }
    if (empty) {
      empty.querySelector(".resource-editor-empty-title").textContent = "Промпт не выбран";
      empty.querySelector(".resource-editor-empty-text").textContent = "Выберите prompt-блок слева.";
    }
    if (disabled) {
      return;
    }

    $("promptTitle").textContent = def.label;
    $("promptMeta").textContent = def.group + " - " + def.field;
    $("promptEditInput").value = promptText(def);
    $("copyPromptButton").disabled = false;
    $("addPromptToChatButton").disabled = !!state.bridgeUnavailable;
    renderPromptPreview(def);
    applyPromptMode();
  }

  function syncSelectedPromptFromEditor() {
    var def = selectedPromptDefinition();
    var input = $("promptEditInput");
    if (!def || !input) {
      return;
    }
    state.promptDrafts[def.key] = input.value;
  }

  function setPromptMode(mode) {
    syncSelectedPromptFromEditor();
    state.promptEditorMode = mode === "edit" ? "edit" : "preview";
    renderPromptPreview(selectedPromptDefinition());
    applyPromptMode();
  }

  function applyPromptMode() {
    var mode = state.promptEditorMode === "edit" ? "edit" : "preview";
    Array.prototype.slice.call(document.querySelectorAll(".prompt-mode-button")).forEach(function (button) {
      button.classList.toggle("active", button.getAttribute("data-prompt-mode") === mode);
    });
    $("promptPreview").classList.toggle("hidden", mode !== "preview");
    $("promptEditInput").classList.toggle("hidden", mode !== "edit");
  }

  function selectedPromptContext() {
    syncSelectedPromptFromEditor();
    var def = selectedPromptDefinition();
    if (!def) {
      return "";
    }

    return [
      "# RNAssistant prompt template",
      "key: " + def.key,
      "field: " + def.field,
      "group: " + def.group,
      "",
      "## Description",
      def.description,
      "",
      "## Current prompt",
      "```markdown",
      promptText(def),
      "```"
    ].join("\n");
  }

  async function addSelectedPromptToChat() {
    var def = selectedPromptDefinition();
    var context = selectedPromptContext();
    if (!def || !context) {
      return;
    }

    await addTextContext(
      "agent_prompt",
      "Prompt: " + def.label,
      "prompt:" + def.key,
      context,
      { type: "agent_prompt", key: def.key, field: def.field });
    switchTab("chat");
    setChatInputText("Улучши RNAssistant prompt template из добавленного контекста. Если правка нужна, предложи обновленный Markdown и после подтверждения используй common.prompts_save для сохранения.", true);
  }

  function readPromptSettings() {
    syncSelectedPromptFromEditor();
    var result = {
      SystemPrompt: state.promptDrafts.systemPrompt || "",
      AgentPrompt: state.promptDrafts.agentPrompt || "",
      AgentPrompts: {}
    };
    promptDefinitions.forEach(function (def) {
      if (def.source === "agent") {
        result.AgentPrompts[def.field] = state.promptDrafts[def.key] || "";
      }
    });
    return result;
  }

  function bindPromptSettingsActions() {
    $("promptSearchInput").addEventListener("input", renderPromptList);
    $("promptEditInput").addEventListener("input", function () {
      syncSelectedPromptFromEditor();
      settingsDirty = true;
      updateSettingsSaveButton();
    });
    Array.prototype.slice.call(document.querySelectorAll(".prompt-mode-button")).forEach(function (button) {
      button.addEventListener("click", function () {
        setPromptMode(button.getAttribute("data-prompt-mode"));
      });
    });
    $("copyPromptButton").addEventListener("click", function () {
      copyText(promptText(selectedPromptDefinition()));
      log("Промпт скопирован.");
    });
    $("addPromptToChatButton").addEventListener("click", function () {
      addSelectedPromptToChat().catch(function (error) {
        log(error.detail || error.message);
      });
    });
  }

  window.renderPromptSettings = renderPromptSettings;
  window.renderPromptList = renderPromptList;
  window.renderPromptEditor = renderPromptEditor;
  window.syncSelectedPromptFromEditor = syncSelectedPromptFromEditor;
  window.readPromptSettings = readPromptSettings;
  window.bindPromptSettingsActions = bindPromptSettingsActions;
}());
