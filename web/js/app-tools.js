var toolLibraryContractVersion = 1;
var toolLibraryContractType = "rnassistant.toolLibrary";
var toolLibraryMutationRequestType = "rnassistant.toolLibraryMutationRequest";
var toolLibraryMutationResultType = "rnassistant.toolLibraryMutationResult";

function toolComponentFromContract(component) {
  if (!component || typeof component.name !== "string" ||
    typeof component.type !== "string" ||
    typeof component.fileName !== "string" ||
    typeof component.code !== "string" ||
    typeof component.codeSha256 !== "string") {
    throw new Error("Некорректный typed component инструмента.");
  }
  return {
    Name: component.name,
    Type: component.type,
    FileName: component.fileName,
    Code: component.code,
    CodeSha256: component.codeSha256
  };
}

function toolFromContract(tool) {
  if (!tool || typeof tool.revision !== "string" || !tool.revision ||
    typeof tool.id !== "string" || !tool.id ||
    typeof tool.host !== "string" || typeof tool.name !== "string" ||
    typeof tool.description !== "string" ||
    !tool.source || !/^[a-f0-9]{64}$/.test(tool.source.sha256) || !Number.isInteger(tool.source.byteLength) || tool.source.byteLength < 0 ||
    ["argumentSchemaJson", "code", "readme", "components"].some(function (key) { return Object.prototype.hasOwnProperty.call(tool, key); }) ||
    typeof tool.executor !== "string" ||
    typeof tool.requiresConfirmation !== "boolean" ||
    typeof tool.mutatesDocument !== "boolean" ||
    typeof tool.mutatesLocalState !== "boolean" ||
    typeof tool.canSourceHtmlData !== "boolean" ||
    typeof tool.agentCanRun !== "boolean" ||
    typeof tool.enabled !== "boolean" || typeof tool.builtIn !== "boolean" ||
    typeof tool.riskLevel !== "number" || typeof tool.useWhen !== "string" ||
    typeof tool.doNotUseWhen !== "string" ||
    typeof tool.capabilityStatus !== "string" ||
    typeof tool.limitations !== "string" ||
    typeof tool.packageVersion !== "string" ||
    typeof tool.entryPoint !== "string" ||
    !Array.isArray(tool.argumentOrder) ||
    typeof tool.scope !== "string" ||
    typeof tool.installationStatus !== "string") {
    throw new Error("Некорректный typed package инструмента.");
  }
  return {
    Id: tool.id,
    Host: tool.host,
    Name: tool.name,
    Description: tool.description,
    Source: { sha256: tool.source.sha256, byteLength: tool.source.byteLength },
    Executor: tool.executor,
    RequiresConfirmation: tool.requiresConfirmation,
    MutatesDocument: tool.mutatesDocument,
    MutatesLocalState: tool.mutatesLocalState,
    CanSourceHtmlData: tool.canSourceHtmlData,
    AgentCanRun: tool.agentCanRun,
    Enabled: tool.enabled,
    BuiltIn: tool.builtIn,
    RiskLevel: tool.riskLevel,
    UseWhen: tool.useWhen,
    DoNotUseWhen: tool.doNotUseWhen,
    CapabilityStatus: tool.capabilityStatus,
    Limitations: tool.limitations,
    PackageVersion: tool.packageVersion,
    EntryPoint: tool.entryPoint,
    ArgumentOrder: tool.argumentOrder.slice(),
    Scope: tool.scope,
    InstallationStatus: tool.installationStatus,
    Revision: tool.revision,
    _baseId: tool.builtIn ? "" : tool.id,
    _baseRevision: tool.builtIn ? "" : tool.revision
  };
}

function toolSourceBody(tool) {
  return { argumentSchemaJson: tool.ArgumentSchemaJson || "", code: tool.Code || "", readme: tool.Readme || "",
    components: (tool.Components || []).map(function (component) { return { name: component.Name || "", type: component.Type || "StdModule",
      fileName: component.FileName || "", code: component.Code || "", codeSha256: component.CodeSha256 || "" }; }) };
}

function toolSourceDirty(tool) {
  return !!(tool && tool._sourceLoaded && JSON.stringify(toolSourceBody(tool)) !== tool._sourceBaseline);
}

