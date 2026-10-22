function vbaEditorCode() {
  if (typeof getCodeEditorValue === "function") {
    return getCodeEditorValue("vbaCodeInput");
  }
  return $("vbaCodeInput").value || "";
}

function setVbaEditorCode(code, documentKey) {
  if (typeof setCodeEditorValue === "function") {
    setCodeEditorValue("vbaCodeInput", code || "", documentKey);
    return;
  }
  $("vbaCodeInput").value = code || "";
}

function setVbaMode(mode) {
  if (typeof syncCodeEditors === "function") {
    syncCodeEditors(["vbaCodeInput"]);
  }
  state.vbaEditorMode = normalizeVbaMode(mode);
  if (state.vbaEditorMode === "diff") {
    previewVbaDiff();
  }
  applyVbaMode();
}

function normalizeVbaMode(mode) {
  var value = mode || "edit";
  return value === "diff" || value === "run" || value === "history" ? value : "edit";
}

function applyVbaMode() {
  var mode = normalizeVbaMode(state.vbaEditorMode);
  state.vbaEditorMode = mode;
  Array.prototype.slice.call(document.querySelectorAll(".vba-mode-button")).forEach(function (button) {
    var active = button.getAttribute("data-vba-mode") === mode;
    button.classList.toggle("active", active);
    if (active) button.setAttribute("aria-current", "page"); else button.removeAttribute("aria-current");
  });
  Array.prototype.slice.call(document.querySelectorAll(".vba-view")).forEach(function (view) {
    view.classList.toggle("hidden", view.getAttribute("data-vba-view") !== mode);
  });
  if (mode === "edit" && typeof refreshCodeEditors === "function") {
    refreshCodeEditors(["vbaCodeInput"]);
  }
}

function renderVbaDiff(diff) {
  window.RNAssistantTextDiff.render($("vbaDiffOutput"), diff);
}

function previewVbaDiff() {
  var module = selectedVbaModule();
  if (!module) {
    renderVbaDiff({ summary: "Модуль не выбран.", lines: [] });
    return;
  }

  renderVbaDiff(window.RNAssistantTextDiff.format(vbaModuleCode(module), vbaEditorCode()));
  $("vbaStatus").textContent = "Сравнение готово.";
}

function readVbaResult(response) {
  cancelVbaModuleRead();
  cancelVbaModuleWrite();
  var result = response.result || response.Result || response;
  var dataJson = result.DataJson || result.dataJson || "";
  var data = dataJson ? JSON.parse(dataJson) : {};
  state.vba.modules = data.modules || data.Modules || [];
  state.vba.backups = response.backups || response.Backups || [];
  $("vbaStatus").textContent = result.Message || result.message || "VBA-проект загружен.";
  renderVbaProject();
  updateVbaMacroRunState();
}

function firstVbaProcedureName(code) {
  var match = /^\s*(?:Public\s+|Private\s+|Friend\s+)?(?:Sub|Function)\s+([A-Za-z_][A-Za-z0-9_]*)\b/im.exec(code || "");
  return match ? match[1] : "";
}

function suggestedVbaMacroName() {
  var module = selectedVbaModule();
  var proc = firstVbaProcedureName(vbaEditorCode());
  return module && proc ? vbaModuleName(module) + "." + proc : "";
}

function setVbaMacroStatus(text, kind) {
  var node = $("vbaMacroStatus");
  if (!node) {
    return;
  }
  node.textContent = text || "";
  node.dataset.kind = kind || "";
}

function updateVbaMacroRunState() {
  var button = $("runVbaMacroButton");
  var input = $("vbaMacroInput");
  if (!button || !input) {
    return;
  }
  var supported = ["excel", "word", "powerpoint"].indexOf(String(state.host || "").toLowerCase()) >= 0 && !state.bridgeUnavailable;
  var macroName = input.value.trim() || input.getAttribute("data-suggested") || "";
  button.disabled = !supported || !macroName;
  input.disabled = state.bridgeUnavailable;
  if (state.bridgeUnavailable) {
    setVbaMacroStatus("Откройте панель внутри Office, чтобы загрузить VBA.", "muted");
  } else if (!supported) {
    setVbaMacroStatus("Запуск макросов доступен для Excel, Word и PowerPoint.", "muted");
  }
}

function updateVbaMacroSuggestion() {
  var input = $("vbaMacroInput");
  if (!input) {
    return;
  }
  var suggested = suggestedVbaMacroName();
  input.setAttribute("data-suggested", suggested);
  input.placeholder = suggested || "Module1.Main";
  updateVbaMacroRunState();
}

function markVbaEditorDirty() {
  cancelVbaModuleWrite();
  state.vbaEditorDirty = true;
  $("saveVbaButton").hidden = false;
  $("discardVbaChangesButton").disabled = false;
  updateVbaMacroSuggestion();
  $("vbaStatus").textContent = "Есть несохраненные изменения VBA.";
}

var vbaActions = window.RNAssistantVbaActions.create({
  send: send,
  cancelRequest: cancelBridgeRequest,
  getChatId: function () { return state.activeChatId; },
  getProject: function () { return state.vba.modules; },
  isAvailable: function () { return !state.bridgeUnavailable; },
  log: log,
  logToolResult: logToolResult,
  getModuleName: function () { return $("vbaModuleSelect").value; },
  getEditorCode: vbaEditorCode,
  getModuleHash: function () {
    var module = selectedVbaModule();
    return module ? (module.codeSha256 || module.CodeSha256 || "") : "";
  },
  getBackupId: function () { return $("vbaBackupSelect").value; },
  previewDiff: previewVbaDiff,
  applyProjectResponse: readVbaResult,
  loadSelectedModule: loadSelectedVbaModule,
  selectModule: function (moduleName) { state.vba.selectedModule = moduleName; },
  markSaved: function () { state.vbaEditorDirty = false; },
  setStatus: function (text) { $("vbaStatus").textContent = text; },
  getMacroName: function () {
    var input = $("vbaMacroInput");
    return (input.value || "").trim() || input.getAttribute("data-suggested") || "";
  },
  setMacroBusy: function (busy) { $("runVbaMacroButton").disabled = !!busy; },
  setMacroStatus: setVbaMacroStatus,
  updateMacroRunState: updateVbaMacroRunState
});

