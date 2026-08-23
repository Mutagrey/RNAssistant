function renderVbaProject() {
  var moduleSelect = $("vbaModuleSelect");
  var backupSelect = $("vbaBackupSelect");
  var moduleList = $("vbaModuleList");
  var query = (($("vbaModuleSearchInput") && $("vbaModuleSearchInput").value) || "").trim().toLowerCase();
  moduleSelect.innerHTML = "";
  backupSelect.innerHTML = "";
  if (moduleList) {
    moduleList.innerHTML = "";
  }
  var renderedModules = 0;
  var filteredModules = [];

  state.vba.modules.forEach(function (module) {
    if (query && !vbaModuleMatchesSearch(module, query)) {
      return;
    }

    filteredModules.push(module);
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
  $("refreshVbaEmptyButton").disabled = state.bridgeUnavailable;
  var editorPanel = document.querySelector(".vba-editor");
  var emptyState = $("vbaEmptyState");
  var isEmpty = state.bridgeUnavailable || !renderedModules;
  if (editorPanel) {
    editorPanel.classList.toggle("is-empty", isEmpty);
  }
  if (emptyState) {
    emptyState.querySelector(".resource-editor-empty-title").textContent = state.bridgeUnavailable ? "VBA недоступен" : "VBA не загружен";
    emptyState.querySelector(".resource-editor-empty-text").textContent = state.bridgeUnavailable
      ? "Откройте RNAssistant внутри Office, чтобы загрузить VBA-проект и работать с модулями."
      : "Нажмите «Загрузить VBA», чтобы редактировать модули, сравнивать diff и сохранять изменения.";
  }
  if (state.bridgeUnavailable) {
    $("vbaStatus").textContent = "Office bridge недоступен. VBA загрузится внутри add-in.";
  } else if (!state.vba.modules.length) {
    $("vbaStatus").textContent = "VBA-проект не загружен.";
  }

  if (state.vba.selectedModule && selectHasOption(moduleSelect, state.vba.selectedModule)) {
    moduleSelect.value = state.vba.selectedModule;
  } else if (renderedModules) {
    moduleSelect.value = moduleSelect.options[0].value;
  }
  renderVbaModuleList(filteredModules, query);
  renderSelectedVbaModule();
}

function renderVbaModuleList(modules, query) {
  var list = $("vbaModuleList");
  if (!list) {
    return;
  }

  list.innerHTML = "";
  if (!modules.length) {
    list.appendChild(createResourceEmptyState(state.bridgeUnavailable ? "Office bridge недоступен." : (query ? "Модули не найдены." : "Модули не загружены.")));
    return;
  }

  var selectedName = $("vbaModuleSelect").value;
  groupVbaModules(modules).forEach(function (group) {
    var section = createResourceGroup({
      key: "vba:" + group.label,
      title: group.label,
      count: group.modules.length
    });
    section.className += " vba-module-group";
    section.setAttribute("role", "group");
    var body = section.treeChildren || section;

    group.modules.forEach(function (module) {
      var name = vbaModuleName(module);
      var type = vbaModuleType(module);
      var lineCount = module.lineCount || module.LineCount || 0;
      var item = createResourceListItem({
        title: name,
        enabled: null,
        active: name === selectedName,
        meta: type + (lineCount ? " - " + lineCount + " строк" : ""),
        description: hasVbaModuleCode(module) ? (firstVbaProcedureName(vbaModuleCode(module)) || "VBA module") : "Код загружается по выбору",
        compact: true,
        icon: vbaModuleIcon(type),
        depth: 1,
        onClick: function () {
          if (state.vbaEditorDirty && !window.confirm("Отменить несохранённые изменения текущего VBA-модуля?")) return;
          state.vbaEditorDirty = false;
          $("vbaModuleSelect").value = name;
          state.vba.selectedModule = name;
          renderVbaModuleList(modules, query);
          renderSelectedVbaModule();
          loadSelectedVbaModule();
        }
      });
      item.setAttribute("role", "treeitem");
      item.setAttribute("aria-selected", name === selectedName ? "true" : "false");
      body.appendChild(item);
    });

    list.appendChild(section);
  });
}

function vbaModuleIcon(type) {
  var value = String(type || "module").toLowerCase();
  if (value.indexOf("class") >= 0) {
    return "CLS";
  }
  if (value.indexOf("form") >= 0) {
    return "FRM";
  }
  if (value.indexOf("document") >= 0 || value.indexOf("worksheet") >= 0 || value.indexOf("workbook") >= 0) {
    return "OBJ";
  }
  return "MOD";
}

function groupVbaModules(modules) {
  var byLabel = {};
  var groups = [];
  modules.forEach(function (module) {
    var type = vbaModuleType(module);
    var label = vbaModuleGroupLabel(type);
    if (!byLabel[label]) {
      byLabel[label] = { label: label, order: vbaModuleGroupOrder(type), modules: [] };
      groups.push(byLabel[label]);
    }
    byLabel[label].modules.push(module);
  });

  groups.sort(function (left, right) {
    if (left.order !== right.order) {
      return left.order - right.order;
    }
    return left.label.localeCompare(right.label);
  });
  return groups;
}

function vbaModuleType(module) {
  return module ? (module.type || module.Type || "module") : "module";
}

function vbaModuleGroupLabel(type) {
  var value = String(type || "module").toLowerCase();
  if (value.indexOf("document") >= 0 || value.indexOf("worksheet") >= 0 || value.indexOf("workbook") >= 0) {
    return "Объекты документа";
  }
  if (value.indexOf("class") >= 0) {
    return "Классы";
  }
  if (value.indexOf("form") >= 0) {
    return "Формы";
  }
  if (value.indexOf("module") >= 0 || value === "standard") {
    return "Модули";
  }
  return type || "Other";
}

function vbaModuleGroupOrder(type) {
  var label = vbaModuleGroupLabel(type);
  if (label === "Модули") {
    return 1;
  }
  if (label === "Объекты документа") {
    return 2;
  }
  if (label === "Классы") {
    return 3;
  }
  if (label === "Формы") {
    return 4;
  }
  return 9;
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
  var loaded = hasVbaModuleCode(module);
  var loading = module && state.vba.loadingModule === vbaModuleName(module);
  var moduleName = vbaModuleName(module);
  var keepDraft = state.vbaEditorDirty && state.vbaEditorLoadedModule === moduleName;
  state.vba.selectedModule = vbaModuleName(module);
  if (!keepDraft) {
    state.vbaEditorDirty = false;
    state.vbaEditorLoadedModule = moduleName;
    setVbaEditorCode(loaded ? vbaModuleCode(module) : "");
  }
  $("vbaModuleTitle").textContent = module ? vbaModuleName(module) : "Модуль не выбран";
  $("vbaModuleMeta").textContent = module ? vbaModuleMetaText(module) + (loaded ? "" : (loading ? " - читаю код..." : " - код не загружен")) : "";
  $("vbaMetaBox").textContent = module ? JSON.stringify({
    name: vbaModuleName(module),
    type: module.type || module.Type,
    lineCount: module.lineCount || module.LineCount
  }, null, 2) : "";
  renderVbaDiff({
    summary: module && loaded ? "Нажмите «Показать diff», чтобы посмотреть изменения." : (module ? "Код модуля еще не загружен." : (state.bridgeUnavailable ? "Office bridge недоступен." : "Модуль не выбран.")),
    lines: []
  });
  if (typeof setCodeEditorReadOnly === "function") {
    setCodeEditorReadOnly("vbaCodeInput", !module || !loaded || state.bridgeUnavailable);
  }
  $("previewVbaDiffButton").disabled = !module || !loaded || state.bridgeUnavailable;
  $("saveVbaButton").disabled = !module || !loaded || state.bridgeUnavailable;
  $("restoreVbaButton").disabled = state.bridgeUnavailable || !$("vbaBackupSelect").value || !module || !loaded;
  $("reviewVbaButton").disabled = !module || !loaded || state.bridgeUnavailable;
  applyVbaMode();
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

function hasVbaModuleCode(module) {
  return !!module && (Object.prototype.hasOwnProperty.call(module, "code") || Object.prototype.hasOwnProperty.call(module, "Code"));
}

async function loadSelectedVbaModule() {
  var module = selectedVbaModule();
  if (!module || hasVbaModuleCode(module) || state.bridgeUnavailable) {
    return;
  }

  var moduleName = vbaModuleName(module);
  state.vba.loadingModule = moduleName;
  renderSelectedVbaModule();
  try {
    var response = await send("getVbaModule", { moduleName: moduleName });
    if (response.Success === false || response.success === false) {
      throw new Error(response.Message || response.message || "VBA-модуль не прочитан.");
    }
    var dataJson = response.DataJson || response.dataJson || "{}";
    var data = JSON.parse(dataJson);
    module.code = data.code !== undefined ? data.code : (data.Code || "");
    module.type = data.type || data.Type || module.type || module.Type;
    module.lineCount = data.lineCount || data.LineCount || module.lineCount || module.LineCount;
    module.codeSha256 = data.codeSha256 || data.CodeSha256 || "";
    $("vbaStatus").textContent = response.Message || response.message || "VBA-модуль загружен.";
  } catch (error) {
    $("vbaStatus").textContent = error.message;
    log(error.detail || error.message);
  } finally {
    if (state.vba.loadingModule === moduleName) {
      state.vba.loadingModule = "";
    }
    if (vbaModuleName(selectedVbaModule()) === moduleName) {
      renderSelectedVbaModule();
    }
    renderVbaModuleList(state.vba.modules.filter(function (item) {
      var query = (($("vbaModuleSearchInput") && $("vbaModuleSearchInput").value) || "").trim().toLowerCase();
      return !query || vbaModuleMatchesSearch(item, query);
    }), (($("vbaModuleSearchInput") && $("vbaModuleSearchInput").value) || "").trim().toLowerCase());
  }
}

function vbaModuleMetaText(module) {
  if (!module) {
    return "";
  }
  var parts = [module.type || module.Type || "module"];
  var lineCount = module.lineCount || module.LineCount;
  if (lineCount) {
    parts.push(lineCount + " строк");
  }
  var procedure = firstVbaProcedureName(vbaModuleCode(module));
  if (procedure) {
    parts.push("процедура: " + procedure);
  }
  return parts.join(" - ");
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

async function runVbaWork(work) {
  try {
    await work();
    return true;
  } catch (error) {
    $("vbaStatus").textContent = error.message;
    log(error.detail || error.message);
    return false;
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
  await runVbaWork(async function () {
    var response = await send("getVbaProject", {});
    readVbaResult(response);
    await loadSelectedVbaModule();
  });
}

async function saveVbaModule() {
  var moduleName = $("vbaModuleSelect").value;
  if (!moduleName) {
    return;
  }

  previewVbaDiff();
  if (await runVbaWork(async function () {
    var response = await send("saveVbaModule", { moduleName: moduleName, code: vbaEditorCode() });
    $("vbaStatus").textContent = response.Message || response.message || "VBA-модуль сохранен.";
  })) {
    await refreshVbaProject();
  }
}

async function restoreVbaBackup() {
  var backupId = $("vbaBackupSelect").value;
  var moduleName = $("vbaModuleSelect").value;
  if (await runVbaWork(async function () {
    var response = await send("restoreVbaBackup", { backupId: backupId, moduleName: moduleName });
    $("vbaStatus").textContent = response.Message || response.message || "Резервная копия VBA восстановлена.";
  })) {
    await refreshVbaProject();
  }
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
    updateVbaMacroRunState();
  }
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