function applyToolSource(tool, body) {
  if (!body || typeof body.argumentSchemaJson !== "string" || typeof body.code !== "string" ||
      typeof body.readme !== "string" || !Array.isArray(body.components) ||
      Object.keys(body).some(function (key) { return ["argumentSchemaJson", "code", "readme", "components"].indexOf(key) < 0; }))
    throw new Error("Некорректный typed исходник инструмента.");
  var components = body.components.map(toolComponentFromContract);
  tool.ArgumentSchemaJson = body.argumentSchemaJson; tool.Code = body.code; tool.Readme = body.readme; tool.Components = components;
  tool._sourceLoaded = true; tool._sourceBaseline = JSON.stringify(toolSourceBody(tool));
}

function trimToolSourceCache(selected) {
  (state.tools || []).forEach(function (tool) {
    if (tool !== selected && !toolSourceDirty(tool)) {
      delete tool.ArgumentSchemaJson; delete tool.Code; delete tool.Readme; delete tool.Components;
      delete tool._sourceLoaded; delete tool._sourceBaseline; delete tool._sourceError;
    }
  });
}

var toolSourceRead = null, toolSourceReadPending = 0;

function closeToolSourceRead(operation) {
  if (!operation || operation.closed || !operation.data || !/^[a-f0-9]{64}$/.test(operation.data.leaseId)) return Promise.resolve();
  operation.closed = true;
  return send("resourceDataClose", { chatId: operation.chatId, workspaceId: "tool-editor", leaseId: operation.data.leaseId }).catch(function () {});
}

function cancelToolSourceRead() {
  var operation = toolSourceRead;
  if (!operation) return;
  operation.abort.abort();
  if (operation.requestId) cancelBridgeRequest(operation.requestId).catch(function () {});
  closeToolSourceRead(operation); toolSourceRead = null;
}

function toolSourceReadFromContract(response, operation) {
  if (!response || response.type !== "rnassistant.toolSourceRead" || response.contractVersion !== toolLibraryContractVersion ||
      response.chatId !== operation.chatId || response.toolId !== operation.id || response.revision !== operation.revision ||
      !response.data || !response.data.payload || response.data.payload.contentType !== "application/json; charset=utf-8" ||
      response.data.payload.sha256 !== operation.hash || response.data.payload.byteLength !== operation.length ||
      !Array.isArray(response.sources) || !response.sources.length || response.sources.length > 256)
    throw new Error("Некорректный снимок исходника инструмента.");
  var seen = {};
  response.sources.forEach(function (resource) {
    var parts = resource && typeof resource.uri === "string" ? resource.uri.split("/") : [];
    if (typeof (resource && resource.revision) !== "string" || !resource.revision || seen[resource.uri] ||
        parts[0] !== "rna:" || parts[1] !== "" || parts.length !== 6 ||
        (operation.documentLocal ? parts[2] !== "vba" || !parts[3] || parts[4] !== "component" || !/^[a-f0-9]{64}$/.test(parts[5]) :
          parts[2] !== "catalog" || parts[3] !== (operation.builtIn ? "builtin-tools-" + operation.host : "tools") ||
          decodeURIComponent(parts[4]) !== operation.id || parts[5] !== "source"))
      throw new Error("Источник инструмента не привязан к точной ревизии.");
    seen[resource.uri] = true;
  });
  if (!operation.documentLocal && response.sources.length !== 1) throw new Error("Неоднозначный источник инструмента.");
  return response;
}

