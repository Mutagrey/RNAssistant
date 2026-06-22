function renderTools() {
  renderResourceList({
    listId: "toolsList",
    searchInputId: "toolSearchInput",
    items: state.tools,
    emptyText: state.bridgeUnavailable ? "Office bridge недоступен. Инструменты загрузятся внутри add-in." : "Инструменты пока не загружены.",
    getSelectedIndex: function () { return state.selectedToolIndex; },
    setSelectedIndex: function (index) { state.selectedToolIndex = index; },
    matches: toolMatchesSearch,
    title: function (tool) { return tool.Id || tool.Name || "инструмент"; },
    enabled: function (tool) { return tool.Enabled !== false; },
    meta: function (tool) { return (tool.Host || "Common") + " - " + (tool.Executor || (tool.BuiltIn ? "builtin" : "pipeline")); },
    description: function (tool) { return tool.Description || (tool.BuiltIn ? "Встроенный Office-инструмент" : "Пользовательский инструмент"); },
    groupKey: function (tool) { return (tool.Host || "Common") + ":" + (tool.BuiltIn ? "builtin" : "custom"); },
    groupLabel: function (tool) { return (tool.Host || "Common") + " · " + (tool.BuiltIn ? "built-in" : "custom"); },
    groupStoragePrefix: "tools",
    compact: true,
    syncEditor: syncSelectedToolFromEditor,
    renderEditor: renderToolEditor,
    renderList: renderTools
  });
}

function toolMatchesSearch(skill, query) {
  var text = [
    skill.Id || "",
    skill.Name || "",
    skill.Host || "",
    skill.Executor || "",
    skill.Description || ""
  ].join(" ").toLowerCase();
  return text.indexOf(query) >= 0;
}

function renderToolEditor() {
  var skill = state.tools[state.selectedToolIndex] || null;
  var disabled = !skill;
  var builtIn = !!(skill && skill.BuiltIn);
  var panel = $("toolEditorPanel");
  var empty = $("toolEditorEmpty");
  if (panel) {
    panel.classList.toggle("is-empty", disabled);
  }
  if (empty) {
    empty.querySelector(".resource-editor-empty-title").textContent = state.bridgeUnavailable ? "Инструменты недоступны" : "Инструмент не выбран";
    empty.querySelector(".resource-editor-empty-text").textContent = state.bridgeUnavailable
      ? "Откройте RNAssistant внутри Office, чтобы загрузить built-in и пользовательские инструменты."
      : "Выберите инструмент слева или создайте новый.";
  }
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
  if (typeof setCodeEditorValue === "function") {
    setCodeEditorValue("toolSchemaInput", $("toolSchemaInput").value);
    setCodeEditorValue("toolRunArgsInput", $("toolRunArgsInput").value);
    setCodeEditorValue("toolPipelineInput", $("toolPipelineInput").value);
    setCodeEditorValue("toolCodeInput", $("toolCodeInput").value);
    setCodeEditorValue("toolReadmeInput", $("toolReadmeInput").value);
  }
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
  if (typeof setCodeEditorReadOnly === "function") {
    setCodeEditorReadOnly("toolSchemaInput", disabled || builtIn);
    setCodeEditorReadOnly("toolRunArgsInput", disabled);
    setCodeEditorReadOnly("toolPipelineInput", disabled || builtIn);
    setCodeEditorReadOnly("toolCodeInput", disabled || builtIn);
    setCodeEditorReadOnly("toolReadmeInput", disabled || builtIn);
  }

  $("deleteToolButton").disabled = disabled || builtIn;
  $("dryRunToolButton").disabled = disabled;
  $("runToolButton").disabled = disabled;
  $("cloneToolButton").disabled = disabled;
  $("copyToolContextButton").disabled = disabled;
  $("askToolBuilderButton").disabled = disabled;
  $("addToolButton").disabled = !!state.bridgeUnavailable;
  $("saveToolsButton").disabled = !!state.bridgeUnavailable;
}

function syncSelectedToolFromEditor() {
  if (typeof syncCodeEditors === "function") {
    syncCodeEditors(["toolSchemaInput", "toolRunArgsInput", "toolPipelineInput", "toolCodeInput", "toolReadmeInput"]);
  }
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
  if (typeof syncCodeEditors === "function") {
    syncCodeEditors(["toolRunArgsInput"]);
  }
  var text = (typeof getCodeEditorValue === "function" ? getCodeEditorValue("toolRunArgsInput") : $("toolRunArgsInput").value).trim();
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

  setActivity(dryRun ? "checking" : "executing", dryRun ? "Проверяю инструмент..." : "Запускаю инструмент...");
  $("toolRunOutput").textContent = dryRun ? "Проверка..." : "Выполняю...";
  try {
    var response = await send("runTool", {
      toolId: skill.Id,
      arguments: parseRunArguments(),
      dryRun: !!dryRun
    });
    $("toolRunOutput").textContent = JSON.stringify(response, null, 2);
    logToolResult(dryRun ? "Проверка инструмента" : "Запуск инструмента", skill.Id, response);
  } catch (error) {
    $("toolRunOutput").textContent = error.detail || error.message;
    log(error.message);
  } finally {
    clearActivity();
  }
}

function bindToolActions() {
  $("toolSearchInput").addEventListener("input", renderTools);

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
      log("Инструменты сохранены.");
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
    log("Контекст инструмента скопирован.");
  });

  $("askToolBuilderButton").addEventListener("click", function () {
    addSelectedToolContextToContext().then(function (added) {
      if (!added) {
        return;
      }

      switchTab("chat");
      setChatInputText("Отредактируй RNAssistant-инструмент из добавленного контекста. Верни обновленные tool.json/pipeline/code блоки, не выполняй действия без подтверждения.", true);
    }).catch(function (error) {
      log(error.detail || error.message);
    });
  });
}
