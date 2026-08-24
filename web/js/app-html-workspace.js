(function () {
  var htmlPreview = window.RNAssistantHtmlWorkspacePreview;
  var workspaceArtifacts = window.RNAssistantHtmlWorkspaceArtifacts;
  var workspaceTree = window.RNAssistantHtmlWorkspaceTree;
  var workspaceModel = window.RNAssistantHtmlWorkspaceModel.create(state);
  var prop = workspaceModel.prop;
  var workspace = workspaceModel.workspace;
  var files = workspaceModel.files;
  var dataSources = workspaceModel.dataSources;
  var artifactId = workspaceModel.artifactId;
  var artifactKind = workspaceModel.artifactKind;
  var artifactTitle = workspaceModel.artifactTitle;
  var artifactInlineText = workspaceModel.artifactInlineText;
  var planStableId = workspaceModel.planStableId;
  var latestPlanArtifacts = workspaceModel.latestPlanArtifacts;
  var historyItems = workspaceModel.historyItems;
  var redoItems = workspaceModel.redoItems;
  var fileId = workspaceModel.fileId;
  var filePath = workspaceModel.filePath;
  var fileKind = workspaceModel.fileKind;
  var fileContent = workspaceModel.fileContent;
  var dataId = workspaceModel.dataId;
  var dataName = workspaceModel.dataName;
  var dataJson = workspaceModel.dataJson;
  var selectedItem = workspaceModel.selectedItem;
  var ensureSelection = workspaceModel.ensureSelection;
  var workspaceEditor = window.RNAssistantHtmlWorkspaceEditor.create({
    state: state,
    model: workspaceModel,
    preview: htmlPreview,
    artifacts: workspaceArtifacts
  });
  var syncHtmlEditorToState = workspaceEditor.sync;
  var markHtmlWorkspaceDirty = workspaceEditor.markDirty;
  var confirmDiscardHtmlChanges = workspaceEditor.confirmDiscard;
  var updateHtmlWorkspaceStatus = workspaceEditor.updateStatus;
  var setHtmlWorkspaceMode = workspaceEditor.setMode;
  var applyHtmlWorkspaceMode = workspaceEditor.applyMode;
  var renderHtmlWorkspaceEditor = workspaceEditor.render;
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
      onSelect: selectHtmlWorkspaceItem,
      onDelete: deleteHtmlWorkspaceTreeItem
    });
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

  function applyHtmlWorkspaceResponse(response) {
    response = response || {};
    state.htmlWorkspace = response.workspace || response.Workspace || { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [] };
    state.htmlWorkspaceDirty = false;
    renderHtmlWorkspace();
  }

  function workspaceActionSelection(type, id) {
    var selected = null;
    if (!type) {
      selected = selectedItem();
    } else if (type === "plan" || type === "artifact") {
      (state.artifacts || []).forEach(function (item) {
        if (artifactId(item) === id) selected = { type: type, item: item };
      });
    } else if (type === "data") {
      dataSources().forEach(function (item) {
        if (dataId(item) === id) selected = { type: "data", item: item };
      });
    } else if (type === "file") {
      files().forEach(function (item) {
        if (fileId(item) === id) selected = { type: "file", item: item };
      });
    }
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

  function deleteHtmlWorkspaceTreeItem(type, id) {
    var current = state.htmlWorkspaceSelection || {};
    var changingSelection = String(current.type || "") !== String(type || "") || String(current.id || "") !== String(id || "");
    if (state.htmlWorkspaceDirty && changingSelection) {
      window.alert("Сначала сохраните изменения текущего артефакта.");
      return;
    }
    return workspaceActions.deleteSelection(workspaceActionSelection(type, id));
  }

  function confirmDiscardArtifactChanges(action) {
    if (!confirmDiscardHtmlChanges(action)) return false;
    if (!state.vbaEditorDirty) return true;
    var accepted = window.confirm(
      "В VBA-модуле есть несохранённые изменения. " +
      (action || "Продолжить") +
      " и потерять их?"
    );
    if (accepted) state.vbaEditorDirty = false;
    return accepted;
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
    if (!button) return;
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
  window.confirmDiscardHtmlWorkspaceChanges = confirmDiscardArtifactChanges;
}());