async function loadSelectedToolSource(tool) {
  if (!tool || tool._sourceLoaded || tool._sourceError || toolSourceRead && toolSourceRead.tool === tool ||
      !tool.Source || !state.activeChatId || state.bridgeUnavailable || state.toolLibraryWriting || state.selectedInstructionKind !== "tool") return;
  cancelToolSourceRead();
  if (toolSourceReadPending >= 2) { log("Предыдущее чтение ещё закрывается. Выберите инструмент повторно после завершения.", "error"); return; }
  var operation = { tool: tool, id: tool._baseId || tool.Id, revision: tool.Revision, chatId: state.activeChatId,
    hash: tool.Source.sha256, length: tool.Source.byteLength, builtIn: !!tool.BuiltIn, host: String(state.host || "common").toLowerCase(),
    documentLocal: tool.Scope === "document", abort: new AbortController() };
  toolSourceRead = operation; toolSourceReadPending++;
  function current() { return toolSourceRead === operation && !operation.abort.signal.aborted && !state.bridgeUnavailable &&
    state.activeChatId === operation.chatId && state.selectedInstructionKind === "tool" && state.tools[state.selectedToolIndex] === tool &&
    tool.Revision === operation.revision && (tool._baseId || tool.Id) === operation.id && !toolSourceDirty(tool) &&
    tool.Source.sha256 === operation.hash && tool.Source.byteLength === operation.length; }
  function active() { if (!current()) throw new Error("RESOURCE_READ_CANCELLED"); }
  try {
    if (operation.length > 16 * 1024 * 1024) throw new Error("RESOURCE_BATCH_TOO_LARGE");
    active();
    var opening = send("readToolSource", { type: "rnassistant.toolSourceRequest", contractVersion: toolLibraryContractVersion,
      chatId: operation.chatId, toolId: operation.id, expectedRevision: operation.revision });
    operation.requestId = opening.requestId;
    var response = await opening; operation.requestId = null; operation.data = response && response.data;
    active();
    var typed = toolSourceReadFromContract(response, operation);
    var bytes = await window.RNAssistantResourceDownload.read(typed.data, { maxBytes: 16 * 1024 * 1024,
      fetch: window.fetch.bind(window), signal: operation.abort.signal, isCurrent: current });
    var body = JSON.parse(new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes));
    if (operation.documentLocal && (!body || !Array.isArray(body.components) || typed.sources.length !== body.components.length ||
        typed.sources.some(function (source) { return source.uri.split("/")[3] !== typed.sources[0].uri.split("/")[3]; })))
      throw new Error("Состав VBA-снимка не совпадает с исходником инструмента.");
    await closeToolSourceRead(operation); active();
    applyToolSource(tool, body);
  } catch (error) {
    if (current()) { tool._sourceError = error.detail || error.message; log(tool._sourceError, "error"); }
  } finally {
    await closeToolSourceRead(operation); toolSourceReadPending--;
    if (toolSourceRead === operation) { toolSourceRead = null; renderToolEditor(); }
  }
}

function toolLibraryItemsFromContract(contract) {
  if (!contract || contract.type !== toolLibraryContractType ||
    contract.contractVersion !== toolLibraryContractVersion ||
    !Array.isArray(contract.tools)) {
    throw new Error("Некорректный typed Tool Library contract.");
  }
  return contract.tools.map(toolFromContract);
}

function requireToolMutationResult(result) {
  if (!result || result.type !== "rnassistant.toolMutationResult" ||
    result.contractVersion !== toolLibraryContractVersion ||
    ["ok", "error", "unknown"].indexOf(result.status) < 0 ||
    typeof result.message !== "string" ||
    typeof result.dispatch !== "string" || typeof result.effect !== "string") {
    throw new Error("Некорректный typed результат изменения инструмента.");
  }
  return result;
}

function toolLibraryMutationFromContract(response) {
  if (!response || response.type !== toolLibraryMutationResultType ||
    response.contractVersion !== toolLibraryContractVersion ||
    !Array.isArray(response.results)) {
    throw new Error("Некорректный typed результат Tool Library.");
  }
  var results = response.results.map(requireToolMutationResult);
  return {
    tools: toolLibraryItemsFromContract(response.library),
    results: results,
    failure: results.filter(function (result) {
      return result.status !== "ok";
    })[0] || null
  };
}

var toolStructuredEditor = window.RNAssistantToolStructuredEditor.create({
  state: state,
  markDirty: markToolLibraryDirty
});
var toolDocumentation = window.RNAssistantToolDocumentation.create({
  state: state,
  send: send,
  cancelRequest: function (id) { return cancelBridgeRequest(id); },
  log: log
});
var toolActions = window.RNAssistantToolActions.create({
  state: state,
  send: send,
  cancelRequest: function (id) { return cancelBridgeRequest(id); },
  updateWriteState: updateToolWriteControls,
  setBusy: setControlBusy,
  setJsonOutput: renderToolRunJson,
  setTextOutput: renderToolRunText,
  syncSelected: syncSelectedToolFromEditor,
  validateSelected: validateSelectedToolEditors,
  validateAll: validateAllToolDefinitions,
  mutationRequest: toolLibraryMutationRequest,
  captureSave: function () { return toolLibraryRecords(state.tools); },
  acknowledgeSave: acknowledgeToolSaves,
  parseMutation: toolLibraryMutationFromContract,
  parseLibrary: toolLibraryItemsFromContract,
  reconcile: reconcileToolLibraryCatalog,
  readNextArguments: toolStructuredEditor.readNextArguments,
  readRunArguments: toolStructuredEditor.readRunArguments,
  renderTools: renderTools,
  renderEditor: renderToolEditor,
  setContinuation: setToolRunContinuation,
  log: log,
  logToolResult: logToolResult
});

