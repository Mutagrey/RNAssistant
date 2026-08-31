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
  var recoveryBlocked = workspaceModel.recoveryBlocked;
  var redoBranches = workspaceModel.redoBranches;
  var fileId = workspaceModel.fileId;
  var filePath = workspaceModel.filePath;
  var fileKind = workspaceModel.fileKind;
  var fileContent = workspaceModel.fileContent;
  var dataId = workspaceModel.dataId;
  var dataName = workspaceModel.dataName;
  var dataJson = workspaceModel.dataJson;
  var dataBinding = workspaceModel.dataBinding;
  var boundDataSources = workspaceModel.boundDataSources;
  var selectedItem = workspaceModel.selectedItem;
  var ensureSelection = workspaceModel.ensureSelection;
  var workspaceActions = null;

  function submitPlanHandoff(revisionUri) {
    var input = $("chatInput");
    var form = $("chatForm");
    if (!input || !form || typeof form.requestSubmit !== "function" || !revisionUri) return false;
    input.value = "Выполни утверждённый план " + revisionUri +
      ". Перед началом прочитай эту точную ревизию через common.resources_read.";
    updateComposerInputState();
    form.requestSubmit();
    return true;
  }

  var workspaceEditor = window.RNAssistantHtmlWorkspaceEditor.create({
    state: state,
    model: workspaceModel,
    preview: htmlPreview,
    artifacts: workspaceArtifacts,
    artifactActions: {
      artifactViewerState: function (uri) {
        return workspaceActions && workspaceActions.artifactViewerState(uri);
      },
      changeArtifactViewerPage: function (request) {
        return workspaceActions && workspaceActions.changeArtifactViewerPage(request);
      },
      downloadArtifactViewer: function (request) {
        return workspaceActions && workspaceActions.downloadArtifactViewer(request);
      },
      handoffPlan: function (request) {
        return workspaceActions && workspaceActions.handoffPlan(request);
      },
      restorePlanRevision: function (request) {
        return workspaceActions && workspaceActions.restorePlanRevision(request);
      },
      importUploadedHtml: function (request) {
        return workspaceActions && workspaceActions.importUploadedHtml(request);
      },
      loadUploadedHtmlSource: function (request) {
        return workspaceActions && workspaceActions.loadUploadedHtmlSource(request);
      },
      loadArtifactViewer: function (request) {
        return workspaceActions && workspaceActions.loadArtifactViewer(request);
      },
      loadArtifactViewerFull: function (request) {
        return workspaceActions && workspaceActions.loadArtifactViewerFull(request);
      },
      uploadedHtmlPreview: function (uri) {
        return workspaceActions && workspaceActions.uploadedHtmlPreview(uri);
      }
    }
  });
  var syncHtmlEditorToState = workspaceEditor.sync;
  var markHtmlWorkspaceDirty = workspaceEditor.markDirty;
  var confirmDiscardHtmlChanges = workspaceEditor.confirmDiscard;
  var updateHtmlWorkspaceStatus = workspaceEditor.updateStatus;
  var setHtmlWorkspaceMode = workspaceEditor.setMode;
  var applyHtmlWorkspaceMode = workspaceEditor.applyMode;
  var renderHtmlWorkspaceEditor = workspaceEditor.render;
  workspaceActions = window.RNAssistantHtmlWorkspaceActions.create({
    state: state,
    send: send,
    log: log,
    getSelection: workspaceActionSelection,
    getActionState: workspaceActionState,
    syncEditor: syncHtmlEditorToState,
    applyWorkspaceResponse: applyHtmlWorkspaceResponse,
    applyPlanRefresh: applyPlanRefresh,
    hasRefreshableData: function (policy) {
      return boundDataSources(policy === "on_preview" ? "on_preview" : "").length > 0;
    },
    switchChatMode: saveChatMode,
    submitPlanHandoff: submitPlanHandoff,
    downloadHtmlExport: downloadHtmlWorkspaceExport,
    downloadArtifactText: downloadArtifactText,
    applyArtifactViewerText: applyArtifactViewerText,
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
    return { id: dataId(data), name: dataName(data), json: dataJson(data), binding: dataBinding(data) };
  }

  function workspaceTreeArtifact(artifact) {
    var kind = artifactKind(artifact);
    var visuals = window.RNAssistantArtifactVisuals || null;
    return {
      id: artifactId(artifact),
      kind: kind,
      title: artifactTitle(artifact),
      mimeType: prop(artifact, "MimeType", "mimeType", ""),
      relativePath: prop(artifact, "RelativePath", "relativePath", ""),
      text: artifactInlineText(artifact),
      category: visuals && typeof visuals.category === "function" ? visuals.category(artifact) : "authored",
      meta: visuals && typeof visuals.meta === "function"
        ? visuals.meta(artifact)
        : (kind === "plan" ? workspaceArtifacts.planSummary(artifact) : workspaceArtifacts.typeLabel(kind))
    };
  }

  function renderHtmlWorkspaceList() {
    var search = $("htmlWorkspaceSearchInput");
    var resourceHeads = typeof artifactResourceHeads === "function" ? artifactResourceHeads() : (state.artifacts || []);
    var libraryArtifacts = resourceHeads.filter(function (artifact) {
      var kind = artifactKind(artifact);
      return kind !== "plan" && kind !== "html_workspace";
    });
    workspaceTree.render({
      root: $("htmlWorkspaceTree"),
      query: search ? search.value : "",
      files: files().map(workspaceTreeFile),
      dataSources: dataSources().map(workspaceTreeData),
      artifacts: libraryArtifacts.map(workspaceTreeArtifact),
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
      return false;
    }
    state.htmlWorkspaceSelection = { type: type, id: id };
    renderHtmlWorkspace();
    return true;
  }

  function applyHtmlWorkspaceResponse(response, expectedChatId) {
    if (expectedChatId && state.activeChatId !== expectedChatId) return false;
    response = response || {};
    var responseChatId = response.activeChatId || response.ActiveChatId || state.activeChatId;
    var revision = window.RNAssistantRunViewState.sessionRevision(response);
    if (!window.RNAssistantRunViewState.accept(state.chatProjectionRevisions, responseChatId, revision)) return false;
    if (response.artifacts !== undefined || response.Artifacts !== undefined) {
      state.artifacts = response.artifacts || response.Artifacts || [];
    }
    if (response.artifactLibrary !== undefined || response.ArtifactLibrary !== undefined) {
      state.artifactLibrary = response.artifactLibrary || response.ArtifactLibrary || { sessionRevision: revision || 0, heads: [] };
    }
    if (response.activeHtmlArtifactId !== undefined || response.ActiveHtmlArtifactId !== undefined) {
      state.activeHtmlArtifactId = response.activeHtmlArtifactId || response.ActiveHtmlArtifactId || "";
    }
    state.htmlWorkspace = response.workspace || response.Workspace || { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [], redoBranches: [], recovery: { status: "empty", canMutate: true, candidates: [] } };
    state.htmlWorkspaceDirty = false;
    renderHtmlWorkspace();
    return true;
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
      result.expectedRevisionArtifactId = artifactId(selected.item);
    } else if (selected.type === "data") {
      result.label = dataName(selected.item);
      result.name = dataName(selected.item);
      result.json = dataJson(selected.item);
      result.binding = dataBinding(selected.item);
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
    var branches = redoBranches();
    var redoSelect = $("redoHtmlWorkspaceBranchSelect");
    var recoverySelect = $("htmlWorkspaceRecoverySelect");
    var redoId = branches.length > 1 && redoSelect ? redoSelect.value : (branches[0] ? prop(branches[0], "Id", "id", "") : "");
    return {
      bridgeUnavailable: !!state.bridgeUnavailable,
      chatId: state.activeChatId,
      dirty: !!state.htmlWorkspaceDirty,
      recoverySnapshotId: recoverySelect ? recoverySelect.value : "",
      undoSnapshotId: undo ? prop(undo, "Id", "id", "") : "",
      redoSnapshotId: redoId
    };
  }

  function applyPlanRefresh(planId, response, expectedChatId) {
    if (typeof applyChatStateForChat === "function" &&
        !applyChatStateForChat(response, expectedChatId)) return false;
    var latest = latestPlanArtifacts().filter(function (artifact) { return planStableId(artifact) === planId; })[0] || null;
    state.htmlWorkspaceSelection = latest
      ? { type: "plan", id: artifactId(latest) }
      : { type: "file", id: "" };
    state.htmlWorkspaceDirty = false;
    renderHtmlWorkspace();
    return true;
  }

  function addHtmlWorkspaceFile(kind) {
    if (state.bridgeUnavailable || recoveryBlocked()) return;
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
    if (state.bridgeUnavailable || recoveryBlocked()) {
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
    if (state.bridgeUnavailable || recoveryBlocked()) {
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
    button.setAttribute("aria-pressed", state.htmlWorkspaceSidebarHidden ? "true" : "false");
    button.innerHTML = state.htmlWorkspaceSidebarHidden
      ? "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"m10 6 6 6-6 6\"/></svg>"
      : "<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"m14 6-6 6 6 6\"/></svg>";
  }

  function toggleHtmlWorkspaceSidebar() {
    state.htmlWorkspaceSidebarHidden = !state.htmlWorkspaceSidebarHidden;
    try {
      window.localStorage.setItem("rnassistant.artifacts.sidebar.hidden", state.htmlWorkspaceSidebarHidden ? "1" : "0");
    } catch (error) {
    }
    renderHtmlWorkspace();
    if (typeof refreshCodeEditors === "function") refreshCodeEditors(["htmlWorkspaceEditorInput"]);
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

  function downloadHtmlWorkspaceExport(exportState) {
    exportState = exportState || {};
    var exportedWorkspace = exportState.workspace || {};
    var exportedFiles = prop(exportedWorkspace, "Files", "files", []) || [];
    var exportedData = prop(exportedWorkspace, "DataSources", "dataSources", []) || [];
    if (!exportedFiles.some(function (file) { return fileKind(file) === "html"; })) {
      throw new Error("HTML export checkpoint has no HTML entry file.");
    }
    var html = htmlPreview.build({
      activeFileId: prop(exportedWorkspace, "ActiveFileId", "activeFileId", ""),
      dataSources: exportedData,
      files: exportedFiles,
      hostBridge: false
    });
    var url = URL.createObjectURL(new Blob([html], { type: "text/html;charset=utf-8" }));
    var link = document.createElement("a");
    link.href = url;
    link.download = "rnassistant-workspace.html";
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
  }

  function applyArtifactViewerText(resourceUri, contentSha256, text) {
    var applied = false;
    (state.artifacts || []).forEach(function (artifact) {
      if (applied || artifactKind(artifact) !== "plan") return;
      var uri = prop(artifact, "ResourceUri", "resourceUri", "") || "";
      var hash = prop(artifact, "ContentSha256", "contentSha256", "") || "";
      if (uri !== resourceUri || (hash && hash.toLowerCase() !== String(contentSha256 || "").toLowerCase())) return;
      workspaceModel.setArtifactInlineProjection(artifact, String(text || ""));
      applied = true;
    });
    return applied;
  }

  function downloadArtifactText(download) {
    download = download || {};
    if (!download.resourceUri || !/^[a-f0-9]{64}$/i.test(download.contentSha256 || "")) {
      throw new Error("Artifact download has no exact revision evidence.");
    }
    var title = String(download.title || "artifact.txt").split(/[\\/]/).pop().replace(/[<>:"|?*\u0000-\u001f]/g, "_");
    if (!title) title = "artifact.txt";
    var mimeType = String(download.mimeType || "text/plain").split(";", 1)[0] + ";charset=utf-8";
    var url = URL.createObjectURL(new Blob([String(download.text || "")], { type: mimeType }));
    var link = document.createElement("a");
    link.href = url;
    link.download = title;
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
    return true;
  }

  function bindHtmlWorkspaceActions() {
    $("htmlWorkspaceSearchInput").addEventListener("input", renderHtmlWorkspaceList);
    $("saveHtmlWorkspaceButton").addEventListener("click", workspaceActions.saveSelection);
    $("deleteHtmlWorkspaceButton").addEventListener("click", workspaceActions.deleteSelection);
    $("undoHtmlWorkspaceButton").addEventListener("click", workspaceActions.undo);
    $("redoHtmlWorkspaceButton").addEventListener("click", workspaceActions.redo);
    $("recoverHtmlWorkspaceButton").addEventListener("click", workspaceActions.recoverRevision);
    $("refreshHtmlDataButton").addEventListener("click", workspaceActions.refreshAll);
    $("exportHtmlWorkspaceButton").addEventListener("click", workspaceActions.exportWorkspace);
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
        if (button.getAttribute("data-html-mode") === "preview") workspaceActions.refreshAuto();
      });
    });
    var artifactsTab = document.querySelector('.tab[data-tab="artifacts"]');
    if (artifactsTab) artifactsTab.addEventListener("click", workspaceActions.refreshAuto);
  }

  window.renderHtmlWorkspace = renderHtmlWorkspace;
  window.bindHtmlWorkspaceActions = bindHtmlWorkspaceActions;
  window.saveHtmlWorkspaceSelection = workspaceActions.saveSelection;
  window.markHtmlWorkspaceDirty = markHtmlWorkspaceDirty;
  window.confirmDiscardHtmlWorkspaceChanges = confirmDiscardArtifactChanges;
}());
