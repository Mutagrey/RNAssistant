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
  var backupSelect = $("vbaBackupSelect");
  var moduleList = $("vbaModuleList");
  var query = (($("vbaModuleSearchInput") && $("vbaModuleSearchInput").value) || "").trim().toLowerCase();
  moduleSelect.innerHTML = "";
  backupSelect.innerHTML = "";
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
  $("addVbaUserFormButton").disabled = state.bridgeUnavailable;
  $("refreshVbaButton").disabled = state.bridgeUnavailable;
  $("refreshVbaEmptyButton").disabled = state.bridgeUnavailable;
  $("vbaCreateNameInput").disabled = state.bridgeUnavailable;
  $("confirmVbaCreateButton").disabled = state.bridgeUnavailable;
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
      var row = document.createElement("div");
      var deletable = isDeletableVbaModule(module);
      row.className = "resource-tree-item-row" + (deletable ? " has-action" : "");
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
      item.classList.add("resource-tree-item-main");
      item.setAttribute("role", "treeitem");
      item.setAttribute("aria-selected", name === selectedName ? "true" : "false");
      row.appendChild(item);

      if (deletable) {
        var deleteButton = document.createElement("button");
        deleteButton.type = "button";
        deleteButton.className = "resource-tree-item-action is-danger";
        deleteButton.title = "Удалить VBA-модуль " + name;
        deleteButton.setAttribute("aria-label", deleteButton.title);
        deleteButton.innerHTML = iconSvg("trash");
        deleteButton.addEventListener("click", function (event) {
          event.preventDefault();
          event.stopPropagation();
          deleteVbaModule(name);
        });
        row.appendChild(deleteButton);
      }
      body.appendChild(row);
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
  return type === "stdmodule" || type === "classmodule";
}

function vbaModuleGroupLabel(type) {
  var value = String(type || "module").toLowerCase();
  if (value.indexOf("document") >= 0 || value.indexOf("worksheet") >= 0 || value.indexOf("workbook") >= 0) return "Объекты документа";
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
    setVbaEditorCode(loaded ? vbaModuleCode(module) : "");
  }
  $("vbaModuleTitle").textContent = module ? vbaModuleName(module) : "Модуль не выбран";
  $("vbaModuleMeta").textContent = module ? vbaModuleMetaText(module) + (loaded ? "" : (loading ? " - читаю код..." : " - код не загружен")) : "";
  renderVbaMetadata(module);
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

function renderVbaMetadata(module) {
  var target = $("vbaMetaBox");
  if (!target) return;
  if (window.RNAssistantViewerRegistry) window.RNAssistantViewerRegistry.unmount(target);
  if (!module) return;
  if (!window.RNAssistantViewerRegistry || !window.RNAssistantViewerRegistry.has("json")) {
    throw new Error("JSON viewer is unavailable.");
  }
  window.RNAssistantViewerRegistry.mount("json", target, {
    text: JSON.stringify({
      name: vbaModuleName(module),
      type: module.type || module.Type,
      lineCount: module.lineCount || module.LineCount
    }, null, 2),
    completeness: "full",
    mode: "tree",
    onCopy: window.copyTextResult
  });
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
        !Number.isInteger(response.totalCharacters) || response.totalCharacters < 0 || response.totalCharacters > 1000000 ||
        !operation.data || !operation.data.payload || operation.data.payload.contentType !== "text/plain; charset=utf-8")
      throw new Error("Ответ чтения VBA-модуля неполон; редактирование и сохранение заблокированы.");
    var bytes = await window.RNAssistantResourceDownload.read(operation.data, {
      fetch: window.fetch.bind(window), signal: operation.abort.signal, isCurrent: current, maxBytes: 4000000
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
