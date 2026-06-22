function renderVbaProject() {
  var moduleSelect = $("vbaModuleSelect");
  var backupSelect = $("vbaBackupSelect");
  var query = (($("vbaModuleSearchInput") && $("vbaModuleSearchInput").value) || "").trim().toLowerCase();
  moduleSelect.innerHTML = "";
  backupSelect.innerHTML = "";
  var renderedModules = 0;

  state.vba.modules.forEach(function (module) {
    if (query && !vbaModuleMatchesSearch(module, query)) {
      return;
    }

    var option = document.createElement("option");
    option.value = module.name || module.Name || "";
    option.textContent = option.value + " (" + (module.type || module.Type || "module") + ")";
    moduleSelect.appendChild(option);
    renderedModules += 1;
  });

  if (!renderedModules) {
    var emptyModule = document.createElement("option");
    emptyModule.value = "";
    emptyModule.textContent = state.bridgeUnavailable ? "Office bridge недоступен" : (query ? "Модули не найдены" : "Модули не загружены");
    moduleSelect.appendChild(emptyModule);
  }

  state.vba.backups.forEach(function (backup) {
    var option = document.createElement("option");
    option.value = backup.BackupId || backup.backupId || "";
    option.textContent = (backup.ModuleName || backup.moduleName || "module") + " - " + (backup.CreatedUtc || backup.createdUtc || "");
    backupSelect.appendChild(option);
  });

  if (!state.vba.backups.length) {
    var emptyBackup = document.createElement("option");
    emptyBackup.value = "";
    emptyBackup.textContent = "Резервных копий нет";
    backupSelect.appendChild(emptyBackup);
  }

  moduleSelect.disabled = state.bridgeUnavailable || !renderedModules;
  backupSelect.disabled = state.bridgeUnavailable || !state.vba.backups.length;
  $("vbaModuleSearchInput").disabled = state.bridgeUnavailable;
  $("refreshVbaButton").disabled = state.bridgeUnavailable;
  var editorPanel = document.querySelector(".vba-editor");
  var emptyState = $("vbaEmptyState");
  var isEmpty = state.bridgeUnavailable || !renderedModules;
  if (editorPanel) {
    editorPanel.classList.toggle("is-empty", isEmpty);
  }
  if (emptyState) {
    emptyState.querySelector(".vba-empty-title").textContent = state.bridgeUnavailable ? "VBA недоступен" : "VBA не загружен";
    emptyState.querySelector(".vba-empty-text").textContent = state.bridgeUnavailable
      ? "Откройте RNAssistant внутри Office, чтобы загрузить VBA-проект и работать с модулями."
      : "Нажмите «Загрузить VBA», чтобы редактировать модули, сравнивать diff и сохранять изменения.";
  }
  if (state.bridgeUnavailable) {
    $("vbaStatus").textContent = "Office bridge недоступен. VBA загрузится внутри add-in.";
  } else if (!state.vba.modules.length) {
    $("vbaStatus").textContent = "VBA-контекст не загружен.";
  }

  if (state.vba.selectedModule && selectHasOption(moduleSelect, state.vba.selectedModule)) {
    moduleSelect.value = state.vba.selectedModule;
  }
  renderSelectedVbaModule();
}

function selectHasOption(select, value) {
  for (var i = 0; i < select.options.length; i += 1) {
    if (select.options[i].value === value) {
      return true;
    }
  }
  return false;
}

function vbaModuleMatchesSearch(module, query) {
  var text = [
    module.name || module.Name || "",
    module.type || module.Type || "",
    module.code || module.Code || ""
  ].join(" ").toLowerCase();
  return text.indexOf(query) >= 0;
}

