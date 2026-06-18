function renderVbaProject() {
  var moduleSelect = $("vbaModuleSelect");
  var backupSelect = $("vbaBackupSelect");
  moduleSelect.innerHTML = "";
  backupSelect.innerHTML = "";

  state.vba.modules.forEach(function (module) {
    var option = document.createElement("option");
    option.value = module.name || module.Name || "";
    option.textContent = option.value + " (" + (module.type || module.Type || "module") + ")";
    moduleSelect.appendChild(option);
  });

  state.vba.backups.forEach(function (backup) {
    var option = document.createElement("option");
    option.value = backup.BackupId || backup.backupId || "";
    option.textContent = (backup.ModuleName || backup.moduleName || "module") + " - " + (backup.CreatedUtc || backup.createdUtc || "");
    backupSelect.appendChild(option);
  });

  if (state.vba.selectedModule) {
    moduleSelect.value = state.vba.selectedModule;
  }
  renderSelectedVbaModule();
}

function renderSelectedVbaModule() {
  var module = selectedVbaModule();
  state.vba.selectedModule = vbaModuleName(module);
  $("vbaCodeInput").value = module ? vbaModuleCode(module) : "";
  renderVbaCodePreview();
  $("vbaMetaBox").textContent = module ? JSON.stringify({
    name: vbaModuleName(module),
    type: module.type || module.Type,
    lineCount: module.lineCount || module.LineCount
  }, null, 2) : "";
  $("vbaDiffOutput").textContent = "";
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

function renderVbaCodePreview() {
  var preview = $("vbaCodePreview");
  if (!preview) {
    return;
  }

  preview.innerHTML = "";
  var codeText = $("vbaCodeInput").value || "";
  if (!codeText.trim()) {
    var empty = document.createElement("div");
    empty.className = "vba-code-empty";
    empty.textContent = "No VBA code loaded.";
    preview.appendChild(empty);
    return;
  }

  var tools = document.createElement("div");
  tools.className = "block-tools vba-preview-tools";
  var language = document.createElement("span");
  language.className = "code-lang";
  language.textContent = "vba";
  tools.appendChild(language);

  var pre = document.createElement("pre");
  var code = document.createElement("code");
  code.className = "language-vba";
  code.textContent = codeText;
  pre.appendChild(code);
  preview.appendChild(tools);
  preview.appendChild(pre);
  highlightCode(code);
}

function formatVbaDiff(before, after) {
  if (before === after) {
    return "No changes.";
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
  var output = ["Changed lines: -" + oldCount + " +" + newCount, ""];
  var i;
  for (i = Math.max(0, start - 3); i < start; i += 1) {
    output.push("  " + oldLines[i]);
  }
  oldLines.slice(start, oldEnd + 1).slice(0, 200).forEach(function (line) {
    output.push("- " + line);
  });
  newLines.slice(start, newEnd + 1).slice(0, 200).forEach(function (line) {
    output.push("+ " + line);
  });
  if (oldCount > 200 || newCount > 200) {
    output.push("...diff truncated...");
  }
  for (i = oldEnd + 1; i < Math.min(oldLines.length, oldEnd + 4); i += 1) {
    output.push("  " + oldLines[i]);
  }
  return output.join("\n");
}

function previewVbaDiff() {
  var module = selectedVbaModule();
  if (!module) {
    $("vbaDiffOutput").textContent = "No module selected.";
    return;
  }

  $("vbaDiffOutput").textContent = formatVbaDiff(vbaModuleCode(module), $("vbaCodeInput").value);
  $("vbaStatus").textContent = "Diff preview ready.";
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
  $("vbaStatus").textContent = result.Message || result.message || "VBA project loaded.";
  renderVbaProject();
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
  if (await withVbaActivity("Сохраняю VBA module...", async function () {
    var response = await send("saveVbaModule", { moduleName: moduleName, code: $("vbaCodeInput").value });
    $("vbaStatus").textContent = response.Message || response.message || "VBA module saved.";
  })) {
    await refreshVbaProject();
  }
}

async function restoreVbaBackup() {
  var backupId = $("vbaBackupSelect").value;
  var moduleName = $("vbaModuleSelect").value;
  if (await withVbaActivity("Восстанавливаю VBA backup...", async function () {
    var response = await send("restoreVbaBackup", { backupId: backupId, moduleName: moduleName });
    $("vbaStatus").textContent = response.Message || response.message || "VBA backup restored.";
  })) {
    await refreshVbaProject();
  }
}

function reviewVbaInChat() {
  var patchTool = (state.host || "excel").toLowerCase() + ".vba_apply_patch";
  ensureVbaContextAttached().then(function () {
    $("chatInput").value = "Проверь VBA код из добавленного контекста: найди ошибки, риски и места для улучшения. Если нужны небольшие правки, используй " + patchTool + "; полную замену модуля предлагай только когда это реально нужно.";
    switchTab("chat");
    $("chatInput").focus();
  }).catch(function (error) {
    log(error.detail || error.message);
  });
}
