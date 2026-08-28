var toolStructuredEditor = window.RNAssistantToolStructuredEditor.create({
  state: state,
  markDirty: markToolLibraryDirty
});
var toolActions = window.RNAssistantToolActions.create({
  state: state,
  send: send,
  setBusy: setControlBusy,
  setOutput: function (value) { $("toolRunOutput").textContent = value || ""; },
  syncSelected: syncSelectedToolFromEditor,
  validateSelected: validateSelectedToolEditors,
  validateAll: validateAllToolDefinitions,
  readTools: readTools,
  readRunArguments: toolStructuredEditor.readRunArguments,
  renderTools: renderTools,
  renderEditor: renderToolEditor,
  acceptSaved: acceptToolLibraryState,
  log: log,
  logToolResult: logToolResult
});

function renderTools() {
  renderInstructions();
}

function emptyToolSchema() {
  return "{\n  \"type\": \"object\",\n  \"properties\": {},\n  \"required\": [],\n  \"additionalProperties\": false\n}";
}

function toolComponents(tool) {
  if (!tool) {
    return [];
  }
  var components = tool.Components || tool.components;
  if (!Array.isArray(components)) {
    components = [];
  }
  if (!components.length && (tool.Code || tool.code)) {
    components.push({
      Name: tool.EntryModuleName || tool.entryModuleName || inferredVbaComponentName(tool),
      Type: "StdModule",
      FileName: inferredVbaComponentName(tool) + ".bas",
      Code: tool.Code || tool.code || ""
    });
  }
  tool.Components = components;
  return components;
}

function inferredVbaComponentName(tool) {
  var raw = String((tool && (tool.Id || tool.id)) || "Tool").replace(/[^A-Za-z0-9_]/g, "_");
  if (!/^[A-Za-z]/.test(raw)) {
    raw = "Tool_" + raw;
  }
  return ("RNA_" + raw).slice(0, 40);
}

function writableToolLibraryItems(tools) {
  return (tools || []).filter(function (tool) {
    return tool && !tool.BuiltIn && String(tool.Scope || tool.scope || "global").toLowerCase() !== "document";
  });
}

function toolLibraryComparable(tool) {
  var components = Array.isArray(tool.Components || tool.components) ? (tool.Components || tool.components) : [];
  return {
      Id: tool.Id || "",
      Host: tool.Host || "Common",
      Name: tool.Name || tool.Id || "",
      Description: tool.Description || "",
      ArgumentSchemaJson: tool.ArgumentSchemaJson || emptyToolSchema(),
      Executor: tool.Executor || "vba",
      RequiresConfirmation: !!tool.RequiresConfirmation,
      Code: tool.Code || "",
      Readme: tool.Readme || "",
      Enabled: tool.Enabled !== false,
      MutatesDocument: !!tool.MutatesDocument,
      MutatesLocalState: !!tool.MutatesLocalState,
      AgentCanRun: !!tool.AgentCanRun,
      RiskLevel: Number(tool.RiskLevel || 0),
      UseWhen: tool.UseWhen || "",
      DoNotUseWhen: tool.DoNotUseWhen || "",
      CapabilityStatus: tool.CapabilityStatus || "available",
      Limitations: tool.Limitations || "",
      PackageVersion: tool.PackageVersion || "1.0.0",
      EntryPoint: tool.EntryPoint || "",
      ArgumentOrder: tool.ArgumentOrder || [],
      Components: components.map(function (component) {
        return {
          Name: component.Name || "",
          Type: component.Type || "StdModule",
          FileName: component.FileName || "",
          Code: component.Code || "",
          CodeSha256: component.CodeSha256 || ""
        };
      })
  };
}

function toolLibraryIdentity(tool) {
  var storagePath = String(tool && (tool.StoragePath || tool.storagePath) || "").toLowerCase();
  return storagePath ? "path:" + storagePath : "id:" + String(tool && (tool.Id || tool.id) || "").toLowerCase();
}

function toolLibraryRecords(tools) {
  return writableToolLibraryItems(tools).map(function (tool) {
    return {
      entity: tool,
      identity: toolLibraryIdentity(tool),
      id: String(tool.Id || "").toLowerCase(),
      comparable: toolLibraryComparable(tool)
    };
  });
}

function toolLibrarySnapshot(tools) {
  return JSON.stringify(toolLibraryRecords(tools).map(function (item) { return item.comparable; }));
}

