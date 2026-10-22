var vbaModuleRead = null;
var vbaModuleReadPending = 0;

function closeVbaModuleRead(operation) {
  if (!operation || operation.closed || !operation.data || !/^[a-f0-9]{64}$/.test(operation.data.leaseId)) return Promise.resolve();
  operation.closed = true;
  return send("resourceDataClose", { chatId: operation.chatId, workspaceId: "vba-editor", leaseId: operation.data.leaseId })
    .catch(function () {});
}

function cancelVbaModuleRead() {
  var operation = vbaModuleRead;
  if (!operation) return;
  operation.abort.abort();
  if (operation.requestId) cancelBridgeRequest(operation.requestId).catch(function () {});
  closeVbaModuleRead(operation);
  vbaModuleRead = null;
  state.vba.loadingModule = "";
}

function renderVbaProject() {
  var moduleSelect = $("vbaModuleSelect");
  var moduleList = $("vbaModuleList");
  var query = (($("vbaModuleSearchInput") && $("vbaModuleSearchInput").value) || "").trim().toLowerCase();
  moduleSelect.innerHTML = "";
  if (moduleList) moduleList.innerHTML = "";
  var renderedModules = 0;
  var filteredModules = [];

  state.vba.modules.forEach(function (module) {
    if (query && !vbaModuleMatchesSearch(module, query)) return;
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

  moduleSelect.disabled = state.bridgeUnavailable || !renderedModules;
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
  } else {
    $("vbaStatus").textContent = "VBA project · модулей: " + state.vba.modules.length + ".";
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
  if (!list) return;
  var selectedName = $("vbaModuleSelect").value;
  var tree = window.RNAssistantNavigationTree;
  var nodes = groupVbaModules(modules).map(function (group) {
    var key = "vba:" + group.label;
    return {
      key: key, kind: "group", title: group.label,
      expanded: !(state.collapsedResourceGroups && state.collapsedResourceGroups[key]),
      children: group.modules.map(function (module) {
        var name = vbaModuleName(module);
        var type = vbaModuleType(module);
        var lineCount = module.lineCount || module.LineCount || 0;
        return {
          key: "vba:module:" + name, kind: "leaf", title: name,
          iconHtml: tree.icon(vbaModuleIcon(type)), selected: name === selectedName,
          tooltip: [name, type, lineCount ? lineCount + " строк" : ""].filter(Boolean).join(" · "),
          moduleName: name,
          actions: isDeletableVbaModule(module) ? [{
            title: "Удалить VBA-модуль " + name, iconHtml: tree.icon("trash"), danger: true,
            onClick: function () { deleteVbaModule(name); }
          }] : []
        };
      })
    };
  });
  tree.render(list, nodes, {
    label: "VBA модули",
    emptyText: state.bridgeUnavailable ? "Office bridge недоступен." : (query ? "Модули не найдены." : "Модули не загружены."),
    onToggle: function (node, expanded) {
      state.collapsedResourceGroups = state.collapsedResourceGroups || {};
      state.collapsedResourceGroups[node.key] = !expanded;
    },
    onSelect: function (node) {
      if (state.vbaEditorDirty && !window.confirm("Отменить несохранённые изменения текущего VBA-модуля?")) return;
      state.vbaEditorDirty = false;
      $("vbaModuleSelect").value = node.moduleName;
      state.vba.selectedModule = node.moduleName;
      renderVbaModuleList(modules, query);
      renderSelectedVbaModule();
      loadSelectedVbaModule();
    }
  });
}

function vbaModuleIcon(type) {
  var value = String(type || "module").toLowerCase();
  if (value.indexOf("class") >= 0) {
    return "class";
  }
  if (value.indexOf("form") >= 0) {
    return "form";
  }
  if (value.indexOf("document") >= 0 || value.indexOf("worksheet") >= 0 || value.indexOf("workbook") >= 0) {
    return "document";
  }
  return "module";
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
    if (left.order !== right.order) return left.order - right.order;
    return left.label.localeCompare(right.label);
  });
  return groups;
}

