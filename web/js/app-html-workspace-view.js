(function () {
  "use strict";
  function key(target) { return "html-editor:" + encodeURIComponent(target.chatId); }
  function create(target) {
  var root = document.getElementById("htmlWorkspaceEditorTemplate").content.firstElementChild.cloneNode(true);
  var refs = {};
  root.querySelectorAll("[data-html-node]").forEach(function (node) { refs[node.dataset.htmlNode] = node; });
  refs.htmlWorkspaceEditor = root;
  function $(id) { return refs[id] || null; }
  RNAssistantIcons.hydrate(root); root.dataset.chatId = target.chatId;
  var modeSwitcher = root.querySelector('.html-workspace-mode-toggle');
  var modeButtons = Array.prototype.slice.call(modeSwitcher.querySelectorAll('button'));
  $("htmlWorkspacePreviewFrame").dataset.chatId = target.chatId;
  var active = false, disposed = false, loading = null, loadGeneration = 0, loaded = false, sourceConflict = false;
  var sourceGeneration = 0, checkedSourceGeneration = 0, authorityGeneration = -1;
  var events = new AbortController();
  var state = { activeChatId: target.chatId, activeHtmlArtifactId: "", activePlanDocumentArtifactId: "", artifacts: [],
    artifactLibrary: { heads: [] }, htmlWorkspace: {}, htmlWorkspaceMode: "edit", htmlWorkspaceSelection: {},
    htmlWorkspaceDirty: false, htmlWorkspaceEditVersion: 0, chatProjectionRevisions: {}, chatNavigationVersion: 0,
    chatDetailPending: true, chatDetailError: "" };
  Object.defineProperty(state, "bridgeUnavailable", { get: function () { return disposed || window.state.bridgeUnavailable; } });
  Object.defineProperty(state, "settings", { get: function () { return window.state.settings; } });
  function chatPanel() { return RNAssistantConversationPanel.office(target.chatId); }
  function acceptChatAuthority(snapshot) {
    if (!snapshot || snapshot.activeChatId !== target.chatId) return;
    state.activePlanDocumentArtifactId = snapshot.activePlanDocumentArtifactId || "";
    state.activeTaskListArtifactId = snapshot.activeTaskListArtifactId || "";
  }
  function updateDrafts() { var panel = chatPanel(); if (panel) panel.controls(); }
  function log(message, level) { if (!disposed) { $("htmlWorkspaceStatus").textContent = message; $("htmlWorkspaceStatus").dataset.error = level === "error"; } }
  var code = RNAssistantCodeEditor.mount($("htmlWorkspaceEdit"), { text: "", mode: "htmlmixed", readOnly: false,
    lineWrapping: true, change: function () { if (workspaceEditor) workspaceEditor.markDirty(); },
    save: function () { return workspaceActions && workspaceActions.saveSelection(); } });
  var htmlPreview = window.RNAssistantHtmlWorkspacePreview;
  var workspaceArtifacts = window.RNAssistantHtmlWorkspaceArtifacts;
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
  var workspaceSource = window.RNAssistantHtmlWorkspaceSource.create({ state: state, send: send,
    cancelRequest: function (id) { return cancelBridgeRequest(id); }, changed: function () { renderHtmlWorkspace(); } });
  var dataId = workspaceModel.dataId;
  var dataName = workspaceModel.dataName;
  var dataJson = workspaceModel.dataJson;
  var dataBinding = workspaceModel.dataBinding;
  var boundDataSources = workspaceModel.boundDataSources;
  var selectedItem = workspaceModel.selectedItem;
  var ensureSelection = workspaceModel.ensureSelection;
  var refreshLibraryHeadSelection = workspaceModel.refreshLibraryHeadSelection;
  var workspaceActions = null;

  function handleResourceAuthorityChanged(notice) {
    if (disposed || !notice) return;
    if (target.documentId && notice.scope === "document:" + target.documentId &&
        Number.isSafeInteger(notice.generation) && notice.generation > authorityGeneration) {
      authorityGeneration = notice.generation; sourceGeneration++;
      if (active && !workspaceActions.isBusy()) ready(true);
    }
  }

  function submitPlanHandoff() {
    RNAssistantConversationPanel.open({ source: "office", sessionId: target.chatId });
    var panel = chatPanel();
    if (!panel) return false;
    var input = panel.node("chatInput"), form = panel.node("chatForm");
    input.value = "Выполни утверждённый активный план. Найди его через core.find и прочитай через core.read.";
    input.dispatchEvent(new Event("input", { bubbles: true })); form.requestSubmit(); return true;
  }
  var workspaceEditor = window.RNAssistantHtmlWorkspaceEditor.create({
    state: state, node: $, root: root, code: code,
    changed: renderHtmlWorkspace,
    previewVisible: function () { return active && state.htmlWorkspaceMode === "preview"; },
    model: workspaceModel,
    source: workspaceSource,
    artifacts: workspaceArtifacts,
    artifactActions: {
      artifactImageThumbnailState: function (uri) {
        return workspaceActions && workspaceActions.artifactImageThumbnailState(uri);
      },
      artifactViewerState: function (uri) {
        return workspaceActions && workspaceActions.artifactViewerState(uri);
      },
      changeArtifactPdfPage: function (request) {
        return workspaceActions && workspaceActions.changeArtifactPdfPage(request);
      },
      selectArtifactPdfPage: function (request) {
        return workspaceActions && workspaceActions.selectArtifactPdfPage(request);
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
      loadArtifactImage: function (request) {
        return workspaceActions && workspaceActions.loadArtifactImage(request);
      },
      loadArtifactImageThumbnail: function (request) {
        return workspaceActions && workspaceActions.loadArtifactImageThumbnail(request);
      },
      loadArtifactPdf: function (request) {
        return workspaceActions && workspaceActions.loadArtifactPdf(request);
      },
      loadArtifactPdfThumbnail: function (request) {
        return workspaceActions && workspaceActions.loadArtifactPdfThumbnail(request);
      },
      loadArtifactViewer: function (request) {
        return workspaceActions && workspaceActions.loadArtifactViewer(request);
      },
      loadArtifactViewerFull: function (request) {
        return workspaceActions && workspaceActions.loadArtifactViewerFull(request);
      },
      collectionItems: function (collectionId) {
        return typeof window.artifactCollectionItems === "function"
          ? window.artifactCollectionItems(collectionId)
          : [];
      },
      openCollectionArtifact: function (artifact, collectionItems, collectionId) {
        return typeof window.openArtifactResource === "function"
          ? window.openArtifactResource(artifact, collectionItems, "library:" + String(collectionId || ""))
          : false;
      },
      imageGalleryContext: function (artifact) {
        return typeof window.artifactImageGalleryContext === "function"
          ? window.artifactImageGalleryContext(artifact)
          : null;
      },
      selectImageGalleryItem: function (index) {
        return typeof window.selectArtifactImageGalleryItem === "function"
          ? window.selectArtifactImageGalleryItem(index)
          : false;
      }
    }
  });
  var syncHtmlEditorToState = workspaceEditor.sync;
  var markHtmlWorkspaceDirty = workspaceEditor.markDirty;
  var confirmDiscardHtmlChanges = workspaceEditor.confirmDiscard;
  var updateHtmlWorkspaceStatus = workspaceEditor.updateStatus;
  function openBrowser() {
    var value = state.browserTarget;
    if (!value || value.state !== "Ready") { log("Сохраните HTML-проект перед открытием Browser.", "error"); return false; }
    if (state.htmlWorkspaceDirty || sourceConflict) { log("Сначала сохраните правки или обновите исходники.", "error"); return false; }
    return RNAssistantBrowser.open(value.displayLocator, value.ownerScope, { reuse: true, title: value.title, referencePanel: key(target) });
  }
  function setHtmlWorkspaceMode(mode) {
    var selection = selectedItem();
    if (mode === "preview" && selection && (selection.type === "file" || selection.type === "data")) return openBrowser();
    return workspaceEditor.setMode(mode);
  }
  var applyHtmlWorkspaceMode = workspaceEditor.applyMode;
  var renderHtmlWorkspaceEditor = workspaceEditor.render;
  workspaceActions = window.RNAssistantHtmlWorkspaceActions.create({
    state: state,
    send: send,
    log: log,
    cancelRequest: function (requestId) { return cancelBridgeRequest(requestId); },
    getSelection: workspaceActionSelection,
    deleteDocumentArtifact: function (artifact) {
      if (!window.confirm("Удалить «" + artifactTitle(artifact) + "» из документа и всех его чатов?")) return false;
      return send("deleteDocumentArtifact", { chatId: target.chatId, expectedSessionRevision: state.chatProjectionRevisions[target.chatId],
        resourceUri: prop(artifact, "ResourceUri", "resourceUri", ""), confirmed: true }).then(function () { return ready(true); });
    },
    getActionState: workspaceActionState,
    syncEditor: syncHtmlEditorToState,
    applyWorkspaceResponse: function (response, chatId) {
      var accepted = applyHtmlWorkspaceResponse(response, chatId);
      if (accepted) refreshChat();
      return accepted;
    },
    applyPlanRefresh: applyPlanRefresh,
    hasRefreshableData: function (policy) {
      return boundDataSources(policy === "on_preview" ? "on_preview" : "").length > 0;
    },
    refreshableDataNames: function (policy) {
      return boundDataSources(policy === "on_preview" ? "on_preview" : "")
        .map(dataName);
    },
    switchChatMode: function (mode) {
      RNAssistantConversationPanel.open({ source: "office", sessionId: target.chatId });
      var panel = chatPanel(); return panel ? panel.composer.setPreference("mode", mode) : false;
    },
    submitPlanHandoff: submitPlanHandoff,
    downloadHtmlExport: downloadHtmlWorkspaceExport,
    downloadArtifactText: downloadArtifactText,
    applyArtifactViewerText: applyArtifactViewerText,
    validatePlanDraft: workspaceArtifacts.validatePlanDraft,
    hideCreate: hideHtmlWorkspaceCreate,
    onArtifactThumbnailChange: function (uri, thumbnail) {
      if (typeof window.updateArtifactThumbnailViews === "function") {
        window.updateArtifactThumbnailViews(uri, thumbnail);
      }
    },
    render: function () {
      renderHtmlWorkspace();
    }
  });

  function renderFiles() {
    var select = $("htmlWorkspaceFileSelect");
    var entries = files().map(function (file) { return { value: "file:" + fileId(file), label: filePath(file) }; })
      .concat(dataSources().map(function (data) { return { value: "data:" + dataId(data), label: dataName(data) }; }));
    var selection = state.htmlWorkspaceSelection || {}, value = selection.type + ":" + selection.id;
    var signature = JSON.stringify(entries);
    if (select.dataset.entries !== signature) {
      select.replaceChildren(); entries.forEach(function (entry) { var option = document.createElement("option");
        option.value = entry.value; option.textContent = entry.label; select.appendChild(option); }); select.dataset.entries = signature;
    }
    select.value = value; select.hidden = selection.type === "plan" || selection.type === "artifact";
  }
  function selectHtmlWorkspaceItem(type, id) {
    var selected = state.htmlWorkspaceSelection || {};
    var changingSelection = String(selected.type || "") !== String(type || "") || String(selected.id || "") !== String(id || "");
    if (state.htmlWorkspaceDirty && changingSelection) {
      window.alert("Сначала сохраните изменения текущего артефакта.");
      return false;
    }
    state.artifactImageGalleryContext = null;
    state.htmlWorkspaceSelection = { type: type, id: id };
    renderHtmlWorkspace();
    return true;
  }

  function applyHtmlWorkspaceResponse(response, expectedChatId) {
    if (disposed || expectedChatId && state.activeChatId !== expectedChatId) return false;
    response = response || {};
    var responseChatId = response.activeChatId || response.ActiveChatId;
    if (responseChatId !== target.chatId) return false;
    var browserTarget = RNAssistantBrowserTarget.accept(response.browserTarget, {
      chatId: target.chatId, documentId: target.documentId, artifactId: response.activeHtmlArtifactId || response.ActiveHtmlArtifactId || ""
    }, "HtmlWorkspace");
    var revision = window.RNAssistantRunViewState.sessionRevision(response);
    if (!window.RNAssistantRunViewState.accept(state.chatProjectionRevisions, responseChatId, revision)) return false;
    if (response.artifacts !== undefined || response.Artifacts !== undefined) {
      state.artifacts = JSON.parse(JSON.stringify(response.artifacts || response.Artifacts || []));
    }
    if (response.artifactLibrary !== undefined || response.ArtifactLibrary !== undefined) {
      state.artifactLibrary = response.artifactLibrary || response.ArtifactLibrary || { sessionRevision: revision || 0, heads: [] };
    }
    if (response.activeHtmlArtifactId !== undefined || response.ActiveHtmlArtifactId !== undefined) {
      state.activeHtmlArtifactId = response.activeHtmlArtifactId || response.ActiveHtmlArtifactId || "";
    }
    state.htmlWorkspacePreflight = response.staticPreflight || response.StaticPreflight ||
      { status: "ok", passed: true, errorCount: 0, warningCount: 0, issues: [] };
    state.htmlWorkspace = response.workspace || response.Workspace || { activeFileId: "", files: [], dataSources: [], history: [], redoHistory: [], redoBranches: [], recovery: { status: "empty", canMutate: true, candidates: [] } };
    var panel = chatPanel(); if (panel) acceptChatAuthority(panel.snapshot());
    state.browserTarget = browserTarget;
    state.htmlWorkspaceDirty = false; sourceConflict = false; state.chatDetailError = ""; loaded = true;
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
    if (disposed || expectedChatId !== target.chatId || (response.activeChatId || response.ActiveChatId) !== target.chatId) return false;
    var revision = RNAssistantRunViewState.sessionRevision(response);
    if (!RNAssistantRunViewState.accept(state.chatProjectionRevisions, target.chatId, revision)) return false;
    state.artifacts = JSON.parse(JSON.stringify(response.artifacts || response.Artifacts || []));
    state.activePlanDocumentArtifactId = response.activePlanDocumentArtifactId || response.ActivePlanDocumentArtifactId || "";
    var latest = latestPlanArtifacts().filter(function (artifact) { return planStableId(artifact) === planId; })[0] || null;
    state.htmlWorkspaceSelection = latest
      ? { type: "plan", id: artifactId(latest) }
      : { type: "file", id: "" };
    state.htmlWorkspaceDirty = false;
    refreshChat();
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

  function renderHtmlWorkspace() {
    if (disposed || !active) return;
    if (loaded && !loading && sourceGeneration !== checkedSourceGeneration && !workspaceActions.isBusy()) ready(true);
    workspace(); ensureSelection(); renderFiles();
    renderHtmlWorkspaceEditor(); applyHtmlWorkspaceMode(); updateHtmlWorkspaceStatus();
    if (sourceConflict) log("HTML изменился в чате. Правки сохранены в этой вкладке; обновите исходники перед записью.", "error");
  }
  function refreshChat() { var panel = chatPanel(); if (panel) panel.refresh(); }
  function ready(force, discard) {
    if (loading) return loading;
    if (loaded && !force) return Promise.resolve(true);
    var generation = ++loadGeneration, editVersion = state.htmlWorkspaceEditVersion;
    state.chatDetailPending = true; state.chatDetailError = "";
    code.readOnly(true);
    loading = (async function () {
      try {
        var response, requestedSourceGeneration;
        do {
          requestedSourceGeneration = sourceGeneration;
          response = await send("getHtmlWorkspace", { chatId: target.chatId });
          if (disposed || generation !== loadGeneration) return false;
          if (state.htmlWorkspaceEditVersion !== editVersion) throw new Error("Появились новые правки. Рабочая копия не заменена.");
        } while (requestedSourceGeneration !== sourceGeneration);
        checkedSourceGeneration = requestedSourceGeneration;
        if (state.htmlWorkspaceDirty && !discard) {
          if ((response.activeChatId || response.ActiveChatId) !== target.chatId) throw new Error("Получено состояние другого чата.");
          if ((response.activeHtmlArtifactId || response.ActiveHtmlArtifactId) !== state.activeHtmlArtifactId) {
            sourceConflict = true; state.chatDetailError = "Исходник изменён. Ваши несохранённые правки сохранены во вкладке.";
          }
          return false;
        }
        if (!applyHtmlWorkspaceResponse(response, target.chatId)) throw new Error("Получено устаревшее состояние HTML или другой чат.");
        return true;
      } catch (error) { if (!disposed && generation === loadGeneration) { checkedSourceGeneration = sourceGeneration; state.chatDetailError = error.detail || error.message; log(state.chatDetailError, "error"); } return false; }
      finally { if (generation === loadGeneration) { loading = null; state.chatDetailPending = false; renderHtmlWorkspace(); } }
    })(); return loading;
  }
  async function reloadHtmlWorkspaceSources() {
    if (loading) await loading;
    if (disposed || workspaceActions.isBusy() || !confirmDiscardHtmlChanges("Перезагрузить исходники")) return false;
    workspaceSource.release(); workspaceActions.cancelWrite();
    return ready(true, true);
  }
  function downloadHtmlWorkspaceExport(target, guard) {
    return RNAssistantHtmlResourceExport.downloadTarget(target, { send: send, fetch: window.fetch.bind(window), signal: events.signal,
      isCurrent: function () { return active && !disposed && !sourceConflict && guard(); }, status: function (message) { if (active && !disposed) log(message); } });
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
    var menus = Array.from(root.querySelectorAll(".html-workspace-menu"));
    document.addEventListener("click", function (event) { menus.forEach(function (menu) {
      if (!menu.contains(event.target) || event.target.closest("button")) menu.open = false;
    }); }, { signal: events.signal });
    root.addEventListener("keydown", function (event) { if (event.key === "Escape") menus.forEach(function (menu) {
      if (menu.open) { menu.open = false; menu.querySelector("summary").focus(); }
    }); }, { signal: events.signal });
    $("htmlWorkspaceFileSelect").addEventListener("change", function () {
      var value = this.value, colon = value.indexOf(":"); selectHtmlWorkspaceItem(value.slice(0, colon), value.slice(colon + 1)); renderFiles();
    });
    $("saveHtmlWorkspaceButton").addEventListener("click", workspaceActions.saveSelection);
    $("reloadHtmlWorkspaceSourceButton").addEventListener("click", reloadHtmlWorkspaceSources);
    $("deleteHtmlWorkspaceButton").addEventListener("click", workspaceActions.deleteSelection);
    $("undoHtmlWorkspaceButton").addEventListener("click", workspaceActions.undo);
    $("redoHtmlWorkspaceButton").addEventListener("click", workspaceActions.redo);
    $("recoverHtmlWorkspaceButton").addEventListener("click", workspaceActions.recoverRevision);
    $("refreshHtmlDataButton").addEventListener("click", workspaceActions.refreshAll);
    $("exportHtmlWorkspaceButton").addEventListener("click", workspaceActions.exportWorkspace);
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
    modeButtons.forEach(function (button) {
      button.addEventListener("click", function () {
        setHtmlWorkspaceMode(button.getAttribute("data-html-mode"));
      });
    });
    $("copyHtmlWorkspaceButton").addEventListener("click", function () {
      var status = $("htmlWorkspaceCopyStatus");
      status.textContent = "";
      workspaceEditor.copySelectedSource().then(function () { status.textContent = "Скопировано"; })
        .catch(function () { status.textContent = "Не удалось скопировать"; });
    });
  }
  var detail = $("artifactDetailPreview"); detail.classList.add("html-workspace-view"); detail.dataset.htmlView = "preview";
  $("htmlWorkspacePreview").before(detail); $("htmlWorkspacePreview").hidden = true;
  $("htmlWorkspacePreviewLink").remove();
  bindHtmlWorkspaceActions();
  var controller = { element: root, target: target, ready: ready, openBrowser: openBrowser,
    select: selectHtmlWorkspaceItem, remove: deleteHtmlWorkspaceTreeItem, mode: setHtmlWorkspaceMode,
    createItem: function (kind) { if (kind === "plan") return workspaceActions.createPlan(); if (kind === "data") addHtmlWorkspaceData(); else addHtmlWorkspaceFile(kind); },
    dirty: function () { return state.htmlWorkspaceDirty; }, busy: function () { return workspaceActions.isBusy(); },
    flushForCheckpoint: async function (value) {
      if (!state.browserTarget || value.documentId !== state.browserTarget.documentId || value.logicalId !== state.browserTarget.logicalId) return false;
      if (disposed || sourceConflict || workspaceActions.isBusy()) throw new Error("Сначала завершите операцию или обновите HTML-редактор.");
      if (!state.htmlWorkspaceDirty) return false;
      await workspaceActions.saveSelection();
      if (state.htmlWorkspaceDirty || sourceConflict || disposed) throw new Error("Правки редактора не сохранены. Версия проекта не создана.");
      return true;
    },
    canClose: function () {
      if (workspaceActions.isBusy()) { log("Дождитесь завершения операции.", "error"); return false; }
      return confirmDiscardHtmlChanges("Закрыть вкладку");
    },
    onSnapshot: function (snapshot) {
      if (disposed || !loaded || snapshot.activeChatId !== target.chatId) return;
      if (snapshot.sessionRevision < (state.chatProjectionRevisions[target.chatId] || 0)) return;
      acceptChatAuthority(snapshot);
      if (snapshot.activeHtmlArtifactId !== state.activeHtmlArtifactId) {
        sourceGeneration++;
        sourceConflict = true;
        if (state.htmlWorkspaceDirty) { state.chatDetailError = "Исходник изменён"; renderHtmlWorkspace(); }
        else if (active && !workspaceActions.isBusy()) ready(true);
      } else if (Number.isSafeInteger(snapshot.sessionRevision)) state.chatProjectionRevisions[target.chatId] = snapshot.sessionRevision;
    },
    authorityChanged: handleResourceAuthorityChanged,
    activate: function () { active = true; ready(sourceGeneration !== checkedSourceGeneration || sourceConflict && !state.htmlWorkspaceDirty); renderHtmlWorkspace(); updateDrafts(); },
    deactivate: function () { active = false; },
    layout: function () { code.refresh(); },
    dispose: function () {
      if (disposed) return; disposed = true; active = false; loadGeneration++; events.abort();
      workspaceSource.release(); workspaceActions.cancelWrite(); workspaceActions.closeArtifactViewers();
      workspaceEditor.dispose();
      code.destroy(); root.remove(); updateDrafts();
    }
  };
  return controller;
  }
  window.RNAssistantHtmlWorkspaceView = { key: key, create: create };
}());
