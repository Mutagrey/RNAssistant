function vbaEditorCode() {
  if (typeof getCodeEditorValue === "function") {
    return getCodeEditorValue("vbaCodeInput");
  }
  return $("vbaCodeInput").value || "";
}

function setVbaEditorCode(code) {
  if (typeof setCodeEditorValue === "function") {
    setCodeEditorValue("vbaCodeInput", code || "");
    return;
  }
  $("vbaCodeInput").value = code || "";
}

function renderVbaCodePreview() {
  var preview = $("vbaCodePreview");
  var module = selectedVbaModule();
  if (!preview) {
    return;
  }

  preview.innerHTML = "";
  if (!module) {
    preview.textContent = state.bridgeUnavailable ? "Office bridge недоступен." : "Модуль не выбран.";
    return;
  }
  if (!hasVbaModuleCode(module)) {
    preview.textContent = "Код модуля еще не загружен.";
    return;
  }

  var pre = document.createElement("pre");
  var code = document.createElement("code");
  code.className = "language-vbnet";
  code.dataset.language = "vba";
  code.textContent = vbaEditorCode() || "' Пустой модуль";
  pre.appendChild(code);
  preview.appendChild(pre);
  if (typeof highlightCode === "function") {
    highlightCode(code);
  }
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
  return value === "diff" || value === "run" || value === "info" ? value : "edit";
}

function applyVbaMode() {
  var mode = normalizeVbaMode(state.vbaEditorMode);
  state.vbaEditorMode = mode;
  Array.prototype.slice.call(document.querySelectorAll(".vba-mode-button")).forEach(function (button) {
    button.classList.toggle("active", button.getAttribute("data-vba-mode") === mode);
  });
  Array.prototype.slice.call(document.querySelectorAll(".vba-view")).forEach(function (view) {
    view.classList.toggle("hidden", view.getAttribute("data-vba-view") !== mode);
  });
  if (mode === "edit" && typeof refreshCodeEditors === "function") {
    refreshCodeEditors(["vbaCodeInput"]);
  }
}

function renderVbaDiff(diff) {
  window.RNAssistantVbaDiff.render($("vbaDiffOutput"), diff);
}

function previewVbaDiff() {
  var module = selectedVbaModule();
  if (!module) {
    renderVbaDiff({ summary: "Модуль не выбран.", lines: [] });
    return;
  }

  renderVbaDiff(window.RNAssistantVbaDiff.format(vbaModuleCode(module), vbaEditorCode()));
  $("vbaStatus").textContent = "Сравнение готово.";
}

function readVbaResult(response) {
  var result = response.result || response.Result || response;
  var dataJson = result.DataJson || result.dataJson || "";
  var data = dataJson ? JSON.parse(dataJson) : {};
  state.vba.modules = data.modules || data.Modules || [];
  state.vba.backups = response.backups || response.Backups || [];
  $("vbaStatus").textContent = result.Message || result.message || "VBA-проект загружен.";
  renderVbaProject();
  updateVbaMacroRunState();
}

function reviewVbaInChat() {
  var module = selectedVbaModule();
  if (!module) {
    return;
  }
  var host = (state.host || "excel").toLowerCase();
  var readTool = host + ".vba_read_module";
  var patchTool = (state.host || "excel").toLowerCase() + ".vba_apply_patch";
  switchTab("chat");
  setChatInputText("Проверь VBA-модуль " + vbaModuleName(module) + ": сначала прочитай его через " + readTool + ", затем найди ошибки, риски и места для улучшения. Для небольших правок используй " + patchTool + ".", true);
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

function vbaMacroToolId() {
  var host = (state.host || "").toLowerCase();
  if (host === "excel" || host === "word" || host === "powerpoint") {
    return host + ".run_macro";
  }
  return "";
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
  var supported = !!vbaMacroToolId() && !state.bridgeUnavailable;
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
  state.vbaEditorDirty = true;
  updateVbaMacroSuggestion();
  $("vbaStatus").textContent = "Есть несохраненные изменения VBA.";
}

var vbaActions = window.RNAssistantVbaActions.create({
  send: send,
  log: log,
  logToolResult: logToolResult,
  getModuleName: function () { return $("vbaModuleSelect").value; },
  getEditorCode: vbaEditorCode,
  getBackupId: function () { return $("vbaBackupSelect").value; },
  previewDiff: previewVbaDiff,
  applyProjectResponse: readVbaResult,
  loadSelectedModule: loadSelectedVbaModule,
  setStatus: function (text) { $("vbaStatus").textContent = text; },
  getMacroToolId: vbaMacroToolId,
  getMacroName: function () {
    var input = $("vbaMacroInput");
    return (input.value || "").trim() || input.getAttribute("data-suggested") || "";
  },
  setMacroBusy: function (busy) { $("runVbaMacroButton").disabled = !!busy; },
  setMacroStatus: setVbaMacroStatus,
  updateMacroRunState: updateVbaMacroRunState
});

function refreshVbaProject() {
  return vbaActions.refreshProject();
}

function saveVbaModule() {
  return vbaActions.saveModule();
}

function restoreVbaBackup() {
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
  $("vbaCodeInput").addEventListener("input", markVbaEditorDirty);
  $("vbaMacroInput").addEventListener("input", updateVbaMacroRunState);
  Array.prototype.slice.call(document.querySelectorAll(".vba-mode-button")).forEach(function (button) {
    button.addEventListener("click", function () {
      setVbaMode(button.getAttribute("data-vba-mode"));
    });
  });
  $("previewVbaDiffButton").addEventListener("click", previewVbaDiff);
  $("saveVbaButton").addEventListener("click", saveVbaModule);
  $("restoreVbaButton").addEventListener("click", restoreVbaBackup);
  $("reviewVbaButton").addEventListener("click", reviewVbaInChat);
  $("runVbaMacroButton").addEventListener("click", runVbaMacro);
  Array.prototype.slice.call(document.querySelectorAll("#tab-vba details")).forEach(function (details) {
    details.addEventListener("toggle", function () {
      if (typeof refreshCodeEditors === "function") {
        refreshCodeEditors(["vbaCodeInput"]);
      }
    });
  });
  renderVbaProject();
  applyVbaMode();
  updateVbaMacroRunState();
}
