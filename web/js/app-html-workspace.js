(function () {
  var htmlPreviewRefreshTimer = 0;
  var htmlPreview = window.RNAssistantHtmlWorkspacePreview;
  var workspaceArtifacts = window.RNAssistantHtmlWorkspaceArtifacts;
  var workspaceTree = window.RNAssistantHtmlWorkspaceTree;
  var workspaceActions = window.RNAssistantHtmlWorkspaceActions.create({
    state: state,
    send: send,
    log: log,
    getSelection: workspaceActionSelection,
    getActionState: workspaceActionState,
    syncEditor: syncHtmlEditorToState,
    applyWorkspaceResponse: applyHtmlWorkspaceResponse,
    applyPlanRefresh: applyPlanRefresh,
    validatePlanDraft: workspaceArtifacts.validatePlanDraft,
    hideCreate: hideHtmlWorkspaceCreate,
    render: renderHtmlWorkspace
  });

  window.addEventListener("message", function (event) {
    var frame = $("htmlWorkspacePreviewFrame");
    var data = event.data || {};
    if (!frame || event.source !== frame.contentWindow || data.type !== "rnassistant-html-fetch") return;
    var payload = data.request || {};
    function reply(ok, value) {
      if (frame.contentWindow) frame.contentWindow.postMessage({ type: "rnassistant-html-fetch-result", requestId: data.requestId, ok: ok, value: value }, "*");
    }
    function execute() {
      return send("htmlFetch", payload).then(function (response) { reply(true, response); });
    }
    execute().catch(function (error) {
      var message = error.detail || error.message || "HTTP request failed";
      var origin = "";
      try { origin = new URL(payload.url).origin; } catch (ignore) {}
      var deniedOrigin = String(message).match(/not allowed:\s*(https?:\/\/[^\s]+)/i);
      if (deniedOrigin) origin = deniedOrigin[1].replace(/[.,;]+$/, "");
      if (origin && /not allowed|не разреш/i.test(message) && window.confirm("HTML workspace запрашивает доступ к сети:\n" + origin + "\n\nРазрешить этот origin?")) {
        send("allowHtmlNetworkOrigin", { origin: origin }).then(function () {
          var list = state.settings.HtmlNetworkAllowedOrigins || state.settings.htmlNetworkAllowedOrigins || [];
          if (list.indexOf(origin) < 0) list.push(origin);
          state.settings.HtmlNetworkAllowedOrigins = list;
          return execute();
        }).catch(function (allowError) { reply(false, allowError.detail || allowError.message); });
        return;
      }
      reply(false, message);
    });
  });

  function prop(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function workspace() {
    var current = state.htmlWorkspace || {};
    current.files = prop(current, "Files", "files", []) || [];
    current.dataSources = prop(current, "DataSources", "dataSources", []) || [];
    current.history = prop(current, "History", "history", []) || [];
    current.redoHistory = prop(current, "RedoHistory", "redoHistory", []) || [];
    current.activeFileId = prop(current, "ActiveFileId", "activeFileId", "") || "";
    state.htmlWorkspace = current;
    return current;
  }

  function files() {
    return workspace().files;
  }

  function dataSources() {
    return workspace().dataSources;
  }

  function artifactId(artifact) {
    return prop(artifact, "Id", "id", "");
  }

  function artifactKind(artifact) {
    return String(prop(artifact, "Kind", "kind", "file") || "file").toLowerCase();
  }

  function artifactTitle(artifact) {
    return prop(artifact, "Title", "title", "Артефакт") || "Артефакт";
  }

  function artifactRevision(artifact) {
    return Number(prop(artifact, "Revision", "revision", 1) || 1);
  }

  function artifactInlineText(artifact) {
    return prop(artifact, "InlineText", "inlineText", "") || "";
  }

  function setArtifactInlineText(artifact, value) {
    if (!artifact) return;
    if (artifact.inlineText !== undefined || artifact.InlineText === undefined) artifact.inlineText = value || "";
    else artifact.InlineText = value || "";
  }

  function artifactById(id) {
    return (state.artifacts || []).filter(function (artifact) { return artifactId(artifact) === id; })[0] || null;
  }

  function planJson(artifact) {
    if (!artifact || artifactKind(artifact) !== "plan") return null;
    try { return JSON.parse(artifactInlineText(artifact)); } catch (error) { return null; }
  }

  function planStableId(artifact) {
    var plan = planJson(artifact);
    return plan && (plan.id || plan.Id) || artifactId(artifact);
  }

  function latestPlanArtifacts() {
    var latest = {};
    (state.artifacts || []).forEach(function (artifact) {
      if (artifactKind(artifact) !== "plan") return;
      var id = planStableId(artifact);
      if (!latest[id] || artifactRevision(artifact) > artifactRevision(latest[id])) latest[id] = artifact;
    });
    return Object.keys(latest).map(function (id) { return latest[id]; });
  }

  function historyItems() {
    return workspace().history || [];
  }

  function redoItems() {
    return workspace().redoHistory || [];
  }

  function fileId(file) {
    return prop(file, "Id", "id", prop(file, "Path", "path", ""));
  }

  function filePath(file) {
    return prop(file, "Path", "path", fileId(file));
  }

  function fileKind(file) {
    return (prop(file, "Kind", "kind", "") || "").toLowerCase();
  }

  function fileContent(file) {
    return prop(file, "Content", "content", "") || "";
  }

  function setFileContent(file, value) {
    if (!file) {
      return;
    }
    if (file.content !== undefined || file.Content === undefined) {
      file.content = value || "";
    } else {
      file.Content = value || "";
    }
  }

  function dataId(data) {
    return prop(data, "Id", "id", prop(data, "Name", "name", ""));
  }

  function dataName(data) {
    return prop(data, "Name", "name", dataId(data));
  }

  function dataJson(data) {
    return prop(data, "Json", "json", "{}") || "{}";
  }

  function setDataJson(data, value) {
    if (!data) {
      return;
    }
    if (data.json !== undefined || data.Json === undefined) {
      data.json = value || "{}";
    } else {
      data.Json = value || "{}";
    }
  }

  function selectedItem() {
    var selection = state.htmlWorkspaceSelection || {};
    var id = selection.id || "";
    var result = null;
    if (selection.type === "plan" || selection.type === "artifact") {
      var artifact = artifactById(id);
      return artifact ? { type: selection.type, item: artifact } : null;
    }
    if (selection.type === "data") {
      dataSources().forEach(function (item) {
        if (dataId(item) === id) {
          result = { type: "data", item: item };
        }
      });
      return result;
    }

    files().forEach(function (item) {
      if (fileId(item) === id) {
        result = { type: "file", item: item };
      }
    });
    return result;
  }

  function activeHtmlFile() {
    var activeId = workspace().activeFileId || "";
    var active = null;
    files().forEach(function (file) {
      if (!active && fileId(file) === activeId && fileKind(file) === "html") {
        active = file;
      }
    });
    if (active) {
      return active;
    }
    files().forEach(function (file) {
      if (!active && fileKind(file) === "html") {
        active = file;
      }
    });
    return active;
  }

  function ensureSelection() {
    if (selectedItem()) {
      return;
    }

    if (state.activePlanArtifactId && artifactById(state.activePlanArtifactId)) {
      state.htmlWorkspaceSelection = { type: "plan", id: state.activePlanArtifactId };
      return;
    }
    var active = activeHtmlFile();
    if (active) {
      state.htmlWorkspaceSelection = { type: "file", id: fileId(active) };
      return;
    }
    if (files().length) {
      state.htmlWorkspaceSelection = { type: "file", id: fileId(files()[0]) };
      return;
    }
    if (dataSources().length) {
      state.htmlWorkspaceSelection = { type: "data", id: dataId(dataSources()[0]) };
      return;
    }
    if ((state.artifacts || []).length) {
      state.htmlWorkspaceSelection = { type: "artifact", id: artifactId(state.artifacts[0]) };
      return;
    }
    state.htmlWorkspaceSelection = { type: "file", id: "" };
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

  function workspaceTreeFile(file) {
    return { id: fileId(file), path: filePath(file), kind: fileKind(file), content: fileContent(file) };
  }

  function workspaceTreeData(data) {
    return { id: dataId(data), name: dataName(data), json: dataJson(data) };
  }

  function workspaceTreeArtifact(artifact) {
    var kind = artifactKind(artifact);
    return {
      id: artifactId(artifact),
      kind: kind,
      title: artifactTitle(artifact),
      mimeType: prop(artifact, "MimeType", "mimeType", ""),
      relativePath: prop(artifact, "RelativePath", "relativePath", ""),
      text: artifactInlineText(artifact),
      meta: kind === "plan" ? workspaceArtifacts.planSummary(artifact) : workspaceArtifacts.typeLabel(kind)
    };
  }

  function renderHtmlWorkspaceList() {
    var search = $("htmlWorkspaceSearchInput");
    workspaceTree.render({
      root: $("htmlWorkspaceTree"),
      query: search ? search.value : "",
      files: files().map(workspaceTreeFile),
      dataSources: dataSources().map(workspaceTreeData),
      artifacts: (state.artifacts || []).map(workspaceTreeArtifact),
      plans: latestPlanArtifacts().map(workspaceTreeArtifact),
      selected: state.htmlWorkspaceSelection || {},
      onSelect: selectHtmlWorkspaceItem
    });
  }
  function snapshotLabel(snapshot) {
    return prop(snapshot || {}, "Label", "label", "HTML workspace snapshot");
  }

  function selectHtmlWorkspaceItem(type, id) {
    var selected = state.htmlWorkspaceSelection || {};
    var changingSelection = String(selected.type || "") !== String(type || "") || String(selected.id || "") !== String(id || "");
    if (state.htmlWorkspaceDirty && changingSelection) {
      window.alert("Сначала сохраните изменения текущего артефакта.");
      return;
    }
    state.htmlWorkspaceSelection = { type: type, id: id };
    renderHtmlWorkspace();
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

  function applyHtmlWorkspaceResponse(response) {
    response = response || {};
    state.htmlWorkspace = response.workspace || response.Workspace || { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [] };
    state.htmlWorkspaceDirty = false;
    renderHtmlWorkspace();
  }

  function workspaceActionSelection() {
    var selected = selectedItem();
    if (!selected) return null;
    var result = { type: selected.type, item: selected.item };
    if (selected.type === "plan") {
      result.label = artifactTitle(selected.item);
      result.planId = planStableId(selected.item);
    } else if (selected.type === "data") {
      result.label = dataName(selected.item);
      result.name = dataName(selected.item);
      result.json = dataJson(selected.item);
    } else if (selected.type === "file") {
      result.label = filePath(selected.item);
      result.path = filePath(selected.item);
      result.kind = fileKind(selected.item);
      result.content = fileContent(selected.item);
    }
    return result;
  }

  function workspaceActionState() {
    var undo = historyItems()[0];
    var redo = redoItems()[0];
    return {
      bridgeUnavailable: !!state.bridgeUnavailable,
      chatId: state.activeChatId,
      dirty: !!state.htmlWorkspaceDirty,
      undoSnapshotId: undo ? prop(undo, "Id", "id", "") : "",
      redoSnapshotId: redo ? prop(redo, "Id", "id", "") : ""
    };
  }

  function applyPlanRefresh(planId, response) {
    if (typeof applyChatState === "function") applyChatState(response);
    var latest = latestPlanArtifacts().filter(function (artifact) { return planStableId(artifact) === planId; })[0] || null;
    state.htmlWorkspaceSelection = latest
      ? { type: "plan", id: artifactId(latest) }
      : { type: "file", id: "" };
    state.htmlWorkspaceDirty = false;
    renderHtmlWorkspace();
  }

  function addHtmlWorkspaceFile(kind) {
    if (state.bridgeUnavailable) return;
    var fallback = kind === "css" ? "styles.css" : (kind === "script" ? "app.js" : "index.html");
    showHtmlWorkspaceCreate(kind, fallback);
  }

  function showHtmlWorkspaceCreate(kind, fallback) {
    state.htmlWorkspaceCreateKind = kind;
    var box = $("htmlWorkspaceCreateBox");
    var input = $("htmlWorkspaceCreateNameInput");
    if (!box || !input) {
      return;
    }

    box.classList.remove("hidden");
    input.value = fallback || "";
    input.placeholder = kind === "data" ? "Имя data source" : "Путь файла";
    input.focus();
    input.select();
  }

  function hideHtmlWorkspaceCreate() {
    state.htmlWorkspaceCreateKind = "";
    if ($("htmlWorkspaceCreateBox")) {
      $("htmlWorkspaceCreateBox").classList.add("hidden");
    }
  }

  async function confirmHtmlWorkspaceCreate() {
    if (state.bridgeUnavailable) {
      return;
    }
    var kind = state.htmlWorkspaceCreateKind || "html";
    var input = $("htmlWorkspaceCreateNameInput");
    var path = input ? input.value : "";
    if (!path || !path.trim()) {
      return;
    }
    if (kind === "data") {
      await workspaceActions.createData(path.trim());
      return;
    }

    await workspaceActions.createFile(kind, path.trim());
  }

  async function addHtmlWorkspaceData() {
    if (state.bridgeUnavailable) {
      return;
    }
    showHtmlWorkspaceCreate("data", "data");
  }

  function updateHtmlSidebarToggle() {
    var button = $("toggleHtmlSidebarButton");
    if (!button) {
      return;
    }
    var label = state.htmlWorkspaceSidebarHidden ? "Показать список" : "Скрыть список";
    button.setAttribute("title", label);
    button.setAttribute("aria-label", label);
  }

  function toggleHtmlWorkspaceSidebar() {
    state.htmlWorkspaceSidebarHidden = !state.htmlWorkspaceSidebarHidden;
    renderHtmlWorkspace();
  }

  function renderHtmlWorkspace() {
    workspace();
    ensureSelection();
    var layout = $("htmlWorkspaceLayout");
    if (layout) {
      layout.classList.toggle("is-sidebar-hidden", !!state.htmlWorkspaceSidebarHidden);
    }
    updateHtmlSidebarToggle();
    renderHtmlWorkspaceList();
    renderHtmlWorkspaceEditor();
    applyHtmlWorkspaceMode();
    updateHtmlWorkspaceStatus();
  }

  function bindHtmlWorkspaceActions() {
    $("htmlWorkspaceSearchInput").addEventListener("input", renderHtmlWorkspaceList);
    $("saveHtmlWorkspaceButton").addEventListener("click", workspaceActions.saveSelection);
    $("deleteHtmlWorkspaceButton").addEventListener("click", workspaceActions.deleteSelection);
    $("undoHtmlWorkspaceButton").addEventListener("click", workspaceActions.undo);
    $("redoHtmlWorkspaceButton").addEventListener("click", workspaceActions.redo);
    $("toggleHtmlSidebarButton").addEventListener("click", toggleHtmlWorkspaceSidebar);
    $("addPlanButton").addEventListener("click", workspaceActions.createPlan);
    $("addHtmlFileButton").addEventListener("click", function () { addHtmlWorkspaceFile("html"); });
    $("addCssFileButton").addEventListener("click", function () { addHtmlWorkspaceFile("css"); });
    $("addJsFileButton").addEventListener("click", function () { addHtmlWorkspaceFile("script"); });
    $("addHtmlDataButton").addEventListener("click", addHtmlWorkspaceData);
    $("confirmHtmlCreateButton").addEventListener("click", confirmHtmlWorkspaceCreate);
    $("cancelHtmlCreateButton").addEventListener("click", hideHtmlWorkspaceCreate);
    $("htmlWorkspaceCreateNameInput").addEventListener("keydown", function (event) {
      if (event.key === "Enter") {
        event.preventDefault();
        confirmHtmlWorkspaceCreate();
      } else if (event.key === "Escape") {
        event.preventDefault();
        hideHtmlWorkspaceCreate();
      }
    });
    Array.prototype.slice.call(document.querySelectorAll(".html-workspace-mode-button")).forEach(function (button) {
      button.addEventListener("click", function () {
        setHtmlWorkspaceMode(button.getAttribute("data-html-mode"));
      });
    });
  }

  window.renderHtmlWorkspace = renderHtmlWorkspace;
  window.bindHtmlWorkspaceActions = bindHtmlWorkspaceActions;
  window.saveHtmlWorkspaceSelection = workspaceActions.saveSelection;
  window.markHtmlWorkspaceDirty = markHtmlWorkspaceDirty;
  window.confirmDiscardHtmlWorkspaceChanges = confirmDiscardHtmlWorkspaceChanges;
}());