function toolRecordIndex(records) {
  var byIdentity = {};
  var byId = {};
  (records || []).forEach(function (item) {
    if (item.identity) byIdentity[item.identity] = item;
    if (item.id) byId[item.id] = item;
  });
  return { byIdentity: byIdentity, byId: byId };
}

function matchingToolRecord(index, record) {
  return index.byIdentity[record.identity] || index.byId[record.id] || null;
}

function toolRecordChanged(current, baseline) {
  return !baseline || JSON.stringify(current.comparable) !== JSON.stringify(baseline.comparable);
}

function setToolLibraryBaseline(tools) {
  state.toolLibraryBaselineItems = toolLibraryRecords(tools);
  state.toolLibraryBaseline = toolLibrarySnapshot(tools);
}

function reconcileToolLibraryCatalog(serverTools) {
  var currentRecords = toolLibraryRecords(state.tools);
  var currentIndex = toolRecordIndex(currentRecords);
  var baselineIndex = toolRecordIndex(state.toolLibraryBaselineItems || []);
  var used = [];
  var merged = [];
  (serverTools || []).forEach(function (serverTool) {
    if (!serverTool || serverTool.BuiltIn || String(serverTool.Scope || serverTool.scope || "global").toLowerCase() === "document") {
      if (serverTool) merged.push(serverTool);
      return;
    }
    var serverRecord = toolLibraryRecords([serverTool])[0];
    var current = matchingToolRecord(currentIndex, serverRecord);
    var baseline = matchingToolRecord(baselineIndex, serverRecord);
    if (!current && baseline) return;
    if (current) used.push(current.entity);
    merged.push(current && toolRecordChanged(current, baseline) ? current.entity : serverTool);
  });
  currentRecords.forEach(function (current) {
    if (used.indexOf(current.entity) >= 0) return;
    var baseline = matchingToolRecord(baselineIndex, current);
    if (toolRecordChanged(current, baseline)) merged.push(current.entity);
  });
  setToolLibraryBaseline(serverTools);
  state.tools = merged;
  updateToolLibraryDirty();
  return state.tools;
}

function updateToolSaveButton() {
  var button = $("saveToolsButton");
  if (!button) return;
  button.hidden = !state.toolLibraryDirty;
  button.disabled = !!state.bridgeUnavailable || !state.toolLibraryDirty;
}

function updateToolLibraryDirty() {
  state.toolLibraryDirty = toolLibrarySnapshot(state.tools) !== state.toolLibraryBaseline;
  updateToolSaveButton();
}

function markToolLibraryDirty() {
  syncSelectedToolFromEditor();
  updateToolLibraryDirty();
}

function acceptToolLibraryState() {
  setToolLibraryBaseline(state.tools);
  state.toolLibraryDirty = false;
  updateToolSaveButton();
}

function selectedToolComponent(tool) {
  var components = toolComponents(tool);
  var index = Number(state.selectedToolComponentIndex || 0);
  if (index < 0 || index >= components.length) {
    index = 0;
  }
  state.selectedToolComponentIndex = index;
  return components[index] || null;
}

function vbaComponentFileName(name, type) {
  return name + (type === "ClassModule" ? ".cls" : type === "MSForm" ? ".form.vba" : ".bas");
}