function vbaModuleType(module) {
  return module ? (module.type || module.Type || "module") : "module";
}

function isDeletableVbaModule(module) {
  var type = String(vbaModuleType(module)).toLowerCase();
  return type === "stdmodule" || type === "classmodule" ||
    type === "msform" && (module.codeOnlyUserForm === true || module.CodeOnlyUserForm === true);
}

function vbaModuleGroupLabel(type) {
  var value = String(type || "module").toLowerCase();
  if (value.indexOf("document") >= 0 || value.indexOf("worksheet") >= 0 || value.indexOf("workbook") >= 0) {
    var host = String(state.host || "").toLowerCase();
    return host === "excel" ? "Объекты Excel" : host === "word" ? "Объекты Word" :
      host === "powerpoint" ? "Объекты PowerPoint" : "Объекты документа";
  }
  if (value.indexOf("class") >= 0) return "Классы";
  if (value.indexOf("form") >= 0) return "Формы";
  if (value.indexOf("module") >= 0 || value === "standard") return "Модули";
  return type || "Other";
}

function vbaModuleGroupOrder(type) {
  var label = vbaModuleGroupLabel(type);
  if (label === "Модули") return 1;
  if (label === "Объекты документа") return 2;
  if (label === "Классы") return 3;
  if (label === "Формы") return 4;
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
    setVbaEditorCode(loaded ? vbaModuleCode(module) : "", moduleName);
  }
  $("vbaModuleTitle").textContent = module ? vbaModuleName(module) : "Модуль не выбран";
  $("vbaModuleMeta").textContent = module ? "VBA / " + (state.title || "Связанный документ") +
    (loaded ? "" : (loading ? " · читаю код..." : " · код не загружен")) : "";
  $("vbaHistoryCurrentText").textContent = loaded ? "Код прочитан из связанного документа Office." : (loading ? "Код загружается из связанного документа Office." : "Код ещё не загружен.");
  renderVbaBackupChoices(module);
  renderVbaDiff({
    summary: module && loaded ? "Нажмите «Показать diff», чтобы посмотреть изменения." : (module ? "Код модуля еще не загружен." : (state.bridgeUnavailable ? "Office bridge недоступен." : "Модуль не выбран.")),
    lines: []
  });
  if (typeof setCodeEditorReadOnly === "function") {
    setCodeEditorReadOnly("vbaCodeInput", !module || !loaded || state.bridgeUnavailable);
  }
  $("previewVbaDiffButton").disabled = !module || !loaded || state.bridgeUnavailable;
  $("saveVbaButton").disabled = !module || !loaded || state.bridgeUnavailable;
  $("saveVbaButton").hidden = !state.vbaEditorDirty;
  $("vbaActionsMenu").hidden = !module;
  $("discardVbaChangesButton").disabled = !module || !loaded || !state.vbaEditorDirty;
  $("downloadVbaModuleButton").disabled = !module || !loaded;
  $("restoreVbaButton").disabled = state.bridgeUnavailable || !$("vbaBackupSelect").value || !module || !loaded;
  applyVbaMode();
  updateVbaMacroSuggestion();
}

