(function () {
  "use strict";
  // Catalog metadata is selected; editable working copies belong to addressed Dock views.
  var htmlPreview = RNAssistantHtmlWorkspacePreview, workspaceArtifacts = RNAssistantHtmlWorkspaceArtifacts;
  var workspaceTree = RNAssistantHtmlWorkspaceTree, workspaceModel = RNAssistantHtmlWorkspaceModel.create(state);
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
  var refreshLibraryHeadSelection = workspaceModel.refreshLibraryHeadSelection;
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
    if (window.RNAssistantArtifactCatalog) window.RNAssistantArtifactCatalog.sync();
    var search = $("artifactNavSearch");
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
      dependencies: htmlPreview.dependencies(files()),
      artifacts: libraryArtifacts.map(workspaceTreeArtifact),
      plans: latestPlanArtifacts().map(workspaceTreeArtifact),
      selected: state.htmlWorkspaceSelection || {},
      onSelect: selectHtmlWorkspaceItem,
      onDelete: deleteHtmlWorkspaceTreeItem
    });
  }


  function view(chatId) { return RNAssistantDockWorkspace.getView(RNAssistantHtmlWorkspaceView.key({ chatId: chatId })); }
  function target(chatId) {
    var chat = (state.chats || []).find(function (entry) { return (entry.Id || entry.id) === chatId; }) || {};
    return { view: "html-editor", chatId: chatId, documentId: chat.DocumentAuthorityId || chat.documentAuthorityId || "",
      documentKey: chatDocumentKey(chat), host: chatHost(chat),
      title: "Редактор · " + (chat.Title || chat.title || "Материалы") };
  }
  function affected(scope) {
    var editors = [];
    RNAssistantDockWorkspace.eachView("html-editor", function (editor) {
      if (scope.chatId ? editor.target.chatId === scope.chatId :
          editor.target.host === scope.host && editor.target.documentKey === scope.documentKey) editors.push(editor);
    }); return editors;
  }
  function open(chatId, options) {
    if (!chatId) return false; options = options || {};
    if (!RNAssistantDockWorkspace.isSectionVisible("artifacts") && !document.getElementById("tab-chat").classList.contains("active"))
      switchTab("artifacts");
    if (!RNAssistantDockWorkspace.openTarget(target(chatId))) return false;
    var editor = view(chatId);
    editor.ready().then(function (loaded) {
      if (!loaded) return;
      if (options.selection && !editor.select(options.selection.type, options.selection.id)) return;
      if (options.mode) editor.mode(options.mode);
      if (options.create) editor.createItem(options.create);
    });
    return true;
  }
  async function openBrowser(chatId) {
    var editor = view(chatId);
    if (editor) { if (await editor.ready()) return editor.openBrowser(); return false; }
    var placement = RNAssistantDockWorkspace.activeTarget(), generation = state.chatNavigationVersion;
    try {
      var response = await send("getHtmlWorkspace", { chatId: chatId });
      if (state.chatNavigationVersion !== generation || RNAssistantDockWorkspace.activeTarget() !== placement) return false;
      var value = RNAssistantBrowserTarget.accept(response.browserTarget, { chatId: chatId }, "HtmlWorkspace");
      if (value.state !== "Ready") throw new Error(value.message || "Сначала создайте HTML-проект.");
      return RNAssistantBrowser.open(value.displayLocator, value.ownerScope, { reuse: true, title: value.title });
    } catch (error) { RNAssistantShellLayout.notice(error.detail || error.message); return false; }
  }
  function selectHtmlWorkspaceItem(type, id) {
    state.htmlWorkspaceSelection = { type: type, id: id };
    return open(state.activeChatId, { selection: { type: type, id: id }, mode: "edit" });
  }
  function deleteHtmlWorkspaceTreeItem(type, id) {
    if (!open(state.activeChatId)) return;
    var editor = view(state.activeChatId); editor.ready().then(function (loaded) { if (loaded) editor.remove(type, id); });
  }
  function renderHtmlWorkspace() {
    if (typeof isPanelActive === "function" && !isPanelActive("artifacts")) return;
    workspace(); ensureSelection(); renderHtmlWorkspaceList();
  }
  function bindHtmlWorkspaceActions() {
    [["addPlanButton", "plan"], ["addHtmlFileButton", "html"], ["addCssFileButton", "css"],
      ["addJsFileButton", "script"], ["addHtmlDataButton", "data"]].forEach(function (item) {
      $(item[0]).addEventListener("click", function () { open(state.activeChatId, { create: item[1] }); });
    });
    $("artifactNavSearch").addEventListener("input", renderHtmlWorkspaceList);
  }
  var thumbnails = RNAssistantArtifactViewerActions.create({ state: state, send: send, log: log,
    onArtifactThumbnailChange: function (uri, value) { if (window.updateArtifactThumbnailViews) updateArtifactThumbnailViews(uri, value); } });
  window.closeArtifactViewerResources = thumbnails.closeAll;
  window.RNAssistantArtifactThumbnailRuntime = { load: thumbnails.loadArtifactImageThumbnail, state: thumbnails.artifactImageThumbnailState };
  window.renderHtmlWorkspace = renderHtmlWorkspace;
  window.bindHtmlWorkspaceActions = bindHtmlWorkspaceActions;
  window.handleResourceAuthorityChanged = function (notice) {
    RNAssistantDockWorkspace.eachView("html-editor", function (editor) { editor.authorityChanged(notice); });
    RNAssistantDockWorkspace.eachView("browser", function (view) { view.authorityChanged(notice); });
  };
  window.RNAssistantHtmlWorkspaceSurface = {
    register: function (dock) { dock.registerView("html-editor", { perObject: true, maximumInstances: 4, renderer: "onlyWhenVisible",
      accepts: function (target) { return target && target.view === "html-editor" && typeof target.chatId === "string" && !!target.chatId &&
        target.chatId.length <= 256 && typeof target.title === "string" && target.title.length <= 1024; },
      key: RNAssistantHtmlWorkspaceView.key, create: RNAssistantHtmlWorkspaceView.create, titleFor: function (target) { return target.title; } }); },
    open: open,
    openCurrent: function () { return openBrowser(state.activeChatId); },
    openArtifact: function (artifact, options) {
      if (!artifact) return false;
      var kind = artifactKind(artifact);
      if (kind === "html_workspace" && artifactId(artifact) !== state.activeHtmlArtifactId) return false;
      if (kind === "html_workspace" && (!options || options.purpose !== "edit")) return openBrowser(state.activeChatId);
      return open(state.activeChatId, { mode: options && options.purpose === "edit" ? "edit" : "preview",
        selection: kind === "html_workspace" ? null : { type: kind === "plan" ? "plan" : "artifact", id: artifactId(artifact) } });
    },
    showAnnotation: async function (item, chatId) {
      if (!open(chatId, { mode: "preview" })) return;
      var editor = view(chatId); if (await editor.ready()) editor.annotations.showSent(item);
    },
    isDirty: function (chatId) { var editor = view(chatId); return !!editor && editor.dirty(); },
    flushForCheckpoint: function (target) { var editor = view(target.chatId); return editor ? editor.flushForCheckpoint(target) : Promise.resolve(false); },
    canDelete: function (scope) {
      if (!affected(scope).some(function (editor) { return editor.dirty() || editor.busy(); })) return true;
      window.alert("Сохраните правки или закройте HTML-редактор перед удалением его чата."); return false;
    },
    closeDeleted: function (scope) { affected(scope).forEach(function (editor) { RNAssistantDockWorkspace.close(RNAssistantHtmlWorkspaceView.key(editor.target)); }); },
    annotations: function (chatId) { var editor = view(chatId); return editor && editor.annotations; },
    onSnapshot: function (snapshot) { var editor = view(snapshot.activeChatId); if (editor) editor.onSnapshot(snapshot); }
  };
}());