function syncSelectedToolComponentFromEditor(tool) {
  if (!tool || String(tool.Executor || "").toLowerCase() !== "vba") {
    return;
  }
  var component = selectedToolComponent(tool);
  if (!component) {
    return;
  }
  component.Name = $("toolComponentNameInput").value.trim();
  component.Type = $("toolComponentTypeInput").value;
  component.FileName = vbaComponentFileName(component.Name, component.Type);
  component.Code = typeof getCodeEditorValue === "function" ? getCodeEditorValue("toolCodeInput") : $("toolCodeInput").value;
  var entry = toolComponents(tool).filter(function (item) {
    return item && String(item.Type || "").toLowerCase() === "stdmodule" && String(item.Code || "").indexOf("<RNAssistantTool>") >= 0;
  })[0];
  tool.Code = entry ? (entry.Code || "") : "";
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

function applyToolEditorPage() {
  var page = state.toolEditorPage || "main";
  Array.prototype.slice.call(document.querySelectorAll(".tool-page-button")).forEach(function (button) { button.classList.toggle("active", button.getAttribute("data-tool-page") === page); });
  Array.prototype.slice.call(document.querySelectorAll("[data-tool-page-view]")).forEach(function (view) { view.classList.toggle("hidden", view.getAttribute("data-tool-page-view") !== page); });
  if (typeof refreshCodeEditors === "function") refreshCodeEditors();
}

function renderToolEditor() {
  var skill = state.tools[state.selectedToolIndex] || null;
  var disabled = !skill;
  var builtIn = !!(skill && skill.BuiltIn);
  var documentLocal = !!(skill && String(skill.Scope || skill.scope || "").toLowerCase() === "document");
  var readOnly = builtIn || documentLocal;
  state.toolBuilderReadOnly = disabled || readOnly;
  var isVba = !!(skill && String(skill.Executor || "").toLowerCase() === "vba");
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
  if ($("toolEditorTitle")) $("toolEditorTitle").textContent = skill ? (skill.Id || skill.Name || "Инструмент") : "Инструмент";
  if ($("toolEditorMeta")) $("toolEditorMeta").textContent = skill ? ((builtIn ? "Встроенный" : "Пользовательский") + " · " + (skill.Host || "Common") + " · " + (skill.Executor || "vba")) : "";
  $("toolEnabledInput").checked = skill ? skill.Enabled !== false : false;
  $("toolIdInput").value = skill ? (skill.Id || "") : "";
  $("toolHostInput").value = skill ? (skill.Host || "Common") : "Common";
  $("toolExecutorInput").value = skill ? (skill.Executor || (builtIn ? "builtin" : "vba")) : "vba";
  $("toolConfirmInput").checked = skill ? !!skill.RequiresConfirmation : false;
  $("toolDescriptionInput").value = skill ? (skill.Description || "") : "";
  $("toolSchemaInput").value = skill ? (skill.ArgumentSchemaJson || emptyToolSchema()) : emptyToolSchema();
  $("toolRunArgsInput").value = skill ? "{}" : "";
  var components = isVba ? toolComponents(skill) : [];
  var component = isVba ? selectedToolComponent(skill) : null;
  $("toolComponentSelect").innerHTML = "";
  components.forEach(function (item, index) {
    var option = document.createElement("option");
    option.value = String(index);
    option.textContent = (item.Name || "Component") + " · " + (item.Type || "StdModule");
    $("toolComponentSelect").appendChild(option);
  });
  $("toolComponentSelect").value = String(state.selectedToolComponentIndex || 0);
  $("toolComponentNameInput").value = component ? (component.Name || "") : "";
  $("toolComponentTypeInput").value = component && (component.Type === "ClassModule" || component.Type === "MSForm") ? component.Type : "StdModule";
  $("toolCodeInput").value = component ? (component.Code || "") : (skill ? (skill.Code || "") : "");
  $("toolReadmeInput").value = skill ? (skill.Readme || "") : "";
  $("toolPackageMeta").textContent = skill ? [
    "scope=" + (skill.Scope || "global"),
    "version=" + (skill.PackageVersion || "—"),
    "entry=" + (skill.EntryPoint || "—"),
    "status=" + (skill.InstallationStatus || skill.CapabilityStatus || "available"),
    skill.CodeSha256 ? "hash=" + String(skill.CodeSha256).slice(0, 12) : ""
  ].filter(Boolean).join(" · ") : "";
  if (typeof setCodeEditorValue === "function") {
    setCodeEditorValue("toolSchemaInput", $("toolSchemaInput").value);
    setCodeEditorValue("toolRunArgsInput", $("toolRunArgsInput").value);
    setCodeEditorValue("toolCodeInput", $("toolCodeInput").value);
    setCodeEditorValue("toolReadmeInput", $("toolReadmeInput").value);
  }
  $("toolRunOutput").textContent = "";
  state.toolSchemaVisualDraft = null;
  toolStructuredEditor.syncSchemaDraft();
  state.toolLibraryRendering = true;
  try {
    toolStructuredEditor.setMode(state.toolSchemaMode || "form");
  } finally {
    state.toolLibraryRendering = false;
  }
  if ($("vbaToolEditor")) $("vbaToolEditor").classList.toggle("hidden", !isVba);
  applyToolEditorPage();

  [
    "toolEnabledInput",
    "toolIdInput",
    "toolHostInput",
    "toolExecutorInput",
    "toolConfirmInput",
    "toolDescriptionInput",
    "toolSchemaInput",
    "toolRunArgsInput",
    "toolCodeInput",
    "toolReadmeInput"
  ].forEach(function (id) {
    $(id).disabled = disabled || readOnly;
  });
  $("toolRunArgsInput").disabled = disabled;
  if (typeof setCodeEditorReadOnly === "function") {
    setCodeEditorReadOnly("toolSchemaInput", disabled || readOnly);
    setCodeEditorReadOnly("toolRunArgsInput", disabled);
    setCodeEditorReadOnly("toolCodeInput", disabled || readOnly || !isVba);
    setCodeEditorReadOnly("toolReadmeInput", disabled || readOnly);
  }

  ["toolComponentSelect", "toolComponentNameInput", "toolComponentTypeInput", "addToolModuleButton", "addToolClassButton", "addToolFormButton", "deleteToolComponentButton"].forEach(function (id) {
    $(id).disabled = disabled || readOnly || !isVba;
  });

  $("deleteToolButton").disabled = disabled || readOnly;
  $("dryRunToolButton").disabled = disabled;
  $("runToolButton").disabled = disabled;
  $("cloneToolButton").disabled = disabled || builtIn;
  $("copyToolContextButton").disabled = disabled;
  $("askToolBuilderButton").disabled = disabled;
  $("addToolButton").disabled = !!state.bridgeUnavailable;
  updateToolSaveButton();
  $("vbaPackageActions").hidden = !isVba || builtIn || documentLocal;
  $("installVbaToolButton").disabled = !isVba || builtIn || documentLocal || !!state.bridgeUnavailable;
  $("uninstallVbaToolButton").disabled = $("installVbaToolButton").disabled || String(skill && skill.InstallationStatus || "") === "not_installed";
}

function syncSelectedToolFromEditor() {
  if (typeof syncCodeEditors === "function") {
    syncCodeEditors(["toolSchemaInput", "toolRunArgsInput", "toolCodeInput", "toolReadmeInput"]);
  }
  var skill = state.tools[state.selectedToolIndex];
  if (!skill || skill.BuiltIn) {
    return;
  }

  syncSelectedToolComponentFromEditor(skill);

  skill.Id = $("toolIdInput").value.trim();
  skill.Host = $("toolHostInput").value;
  skill.Name = skill.Id;
  skill.Executor = $("toolExecutorInput").value;
  skill.RequiresConfirmation = $("toolConfirmInput").checked;
  skill.Description = $("toolDescriptionInput").value;
  skill.ArgumentSchemaJson = $("toolSchemaInput").value || emptyToolSchema();
  if (String(skill.Executor || "").toLowerCase() !== "vba") {
    skill.Code = $("toolCodeInput").value;
  }
  skill.Components = toolComponents(skill);
  skill.Readme = $("toolReadmeInput").value;
  skill.Enabled = $("toolEnabledInput").checked;
  skill.BuiltIn = false;
}

function validateSelectedToolEditors() {
  var tool = state.tools[state.selectedToolIndex];
  if (!tool) return true;
  if (!toolStructuredEditor.syncSchemaDraft()) { state.toolEditorPage = "schema"; applyToolEditorPage(); return false; }
  return true;
}

function validateAllToolDefinitions() {
  for (var index = 0; index < state.tools.length; index += 1) {
    var tool = state.tools[index] || {};
    try { JSON.parse(tool.ArgumentSchemaJson || emptyToolSchema()); }
    catch (error) { throw new Error("Некорректная schema у " + (tool.Id || "инструмента") + ": " + error.message); }

  }
}

function readTools() {
  syncSelectedToolFromEditor();
  return state.tools.map(function (skill) {
    return {
      Id: skill.Id || "",
      Host: skill.Host || "Common",
      Name: skill.Name || skill.Id || "",
      Description: skill.Description || "",
      ArgumentSchemaJson: skill.ArgumentSchemaJson || emptyToolSchema(),
      Executor: skill.Executor || (skill.BuiltIn ? "builtin" : "vba"),
      RequiresConfirmation: !!skill.RequiresConfirmation,
      Code: skill.Code || "",
      Readme: skill.Readme || "",
      Enabled: skill.Enabled !== false,
      BuiltIn: !!skill.BuiltIn,
      MutatesDocument: !!skill.MutatesDocument,
      MutatesLocalState: !!skill.MutatesLocalState,
      AgentCanRun: !!skill.AgentCanRun,
      RiskLevel: Number(skill.RiskLevel || 0),
      UseWhen: skill.UseWhen || "",
      DoNotUseWhen: skill.DoNotUseWhen || "",
      CapabilityStatus: skill.CapabilityStatus || "available",
      Limitations: skill.Limitations || "",
      PackageVersion: skill.PackageVersion || "1.0.0",
      EntryPoint: skill.EntryPoint || "",
      ArgumentOrder: skill.ArgumentOrder || [],
      Components: toolComponents(skill).map(function (component) {
        return {
          Name: component.Name || "",
          Type: component.Type || "StdModule",
          FileName: component.FileName || "",
          Code: component.Code || "",
          CodeSha256: component.CodeSha256 || ""
        };
      }),
      Scope: skill.Scope || "global",
      InstallationStatus: skill.InstallationStatus || ""
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
    "executor: " + (skill.Executor || "vba"),
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
    "## Code",
    toolComponents(skill).map(function (component) {
      return "### " + (component.Name || "Component") + " (" + (component.Type || "StdModule") + ")\n```vba\n" + (component.Code || "") + "\n```";
    }).join("\n\n") || "```vba\n" + (skill.Code || "") + "\n```",
    "",
    "## README",
    skill.Readme || ""
  ].join("\n");
}

function addVbaComponent(type) {
  syncSelectedToolFromEditor();
  var tool = state.tools[state.selectedToolIndex];
  if (!tool || String(tool.Executor || "").toLowerCase() !== "vba") {
    return;
  }
  var components = toolComponents(tool);
  var suffix = type === "ClassModule" ? "Service" : type === "MSForm" ? "Form" : "Module";
  var base = inferredVbaComponentName(tool).slice(0, Math.max(1, 39 - suffix.length));
  var name = (base + "_" + suffix).slice(0, 40);
  var serial = 2;
  while (components.some(function (component) { return String(component.Name || "").toLowerCase() === name.toLowerCase(); })) {
    name = (base.slice(0, Math.max(1, 38 - String(serial).length)) + "_" + serial).slice(0, 40);
    serial += 1;
  }
  components.push({ Name: name, Type: type, FileName: vbaComponentFileName(name, type), Code: "Option Explicit\n" });
  state.selectedToolComponentIndex = components.length - 1;
  updateToolLibraryDirty();
  renderToolEditor();
}

function bindToolActions() {
  Array.prototype.slice.call(document.querySelectorAll(".tool-page-button")).forEach(function (button) { button.addEventListener("click", function () { syncSelectedToolFromEditor(); state.toolEditorPage = button.getAttribute("data-tool-page") || "main"; applyToolEditorPage(); }); });
  toolStructuredEditor.bind();

  $("toolComponentSelect").addEventListener("change", function () {
    var tool = state.tools[state.selectedToolIndex];
    syncSelectedToolComponentFromEditor(tool);
    state.selectedToolComponentIndex = Number($("toolComponentSelect").value || 0);
    renderToolEditor();
  });
  $("addToolModuleButton").addEventListener("click", function () { addVbaComponent("StdModule"); });
  $("addToolClassButton").addEventListener("click", function () { addVbaComponent("ClassModule"); });
  $("addToolFormButton").addEventListener("click", function () { addVbaComponent("MSForm"); });
  $("deleteToolComponentButton").addEventListener("click", function () {
    syncSelectedToolFromEditor();
    var tool = state.tools[state.selectedToolIndex];
    var components = toolComponents(tool);
    if (!components.length) return;
    components.splice(Number(state.selectedToolComponentIndex || 0), 1);
    state.selectedToolComponentIndex = Math.max(0, Math.min(Number(state.selectedToolComponentIndex || 0), components.length - 1));
    updateToolLibraryDirty();
    renderToolEditor();
  });
  $("toolExecutorInput").addEventListener("change", function () {
    var tool = state.tools[state.selectedToolIndex];
    if (!tool) return;
    tool.Executor = $("toolExecutorInput").value;
    if (tool.Executor === "vba" && !toolComponents(tool).length) {
      var name = inferredVbaComponentName(tool);
      tool.Components = [{ Name: name, Type: "StdModule", FileName: name + ".bas", Code: "Option Explicit\n" }];
      state.selectedToolComponentIndex = 0;
    }
    updateToolLibraryDirty();
    renderToolEditor();
  });
  $("installVbaToolButton").addEventListener("click", toolActions.installVba);
  $("uninstallVbaToolButton").addEventListener("click", toolActions.uninstallVba);

  $("addToolButton").addEventListener("click", function () {
    if (typeof syncSelectedLibraryItem === "function") syncSelectedLibraryItem();
    else if (state.selectedInstructionKind === "tool") syncSelectedToolFromEditor();
    state.tools.push({
      Id: (state.host || "common").toLowerCase() + ".new_tool",
      Host: state.host || "Common",
      Name: "new_tool",
      Description: "",
      ArgumentSchemaJson: emptyToolSchema(),
      Executor: "vba",
      RequiresConfirmation: true,
      Code: "",
      Readme: "",
      Enabled: true,
      BuiltIn: false,
      MutatesDocument: true,
      AgentCanRun: false,
      RiskLevel: 1,
      CapabilityStatus: "available",
      Scope: "global",
      PackageVersion: "1.0.0",
      Components: [{ Name: "RNA_NewTool", Type: "StdModule", FileName: "RNA_NewTool.bas", Code: "Option Explicit\n" }]
    });
    state.selectedToolIndex = state.tools.length - 1;
    state.selectedInstructionKind = "tool";
    updateToolLibraryDirty();
    renderTools();
  });

  $("cloneToolButton").addEventListener("click", function () {
    syncSelectedToolFromEditor();
    var source = state.tools[state.selectedToolIndex];
    if (!source || source.BuiltIn) {
      return;
    }

    var id = (source.Id || "tool") + ".copy";
    state.tools.push({
      Id: id,
      Host: source.Host || state.host || "Common",
      Name: id,
      Description: source.Description || "",
      ArgumentSchemaJson: source.ArgumentSchemaJson || emptyToolSchema(),
      Executor: source.BuiltIn ? "vba" : (source.Executor || "vba"),
      RequiresConfirmation: source.BuiltIn ? true : !!source.RequiresConfirmation,
      Code: source.Code || "",
      Readme: source.Readme || "",
      Enabled: true,
      BuiltIn: false,
      MutatesDocument: source.BuiltIn ? true : !!source.MutatesDocument,
      MutatesLocalState: source.BuiltIn ? false : !!source.MutatesLocalState,
      AgentCanRun: source.BuiltIn ? false : !!source.AgentCanRun,
      RiskLevel: Number(source.RiskLevel || (source.BuiltIn ? 1 : 0)),
      UseWhen: source.UseWhen || "",
      DoNotUseWhen: source.DoNotUseWhen || "",
      CapabilityStatus: source.CapabilityStatus || "available",
      Limitations: source.Limitations || "",
      PackageVersion: source.PackageVersion || "1.0.0",
      EntryPoint: source.EntryPoint || "",
      ArgumentOrder: (source.ArgumentOrder || []).slice(),
      Components: toolComponents(source).map(function (component) { return JSON.parse(JSON.stringify(component)); }),
      Scope: "global",
      InstallationStatus: "not_installed"
    });
    state.selectedToolIndex = state.tools.length - 1;
    state.selectedInstructionKind = "tool";
    updateToolLibraryDirty();
    renderTools();
  });

  $("saveToolsButton").addEventListener("click", toolActions.save);

  $("deleteToolButton").addEventListener("click", function () {
    var skill = state.tools[state.selectedToolIndex];
    if (!skill || skill.BuiltIn) {
      return;
    }

    state.tools.splice(state.selectedToolIndex, 1);
    if (state.selectedToolIndex >= state.tools.length) {
      state.selectedToolIndex = state.tools.length - 1;
    }
    updateToolLibraryDirty();
    renderTools();
  });

  $("dryRunToolButton").addEventListener("click", toolActions.validate);
  $("runToolButton").addEventListener("click", toolActions.run);

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
      setChatInputText("Отредактируй RNAssistant-инструмент из добавленного контекста. Верни обновленные tool.json и VBA .bas/.cls components; не выполняй действия без подтверждения.", true);
    }).catch(function (error) {
      log(error.detail || error.message, "error");
    });
  });

  [
    "toolEnabledInput", "toolIdInput", "toolHostInput", "toolExecutorInput", "toolConfirmInput",
    "toolDescriptionInput", "toolComponentNameInput", "toolComponentTypeInput"
  ].forEach(function (id) {
    var control = $(id);
    if (!control) return;
    control.addEventListener(control.type === "checkbox" || control.tagName === "SELECT" ? "change" : "input", markToolLibraryDirty);
  });
}
