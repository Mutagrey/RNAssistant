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
  var ingestionQueue = window.state.chatResourceIngestions || (window.state.chatResourceIngestions = {});
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
  var resourceHandles = new Map();
  var resourcePreviewEpoch = 0;

  function handleResourceAuthorityChanged(notice) {
    if (disposed || !notice) return;
    if (target.documentId && notice.scope === "document:" + target.documentId &&
        Number.isSafeInteger(notice.generation) && notice.generation > authorityGeneration) {
      authorityGeneration = notice.generation; sourceGeneration++;
      if (active && !workspaceActions.isBusy()) ready(true);
    }
    var frame = $("htmlWorkspacePreviewFrame");
    if (!frame || !frame.contentWindow) return;
    var sources = dataSources();
    var names = sources.filter(function (source) {
      var binding = dataBinding(source) || {};
      var reference = binding.resource || binding.Resource || {};
      var uri = reference.uri || reference.Uri || "";
      var identity = uri.replace(/\/revision\/[^/]+(?=\/|$)/, "");
      return (binding.policy || binding.Policy) === "head" &&
        ((notice.resources || []).indexOf(identity) >= 0 || notice.allInScope === true);
    }).map(function (source) { return source.Name || source.name; });
    if (!disposed && names.length) frame.contentWindow.postMessage({ type: "rnassistant-resource-changed",
      names: names, generation: notice.generation }, "*");
  };

  function closePreviewResources() {
    resourcePreviewEpoch += 1;
    resourceHandles.forEach(function (owner, leaseId) {
      send("resourceDataClose", { chatId: owner.chatId, workspaceId: owner.workspaceId, leaseId: leaseId }).catch(function () {});
    });
    resourceHandles.clear();
  }

  window.addEventListener("message", function (event) {
    var frame = $("htmlWorkspacePreviewFrame"), request = event.data || {};
    if (!frame || event.source !== frame.contentWindow || request.type !== "rnassistant-resource-control" ||
        !event.ports || event.ports.length !== 1) return;
    var port = event.ports[0];
    var requestEpoch = resourcePreviewEpoch;
    var owner = { chatId: state.activeChatId, workspaceId: state.activeHtmlArtifactId };
    var work;
    if (request.operation === "open" && typeof request.bindingName === "string" && request.bindingName.length <= 128) {
      work = send("resourceDataOpen", { chatId: owner.chatId, workspaceId: owner.workspaceId, bindingName: request.bindingName })
        .then(function (lease) {
          if (requestEpoch !== resourcePreviewEpoch || state.activeChatId !== owner.chatId || state.activeHtmlArtifactId !== owner.workspaceId) {
            send("resourceDataClose", { chatId: owner.chatId, workspaceId: owner.workspaceId, leaseId: lease.leaseId }).catch(function () {});
            throw new Error("RESOURCE_WORKSPACE_CHANGED");
          }
          resourceHandles.set(lease.leaseId, owner); return lease;
        });
    } else if (request.operation === "close" && resourceHandles.has(request.leaseId)) {
      owner = resourceHandles.get(request.leaseId); resourceHandles.delete(request.leaseId);
      work = send("resourceDataClose", { chatId: owner.chatId, workspaceId: owner.workspaceId, leaseId: request.leaseId });
    } else work = Promise.reject(new Error("RESOURCE_ACCESS_DENIED"));
    work.then(function (value) { port.postMessage({ ok: true, value: value }); },
      function (error) { port.postMessage({ ok: false, error: error.detail || error.message }); })
      .finally(function () { port.close(); });
  }, { signal: events.signal });

  function submitPlanHandoff() {
    RNAssistantConversationPanel.open({ source: "office", sessionId: target.chatId });
    var panel = chatPanel();
    if (!panel) return false;
    var input = panel.node("chatInput"), form = panel.node("chatForm");
    input.value = "Выполни утверждённый активный план. Найди его через core.find и прочитай через core.read.";
    input.dispatchEvent(new Event("input", { bubbles: true })); form.requestSubmit(); return true;
  }
  var stagingVisualSelection = false, visualAnnotations = null;
  function annotationSource() {
    var item = state.artifacts.find(function (item) { return artifactId(item) === state.activeHtmlArtifactId; });
    return item ? { id: artifactId(item), revision: Number(prop(item, "Revision", "revision", 0)) } : null;
  }
  var visualInspector = RNAssistantVisualInspector.createHost({ frame: $("htmlWorkspacePreviewFrame"),
    button: $("inspectHtmlWorkspaceButton"), status: $("htmlWorkspaceInspectStatus"),
    enabled: function () { var panel = chatPanel(); return active && state.browserTarget && state.browserTarget.capabilities.annotations && !sourceConflict && !state.htmlWorkspaceDirty &&
      !state.chatDetailPending && !state.chatDetailError && !(panel && panel.snapshot().chatReadOnly); },
    onSelectionChanged: function (selected, reason) { if (visualAnnotations) visualAnnotations.onSelection(selected ? visualInspector.selectionRecord() : null, reason); },
    onLocations: function (locations) { if (visualAnnotations) visualAnnotations.onLocations(locations); },
    onReveal: function (result) { if (visualAnnotations) visualAnnotations.onReveal(result); }
  });
  async function stageVisualSelection(record, comment, number, replacing) {
    if (stagingVisualSelection || ingestionQueue[target.chatId] || disposed || sourceConflict || state.htmlWorkspaceDirty ||
        !state.browserTarget || !state.browserTarget.capabilities.annotations) return null;
    var source = annotationSource(), panel = chatPanel();
    if (!record || !source || source.revision < 1 || panel && panel.snapshot().chatReadOnly ||
        RNAssistantConversationInput.isPending("office", { chatId: target.chatId })) throw new Error("Чат занят или источник недоступен.");
    var generation = visualInspector.generation(), attachment = null, attached = false;
    if (record.generation !== generation || record.sourceId && (record.sourceId !== source.id || record.sourceRevision !== source.revision))
      throw new Error("Источник выделения изменился.");
    stagingVisualSelection = true;
    var complete, succeeded = false;
    var ingestion = ingestionQueue[target.chatId] = new Promise(function (resolve) { complete = resolve; });
    updateDrafts();
    try {
      var response = await send("stageHtmlVisualSelection", { chatId: target.chatId, artifactId: source.id,
        artifactRevision: source.revision, previewGeneration: generation, selection: record.selection,
        comment: number ? String(comment || "") : null, annotationNumber: number || 0 });
      attachment = response && (response.resource || response.Resource);
      if (!attachment) throw new Error("Не получен подготовленный элемент.");
      if (disposed || sourceConflict || state.activeHtmlArtifactId !== source.id || visualInspector.generation() !== generation)
        throw new Error("Просмотр изменился. Выберите элемент заново.");
      attachment.visualAnnotation = { selection: record.selection, comment: String(comment || ""), annotationNumber: number };
      attachment.visualSourceArtifactId = source.id; attachment.visualSourceRevision = source.revision;
      var draft = readOfficeDraft(target.chatId), items = (draft && draft.attachments || []).slice();
      var index = replacing ? items.findIndex(function (item) { return attachmentId(item) === attachmentId(replacing); }) : -1;
      if (replacing && index < 0) throw new Error("Аннотация удалена из черновика.");
      if (!replacing && items.length >= ATTACHMENT_MAX_FILES) throw new Error("В сообщении уже 10 вложений.");
      if (replacing) items[index] = attachment; else items.push(attachment);
      saveOfficeDraft(target.chatId, draft && draft.text || "", items, true, draft && draft.nextAnnotationNumber, null);
      attached = true; await RNAssistantChatDrafts.flushOffice(target.chatId);
      if (replacing) await send("discardChatResourceDraft", { chatId: target.chatId, id: attachmentId(replacing) }).catch(function () {
        log("Новый черновик сохранён; старый снимок не удалён.", "error");
      });
      succeeded = true; updateDrafts(); return attachment;
    } catch (error) {
      if (attachment && !attached) await send("discardChatResourceDraft", { chatId: target.chatId, id: attachmentId(attachment) }).catch(function () {});
      throw error;
    } finally { complete(succeeded); if (ingestionQueue[target.chatId] === ingestion) delete ingestionQueue[target.chatId]; stagingVisualSelection = false; updateDrafts(); }
  }
  visualAnnotations = RNAssistantVisualAnnotations.create({ frame: $("htmlWorkspacePreviewFrame"), inspector: visualInspector,
    stage: stageVisualSelection, context: {
      mount: $("htmlWorkspacePreview"), browser: $("htmlWorkspacePreview"), button: $("inspectHtmlWorkspaceButton"),
      statusNode: $("htmlWorkspaceInspectStatus"), source: annotationSource,
      get draftBox() { var panel = chatPanel(); return panel && panel.node("attachmentDrafts"); },
      isOpen: function () { return active && state.htmlWorkspaceMode === "preview" && !$("htmlWorkspacePreview").hidden; },
      pending: function () { var draft = readOfficeDraft(target.chatId); return draft && draft.pendingVisualAnnotation || null; },
      setPending: function (value) { RNAssistantChatDrafts.setPendingOfficeAnnotation(target.chatId, value); },
      drafts: function () { var draft = readOfficeDraft(target.chatId); return draft && draft.attachments || []; },
      messages: function () { var panel = chatPanel(); return panel ? panel.snapshot().messages || [] : []; },
      updated: updateDrafts,
      remove: async function (item) {
        await send("discardChatResourceDraft", { chatId: target.chatId, id: attachmentId(item) });
        var draft = readOfficeDraft(target.chatId);
        if (draft) saveOfficeDraft(target.chatId, draft.text, draft.attachments.filter(function (value) { return attachmentId(value) !== attachmentId(item); }), true);
        updateDrafts();
      },
      reserve: function () { return RNAssistantChatDrafts.reserveOfficeAnnotationNumber(target.chatId); },
      openSource: function () { RNAssistantDockWorkspace.openTarget(target); setHtmlWorkspaceMode("preview"); return true; },
      status: function (message) { setAttachmentStatus(target.chatId, message, "warning"); }
    } });
  var workspaceEditor = window.RNAssistantHtmlWorkspaceEditor.create({
    state: state, node: $, root: root, code: code,
    changed: renderHtmlWorkspace,
    previewVisible: function () { return active && state.htmlWorkspaceMode === "preview"; },
    model: workspaceModel,
    inspector: visualInspector,
    source: workspaceSource,
    closeResources: closePreviewResources,
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
    if (!browserTarget.capabilities.annotations) { visualInspector.deactivate(); visualAnnotations.collapseEditor(); }
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
    workspace(); ensureSelection(); renderFiles(); visualInspector.refresh();
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
  async function downloadHtmlWorkspaceExport(exportState) {
    exportState = exportState || {};
    var exportedWorkspace = exportState.workspace || {};
    var exportedFiles = prop(exportedWorkspace, "Files", "files", []) || [];
    var exportedData = prop(exportedWorkspace, "DataSources", "dataSources", []) || [];
    if (!exportedFiles.some(function (file) { return fileKind(file) === "html"; })) {
      throw new Error("HTML export checkpoint has no HTML entry file.");
    }
    function isCurrent() {
      return state.activeChatId === exportState.chatId && state.activeHtmlArtifactId === exportState.revisionArtifactId && !state.htmlWorkspaceDirty;
    }
    await workspaceSource.exportSources(exportedWorkspace, isCurrent);
    await htmlPreview.ensureVendors(exportedFiles);
    var snapshot = await window.RNAssistantHtmlResourceExport.capture(exportState.resourceExport, {
      fetch: window.fetch.bind(window), isCurrent: isCurrent
    });
    snapshot.workspace = { resourceUri: exportState.resourceUri, contentSha256: exportState.contentSha256 };
    var html = htmlPreview.build({
      activeFileId: prop(exportedWorkspace, "ActiveFileId", "activeFileId", ""),
      dataSources: exportedData,
      files: exportedFiles,
      resourceSnapshot: snapshot,
      hostBridge: false
    });
    if (!isCurrent()) throw new Error("RESOURCE_EXPORT_CANCELLED");
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
  var controller = { element: root, target: target, ready: ready, annotations: visualAnnotations, openBrowser: openBrowser,
    select: selectHtmlWorkspaceItem, remove: deleteHtmlWorkspaceTreeItem, mode: setHtmlWorkspaceMode,
    createItem: function (kind) { if (kind === "plan") return workspaceActions.createPlan(); if (kind === "data") addHtmlWorkspaceData(); else addHtmlWorkspaceFile(kind); },
    dirty: function () { return state.htmlWorkspaceDirty; }, busy: function () { return stagingVisualSelection || workspaceActions.isBusy(); },
    flushForCheckpoint: async function (value) {
      if (!state.browserTarget || value.documentId !== state.browserTarget.documentId || value.logicalId !== state.browserTarget.logicalId) return false;
      if (disposed || sourceConflict || workspaceActions.isBusy() || stagingVisualSelection) throw new Error("Сначала завершите операцию или обновите HTML-редактор.");
      if (!state.htmlWorkspaceDirty) return false;
      await workspaceActions.saveSelection();
      if (state.htmlWorkspaceDirty || sourceConflict || disposed) throw new Error("Правки редактора не сохранены. Версия проекта не создана.");
      return true;
    },
    canClose: function () {
      if (workspaceActions.isBusy() || stagingVisualSelection) { log("Дождитесь завершения операции.", "error"); return false; }
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
    deactivate: function () { active = false; visualInspector.deactivate(); visualAnnotations.collapseEditor(); },
    layout: function () { code.refresh(); },
    dispose: function () {
      if (disposed) return; disposed = true; active = false; loadGeneration++; events.abort();
      workspaceSource.release(); workspaceActions.cancelWrite(); workspaceActions.closeArtifactViewers();
      visualAnnotations.dispose(); visualInspector.dispose(); workspaceEditor.dispose();
      code.destroy(); root.remove(); updateDrafts();
    }
  };
  return controller;
  }
  window.RNAssistantHtmlWorkspaceView = { key: key, create: create };
}());
