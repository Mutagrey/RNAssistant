(function () {
  var promptDefinitions = [
    { key: "systemPrompt", label: "Главный промпт агента", group: "Base", source: "root", field: "SystemPrompt", description: "AgentDecision v1, transport, контекст, tools, skills и правила self-improvement." },
    { key: "chatSystemPrompt", label: "Базовый промпт чата", group: "Base", source: "root", field: "ChatSystemPrompt", description: "Прямой текстовый ответ без planner JSON и внутреннего reasoning." },
    { key: "forceToolUsePrompt", label: "Force tool use", group: "Recovery", source: "agent", field: "ForceToolUsePrompt", description: "Follow-up, когда модель ответила текстом на явное действие." },
    { key: "repairDecisionPrompt", label: "Repair AgentDecision", group: "Recovery", source: "agent", field: "RepairDecisionPrompt", description: "Промпт ограниченных повторов для невалидного решения модели." },
    { key: "planContinuationPrompt", label: "Continue plan", group: "Loop", source: "agent", field: "PlanContinuationPrompt", description: "Переход от видимого plan к следующему решению." },
    { key: "chatTitlePrompt", label: "Название чата", group: "Utility", source: "agent", field: "ChatTitlePrompt", description: "Отдельный короткий запрос для генерации названия чата." }
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
      icon: function () { return "PRM"; },
      meta: function (def) { return def.group + " - " + def.field; },
      description: function (def) { return def.description; },
      groupKey: function (def) { return def.group; },
      groupLabel: function (def) { return def.group; },
      groupStoragePrefix: "prompts",
      compact: true,
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
      ChatSystemPrompt: state.promptDrafts.chatSystemPrompt || "",
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
    $("resetAllPromptsButton").addEventListener("click", function () {
      promptDefinitions.forEach(function (def) {
        state.promptDrafts[def.key] = "";
      });
      $("promptEditInput").value = "";
      $("systemPromptRoleInput").value = "developer";
      settingsDirty = true;
      renderPromptList();
      updateSettingsSaveButton();
      log("Все промпты будут сброшены после сохранения настроек.");
    });
  }

  window.renderPromptSettings = renderPromptSettings;
  window.renderPromptList = renderPromptList;
  window.renderPromptEditor = renderPromptEditor;
  window.syncSelectedPromptFromEditor = syncSelectedPromptFromEditor;
  window.readPromptSettings = readPromptSettings;
  window.bindPromptSettingsActions = bindPromptSettingsActions;
}());
