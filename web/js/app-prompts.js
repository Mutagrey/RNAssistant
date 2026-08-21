(function () {
  var promptDefinitions = [
    { key: "systemPrompt", label: "Агент", group: "Основные", source: "root", field: "SystemPrompt", description: "Главные правила Agent-потока, tools и skills." },
    { key: "chatSystemPrompt", label: "Чат", group: "Основные", source: "root", field: "ChatSystemPrompt", description: "Прямой ответ без локальных tools." },
    { key: "contextCompactionPrompt", label: "Сжатие контекста", group: "Служебные", source: "root", field: "ContextCompactionPrompt", description: "Правила создания checkpoint активной истории." },
    { key: "chatTitlePrompt", label: "Название чата", group: "Служебные", source: "root", field: "ChatTitlePrompt", description: "Короткий запрос для генерации названия." }
  ];

  function promptValue(settings, def) {
    settings = settings || {};
    return settings[def.field] !== undefined ? settings[def.field] : (settings[lowerFirst(def.field)] || "");
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

  function promptEditorValue() {
    return typeof getCodeEditorValue === "function"
      ? getCodeEditorValue("promptEditInput")
      : ($("promptEditInput") ? $("promptEditInput").value : "");
  }

  function setPromptEditorValue(value) {
    if (typeof setCodeEditorValue === "function") {
      setCodeEditorValue("promptEditInput", value || "");
    } else if ($("promptEditInput")) {
      $("promptEditInput").value = value || "";
    }
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
      meta: function (def) { return "Markdown · " + def.field + ".md"; },
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
    $("promptMeta").textContent = def.group + " · Markdown · " + def.field + ".md";
    setPromptEditorValue(promptText(def));
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
    state.promptDrafts[def.key] = promptEditorValue();
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
    if (typeof setCodeEditorVisible === "function") {
      setCodeEditorVisible("promptEditInput", mode === "edit");
    } else {
      $("promptEditInput").classList.toggle("hidden", mode !== "edit");
    }
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
      ContextCompactionPrompt: state.promptDrafts.contextCompactionPrompt || "",
      ChatTitlePrompt: state.promptDrafts.chatTitlePrompt || ""
    };
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
      setPromptEditorValue("");
      settingsDirty = true;
      renderPromptList();
      updateSettingsSaveButton();
      log("Все промпты будут сброшены после сохранения настроек.");
    });
  }

  function markPromptEditorDirty() {
    syncSelectedPromptFromEditor();
    settingsDirty = true;
    updateSettingsSaveButton();
  }

  window.renderPromptSettings = renderPromptSettings;
  window.renderPromptList = renderPromptList;
  window.renderPromptEditor = renderPromptEditor;
  window.syncSelectedPromptFromEditor = syncSelectedPromptFromEditor;
  window.readPromptSettings = readPromptSettings;
  window.bindPromptSettingsActions = bindPromptSettingsActions;
  window.markPromptEditorDirty = markPromptEditorDirty;
}());