function renderVbaBackupChoices(module) {
  var select = $("vbaBackupSelect");
  var selectedId = select.value;
  var name = vbaModuleName(module).toLowerCase();
  var backups = (state.vba.backups || []).filter(function (backup) {
    return name && String(backup.ModuleName || backup.moduleName || "").toLowerCase() === name;
  });
  select.innerHTML = "";
  backups.forEach(function (backup) {
    var option = document.createElement("option");
    option.value = backup.BackupId || backup.backupId || "";
    option.textContent = backup.CreatedUtc || backup.createdUtc || "Резервная копия";
    select.appendChild(option);
  });
  if (!backups.length) {
    var empty = document.createElement("option");
    empty.value = "";
    empty.textContent = "Резервных копий этого модуля нет";
    select.appendChild(empty);
  } else if (selectedId && selectHasOption(select, selectedId)) {
    select.value = selectedId;
  }
  select.disabled = state.bridgeUnavailable || !backups.length;
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
  if (typeof cancelVbaModuleWrite === "function") cancelVbaModuleWrite();
  var module = selectedVbaModule();
  var chatId = state.activeChatId;
  if (vbaModuleRead && vbaModuleRead.module === module && vbaModuleRead.chatId === chatId) return;
  cancelVbaModuleRead();
  if (!module || hasVbaModuleCode(module) || state.bridgeUnavailable || !chatId) {
    return;
  }
  if (vbaModuleReadPending >= 2) {
    $("vbaStatus").textContent = "Предыдущее чтение ещё закрывается. Выберите модуль повторно после завершения.";
    return;
  }
  var moduleName = vbaModuleName(module);
  var operation = { module: module, chatId: chatId, abort: new AbortController(), data: null, requestId: null, closed: false };
  vbaModuleRead = operation;
  vbaModuleReadPending++;
  state.vba.loadingModule = moduleName;
  function current() {
    return vbaModuleRead === operation && !operation.abort.signal.aborted && !state.bridgeUnavailable &&
      state.activeChatId === chatId && selectedVbaModule() === module && state.vba.modules.indexOf(module) >= 0;
  }
  try {
    renderSelectedVbaModule();
    var request = send("getVbaModule", { chatId: chatId, moduleName: moduleName });
    operation.requestId = request.requestId;
    var response = await request;
    operation.requestId = null;
    operation.data = response && response.data;
    if (!current()) return;
    if (!response || response.chatId !== chatId || response.moduleName !== moduleName ||
        !response.resource || !/^rna:\/\/vba\/[^/]+\/component\/[^/]+$/.test(response.resource.uri) ||
        typeof response.resource.revision !== "string" || !response.resource.revision ||
        !/^[a-f0-9]{64}$/.test(response.codeSha256) || typeof response.componentType !== "string" || !response.componentType ||
        !Number.isInteger(response.lineCount) || response.lineCount < 0 ||
        !Number.isInteger(response.totalCharacters) || response.totalCharacters < 0 ||
        !operation.data || !operation.data.payload || operation.data.payload.contentType !== "text/plain; charset=utf-8")
      throw new Error("Ответ чтения VBA-модуля неполон; редактирование и сохранение заблокированы.");
    var bytes = await window.RNAssistantResourceDownload.read(operation.data, {
      fetch: window.fetch.bind(window), signal: operation.abort.signal, isCurrent: current, maxBytes: Number.MAX_SAFE_INTEGER
    });
    var code = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
    if (code.length !== response.totalCharacters) throw new Error("VBA-модуль прочитан не полностью; сохранение заблокировано.");
    await closeVbaModuleRead(operation);
    if (!current()) return;
    module.code = code;
    module.type = response.componentType;
    module.lineCount = response.lineCount;
    module.codeSha256 = response.codeSha256;
    module.resource = response.resource;
    $("vbaStatus").textContent = "VBA-модуль загружен.";
  } catch (error) {
    if (current()) {
      $("vbaStatus").textContent = error.message;
      log(error.detail || error.message, "error");
    }
  } finally {
    await closeVbaModuleRead(operation);
    vbaModuleReadPending--;
    if (vbaModuleRead === operation) {
      vbaModuleRead = null;
      state.vba.loadingModule = "";
      renderSelectedVbaModule();
      renderVbaModuleList(state.vba.modules.filter(function (item) {
        var query = (($("vbaModuleSearchInput") && $("vbaModuleSearchInput").value) || "").trim().toLowerCase();
        return !query || vbaModuleMatchesSearch(item, query);
      }), (($("vbaModuleSearchInput") && $("vbaModuleSearchInput").value) || "").trim().toLowerCase());
    }
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
