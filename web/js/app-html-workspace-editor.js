(function () {
  "use strict";

  function create(options) {
    options = options || {};
    var state = options.state;
    var model = options.model;
    var htmlPreview = options.preview;
    var source = options.source;
    var renderedFile = null, renderedSourceKey = null;
    var workspaceArtifacts = options.artifacts;
    var htmlPreviewRefreshTimer = 0;
    var workspace = model.workspace;
    var files = model.files;
    var dataSources = model.dataSources;
    var artifactKind = model.artifactKind;
    var artifactTitle = model.artifactTitle;
    var artifactRevision = model.artifactRevision;
    var artifactInlineText = model.artifactInlineText;
    var artifactInlineTruncated = model.artifactInlineTruncated;
    var setArtifactInlineText = model.setArtifactInlineText;
    var historyItems = model.historyItems;
    var redoBranches = model.redoBranches;
    var recovery = model.recovery;
    var recoveryBlocked = model.recoveryBlocked;
    var filePath = model.filePath;
    var fileKind = model.fileKind;
    var fileContent = model.fileContent;
    var setFileContent = model.setFileContent;
    var dataName = model.dataName;
    var dataJson = model.dataJson;
    var dataBinding = model.dataBinding;
    var bindingValue = model.bindingValue;
    var boundDataSources = model.boundDataSources;
    var setDataJson = model.setDataJson;
    var selectedItem = model.selectedItem;
    var snapshotLabel = model.snapshotLabel;

    function renderRedoBranches(selected) {
      var select = $("redoHtmlWorkspaceBranchSelect");
      if (!select) return;
      var branches = redoBranches();
      var selectedId = select.value;
      select.innerHTML = "";
      branches.forEach(function (branch) {
        var option = document.createElement("option");
        var revision = Number(model.prop(branch, "Revision", "revision", 1) || 1);
        option.value = model.prop(branch, "Id", "id", "");
        option.textContent = "v" + revision + " · " + snapshotLabel(branch);
        select.appendChild(option);
      });
      if (branches.some(function (branch) { return model.prop(branch, "Id", "id", "") === selectedId; })) {
        select.value = selectedId;
      }
      select.classList.toggle("hidden", branches.length <= 1 || !!selected &&
        (selected.type === "plan" || selected.type === "artifact" || selected.type === "collection"));
    }

    function fileIsInEditor(file) {
      return source.ready(file) && renderedFile === file && renderedSourceKey === file.sourceReadKey;
    }

    function syncHtmlEditorToState() {
      var selected = selectedItem();
      if (!selected) {
        return;
      }
      var value = typeof getCodeEditorValue === "function"
        ? getCodeEditorValue("htmlWorkspaceEditorInput")
        : ($("htmlWorkspaceEditorInput").value || "");
      if (selected.type === "plan") {
        setArtifactInlineText(selected.item, value);
      } else if (selected.type === "artifact" || selected.type === "collection") {
        return;
      } else if (selected.type === "data") {
        return;
      } else {
        if (!fileIsInEditor(selected.item)) return;
        setFileContent(selected.item, value);
      }
    }

    function markHtmlWorkspaceDirty() {
      var selected = selectedItem();
      if (!selected || selected.type === "artifact" || selected.type === "collection" || selected.type === "data") return;
      if (recoveryBlocked() && selected.type !== "plan") return;
      if (selected.type === "file" && !fileIsInEditor(selected.item)) return;
      syncHtmlEditorToState();
      state.htmlWorkspaceDirty = true;
      state.htmlWorkspaceEditVersion = (state.htmlWorkspaceEditVersion || 0) + 1;
      updateHtmlWorkspaceStatus();
      scheduleHtmlWorkspacePreviewRefresh();
    }

    function confirmDiscardHtmlWorkspaceChanges(action) {
      if (!state.htmlWorkspaceDirty) {
        return true;
      }
      return window.confirm(
        "В артефакте есть несохраненные изменения. " +
        (action || "Продолжить") +
        " и потерять их?"
      );
    }

    function scheduleHtmlWorkspacePreviewRefresh() {
      if (htmlPreviewRefreshTimer) {
        window.clearTimeout(htmlPreviewRefreshTimer);
      }
      htmlPreviewRefreshTimer = window.setTimeout(function () {
        htmlPreviewRefreshTimer = 0;
        renderHtmlWorkspacePreview();
      }, 160);
    }

    function updateHtmlWorkspaceStatus() {
      var status = $("htmlWorkspaceStatus");
      var save = $("saveHtmlWorkspaceButton");
      var selected = selectedItem();
      var blocked = recoveryBlocked();
      renderHtmlWorkspaceRecovery();
      if ($("addPlanButton")) $("addPlanButton").disabled = !!state.bridgeUnavailable;
      ["addHtmlFileButton", "addCssFileButton", "addJsFileButton", "addHtmlDataButton"].forEach(function (id) {
        if ($(id)) $(id).disabled = !!state.bridgeUnavailable || blocked;
      });
      if (status) {
        if (state.bridgeUnavailable) {
          status.textContent = "Office bridge недоступен.";
        } else if (blocked) {
          status.textContent = "HTML workspace требует восстановления.";
        } else if (!files().length && !dataSources().length && !(state.artifacts || []).length) {
          status.textContent = "Ресурсов пока нет.";
        } else {
          var resourceCount = typeof artifactResourceHeads === "function" ? artifactResourceHeads().length : (state.artifacts || []).length;
          var preflight = state.htmlWorkspacePreflight || {};
          var errors = Number(preflight.errorCount || preflight.ErrorCount || 0);
          var warnings = Number(preflight.warningCount || preflight.WarningCount || 0);
          var diagnostic = errors || warnings
            ? " · проверка: " + errors + " ошибок, " + warnings + " предупреждений"
            : "";
          status.textContent = resourceCount + " ресурсов · " + files().length + " файлов · " + dataSources().length + " наборов данных" + diagnostic + (state.htmlWorkspaceDirty ? " · не сохранено" : "");
        }
      }
      if (save) {
        save.disabled = state.bridgeUnavailable || !selected || selected.type === "artifact" ||
          selected.type === "collection" || !state.htmlWorkspaceDirty || (blocked && selected.type !== "plan") ||
          selected.type === "file" && (!source.ready(selected.item) || !source.current(workspace()));
        save.title = "Сохранить изменения (Ctrl+S)";
      }
      if ($("refreshHtmlDataButton")) {
        var boundCount = boundDataSources().length;
        $("refreshHtmlDataButton").disabled = state.bridgeUnavailable || blocked || !!state.htmlWorkspaceDirty || !!state.htmlWorkspaceRefreshPending || !boundCount;
        $("refreshHtmlDataButton").textContent = state.htmlWorkspaceRefreshPending ? "Обновляю…" : "Данные ↻";
        $("refreshHtmlDataButton").title = boundCount ? "Перечитать " + boundCount + " привязанных наборов из Office" : "Нет привязанных данных";
      }
      if ($("exportHtmlWorkspaceButton")) {
        var exportBlocked = !!state.htmlWorkspaceDirty || !!state.htmlWorkspaceExportPending ||
          !state.activeHtmlArtifactId || !files().some(function (file) { return fileKind(file) === "html"; });
        $("exportHtmlWorkspaceButton").disabled = exportBlocked;
        $("exportHtmlWorkspaceButton").title = state.htmlWorkspaceDirty
          ? "Сначала сохраните текущие изменения"
          : "Зафиксировать exact workspace revision и скачать автономный HTML";
      }
      if ($("deleteHtmlWorkspaceButton")) {
        $("deleteHtmlWorkspaceButton").disabled = state.bridgeUnavailable || !selected ||
          selected.type === "artifact" || selected.type === "collection" || (blocked && selected.type !== "plan");
        $("deleteHtmlWorkspaceButton").title = selected
          ? (selected.type === "plan" ? "Удалить план" : "Удалить выбранный файл или источник данных")
          : "Выберите артефакт";
      }
      if ($("undoHtmlWorkspaceButton")) {
        $("undoHtmlWorkspaceButton").classList.toggle("hidden", !!selected &&
          (selected.type === "plan" || selected.type === "artifact" || selected.type === "collection"));
        $("undoHtmlWorkspaceButton").disabled = state.bridgeUnavailable || blocked || !historyItems().length;
        $("undoHtmlWorkspaceButton").title = historyItems().length
          ? "Вернуть: " + snapshotLabel(historyItems()[0])
          : "Нет предыдущих версий";
      }
      if ($("redoHtmlWorkspaceButton")) {
        renderRedoBranches(selected);
        var branches = redoBranches();
        $("redoHtmlWorkspaceButton").classList.toggle("hidden", !!selected &&
          (selected.type === "plan" || selected.type === "artifact" || selected.type === "collection"));
        $("redoHtmlWorkspaceButton").disabled = state.bridgeUnavailable || blocked || !branches.length;
        $("redoHtmlWorkspaceButton").title = branches.length > 1
          ? "Повторить выбранную ветку"
          : branches.length
            ? "Повторить: " + snapshotLabel(branches[0])
          : "Нет отмененных версий";
      }
    }

    function renderHtmlWorkspaceRecovery() {
      var panel = $("htmlWorkspaceRecovery");
      if (!panel) return;
      var current = recovery();
      var degraded = current.status === "degraded";
      panel.classList.toggle("hidden", !degraded);
      if (!degraded) return;
      if ($("htmlWorkspaceRecoveryMessage")) {
        $("htmlWorkspaceRecoveryMessage").textContent = current.message || "Цепочка HTML-ревизий повреждена. Выберите доступную ревизию.";
      }
      var select = $("htmlWorkspaceRecoverySelect");
      var candidates = current.candidates || [];
      var selectedId = select ? select.value : "";
      if (select) {
        select.innerHTML = "";
        candidates.forEach(function (candidate) {
          var option = document.createElement("option");
          var revision = Number(model.prop(candidate, "Revision", "revision", 1) || 1);
          option.value = model.prop(candidate, "Id", "id", "");
          option.textContent = "v" + revision + " · " + model.prop(candidate, "Label", "label", "HTML workspace");
          select.appendChild(option);
        });
        if (candidates.some(function (candidate) { return model.prop(candidate, "Id", "id", "") === selectedId; })) {
          select.value = selectedId;
        }
        select.disabled = !candidates.length || !!state.bridgeUnavailable;
      }
      if ($("recoverHtmlWorkspaceButton")) {
        $("recoverHtmlWorkspaceButton").disabled = !candidates.length || !!state.bridgeUnavailable;
        $("recoverHtmlWorkspaceButton").title = candidates.length
          ? "Проверить и активировать выбранную HTML-ревизию"
          : "Нет других HTML-ревизий";
      }
    }

    function setHtmlWorkspaceMode(mode) {
      if (typeof syncCodeEditors === "function") {
        syncCodeEditors(["htmlWorkspaceEditorInput"]);
      }
      syncHtmlEditorToState();
      state.htmlWorkspaceMode = mode === "edit" ? "edit" : "preview";
      applyHtmlWorkspaceMode();
      renderHtmlWorkspaceEditor();
    }

    function applyHtmlWorkspaceMode() {
      var mode = state.htmlWorkspaceMode === "edit" ? "edit" : "preview";
      Array.prototype.slice.call(document.querySelectorAll(".html-workspace-mode-button")).forEach(function (button) {
        button.classList.toggle("active", button.getAttribute("data-html-mode") === mode);
      });
      Array.prototype.slice.call(document.querySelectorAll(".html-workspace-view")).forEach(function (view) {
        view.classList.toggle("hidden", view.getAttribute("data-html-view") !== mode);
      });
      if (mode === "edit" && typeof refreshCodeEditors === "function") {
        refreshCodeEditors(["htmlWorkspaceEditorInput"]);
      }
    }

    function selectedEditorValue(selected) {
      if (!selected) {
        return "";
      }
      if (selected.type === "plan") {
        return artifactInlineText(selected.item);
      }
      if (selected.type === "collection") return "";
      if (selected.type === "artifact") return artifactInlineText(selected.item);
      return selected.type === "data" ? dataJson(selected.item) : (source.ready(selected.item) ? fileContent(selected.item) : "");
    }

    function renderHtmlWorkspaceEditor() {
      var selected = selectedItem();
      var empty = $("htmlWorkspaceEmptyState");
      var editor = $("htmlWorkspaceEditor");
      var title = $("htmlWorkspaceTitle");
      var meta = $("htmlWorkspaceMeta");
      var hasItems = !!selected;
      var isPlan = !!selected && selected.type === "plan";
      var isArtifact = !!selected && selected.type === "artifact";
      var isCollection = !!selected && selected.type === "collection";
      var blocked = recoveryBlocked();
      var wanted = isPlan || isArtifact || isCollection || !selected ? [] :
        state.htmlWorkspaceMode === "edit" ? (selected.type === "file" ? [selected.item] : []) : files();
      var sourcesReady = source.ensure(wanted);
      if (editor) {
        editor.classList.toggle("is-empty", !hasItems);
      }
      if (empty) {
        empty.classList.toggle("hidden", hasItems);
      }
      if (title) {
        title.textContent = selected
          ? (isCollection
            ? workspaceArtifacts.collectionLabel(selected.item.id)
            : (isPlan || isArtifact ? artifactTitle(selected.item) :
              (selected.type === "data" ? dataName(selected.item) : filePath(selected.item))))
          : "Артефакт не выбран";
      }
      if (meta) {
        var binding = selected && selected.type === "data" ? dataBinding(selected.item) : null;
        meta.textContent = selected
          ? (isCollection ? "Коллекция · " + workspaceArtifacts.collectionCount(selected.item.id, options.artifactActions) + " ресурсов" :
            (isPlan ? "План · Markdown · v" + artifactRevision(selected.item) : (isArtifact ? workspaceArtifacts.typeLabel(artifactKind(selected.item)) + " · только чтение" : (selected.type === "data" ? ("Ресурс · " + bindingValue(binding, "View", "view", "text") + " · " + bindingValue(binding, "Policy", "policy", "exact")) : (fileKind(selected.item) || "file")))))
          : "";
        meta.title = binding ? dataJson(selected.item) : "";
        if (wanted.length && !sourcesReady) meta.textContent += " · " + source.message();
      }
      var previewButton = document.querySelector('.html-workspace-mode-button[data-html-mode="preview"]');
      var editButton = document.querySelector('.html-workspace-mode-button[data-html-mode="edit"]');
      if (previewButton) previewButton.textContent = isPlan ? "План" : "Просмотр";
      if (editButton) {
        editButton.textContent = isPlan ? "Источник" : "Код";
        editButton.classList.toggle("hidden", isArtifact || isCollection);
        editButton.disabled = (blocked && !isPlan) || (isPlan && artifactInlineTruncated(selected.item));
        if (isPlan && artifactInlineTruncated(selected.item)) {
          editButton.title = "Точный Markdown source ещё загружается";
        } else {
          editButton.title = "";
        }
      }
      if (isPlan && artifactInlineTruncated(selected.item)) state.htmlWorkspaceMode = "preview";
      if (isArtifact || isCollection) state.htmlWorkspaceMode = "preview";
      if ($("saveHtmlWorkspaceButton")) $("saveHtmlWorkspaceButton").classList.toggle("hidden", isArtifact || isCollection || selected && selected.type === "data");
      if ($("deleteHtmlWorkspaceButton")) $("deleteHtmlWorkspaceButton").classList.toggle("hidden", isArtifact || isCollection);
      if (typeof setCodeEditorValue === "function") {
        setCodeEditorValue("htmlWorkspaceEditorInput", isArtifact || isCollection ? "" : selectedEditorValue(selected));
      } else if ($("htmlWorkspaceEditorInput")) {
        $("htmlWorkspaceEditorInput").value = isArtifact || isCollection ? "" : selectedEditorValue(selected);
      }
      renderedFile = selected && selected.type === "file" && source.ready(selected.item) ? selected.item : null;
      renderedSourceKey = renderedFile ? renderedFile.sourceReadKey : null;
      if (typeof setCodeEditorReadOnly === "function") setCodeEditorReadOnly("htmlWorkspaceEditorInput",
        isArtifact || isCollection || selected && selected.type === "data" || (blocked && !isPlan) ||
        selected && selected.type === "file" && !source.ready(selected.item));
      renderHtmlWorkspacePreview();
    }

    function renderHtmlWorkspacePreview() {
      if (options.closeResources) options.closeResources();
      var frame = $("htmlWorkspacePreviewFrame");
      var detail = $("artifactDetailPreview");
      if (!frame || !detail) {
        return;
      }
      var selected = selectedItem();
      var special = selected && (selected.type === "plan" || selected.type === "artifact" || selected.type === "collection");
      frame.classList.toggle("hidden", !!special);
      detail.classList.toggle("hidden", !special);
      if (special) {
        workspaceArtifacts.renderDetail(detail, selected, selectedEditorValue(selected), options.artifactActions);
        frame.removeAttribute("src");
        frame.srcdoc = "";
        return;
      }
      detail.replaceChildren();
      frame.removeAttribute("src");
      if (state.htmlWorkspaceMode === "edit") { frame.srcdoc = ""; return; }
      var workspaceFiles = files();
      if (!source.current(workspace()) || !workspaceFiles.every(source.ready)) {
        frame.srcdoc = "";
        detail.classList.remove("hidden"); detail.textContent = source.message(); frame.classList.add("hidden");
        return;
      }
      if (typeof htmlPreview.usesECharts === "function" && htmlPreview.usesECharts(workspaceFiles) &&
          typeof htmlPreview.echartsReady === "function" && !htmlPreview.echartsReady()) {
        frame.srcdoc = "<!doctype html><html><body style=\"font-family:Segoe UI,Arial,sans-serif;padding:24px;color:#475467\">Загрузка диаграммы...</body></html>";
        htmlPreview.ensureECharts().then(function () {
          if (typeof window.renderHtmlWorkspace === "function") window.renderHtmlWorkspace();
          else renderHtmlWorkspacePreview();
        }).catch(function (error) {
          frame.srcdoc = "<!doctype html><html><body style=\"font-family:Segoe UI,Arial,sans-serif;padding:24px;color:#b42318\">" +
            String(error && error.message || "ECharts не загружен.").replace(/[&<>]/g, function (character) {
              return { "&": "&amp;", "<": "&lt;", ">": "&gt;" }[character];
            }) + "</body></html>";
        });
        return;
      }
      frame.srcdoc = htmlPreview.build({
        activeFileId: workspace().activeFileId,
        dataSources: dataSources(),
        files: workspaceFiles
      });
    }

    return {
      applyMode: applyHtmlWorkspaceMode,
      confirmDiscard: confirmDiscardHtmlWorkspaceChanges,
      markDirty: markHtmlWorkspaceDirty,
      render: renderHtmlWorkspaceEditor,
      renderPreview: renderHtmlWorkspacePreview,
      setMode: setHtmlWorkspaceMode,
      sync: syncHtmlEditorToState,
      updateStatus: updateHtmlWorkspaceStatus
    };
  }

  window.RNAssistantHtmlWorkspaceEditor = { create: create };
}());