function renderSelectedVbaModule() {
  var module = selectedVbaModule();
  state.vba.selectedModule = vbaModuleName(module);
  setVbaEditorCode(module ? vbaModuleCode(module) : "");
  $("vbaMetaBox").textContent = module ? JSON.stringify({
    name: vbaModuleName(module),
    type: module.type || module.Type,
    lineCount: module.lineCount || module.LineCount
  }, null, 2) : "";
  renderVbaDiff({
    summary: module ? "Нажмите «Показать diff», чтобы посмотреть изменения." : (state.bridgeUnavailable ? "Office bridge недоступен." : "Модуль не выбран."),
    lines: []
  });
  if (typeof setCodeEditorReadOnly === "function") {
    setCodeEditorReadOnly("vbaCodeInput", !module || state.bridgeUnavailable);
  }
  $("previewVbaDiffButton").disabled = !module || state.bridgeUnavailable;
  $("saveVbaButton").disabled = !module || state.bridgeUnavailable;
  $("restoreVbaButton").disabled = state.bridgeUnavailable || !$("vbaBackupSelect").value || !module;
  $("reviewVbaButton").disabled = !module || state.bridgeUnavailable;
  updateVbaMacroSuggestion();
}

function selectedVbaModule() {
  var selectedName = $("vbaModuleSelect").value;
  var found = null;
  state.vba.modules.forEach(function (item) {
    if ((item.name || item.Name) === selectedName) {
      found = item;
    }
  });
  return found;
}

function vbaModuleName(module) {
  return module ? (module.name || module.Name || "") : "";
}

function vbaModuleCode(module) {
  return module ? (module.code || module.Code || "") : "";
}

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

function formatVbaDiff(before, after) {
  if (before === after) {
    return { summary: "Изменений нет.", lines: [] };
  }

  var oldLines = String(before || "").replace(/\r\n/g, "\n").split("\n");
  var newLines = String(after || "").replace(/\r\n/g, "\n").split("\n");
  var start = 0;
  while (start < oldLines.length && start < newLines.length && oldLines[start] === newLines[start]) {
    start += 1;
  }

  var oldEnd = oldLines.length - 1;
  var newEnd = newLines.length - 1;
  while (oldEnd >= start && newEnd >= start && oldLines[oldEnd] === newLines[newEnd]) {
    oldEnd -= 1;
    newEnd -= 1;
  }

  var oldCount = Math.max(0, oldEnd - start + 1);
  var newCount = Math.max(0, newEnd - start + 1);
  var output = [];
  var i;
  for (i = Math.max(0, start - 3); i < start; i += 1) {
    output.push({ type: "context", text: oldLines[i] });
  }
  oldLines.slice(start, oldEnd + 1).slice(0, 200).forEach(function (line) {
    output.push({ type: "remove", text: line });
  });
  newLines.slice(start, newEnd + 1).slice(0, 200).forEach(function (line) {
    output.push({ type: "add", text: line });
  });
  if (oldCount > 200 || newCount > 200) {
    output.push({ type: "note", text: "...сравнение обрезано..." });
  }
  for (i = oldEnd + 1; i < Math.min(oldLines.length, oldEnd + 4); i += 1) {
    output.push({ type: "context", text: oldLines[i] });
  }
  return {
    summary: "Измененные строки: -" + oldCount + " +" + newCount,
    lines: output
  };
}

function renderVbaDiff(diff) {
  var box = $("vbaDiffOutput");
  if (!box) {
    return;
  }

  box.innerHTML = "";
  var summary = document.createElement("div");
  summary.className = "vba-diff-summary";
  summary.textContent = diff.summary || "";
  box.appendChild(summary);

  if (!diff.lines || !diff.lines.length) {
    var empty = document.createElement("div");
    empty.className = "vba-diff-empty";
    empty.textContent = diff.summary === "Изменений нет." ? "Текст редактора совпадает с загруженным модулем." : "Diff пока не построен.";
    box.appendChild(empty);
    return;
  }

  diff.lines.forEach(function (line) {
    var row = document.createElement("div");
    row.className = "vba-diff-line " + line.type;

    var marker = document.createElement("span");
    marker.className = "vba-diff-marker";
    marker.textContent = line.type === "add" ? "+" : (line.type === "remove" ? "-" : " ");

    var text = document.createElement("code");
    text.textContent = line.text || "";

    row.appendChild(marker);
    row.appendChild(text);
    box.appendChild(row);
  });
}

