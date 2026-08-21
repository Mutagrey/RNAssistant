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
    } else if (state.selectedInstructionKind === "tool") {
      if (typeof syncSelectedToolFromEditor === "function") syncSelectedToolFromEditor();
    } else {
      syncSelectedPromptFromEditor();
    }
  }

  function instructionRows() {
    var query = (($("skillSearchInput") && $("skillSearchInput").value) || "").trim().toLowerCase();
    var rows = [];
    promptDefinitions.forEach(function (def, index) {
      if (!query || promptMatchesSearch(def, query)) rows.push({ kind: "prompt", index: index, value: def });
    });
    (state.skills || []).forEach(function (skill, index) {
      if (!query || (typeof skillMatchesSearch === "function" && skillMatchesSearch(skill, query))) rows.push({ kind: "skill", index: index, value: skill });
    });
    (state.tools || []).forEach(function (tool, index) {
      if (!query || (typeof toolMatchesSearch === "function" && toolMatchesSearch(tool, query))) rows.push({ kind: "tool", index: index, value: tool });
    });
    return rows;
  }

  function instructionRowKey(row) {
    return row.kind + ":" + row.index;
  }

  function selectedInstructionKey() {
    var index = state.selectedInstructionKind === "skill"
      ? state.selectedSkillIndex
      : (state.selectedInstructionKind === "tool" ? state.selectedToolIndex : state.selectedPromptIndex);
    return state.selectedInstructionKind + ":" + index;
  }

  function selectInstructionRow(row) {
    state.selectedInstructionKind = row.kind;
    if (row.kind === "skill") state.selectedSkillIndex = row.index;
    else if (row.kind === "tool") {
      state.selectedToolIndex = row.index;
      state.selectedToolComponentIndex = 0;
    } else state.selectedPromptIndex = row.index;
  }

  function hostName(row) {
    return String((row.value && row.value.Host) || "Common");
  }

  function orderedHosts(rows) {
    var preferred = ["Common", "Excel", "Word", "PowerPoint", "Outlook"];
    var found = {};
    rows.forEach(function (row) { found[hostName(row)] = true; });
    return Object.keys(found).sort(function (left, right) {
      var leftIndex = preferred.indexOf(left);
      var rightIndex = preferred.indexOf(right);
      if (leftIndex < 0) leftIndex = preferred.length;
      if (rightIndex < 0) rightIndex = preferred.length;
      return leftIndex === rightIndex ? left.localeCompare(right) : leftIndex - rightIndex;
    });
  }

  function resourceGroup(key, title, count, nested) {
    var group = createResourceGroup({ key: key, title: title, count: count });
    if (nested) group.className += " resource-tree-subgroup";
    return group;
  }

  function appendInstructionItem(parent, row, activeKey) {
    var value = row.value || {};
    var meta = "Промпт";
    if (row.kind === "skill") meta = value.BuiltIn ? "Встроенный" : "Пользовательский";
    if (row.kind === "tool") meta = value.BuiltIn ? "Встроенный" : (value.Executor || "pipeline");
    parent.appendChild(createResourceListItem({
      title: row.kind === "prompt" ? value.label : (value.Id || value.Name || (row.kind === "tool" ? "Инструмент" : "Навык")),
      meta: meta,
      description: row.kind === "prompt" ? value.description : (value.Description || (row.kind === "tool" ? "Office-инструмент" : "Инструкция навыка")),
      enabled: row.kind === "prompt" ? null : value.Enabled !== false,
      active: instructionRowKey(row) === activeKey,
      compact: true,
      onClick: function () {
        syncSelectedInstruction();
        selectInstructionRow(row);
        renderInstructions();
      }
    }));
  }

  function appendPromptGroups(parent, rows, activeKey) {
    var prompts = resourceGroup("library:instructions:prompts", "Промпты", rows.length, true);
    parent.appendChild(prompts);
    ["Основные", "Служебные"].forEach(function (name) {
      var grouped = rows.filter(function (row) { return row.value.group === name; });
      if (!grouped.length) return;
      var group = resourceGroup("library:prompts:" + name, name, grouped.length, true);
      prompts.treeChildren.appendChild(group);
      grouped.forEach(function (row) { appendInstructionItem(group.treeChildren, row, activeKey); });
    });
  }

  function appendHostedGroups(parent, key, title, rows, activeKey) {
    var root = resourceGroup("library:" + key, title, rows.length, true);
    parent.appendChild(root);
    appendHostGroups(root.treeChildren, key, rows, activeKey);
  }

  function appendHostGroups(parent, key, rows, activeKey) {
    orderedHosts(rows).forEach(function (host) {
      var hosted = rows.filter(function (row) { return hostName(row) === host; });
      var group = resourceGroup("library:" + key + ":" + host, host, hosted.length, true);
      parent.appendChild(group);
      hosted.forEach(function (row) { appendInstructionItem(group.treeChildren, row, activeKey); });
    });
  }

  function renderInstructions() {
    var list = $("instructionsList");
    if (!list) return;
    var rows = instructionRows();
    list.innerHTML = "";
    if (!rows.length) {
      list.appendChild(createResourceEmptyState("В библиотеке ничего не найдено."));
      renderInstructionEditor();
      return;
    }
    var key = selectedInstructionKey();
    if (!rows.some(function (row) { return instructionRowKey(row) === key; })) {
      selectInstructionRow(rows[0]);
      key = instructionRowKey(rows[0]);
    }
    var prompts = rows.filter(function (row) { return row.kind === "prompt"; });
    var skills = rows.filter(function (row) { return row.kind === "skill"; });
    var tools = rows.filter(function (row) { return row.kind === "tool"; });
    if (prompts.length || skills.length) {
      var instructions = resourceGroup("library:instructions", "Инструкции", prompts.length + skills.length, false);
      list.appendChild(instructions);
      if (prompts.length) appendPromptGroups(instructions.treeChildren, prompts, key);
      if (skills.length) appendHostedGroups(instructions.treeChildren, "skills", "Навыки", skills, key);
    }
    if (tools.length) {
      var toolRoot = resourceGroup("library:tools", "Инструменты", tools.length, false);
      list.appendChild(toolRoot);
      appendHostGroups(toolRoot.treeChildren, "tools", tools, key);
    }
    renderInstructionEditor();
  }

  function renderInstructionEditor() {
    var promptPanel = $("promptEditorPanel");
    var skillPanel = $("skillEditorPanel");
    var empty = $("instructionEditorEmpty");
    var promptSelected = state.selectedInstructionKind === "prompt" && !!selectedPromptDefinition();
    var skillSelected = state.selectedInstructionKind === "skill" && !!state.skills[state.selectedSkillIndex];
    var toolSelected = state.selectedInstructionKind === "tool" && !!state.tools[state.selectedToolIndex];
    var instructionPanel = $("instructionEditorPanel");
    var toolPanel = $("toolEditorPanel");
    if (instructionPanel) instructionPanel.classList.toggle("hidden", toolSelected);
    if (toolPanel) toolPanel.classList.toggle("hidden", !toolSelected);
    if (empty) empty.style.display = promptSelected || skillSelected ? "none" : "grid";
    if (promptPanel) promptPanel.classList.toggle("hidden", !promptSelected);
    if (skillPanel) skillPanel.classList.toggle("hidden", !skillSelected);
    if (promptSelected) renderPromptEditor();
    if (skillSelected && typeof renderSkillEditor === "function") renderSkillEditor();
    if (toolSelected && typeof renderToolEditor === "function") renderToolEditor();
    if ($("cloneSkillButton")) $("cloneSkillButton").classList.toggle("hidden", !skillSelected);
    if ($("cloneToolButton")) $("cloneToolButton").classList.toggle("hidden", !toolSelected);
  }

  function mountUnifiedLibrary() {
    var layout = $("instructionsLayout");
    var toolPanel = $("toolEditorPanel");
    if (layout && toolPanel && toolPanel.parentNode !== layout) layout.appendChild(toolPanel);
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
    mountUnifiedLibrary();
    $("skillSearchInput").addEventListener("input", function () {
      syncSelectedInstruction();
      renderInstructions();
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
  window.syncSelectedLibraryItem = syncSelectedInstruction;
}());
