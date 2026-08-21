function renderTools() {
  renderResourceList({
    listId: "toolsList",
    searchInputId: "toolSearchInput",
    items: state.tools,
    emptyText: state.bridgeUnavailable ? "Office bridge недоступен. Инструменты загрузятся внутри add-in." : "Инструменты пока не загружены.",
    getSelectedIndex: function () { return state.selectedToolIndex; },
    setSelectedIndex: function (index) { state.selectedToolIndex = index; state.selectedToolComponentIndex = 0; },
    matches: toolMatchesSearch,
    title: function (tool) { return tool.Id || tool.Name || "инструмент"; },
    enabled: function (tool) { return tool.Enabled !== false; },
    icon: function (tool) { return tool.BuiltIn ? "BIN" : (String(tool.Executor || "PIPE").slice(0, 4).toUpperCase()); },
    meta: toolListMeta,
    description: function (tool) { return tool.Description || (tool.BuiltIn ? "Встроенный Office-инструмент" : "Пользовательский инструмент"); },
    groupKey: function (tool) { return (tool.Host || "Common") + ":" + (tool.BuiltIn ? "builtin" : "custom"); },
    groupLabel: toolGroupLabel,
    groupStoragePrefix: "tools",
    compact: true,
    syncEditor: syncSelectedToolFromEditor,
    renderEditor: renderToolEditor,
    renderList: renderTools
  });
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

function selectedToolComponent(tool) {
  var components = toolComponents(tool);
  var index = Number(state.selectedToolComponentIndex || 0);
  if (index < 0 || index >= components.length) {
    index = 0;
  }
  state.selectedToolComponentIndex = index;
  return components[index] || null;
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
  component.FileName = component.Name + (component.Type === "ClassModule" ? ".cls" : ".bas");
  component.Code = typeof getCodeEditorValue === "function" ? getCodeEditorValue("toolCodeInput") : $("toolCodeInput").value;
  var entry = toolComponents(tool).filter(function (item) {
    return item && String(item.Type || "").toLowerCase() === "stdmodule" && String(item.Code || "").indexOf("<RNAssistantTool>") >= 0;
  })[0];
  tool.Code = entry ? (entry.Code || "") : "";
}

function cleanHostLabel(host) {
  return String(host || "Common").toLowerCase() === "common" ? "" : (host || "Common");
}

function toolListMeta(tool) {
  if (!tool) {
    return "";
  }
  if (tool.BuiltIn) {
    return cleanHostLabel(tool.Host);
  }
  return tool.Executor || "pipeline";
}

function toolGroupLabel(tool) {
  var host = cleanHostLabel(tool && tool.Host);
  var type = tool && tool.BuiltIn ? "Built-in" : "Custom";
  return host ? host + " · " + type : type;
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

function toolEditorJson(id) {
  if (typeof syncCodeEditors === "function") syncCodeEditors([id]);
  var text = typeof getCodeEditorValue === "function" ? getCodeEditorValue(id) : $(id).value;
  return JSON.parse(text || "{}");
}

function setToolEditorJson(id, value) {
  var text = JSON.stringify(value, null, 2);
  $(id).value = text;
  if (typeof setCodeEditorValue === "function") setCodeEditorValue(id, text);
}

function toolJsonError(id, message) {
  if ($(id)) $(id).textContent = message || "";
}

function schemaDefaultText(value) {
  return value === undefined ? "" : (typeof value === "string" ? value : JSON.stringify(value));
}

function parseSchemaDefault(text, type) {
  if (text === "") return undefined;
  if (type === "boolean") return String(text).toLowerCase() === "true";
  if (type === "number" || type === "integer") return Number(text);
  if (type === "array" || type === "object") return JSON.parse(text);
  return text;
}

function syncSchemaDraft() {
  try {
    state.toolSchemaVisualDraft = toolEditorJson("toolSchemaInput");
    toolJsonError("toolSchemaError", "");
    return true;
  } catch (error) {
    toolJsonError("toolSchemaError", "Ошибка JSON: " + error.message);
    return false;
  }
}

function renderToolSchemaVisual() {
  var root = $("toolSchemaVisual");
  if (!root) return;
  if (!state.toolSchemaVisualDraft && !syncSchemaDraft()) return;
  var schema = state.toolSchemaVisualDraft || {};
  schema.type = "object";
  schema.properties = schema.properties || {};
  schema.required = Array.isArray(schema.required) ? schema.required : [];
  schema.additionalProperties = false;
  root.innerHTML = "";
  Object.keys(schema.properties).forEach(function (name) {
    var property = schema.properties[name] || {};
    var row = document.createElement("div");
    row.className = "schema-property-row";
    var nameInput = document.createElement("input"); nameInput.value = name; nameInput.placeholder = "name"; nameInput.title = "Имя параметра";
    var type = document.createElement("select");
    ["string", "integer", "number", "boolean", "array", "object"].forEach(function (value) { var option = document.createElement("option"); option.value = value; option.textContent = value; type.appendChild(option); });
    type.value = property.type || "string";
    var required = document.createElement("input"); required.type = "checkbox"; required.checked = schema.required.indexOf(name) >= 0; required.title = "Обязательный";
    var description = document.createElement("input"); description.value = property.description || ""; description.placeholder = "Описание";
    var defaultValue = document.createElement("input"); defaultValue.value = schemaDefaultText(property.default); defaultValue.placeholder = "Default";
    var remove = document.createElement("button"); remove.type = "button"; remove.className = "secondary danger-soft"; remove.textContent = "×"; remove.title = "Удалить параметр";
    nameInput.addEventListener("change", function () {
      var next = nameInput.value.trim(); if (!next || next === name || schema.properties[next]) { nameInput.value = name; return; }
      schema.properties[next] = property; delete schema.properties[name]; schema.required = schema.required.map(function (item) { return item === name ? next : item; }); setToolEditorJson("toolSchemaInput", schema); renderToolSchemaVisual();
    });
    type.addEventListener("change", function () { property.type = type.value; setToolEditorJson("toolSchemaInput", schema); });
    required.addEventListener("change", function () { schema.required = schema.required.filter(function (item) { return item !== name; }); if (required.checked) schema.required.push(name); setToolEditorJson("toolSchemaInput", schema); });
    description.addEventListener("input", function () { property.description = description.value; setToolEditorJson("toolSchemaInput", schema); });
    defaultValue.addEventListener("change", function () { try { var parsed = parseSchemaDefault(defaultValue.value, property.type); if (parsed === undefined) delete property.default; else property.default = parsed; toolJsonError("toolSchemaError", ""); setToolEditorJson("toolSchemaInput", schema); } catch (error) { toolJsonError("toolSchemaError", "Некорректный default: " + error.message); } });
    remove.addEventListener("click", function () { delete schema.properties[name]; schema.required = schema.required.filter(function (item) { return item !== name; }); setToolEditorJson("toolSchemaInput", schema); renderToolSchemaVisual(); });
    [nameInput, type, required, description, defaultValue, remove].forEach(function (node) { row.appendChild(node); }); root.appendChild(row);
  });
  var add = document.createElement("button"); add.type = "button"; add.className = "secondary"; add.textContent = "+ Параметр";
  add.addEventListener("click", function () { var index = 1; var name = "argument"; while (schema.properties[name]) name = "argument" + (++index); schema.properties[name] = { type: "string", description: "" }; setToolEditorJson("toolSchemaInput", schema); renderToolSchemaVisual(); });
  root.appendChild(add);
  Array.prototype.slice.call(root.querySelectorAll("input,select,textarea,button")).forEach(function (control) { control.disabled = !!state.toolBuilderReadOnly; });
  renderToolRunArgsVisual();
}

function syncPipelineDraft() {
  try {
    state.toolPipelineVisualDraft = toolEditorJson("toolPipelineInput");
    state.toolPipelineVisualDraft.steps = Array.isArray(state.toolPipelineVisualDraft.steps) ? state.toolPipelineVisualDraft.steps : [];
    toolJsonError("toolPipelineError", ""); return true;
  } catch (error) { toolJsonError("toolPipelineError", "Ошибка JSON: " + error.message); return false; }
}

function renderToolPipelineVisual() {
  var root = $("toolPipelineVisual"); if (!root) return;
  if (!state.toolPipelineVisualDraft && !syncPipelineDraft()) return;
  var pipeline = state.toolPipelineVisualDraft; root.innerHTML = "";
  pipeline.steps.forEach(function (step, index) {
    var card = document.createElement("div"); card.className = "pipeline-step-card";
    var number = document.createElement("strong"); number.textContent = String(index + 1);
    var id = document.createElement("input"); id.value = step.id || ""; id.placeholder = "ID шага";
    var toolId = document.createElement("input"); toolId.value = step.toolId || ""; toolId.placeholder = "excel.read_range";
    var args = document.createElement("textarea"); args.rows = 3; args.value = JSON.stringify(step.arguments || {}, null, 2); args.placeholder = "Arguments JSON";
    var controls = document.createElement("div"); controls.className = "pipeline-step-actions";
    [["↑", -1], ["↓", 1]].forEach(function (move) { var button = document.createElement("button"); button.type = "button"; button.className = "secondary"; button.textContent = move[0]; button.disabled = index + move[1] < 0 || index + move[1] >= pipeline.steps.length; button.addEventListener("click", function () { var target = index + move[1]; var item = pipeline.steps.splice(index, 1)[0]; pipeline.steps.splice(target, 0, item); setToolEditorJson("toolPipelineInput", pipeline); renderToolPipelineVisual(); }); controls.appendChild(button); });
    var remove = document.createElement("button"); remove.type = "button"; remove.className = "secondary danger-soft"; remove.textContent = "Удалить"; remove.addEventListener("click", function () { pipeline.steps.splice(index, 1); setToolEditorJson("toolPipelineInput", pipeline); renderToolPipelineVisual(); }); controls.appendChild(remove);
    id.addEventListener("input", function () { step.id = id.value; setToolEditorJson("toolPipelineInput", pipeline); });
    toolId.addEventListener("input", function () { step.toolId = toolId.value; setToolEditorJson("toolPipelineInput", pipeline); });
    args.addEventListener("change", function () { try { var value = JSON.parse(args.value || "{}"); if (!value || Array.isArray(value) || typeof value !== "object") throw new Error("ожидается object"); step.arguments = value; toolJsonError("toolPipelineError", ""); setToolEditorJson("toolPipelineInput", pipeline); } catch (error) { toolJsonError("toolPipelineError", "Аргументы шага: " + error.message); } });
    [number, id, toolId, args, controls].forEach(function (node) { card.appendChild(node); }); root.appendChild(card);
  });
  var add = document.createElement("button"); add.type = "button"; add.className = "secondary"; add.textContent = "+ Шаг"; add.addEventListener("click", function () { pipeline.steps.push({ id: "step" + (pipeline.steps.length + 1), toolId: "", arguments: {} }); setToolEditorJson("toolPipelineInput", pipeline); renderToolPipelineVisual(); }); root.appendChild(add);
  Array.prototype.slice.call(root.querySelectorAll("input,select,textarea,button")).forEach(function (control) { control.disabled = !!state.toolBuilderReadOnly; });
}

function renderToolRunArgsVisual() {
  var root = $("toolRunArgsVisual"); if (!root) return; root.innerHTML = "";
  var schema = state.toolSchemaVisualDraft || {}; var properties = schema.properties || {}; var args = {};
  try { args = toolEditorJson("toolRunArgsInput"); } catch (error) { args = {}; }
  Object.keys(properties).forEach(function (name) {
    var property = properties[name] || {}; var label = document.createElement("label"); label.textContent = name; var input = document.createElement("input"); input.value = args[name] === undefined ? "" : (typeof args[name] === "string" ? args[name] : JSON.stringify(args[name])); input.placeholder = property.description || property.type || "value";
    input.addEventListener("change", function () { if (!input.value) delete args[name]; else { try { args[name] = parseSchemaDefault(input.value, property.type || "string"); } catch (error) { args[name] = input.value; } } setToolEditorJson("toolRunArgsInput", args); }); label.appendChild(input); root.appendChild(label);
  });
  if (!Object.keys(properties).length) root.appendChild(createResourceEmptyState("У инструмента нет параметров."));
}

function setToolStructuredMode(kind, mode) {
  var isSchema = kind === "schema"; var valid = isSchema ? syncSchemaDraft() : syncPipelineDraft();
  if (mode === "form" && !valid) return;
  if (isSchema) state.toolSchemaMode = mode; else state.toolPipelineMode = mode;
  Array.prototype.slice.call(document.querySelectorAll(isSchema ? ".tool-schema-mode" : ".tool-pipeline-mode")).forEach(function (button) { button.classList.toggle("active", button.getAttribute(isSchema ? "data-tool-schema-mode" : "data-tool-pipeline-mode") === mode); });
  var visual = $(isSchema ? "toolSchemaVisual" : "toolPipelineVisual"); if (visual) visual.classList.toggle("hidden", mode !== "form");
  Array.prototype.slice.call(document.querySelectorAll(isSchema ? ".tool-schema-json" : ".tool-pipeline-json")).forEach(function (node) { node.classList.toggle("hidden", mode !== "json"); });
  if (mode === "form") { if (isSchema) renderToolSchemaVisual(); else renderToolPipelineVisual(); }
  else if (typeof refreshCodeEditors === "function") refreshCodeEditors([isSchema ? "toolSchemaInput" : "toolPipelineInput"]);
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
  if ($("toolEditorMeta")) $("toolEditorMeta").textContent = skill ? ((builtIn ? "Встроенный" : "Пользовательский") + " · " + (skill.Host || "Common") + " · " + (skill.Executor || "pipeline")) : "";
  $("toolEnabledInput").checked = skill ? skill.Enabled !== false : false;
  $("toolIdInput").value = skill ? (skill.Id || "") : "";
  $("toolHostInput").value = skill ? (skill.Host || "Common") : "Common";
  $("toolExecutorInput").value = skill ? (skill.Executor || (builtIn ? "builtin" : "pipeline")) : "pipeline";
  $("toolConfirmInput").checked = skill ? !!skill.RequiresConfirmation : false;
  $("toolDescriptionInput").value = skill ? (skill.Description || "") : "";
  $("toolSchemaInput").value = skill ? (skill.ArgumentSchemaJson || emptyToolSchema()) : emptyToolSchema();
  $("toolRunArgsInput").value = skill ? "{}" : "";
  $("toolPipelineInput").value = skill ? (skill.PipelineJson || "") : "";
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
  $("toolComponentTypeInput").value = component && component.Type === "ClassModule" ? "ClassModule" : "StdModule";
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
    setCodeEditorValue("toolPipelineInput", $("toolPipelineInput").value);
    setCodeEditorValue("toolCodeInput", $("toolCodeInput").value);
    setCodeEditorValue("toolReadmeInput", $("toolReadmeInput").value);
  }
  $("toolRunOutput").textContent = "";
  state.toolSchemaVisualDraft = null;
  state.toolPipelineVisualDraft = null;
  syncSchemaDraft();
  syncPipelineDraft();
  setToolStructuredMode("schema", state.toolSchemaMode || "form");
  setToolStructuredMode("pipeline", state.toolPipelineMode || "form");
  if ($("pipelineToolEditor")) $("pipelineToolEditor").classList.toggle("hidden", !skill || String(skill.Executor || "").toLowerCase() !== "pipeline");
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
    "toolPipelineInput",
    "toolCodeInput",
    "toolReadmeInput"
  ].forEach(function (id) {
    $(id).disabled = disabled || readOnly;
  });
  $("toolRunArgsInput").disabled = disabled;
  if (typeof setCodeEditorReadOnly === "function") {
    setCodeEditorReadOnly("toolSchemaInput", disabled || readOnly);
    setCodeEditorReadOnly("toolRunArgsInput", disabled);
    setCodeEditorReadOnly("toolPipelineInput", disabled || readOnly);
    setCodeEditorReadOnly("toolCodeInput", disabled || readOnly || !isVba);
    setCodeEditorReadOnly("toolReadmeInput", disabled || readOnly);
  }

  ["toolComponentSelect", "toolComponentNameInput", "toolComponentTypeInput", "addToolModuleButton", "addToolClassButton", "deleteToolComponentButton"].forEach(function (id) {
    $(id).disabled = disabled || readOnly || !isVba;
  });

  $("deleteToolButton").disabled = disabled || readOnly;
  $("dryRunToolButton").disabled = disabled;
  $("runToolButton").disabled = disabled;
  $("cloneToolButton").disabled = disabled;
  $("copyToolContextButton").disabled = disabled;
  $("askToolBuilderButton").disabled = disabled;
  $("addToolButton").disabled = !!state.bridgeUnavailable;
  $("saveToolsButton").disabled = !!state.bridgeUnavailable;
  $("vbaPackageActions").hidden = !isVba || builtIn || documentLocal;
  $("installVbaToolButton").disabled = !isVba || builtIn || documentLocal || !!state.bridgeUnavailable;
  $("uninstallVbaToolButton").disabled = $("installVbaToolButton").disabled || String(skill && skill.InstallationStatus || "") === "not_installed";
}

