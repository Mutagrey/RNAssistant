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

  function syncSelectedInstruction() {
    if (state.selectedInstructionKind === "skill") {
      if (typeof syncSelectedSkillFromEditor === "function") syncSelectedSkillFromEditor();
    } else {
      syncSelectedPromptFromEditor();
    }
  }

  function instructionRows() {
    var query = (($("skillSearchInput") && $("skillSearchInput").value) || "").trim().toLowerCase();
    var filter = state.instructionFilter || "all";
    var rows = [];
    if (filter === "all" || filter === "prompt") {
      promptDefinitions.forEach(function (def, index) {
        if (!query || promptMatchesSearch(def, query)) rows.push({ kind: "prompt", index: index, value: def });
      });
    }
    if (filter === "all" || filter === "skill") {
      (state.skills || []).forEach(function (skill, index) {
        if (!query || (typeof skillMatchesSearch === "function" && skillMatchesSearch(skill, query))) rows.push({ kind: "skill", index: index, value: skill });
      });
    }
    return rows;
  }

  function instructionGroup(row) {
    if (row.kind === "prompt") return "Промпты";
    return row.value && row.value.BuiltIn ? "Встроенные навыки" : "Пользовательские навыки";
  }

  function selectedInstructionKey() {
    return state.selectedInstructionKind + ":" + (state.selectedInstructionKind === "skill" ? state.selectedSkillIndex : state.selectedPromptIndex);
  }

  function renderInstructions() {
    var list = $("instructionsList");
    if (!list) return;
    var rows = instructionRows();
    list.innerHTML = "";
    Array.prototype.slice.call(document.querySelectorAll("[data-instruction-filter]")).forEach(function (button) {
      button.classList.toggle("active", button.getAttribute("data-instruction-filter") === (state.instructionFilter || "all"));
    });
    if (!rows.length) {
      list.appendChild(createResourceEmptyState("Инструкции не найдены."));
      renderInstructionEditor();
      return;
    }
    var key = selectedInstructionKey();
    if (!rows.some(function (row) { return row.kind + ":" + row.index === key; })) {
      state.selectedInstructionKind = rows[0].kind;
      if (rows[0].kind === "skill") state.selectedSkillIndex = rows[0].index;
      else state.selectedPromptIndex = rows[0].index;
      key = rows[0].kind + ":" + rows[0].index;
    }
    var groups = {};
    rows.forEach(function (row) {
      var label = instructionGroup(row);
      if (!groups[label]) {
        groups[label] = createResourceGroup({ key: "instructions:" + label, title: label, count: rows.filter(function (candidate) { return instructionGroup(candidate) === label; }).length });
        list.appendChild(groups[label]);
      }
      var value = row.value || {};
      var item = createResourceListItem({
        title: row.kind === "prompt" ? value.label : (value.Id || value.Name || "Навык"),
        meta: row.kind === "prompt" ? "Промпт" : ((value.Host || "Common") + (value.BuiltIn ? " · built-in" : " · custom")),
        description: row.kind === "prompt" ? value.description : (value.Description || "Инструкция навыка"),
        enabled: row.kind === "skill" ? value.Enabled !== false : null,
        active: row.kind + ":" + row.index === key,
        compact: true,
        onClick: function () {
          syncSelectedInstruction();
          state.selectedInstructionKind = row.kind;
          if (row.kind === "skill") state.selectedSkillIndex = row.index;
          else state.selectedPromptIndex = row.index;
          renderInstructions();
        }
      });
      (groups[label].treeChildren || groups[label]).appendChild(item);
    });
    renderInstructionEditor();
  }

  function renderInstructionEditor() {
    var promptPanel = $("promptEditorPanel");
    var skillPanel = $("skillEditorPanel");
    var empty = $("instructionEditorEmpty");
    var promptSelected = state.selectedInstructionKind === "prompt" && !!selectedPromptDefinition();
    var skillSelected = state.selectedInstructionKind === "skill" && !!state.skills[state.selectedSkillIndex];
    if (empty) empty.style.display = promptSelected || skillSelected ? "none" : "grid";
    if (promptPanel) promptPanel.classList.toggle("hidden", !promptSelected);
    if (skillPanel) skillPanel.classList.toggle("hidden", !skillSelected);
    if (promptSelected) renderPromptEditor();
    if (skillSelected && typeof renderSkillEditor === "function") renderSkillEditor();
    if ($("addSkillButton")) $("addSkillButton").hidden = state.instructionFilter === "prompt";
    if ($("cloneSkillButton")) $("cloneSkillButton").hidden = !skillSelected;
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
    renderInstructions();
  }

  function renderPromptEditor() {
    var def = selectedPromptDefinition();
    var disabled = !def;
    var panel = $("promptEditorPanel");
    if (panel) panel.classList.toggle("hidden", disabled);
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
    state.promptEditorMode = mode === "preview" ? "preview" : "edit";
    renderPromptPreview(selectedPromptDefinition());
    applyPromptMode();
  }

  function applyPromptMode() {
    var mode = state.promptEditorMode === "preview" ? "preview" : "edit";
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
    $("skillSearchInput").addEventListener("input", renderInstructions);
    Array.prototype.slice.call(document.querySelectorAll("[data-instruction-filter]")).forEach(function (button) {
      button.addEventListener("click", function () { syncSelectedInstruction(); state.instructionFilter = button.getAttribute("data-instruction-filter") || "all"; renderInstructions(); });
    });
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
    $("resetCurrentPromptButton").addEventListener("click", function () {
      var def = selectedPromptDefinition();
      if (def) state.promptDrafts[def.key] = "";
      setPromptEditorValue("");
      settingsDirty = true;
      renderPromptEditor();
      updateSettingsSaveButton();
      log("Промпт будет сброшен после сохранения.");
    });
    $("savePromptButton").addEventListener("click", async function () {
      setControlBusy("savePromptButton", true);
      try { await persistSettingsFromForm(); log("Промпт сохранён."); }
      catch (error) { log(error.message); }
      finally { setControlBusy("savePromptButton", false); renderInstructions(); }
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
  window.renderInstructions = renderInstructions;
  window.renderInstructionEditor = renderInstructionEditor;
}());