function clearToolRunOutput() {
  var target = $("toolRunOutput");
  if (!target) return null;
  if (window.RNAssistantViewerRegistry) window.RNAssistantViewerRegistry.unmount(target);
  target.classList.add("is-text");
  target.textContent = "";
  return target;
}

function renderToolRunText(value) {
  var target = clearToolRunOutput();
  if (target) target.textContent = value === null || value === undefined ? "" : String(value);
}

function renderToolRunJson(value) {
  var target = clearToolRunOutput();
  if (!target) return;
  if (!window.RNAssistantViewerRegistry || !window.RNAssistantViewerRegistry.has("json")) {
    throw new Error("JSON viewer is unavailable.");
  }
  target.classList.remove("is-text");
  var text = typeof value === "string" ? value : JSON.stringify(value === undefined ? null : value, null, 2);
  window.RNAssistantViewerRegistry.mount("json", target, {
    text: text,
    completeness: "full",
    mode: "tree",
    onCopy: window.copyTextResult
  });
}

function renderTools() {
  renderInstructions();
}

function setToolRunContinuation(continuation) {
  var tool = state.tools[state.selectedToolIndex] || null;
  var valid = !!(continuation && tool && continuation.toolId === tool.Id);
  state.toolRunContinuation = valid ? continuation : null;
  var button = $("nextToolPageButton");
  if (!button) return;
  button.hidden = !valid;
  button.disabled = !valid;
}

function emptyToolSchema() {
  return "{\n  \"type\": \"object\",\n  \"properties\": {},\n  \"required\": [],\n  \"additionalProperties\": false\n}";
}

function toolComponents(tool) {
  if (!tool || !tool._sourceLoaded) {
    return [];
  }
  return tool.Components || [];
}

function inferredVbaComponentName(tool) {
  var raw = String(tool && tool.Id || "Tool").replace(/[^A-Za-z0-9_]/g, "_");
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
  return {
      Id: tool.Id || "",
      Host: tool.Host || "Common",
      Name: tool.Name || tool.Id || "",
      Description: tool.Description || "",
      SourceState: toolSourceDirty(tool) ? JSON.stringify(toolSourceBody(tool)) : tool.Source && tool.Source.sha256 || "",
      Executor: tool.Executor || "vba",
      RequiresConfirmation: !!tool.RequiresConfirmation,
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
      ArgumentOrder: (tool.ArgumentOrder || []).slice()
  };
}

function toolLibraryIdentity(tool) {
  var baseId = String(tool && tool._baseId || "").toLowerCase();
  return "id:" + (baseId || String(tool && tool.Id || "").toLowerCase());
}

