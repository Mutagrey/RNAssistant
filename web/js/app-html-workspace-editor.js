(function () {
  "use strict";

  function create(options) {
    options = options || {};
    var state = options.state;
    var model = options.model;
    var htmlPreview = options.preview;
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
      select.classList.toggle("hidden", branches.length <= 1 || !!selected && (selected.type === "plan" || selected.type === "artifact"));
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
      } else if (selected.type === "artifact") {
        return;
      } else if (selected.type === "data") {
        setDataJson(selected.item, value);
      } else {
        setFileContent(selected.item, value);
      }
    }

    function markHtmlWorkspaceDirty() {
      var selected = selectedItem();
      if (!selected || selected.type === "artifact") return;
      if (recoveryBlocked() && selected.type !== "plan") return;
      syncHtmlEditorToState();
      state.htmlWorkspaceDirty = true;
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
        save.disabled = state.bridgeUnavailable || !selected || selected.type === "artifact" || !state.htmlWorkspaceDirty || (blocked && selected.type !== "plan");
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
        $("deleteHtmlWorkspaceButton").disabled = state.bridgeUnavailable || !selected || selected.type === "artifact" || (blocked && selected.type !== "plan");
        $("deleteHtmlWorkspaceButton").title = selected
          ? (selected.type === "plan" ? "Удалить план" : "Удалить выбранный файл или источник данных")
          : "Выберите артефакт";
      }
      if ($("undoHtmlWorkspaceButton")) {
        $("undoHtmlWorkspaceButton").classList.toggle("hidden", !!selected && (selected.type === "plan" || selected.type === "artifact"));
        $("undoHtmlWorkspaceButton").disabled = state.bridgeUnavailable || blocked || !historyItems().length;
        $("undoHtmlWorkspaceButton").title = historyItems().length
          ? "Вернуть: " + snapshotLabel(historyItems()[0])
          : "Нет предыдущих версий";
      }
      if ($("redoHtmlWorkspaceButton")) {
        renderRedoBranches(selected);
        var branches = redoBranches();
        $("redoHtmlWorkspaceButton").classList.toggle("hidden", !!selected && (selected.type === "plan" || selected.type === "artifact"));
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
      if (state.htmlWorkspaceMode === "preview") {
        renderHtmlWorkspacePreview();
      }
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
      if (selected.type === "artifact") return artifactInlineText(selected.item);
      return selected.type === "data" ? dataJson(selected.item) : fileContent(selected.item);
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
      var blocked = recoveryBlocked();
      if (editor) {
        editor.classList.toggle("is-empty", !hasItems);
      }
      if (empty) {
        empty.classList.toggle("hidden", hasItems);
      }
      if (title) {
        title.textContent = selected
          ? (isPlan || isArtifact ? artifactTitle(selected.item) : (selected.type === "data" ? dataName(selected.item) : filePath(selected.item)))
          : "Артефакт не выбран";
      }
      if (meta) {
        var binding = selected && selected.type === "data" ? dataBinding(selected.item) : null;
        var bindingStatus = binding ? String(bindingValue(binding, "Status", "status", "ready")) : "";
        var payloadCompleteness = binding ? String(bindingValue(binding, "PayloadCompleteness", "payloadCompleteness", "bounded")) : "";
        meta.textContent = selected
          ? (isPlan ? "План · Markdown · v" + artifactRevision(selected.item) : (isArtifact ? workspaceArtifacts.typeLabel(artifactKind(selected.item)) + " · только чтение" : (selected.type === "data" ? (binding ? "JSON · " + bindingValue(binding, "ToolId", "toolId", "Office") + " · " + bindingStatus + " · " + payloadCompleteness + " · " + bindingValue(binding, "RefreshPolicy", "refreshPolicy", "manual") : "JSON data source · static") : (fileKind(selected.item) || "file"))))
          : "";
        meta.title = binding && bindingValue(binding, "LastError", "lastError", "") ? bindingValue(binding, "LastError", "lastError", "") : "";
      }
      var previewButton = document.querySelector('.html-workspace-mode-button[data-html-mode="preview"]');
      var editButton = document.querySelector('.html-workspace-mode-button[data-html-mode="edit"]');
      if (previewButton) previewButton.textContent = isPlan ? "План" : "Просмотр";
      if (editButton) {
        editButton.textContent = isPlan ? "Источник" : "Код";
        editButton.classList.toggle("hidden", isArtifact);
        editButton.disabled = (blocked && !isPlan) || (isPlan && artifactInlineTruncated(selected.item));
        if (isPlan && artifactInlineTruncated(selected.item)) {
          editButton.title = "Точный Markdown source ещё загружается";
        } else {
          editButton.title = "";
        }
      }
      if (isPlan && artifactInlineTruncated(selected.item)) state.htmlWorkspaceMode = "preview";
      if (isArtifact) state.htmlWorkspaceMode = "preview";
      if ($("saveHtmlWorkspaceButton")) $("saveHtmlWorkspaceButton").classList.toggle("hidden", isArtifact);
      if ($("deleteHtmlWorkspaceButton")) $("deleteHtmlWorkspaceButton").classList.toggle("hidden", isArtifact);
      if (typeof setCodeEditorValue === "function") {
        setCodeEditorValue("htmlWorkspaceEditorInput", selectedEditorValue(selected));
      } else if ($("htmlWorkspaceEditorInput")) {
        $("htmlWorkspaceEditorInput").value = selectedEditorValue(selected);
      }
      if (typeof setCodeEditorReadOnly === "function") setCodeEditorReadOnly("htmlWorkspaceEditorInput", isArtifact || (blocked && !isPlan));
      renderHtmlWorkspacePreview();
    }

    function renderHtmlWorkspacePreview() {
      var frame = $("htmlWorkspacePreviewFrame");
      var detail = $("artifactDetailPreview");
      if (!frame || !detail) {
        return;
      }
      var selected = selectedItem();
      var special = selected && (selected.type === "plan" || selected.type === "artifact");
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
      frame.srcdoc = htmlPreview.build({
        activeFileId: workspace().activeFileId,
        dataSources: dataSources(),
        files: files()
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
