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
    var setArtifactInlineText = model.setArtifactInlineText;
    var historyItems = model.historyItems;
    var redoItems = model.redoItems;
    var filePath = model.filePath;
    var fileKind = model.fileKind;
    var fileContent = model.fileContent;
    var setFileContent = model.setFileContent;
    var dataName = model.dataName;
    var dataJson = model.dataJson;
    var setDataJson = model.setDataJson;
    var selectedItem = model.selectedItem;
    var snapshotLabel = model.snapshotLabel;

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
      if ($("addPlanButton")) $("addPlanButton").disabled = !!state.bridgeUnavailable;
      ["addHtmlFileButton", "addCssFileButton", "addJsFileButton", "addHtmlDataButton"].forEach(function (id) {
        if ($(id)) $(id).disabled = !!state.bridgeUnavailable;
      });
      if (status) {
        if (state.bridgeUnavailable) {
          status.textContent = "Office bridge недоступен.";
        } else if (!files().length && !dataSources().length && !(state.artifacts || []).length) {
          status.textContent = "Артефактов пока нет.";
        } else {
          status.textContent = (state.artifacts || []).length + " артефактов · " + files().length + " файлов · " + dataSources().length + " наборов данных" + (state.htmlWorkspaceDirty ? " · не сохранено" : "");
        }
      }
      if (save) {
        save.disabled = state.bridgeUnavailable || !selected || selected.type === "artifact" || !state.htmlWorkspaceDirty;
        save.title = "Сохранить изменения (Ctrl+S)";
      }
      if ($("deleteHtmlWorkspaceButton")) {
        $("deleteHtmlWorkspaceButton").disabled = state.bridgeUnavailable || !selected || selected.type === "artifact";
        $("deleteHtmlWorkspaceButton").title = selected
          ? (selected.type === "plan" ? "Удалить план" : "Удалить выбранный файл или источник данных")
          : "Выберите артефакт";
      }
      if ($("undoHtmlWorkspaceButton")) {
        $("undoHtmlWorkspaceButton").classList.toggle("hidden", !!selected && (selected.type === "plan" || selected.type === "artifact"));
        $("undoHtmlWorkspaceButton").disabled = state.bridgeUnavailable || !historyItems().length;
        $("undoHtmlWorkspaceButton").title = historyItems().length
          ? "Вернуть: " + snapshotLabel(historyItems()[0])
          : "Нет предыдущих версий";
      }
      if ($("redoHtmlWorkspaceButton")) {
        $("redoHtmlWorkspaceButton").classList.toggle("hidden", !!selected && (selected.type === "plan" || selected.type === "artifact"));
        $("redoHtmlWorkspaceButton").disabled = state.bridgeUnavailable || !redoItems().length;
        $("redoHtmlWorkspaceButton").title = redoItems().length
          ? "Повторить: " + snapshotLabel(redoItems()[0])
          : "Нет отмененных версий";
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
        try { return JSON.stringify(JSON.parse(artifactInlineText(selected.item)), null, 2); }
        catch (error) { return artifactInlineText(selected.item); }
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
        meta.textContent = selected
          ? (isPlan ? "План · JSON · v" + artifactRevision(selected.item) : (isArtifact ? workspaceArtifacts.typeLabel(artifactKind(selected.item)) + " · только чтение" : (selected.type === "data" ? "JSON data source" : (fileKind(selected.item) || "file"))))
          : "";
      }
      var previewButton = document.querySelector('.html-workspace-mode-button[data-html-mode="preview"]');
      var editButton = document.querySelector('.html-workspace-mode-button[data-html-mode="edit"]');
      if (previewButton) previewButton.textContent = isPlan ? "План" : "Просмотр";
      if (editButton) {
        editButton.textContent = isPlan ? "JSON" : "Код";
        editButton.classList.toggle("hidden", isArtifact);
      }
      if (isArtifact) state.htmlWorkspaceMode = "preview";
      if ($("saveHtmlWorkspaceButton")) $("saveHtmlWorkspaceButton").classList.toggle("hidden", isArtifact);
      if ($("deleteHtmlWorkspaceButton")) $("deleteHtmlWorkspaceButton").classList.toggle("hidden", isArtifact);
      if (typeof setCodeEditorValue === "function") {
        setCodeEditorValue("htmlWorkspaceEditorInput", selectedEditorValue(selected));
      } else if ($("htmlWorkspaceEditorInput")) {
        $("htmlWorkspaceEditorInput").value = selectedEditorValue(selected);
      }
      if (typeof setCodeEditorReadOnly === "function") setCodeEditorReadOnly("htmlWorkspaceEditorInput", isArtifact);
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
        workspaceArtifacts.renderDetail(detail, selected, selectedEditorValue(selected));
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