function toolLibraryRecords(tools) {
  return writableToolLibraryItems(tools).map(function (tool) {
    return {
      entity: tool,
      identity: toolLibraryIdentity(tool),
      id: String(tool.Id || "").toLowerCase(),
      baseId: tool._baseId || "",
      revision: tool._baseRevision || "",
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
  state.toolLibraryBaselineItems = toolLibraryRecords(tools).map(function (record) { delete record.entity; return record; });
  state.toolLibraryBaseline = toolLibrarySnapshot(tools);
}

function acknowledgeToolSaves(submitted, saved) {
  submitted.forEach(function (record) {
    var outcome = saved.results.find(function (result) { return result.status === "ok" && result.id === record.entity.Id; });
    var published = outcome && saved.tools.find(function (tool) { return tool.Id === outcome.id && tool.Revision === outcome.revision; });
    var index = state.tools.indexOf(record.entity);
    if (published && index >= 0 && !toolRecordChanged(toolLibraryRecords([record.entity])[0], record)) state.tools[index] = published;
  });
}

function cancelToolLibraryWrite() { toolActions.cancelWrite(); }
function cancelToolDocumentationRead() { toolDocumentation.cancel(); }

function updateToolWriteControls() {
  var tool = state.tools[state.selectedToolIndex], unavailable = !!state.bridgeUnavailable || !!state.toolLibraryWriting;
  var readOnly = !tool || !!tool.BuiltIn || String(tool.Scope || "").toLowerCase() === "document";
  var isVba = tool && String(tool.Executor).toLowerCase() === "vba";
  if ($("deleteToolButton")) $("deleteToolButton").disabled = unavailable || readOnly;
  var sourceUnavailable = !tool || !tool._sourceLoaded;
  if ($("cloneToolButton")) $("cloneToolButton").disabled = unavailable || sourceUnavailable || !!tool.BuiltIn;
  if ($("addToolButton")) $("addToolButton").disabled = unavailable;
  ["installVbaToolButton", "uninstallVbaToolButton"].forEach(function (id) {
    if ($(id)) $(id).disabled = unavailable || sourceUnavailable || readOnly || !isVba || !!tool._sourceConflict ||
      id === "uninstallVbaToolButton" && tool.InstallationStatus === "not_installed";
  });
  ["dryRunToolButton", "runToolButton", "copyToolContextButton", "askToolBuilderButton"].forEach(function (id) {
    if ($(id)) $(id).disabled = unavailable || sourceUnavailable;
  });
  updateToolSaveButton();
}

function reconcileToolLibraryCatalog(serverTools) {
  cancelToolSourceRead();
  cancelToolDocumentationRead();
  (serverTools || []).forEach(function (serverTool) {
    var current = state.tools.find(function (tool) { return (tool._baseId || tool.Id) === serverTool.Id; });
    if (!current || !current._sourceLoaded) return;
    if (current.Source && serverTool.Source && current.Source.sha256 === serverTool.Source.sha256) {
      if (!toolSourceDirty(current)) applyToolSource(serverTool, toolSourceBody(current));
    } else if (toolSourceDirty(current)) current._sourceConflict = true;
  });
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
    var retained = current && toolRecordChanged(current, baseline);
    if (retained && !toolSourceDirty(current.entity) && current.entity.Source.sha256 !== serverTool.Source.sha256) {
      // Metadata-only local edits may follow a new body only after an explicit fresh read.
      delete current.entity.ArgumentSchemaJson; delete current.entity.Code; delete current.entity.Readme; delete current.entity.Components;
      delete current.entity._sourceLoaded; delete current.entity._sourceBaseline;
      current.entity.Source = serverTool.Source; current.entity.Revision = serverTool.Revision;
    }
    merged.push(retained ? current.entity : serverTool);
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
  button.disabled = !!state.bridgeUnavailable || !!state.toolLibraryWriting || !state.toolLibraryDirty ||
    state.tools.some(function (tool) { return !!tool._sourceConflict; });
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

function toolLibraryMutations() {
  syncSelectedToolFromEditor();
  var current = toolLibraryRecords(state.tools);
  var baseline = state.toolLibraryBaselineItems || [];
  var currentIndex = toolRecordIndex(current);
  var baselineIndex = toolRecordIndex(baseline);
  var mutations = [];
  current.forEach(function (record) {
    var previous = matchingToolRecord(baselineIndex, record);
    if (previous && !toolRecordChanged(record, previous)) return;
    var tool = record.entity;
    if (!tool._sourceLoaded || tool._sourceConflict)
      throw new Error("Исходник " + tool.Id + " не загружен либо изменился на диске. Обновите Library и сверьте черновик.");
    var comparable = toolLibraryComparable(tool);
    var source = toolSourceBody(tool);
    mutations.push({
      kind: "upsert",
      baseId: previous ? previous.baseId : "",
      expectedRevision: previous ? previous.revision : "",
      id: comparable.Id,
      host: comparable.Host,
      name: comparable.Name,
      description: comparable.Description,
      argumentSchemaJson: source.argumentSchemaJson,
      executor: comparable.Executor,
      requiresConfirmation: comparable.RequiresConfirmation,
      mutatesDocument: comparable.MutatesDocument,
      mutatesLocalState: comparable.MutatesLocalState,
      agentCanRun: comparable.AgentCanRun,
      code: source.code,
      readme: source.readme,
      enabled: comparable.Enabled,
      riskLevel: comparable.RiskLevel,
      useWhen: comparable.UseWhen,
      doNotUseWhen: comparable.DoNotUseWhen,
      capabilityStatus: comparable.CapabilityStatus,
      limitations: comparable.Limitations,
      packageVersion: comparable.PackageVersion,
      entryPoint: comparable.EntryPoint,
      argumentOrder: comparable.ArgumentOrder,
      components: source.components
    });
  });
  baseline.forEach(function (record) {
    if (matchingToolRecord(currentIndex, record)) return;
    mutations.push({
      kind: "delete",
      baseId: record.baseId,
      expectedRevision: record.revision
    });
  });
  return mutations;
}

function toolLibraryMutationRequest() {
  return {
    type: toolLibraryMutationRequestType,
    contractVersion: toolLibraryContractVersion,
    mutations: toolLibraryMutations()
  };
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
  if (!tool || !tool._sourceLoaded || tool.BuiltIn || tool.Scope === "document" || String(tool.Executor || "").toLowerCase() !== "vba") {
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
  var refresh = function () {
    if (typeof refreshCodeEditors === "function") refreshCodeEditors();
  };
  if (typeof window.requestAnimationFrame === "function") window.requestAnimationFrame(refresh);
  else refresh();
  toolDocumentation.ensure();
}

function renderToolEditor() {
  var skill = state.tools[state.selectedToolIndex] || null;
  if (toolSourceRead && (toolSourceRead.tool !== skill || toolSourceRead.chatId !== state.activeChatId ||
      toolSourceRead.revision !== skill.Revision)) cancelToolSourceRead();
  trimToolSourceCache(skill);
  var disabled = !skill;
  var builtIn = !!(skill && skill.BuiltIn);
  var documentLocal = !!(skill && String(skill.Scope || skill.scope || "").toLowerCase() === "document");
  var sourceUnavailable = !skill || !skill._sourceLoaded;
  var readOnly = builtIn || documentLocal || sourceUnavailable || !!state.bridgeUnavailable;
  setToolRunContinuation(null);
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
  if ($("toolEditorMeta")) $("toolEditorMeta").textContent = skill ? ((builtIn ? "Встроенный" : "Пользовательский") + " · " + (skill.Host || "Common") + " · " + (skill.Executor || "vba") +
    (skill._sourceConflict ? " · Исходник изменился: черновик сохранён, запись заблокирована" : skill._sourceError ? " · Ошибка чтения: обновите Library" : sourceUnavailable ? " · Загрузка исходника…" : "")) : "";
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
  toolDocumentation.prepare(skill);
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
  renderToolRunText("");
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
  $("toolRunArgsInput").disabled = sourceUnavailable;
  $("applyToolRunJsonButton").disabled = sourceUnavailable;
  if (typeof setCodeEditorReadOnly === "function") {
    setCodeEditorReadOnly("toolSchemaInput", disabled || readOnly);
    setCodeEditorReadOnly("toolRunArgsInput", sourceUnavailable);
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
  updateToolWriteControls();
  loadSelectedToolSource(skill);
}

function syncSelectedToolFromEditor() {
  if (typeof syncCodeEditors === "function") {
    syncCodeEditors(["toolSchemaInput", "toolRunArgsInput", "toolCodeInput", "toolReadmeInput"]);
  }
  var skill = state.tools[state.selectedToolIndex];
  if (!skill || !skill._sourceLoaded || skill.BuiltIn || skill.Scope === "document") {
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
  if (!tool || !tool._sourceLoaded) return true;
  if (!toolStructuredEditor.syncSchemaDraft()) { state.toolEditorPage = "schema"; applyToolEditorPage(); return false; }
  return true;
}

function validateAllToolDefinitions() {
  for (var index = 0; index < state.tools.length; index += 1) {
    var tool = state.tools[index] || {};
    if (!tool._sourceLoaded) continue;
    try { JSON.parse(tool.ArgumentSchemaJson || emptyToolSchema()); }
    catch (error) { throw new Error("Некорректная schema у " + (tool.Id || "инструмента") + ": " + error.message); }

  }
}


function selectedToolContext() {
  syncSelectedToolFromEditor();
  var skill = state.tools[state.selectedToolIndex];
  if (!skill || !skill._sourceLoaded) {
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
  if (!tool || !tool._sourceLoaded || tool.BuiltIn || tool.Scope === "document" || String(tool.Executor || "").toLowerCase() !== "vba") {
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
  window.addEventListener("pagehide", cancelToolDocumentationRead);
  window.addEventListener("pagehide", cancelToolSourceRead);
  window.addEventListener("pagehide", cancelToolLibraryWrite);
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
      Components: [{ Name: "RNA_NewTool", Type: "StdModule", FileName: "RNA_NewTool.bas", Code: "Option Explicit\n" }],
      _baseId: "",
      _baseRevision: "",
      _sourceLoaded: true
    });
    state.selectedToolIndex = state.tools.length - 1;
    state.selectedInstructionKind = "tool";
    updateToolLibraryDirty();
    renderTools();
  });

  $("cloneToolButton").addEventListener("click", function () {
    syncSelectedToolFromEditor();
    var source = state.tools[state.selectedToolIndex];
    if (!source || !source._sourceLoaded || source.BuiltIn) {
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
      InstallationStatus: "not_installed",
      _baseId: "",
      _baseRevision: "",
      _sourceLoaded: true
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
  $("nextToolPageButton").addEventListener("click", toolActions.next);

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