function cancelVbaModuleWrite() { if (vbaActions) vbaActions.cancelWrite(); }

function refreshVbaProject() {
  if (state.vbaEditorDirty && !window.confirm("Перезагрузить VBA project и потерять несохранённые изменения?")) {
    return Promise.resolve(false);
  }
  state.vbaEditorDirty = false;
  return vbaActions.refreshProject();
}

async function deleteVbaModule(moduleName) {
  var module = (state.vba.modules || []).filter(function (item) {
    return vbaModuleName(item) === moduleName;
  })[0] || null;
  if (!isDeletableVbaModule(module)) return false;
  var selectedName = vbaModuleName(selectedVbaModule());
  if (state.vbaEditorDirty && selectedName !== moduleName) {
    window.alert("Сначала сохраните изменения текущего VBA-модуля.");
    return false;
  }
  var warning = "Удалить VBA-модуль «" + moduleName + "»? Перед удалением будет создана резервная копия.";
  if (state.vbaEditorDirty) warning = "Есть несохранённые изменения. " + warning;
  if (!window.confirm(warning)) return false;

  try {
    var deleted = await vbaActions.deleteModule(moduleName);
    if (!deleted) return false;
    state.vbaEditorDirty = false;
    return true;
  } catch (error) {
    $("vbaStatus").textContent = error.message;
    log(error.detail || error.message, "error");
    return false;
  }
}

function saveVbaModule() {
  return vbaActions.saveModule();
}

function closeVbaActionsMenu() {
  $("vbaActionsMenu").open = false;
}

function discardVbaChanges() {
  closeVbaActionsMenu();
  var module = selectedVbaModule();
  if (!module || !hasVbaModuleCode(module) || !state.vbaEditorDirty) return;
  if (!window.confirm("Отменить изменения? Несохранённый код модуля будет заменён загруженной версией.")) return;
  cancelVbaModuleWrite();
  setVbaEditorCode(vbaModuleCode(module), vbaModuleName(module));
  state.vbaEditorDirty = false;
  $("saveVbaButton").hidden = true;
  $("discardVbaChangesButton").disabled = true;
  $("vbaStatus").textContent = "Изменения отменены.";
  updateVbaMacroSuggestion();
  if (state.vbaEditorMode === "diff") previewVbaDiff();
}

function downloadVbaModule() {
  closeVbaActionsMenu();
  var module = selectedVbaModule();
  if (!module || !hasVbaModuleCode(module)) return;
  var fileName = (vbaModuleName(module) || "Module").replace(/[^A-Za-z0-9_.-]/g, "_") + ".bas";
  var url = URL.createObjectURL(new Blob([vbaEditorCode()], { type: "text/plain;charset=utf-8" }));
  var link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  window.setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
}

function restoreVbaBackup() {
  var backupId = $("vbaBackupSelect").value;
  if (!backupId) return Promise.resolve(false);
  var backup = (state.vba.backups || []).filter(function (item) {
    return (item.BackupId || item.backupId || "") === backupId;
  })[0] || {};
  var moduleName = backup.ModuleName || backup.moduleName || $("vbaModuleSelect").value || "module";
  if (!window.confirm("Восстановить «" + moduleName + "» из выбранной резервной копии?\n\nТекущее состояние будет сохранено как новая journaled VBA mutation.")) {
    return Promise.resolve(false);
  }
  return vbaActions.restoreBackup();
}

function runVbaMacro() {
  return vbaActions.runMacro();
}

function bindVbaActions() {
  $("refreshVbaButton").addEventListener("click", refreshVbaProject);
  $("refreshVbaEmptyButton").addEventListener("click", refreshVbaProject);
  $("vbaModuleSearchInput").addEventListener("input", renderVbaProject);
  $("vbaModuleSelect").addEventListener("change", function () {
    renderSelectedVbaModule();
    loadSelectedVbaModule();
  });
  window.addEventListener("pagehide", cancelVbaModuleRead);
  window.addEventListener("pagehide", cancelVbaModuleWrite);
  $("vbaCodeInput").addEventListener("input", markVbaEditorDirty);
  $("vbaMacroInput").addEventListener("input", updateVbaMacroRunState);
  Array.prototype.slice.call(document.querySelectorAll(".vba-mode-button")).forEach(function (button) {
    button.addEventListener("click", function () {
      setVbaMode(button.getAttribute("data-vba-mode"));
    });
  });
  $("previewVbaDiffButton").addEventListener("click", previewVbaDiff);
  $("saveVbaButton").addEventListener("click", saveVbaModule);
  $("discardVbaChangesButton").addEventListener("click", discardVbaChanges);
  $("downloadVbaModuleButton").addEventListener("click", downloadVbaModule);
  $("restoreVbaButton").addEventListener("click", restoreVbaBackup);
  $("runVbaMacroButton").addEventListener("click", runVbaMacro);
  Array.prototype.slice.call(document.querySelectorAll("#tab-vba details")).forEach(function (details) {
    details.addEventListener("toggle", function () {
      if (typeof refreshCodeEditors === "function") refreshCodeEditors(["vbaCodeInput"]);
    });
  });
  renderVbaProject();
  applyVbaMode();
  updateVbaMacroRunState();
}