function previewVbaDiff() {
  var module = selectedVbaModule();
  if (!module) {
    renderVbaDiff({ summary: "Модуль не выбран.", lines: [] });
    return;
  }

  renderVbaDiff(formatVbaDiff(vbaModuleCode(module), vbaEditorCode()));
  $("vbaStatus").textContent = "Сравнение готово.";
}

async function withVbaActivity(message, work) {
  setActivity("vba", message);
  try {
    await work();
    return true;
  } catch (error) {
    $("vbaStatus").textContent = error.message;
    log(error.detail || error.message);
    return false;
  } finally {
    clearActivity();
  }
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

async function refreshVbaProject() {
  await withVbaActivity("Читаю VBA проект...", async function () {
    var response = await send("getVbaProject", { maxChars: Number($("vbaContextLimitInput").value || 30000) });
    readVbaResult(response);
  });
}

async function saveVbaModule() {
  var moduleName = $("vbaModuleSelect").value;
  if (!moduleName) {
    return;
  }

  previewVbaDiff();
  if (await withVbaActivity("Сохраняю VBA-модуль...", async function () {
    var response = await send("saveVbaModule", { moduleName: moduleName, code: vbaEditorCode() });
    $("vbaStatus").textContent = response.Message || response.message || "VBA-модуль сохранен.";
  })) {
    await refreshVbaProject();
  }
}

async function restoreVbaBackup() {
  var backupId = $("vbaBackupSelect").value;
  var moduleName = $("vbaModuleSelect").value;
  if (await withVbaActivity("Восстанавливаю резервную копию VBA...", async function () {
    var response = await send("restoreVbaBackup", { backupId: backupId, moduleName: moduleName });
    $("vbaStatus").textContent = response.Message || response.message || "Резервная копия VBA восстановлена.";
  })) {
    await refreshVbaProject();
  }
}

function reviewVbaInChat() {
  var patchTool = (state.host || "excel").toLowerCase() + ".vba_apply_patch";
  ensureVbaContextAttached().then(function () {
    switchTab("chat");
    setChatInputText("Проверь VBA код из добавленного контекста: найди ошибки, риски и места для улучшения. Если нужны небольшие правки, используй " + patchTool + "; полную замену модуля предлагай только когда это реально нужно.", true);
  }).catch(function (error) {
    log(error.detail || error.message);
  });
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
  updateVbaMacroSuggestion();
  $("vbaStatus").textContent = "Есть несохраненные изменения VBA.";
}

async function runVbaMacro() {
  var toolId = vbaMacroToolId();
  var input = $("vbaMacroInput");
  var macroName = (input.value || "").trim() || input.getAttribute("data-suggested") || "";
  if (!toolId) {
    setVbaMacroStatus("Текущее приложение не поддерживает запуск макросов.", "error");
    return;
  }
  if (!macroName) {
    setVbaMacroStatus("Введите имя макроса.", "error");
    return;
  }

  setActivity("vba", "Запускаю макрос...");
  $("runVbaMacroButton").disabled = true;
  try {
    var response = await send("runTool", {
      toolId: toolId,
      arguments: { macroName: macroName },
      dryRun: false
    });
    setVbaMacroStatus(response.Message || response.message || "Макрос выполнен: " + macroName, "ok");
    logToolResult("Запуск макроса", toolId, response);
  } catch (error) {
    setVbaMacroStatus(error.detail || error.message, "error");
    log(error.detail || error.message);
  } finally {
    clearActivity();
    updateVbaMacroRunState();
  }
}

function bindVbaActions() {
  $("refreshVbaButton").addEventListener("click", refreshVbaProject);
  $("vbaModuleSearchInput").addEventListener("input", renderVbaProject);
  $("vbaModuleSelect").addEventListener("change", renderSelectedVbaModule);
  $("vbaCodeInput").addEventListener("input", markVbaEditorDirty);
  $("vbaMacroInput").addEventListener("input", updateVbaMacroRunState);
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
  renderVbaDiff({ summary: "Обновите VBA, чтобы загрузить модули.", lines: [] });
  updateVbaMacroRunState();
}
