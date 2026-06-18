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
