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

function bindToolActions() {
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
}