function syncSelectedToolFromEditor() {
  if (typeof syncCodeEditors === "function") {
    syncCodeEditors(["toolSchemaInput", "toolRunArgsInput", "toolPipelineInput", "toolCodeInput", "toolReadmeInput"]);
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
  skill.PipelineJson = $("toolPipelineInput").value;
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
  if (!syncSchemaDraft()) { state.toolEditorPage = "schema"; applyToolEditorPage(); return false; }
  if (String($("toolExecutorInput").value || "").toLowerCase() === "pipeline" && !syncPipelineDraft()) { state.toolEditorPage = "implementation"; applyToolEditorPage(); return false; }
  return true;
}

function validateAllToolDefinitions() {
  for (var index = 0; index < state.tools.length; index += 1) {
    var tool = state.tools[index] || {};
    try { JSON.parse(tool.ArgumentSchemaJson || emptyToolSchema()); }
    catch (error) { throw new Error("Некорректная schema у " + (tool.Id || "инструмента") + ": " + error.message); }
    if (String(tool.Executor || "").toLowerCase() === "pipeline") {
      try { JSON.parse(tool.PipelineJson || "{}"); }
      catch (error2) { throw new Error("Некорректный pipeline у " + (tool.Id || "инструмента") + ": " + error2.message); }
    }
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
      Executor: skill.Executor || (skill.BuiltIn ? "builtin" : "pipeline"),
      RequiresConfirmation: !!skill.RequiresConfirmation,
      PipelineJson: skill.PipelineJson || "",
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
  var suffix = type === "ClassModule" ? "Service" : "Module";
  var base = inferredVbaComponentName(tool).slice(0, Math.max(1, 39 - suffix.length));
  var name = (base + "_" + suffix).slice(0, 40);
  var serial = 2;
  while (components.some(function (component) { return String(component.Name || "").toLowerCase() === name.toLowerCase(); })) {
    name = (base.slice(0, Math.max(1, 38 - String(serial).length)) + "_" + serial).slice(0, 40);
    serial += 1;
  }
  components.push({ Name: name, Type: type, FileName: name + (type === "ClassModule" ? ".cls" : ".bas"), Code: "Option Explicit\n" });
  state.selectedToolComponentIndex = components.length - 1;
  renderToolEditor();
}

async function changeVbaInstallation(action) {
  syncSelectedToolFromEditor();
  var tool = state.tools[state.selectedToolIndex];
  if (!tool) {
    return;
  }
  var actionButtonId = action === "installVbaTool" ? "installVbaToolButton" : "uninstallVbaToolButton";
  setControlBusy(actionButtonId, true);
  try {
    if (action === "installVbaTool") {
      var selectedId = tool.Id;
      state.tools = await send("saveTools", { tools: readTools() }) || state.tools;
      state.selectedToolIndex = state.tools.findIndex(function (item) {
        return item && String(item.Id || "").toLowerCase() === String(selectedId || "").toLowerCase();
      });
      tool = state.tools[state.selectedToolIndex];
      if (!tool) {
        throw new Error("VBA package was not found after saving.");
      }
    }
    var response = await send(action, { id: tool.Id, dryRun: false });
    var result = response.result || response.Result || {};
    state.tools = response.tools || response.Tools || state.tools;
    state.selectedToolIndex = state.tools.findIndex(function (item) { return item && String(item.Id || "").toLowerCase() === String(tool.Id || "").toLowerCase(); });
    state.selectedToolComponentIndex = 0;
    renderTools();
    $("toolRunOutput").textContent = JSON.stringify(result, null, 2);
    log(result.Message || result.message || "VBA package state updated.");
  } catch (error) {
    $("toolRunOutput").textContent = error.detail || error.message;
    log(error.message);
  } finally {
    setControlBusy(actionButtonId, false);
    renderToolEditor();
  }
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
  if (!validateSelectedToolEditors()) {
    log("Исправьте JSON инструмента перед запуском.");
    return;
  }
  syncSelectedToolFromEditor();
  var skill = state.tools[state.selectedToolIndex];
  if (!skill) {
    return;
  }

  var runButtonId = dryRun ? "dryRunToolButton" : "runToolButton";
  setControlBusy(runButtonId, true);
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
    setControlBusy(runButtonId, false);
  }
}

function bindToolActions() {
  $("toolSearchInput").addEventListener("input", renderTools);
  Array.prototype.slice.call(document.querySelectorAll(".tool-page-button")).forEach(function (button) { button.addEventListener("click", function () { syncSelectedToolFromEditor(); state.toolEditorPage = button.getAttribute("data-tool-page") || "main"; applyToolEditorPage(); }); });
  Array.prototype.slice.call(document.querySelectorAll(".tool-schema-mode")).forEach(function (button) { button.addEventListener("click", function () { setToolStructuredMode("schema", button.getAttribute("data-tool-schema-mode")); }); });
  Array.prototype.slice.call(document.querySelectorAll(".tool-pipeline-mode")).forEach(function (button) { button.addEventListener("click", function () { setToolStructuredMode("pipeline", button.getAttribute("data-tool-pipeline-mode")); }); });

  $("toolComponentSelect").addEventListener("change", function () {
    var tool = state.tools[state.selectedToolIndex];
    syncSelectedToolComponentFromEditor(tool);
    state.selectedToolComponentIndex = Number($("toolComponentSelect").value || 0);
    renderToolEditor();
  });
  $("addToolModuleButton").addEventListener("click", function () { addVbaComponent("StdModule"); });
  $("addToolClassButton").addEventListener("click", function () { addVbaComponent("ClassModule"); });
  $("deleteToolComponentButton").addEventListener("click", function () {
    syncSelectedToolFromEditor();
    var tool = state.tools[state.selectedToolIndex];
    var components = toolComponents(tool);
    if (!components.length) return;
    components.splice(Number(state.selectedToolComponentIndex || 0), 1);
    state.selectedToolComponentIndex = Math.max(0, Math.min(Number(state.selectedToolComponentIndex || 0), components.length - 1));
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
    renderToolEditor();
  });
  $("installVbaToolButton").addEventListener("click", function () { changeVbaInstallation("installVbaTool"); });
  $("uninstallVbaToolButton").addEventListener("click", function () { changeVbaInstallation("uninstallVbaTool"); });

  $("addToolButton").addEventListener("click", function () {
    syncSelectedToolFromEditor();
    state.tools.push({
      Id: (state.host || "common").toLowerCase() + ".new_tool",
      Host: state.host || "Common",
      Name: "new_tool",
      Description: "",
      ArgumentSchemaJson: emptyToolSchema(),
      Executor: "pipeline",
      RequiresConfirmation: true,
      PipelineJson: "{\n  \"version\": 1,\n  \"steps\": []\n}",
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
      Components: []
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
      ArgumentSchemaJson: source.ArgumentSchemaJson || emptyToolSchema(),
      Executor: source.BuiltIn ? "pipeline" : (source.Executor || "pipeline"),
      RequiresConfirmation: source.BuiltIn ? true : !!source.RequiresConfirmation,
      PipelineJson: source.PipelineJson || "{\n  \"version\": 1,\n  \"steps\": []\n}",
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
    renderTools();
  });

  $("saveToolsButton").addEventListener("click", async function () {
    setControlBusy("saveToolsButton", true);
    try {
      if (!validateSelectedToolEditors()) throw new Error("Исправьте JSON перед сохранением.");
      syncSelectedToolFromEditor();
      validateAllToolDefinitions();
      var selected = state.tools[state.selectedToolIndex];
      var selectedId = selected ? selected.Id : "";
      var response = await send("saveTools", { tools: readTools() });
      state.tools = response || [];
      state.selectedToolIndex = selectedId
        ? state.tools.findIndex(function (tool) { return tool && String(tool.Id || "").toLowerCase() === String(selectedId).toLowerCase(); })
        : -1;
      renderTools();
      log("Инструменты сохранены.");
    } catch (error) {
      log(error.message);
    } finally {
      setControlBusy("saveToolsButton", false);
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
      setChatInputText("Отредактируй RNAssistant-инструмент из добавленного контекста. Верни обновленные tool.json, pipeline или VBA .bas/.cls components; не выполняй действия без подтверждения.", true);
    }).catch(function (error) {
      log(error.detail || error.message);
    });
  });
}
