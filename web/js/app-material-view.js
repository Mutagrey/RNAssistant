(function () {
  "use strict";
  var maximumInstances = 6, maximumImageBytes = 8 * 1024 * 1024, maximumTextCharacters = 512000;
  var readers = window.RNAssistantArtifactViewers;
  function node(tag, className, text) {
    var result = document.createElement(tag); result.className = className || "";
    if (text) result.textContent = text;
    return result;
  }
  function accepts(target) {
    return !!target && target.view === "material" && typeof target.chatId === "string" && !!target.chatId &&
      target.chatId.length <= 256 && typeof target.documentId === "string" && target.documentId.length <= 256 && typeof target.resourceUri === "string" &&
      /^rna:\/\//.test(target.resourceUri) && target.resourceUri.length <= 4096 &&
      typeof target.kind === "string" && typeof target.title === "string" && target.title.length <= 1024 &&
      typeof target.mimeType === "string" && target.mimeType.length <= 200;
  }
  function key(target) { return "material:" + encodeURIComponent(JSON.stringify([target.chatId, target.documentId, target.resourceUri])); }
  function artifactFor(target) { return { ResourceUri: target.resourceUri, Kind: target.kind, Title: target.title, MimeType: target.mimeType }; }
  function targetFor(artifact, context) {
    context = context || {};
    var prop = readers.prop, head = context.head || readers.libraryHead(artifact);
    var actor = (state.chats || []).find(function (chat) { return prop(chat, "Id", "id", "") === (context.chatId || state.activeChatId); });
    return { view: "material", chatId: context.chatId || state.activeChatId,
      documentId: context.documentId || prop(artifact, "DocumentId", "documentId", "") || "",
      documentTitle: context.documentTitle || prop(artifact, "DocumentTitle", "documentTitle", "") || prop(actor, "DocumentTitle", "documentTitle", ""),
      chatTitle: context.chatTitle !== undefined ? context.chatTitle : prop(actor, "Title", "title", ""), resourceUri: readers.exactArtifactUri(artifact),
      title: prop(artifact, "Title", "title", "Материал"), kind: prop(artifact, "Kind", "kind", "file"),
      mimeType: readers.artifactMimeType(artifact), byteLength: prop(artifact, "ContentByteLength", "contentByteLength", prop(artifact, "ByteLength", "byteLength", null)),
      revision: context.revision || prop(artifact, "Revision", "revision", null),
      head: head ? { artifactId: head.artifactId, resourceUri: head.resourceUri, revision: head.revision,
        historyCount: head.historyCount, resourceClass: head.resourceClass } : null,
      sessionRevision: context.sessionRevision || (state.artifactLibrary || {}).sessionRevision || null };
  }
  function create(target) {
    if (target.kind === "html_workspace") return RNAssistantHtmlArtifactView.create(target);
    var root = node("section", "material-object-view"), header = node("header", "resource-editor-head");
    root.dataset.resourceUri = target.resourceUri; root.setAttribute("aria-label", target.title);
    var titleBox = node("div", "resource-editor-titlebox"), buttons = node("div", "resource-editor-actions");
    titleBox.append(node("strong", "resource-editor-title", target.title), node("small", "resource-editor-meta",
      [target.documentTitle || "Документ исходного чата", target.chatTitle, target.revision ? "Версия " + target.revision : "Сохранённый материал"].filter(Boolean).join(" · ")));
    titleBox.title = titleBox.textContent;
    var more = RNAssistantActionMenu.createTrigger("Действия с материалом");
    buttons.appendChild(more); header.append(titleBox, buttons);
    var message = node("p", "material-object-status"); message.setAttribute("role", "status"); message.setAttribute("aria-live", "polite");
    var retry = node("button", "button compact secondary", "Повторить чтение"); retry.type = "button"; retry.hidden = true;
    var content = node("div", "material-object-content product-viewer"); content.tabIndex = 0;
    root.append(header, message, retry, content);
    var active = false, disposed = false, generation = 0, abort = new AbortController(), rendered = false, requests = new Set();
    var events = new AbortController(), savedScroll = 0;
    var localState = { activeChatId: target.chatId, artifactViewerPages: {} };
    function current(version) { return !disposed && active && generation === version && !abort.signal.aborted; }
    function status(text) { message.textContent = text || ""; message.hidden = !text; }
    function closeLate(value) {
      var data = value && (value.data || value.Data);
      if (data && data.leaseId) send("resourceDataClose", { chatId: target.chatId, workspaceId: "viewer", leaseId: data.leaseId }).catch(function () {});
    }
    async function scopedSend(command, payload) {
      if (command === "resourceDataClose") return send(command, payload);
      var version = generation;
      if (!current(version)) throw new Error("Чтение отменено.");
      payload = Object.assign({}, payload, { chatId: target.chatId });
      if (/^readArtifact/.test(command)) payload.documentId = target.documentId;
      var request = send(command, payload); if (request.requestId) requests.add(request.requestId);
      try {
        var result = await request;
        if (!current(version)) { closeLate(result); throw new Error("Чтение отменено."); }
        if (command === "readArtifactViewerPage" && readers.isHtmlFileArtifact(artifactFor(target))) {
          try {
            var resolved = RNAssistantBrowserTarget.accept(result.browserTarget, target, "HtmlResource");
            if (!resolved.capabilities.source) throw new Error("Исходник HTML недоступен.");
          } catch (error) { closeLate(result); throw error; }
        }
        return result;
      } finally { requests.delete(request.requestId); }
    }
    function unmount() {
      content.querySelectorAll(".artifact-viewer-host, .collection-page").forEach(function (element) { RNAssistantViewerRegistry.unmount(element); });
      RNAssistantViewerRegistry.unmount(content);
    }
    function textDownload(value) {
      var url = URL.createObjectURL(new Blob([value.text], { type: value.mimeType || "text/plain;charset=utf-8" }));
      var link = document.createElement("a"); link.href = url; link.download = value.title || target.title; link.click();
      setTimeout(function () { URL.revokeObjectURL(url); }, 0); return true;
    }
    var actions = RNAssistantArtifactViewerActions.create({ state: localState, send: scopedSend,
      maximumBinaryBytes: readers.artifactViewerKind(artifactFor(target)) === "image" ? maximumImageBytes : 10 * 1024 * 1024,
      maximumTextCharacters: maximumTextCharacters, maximumTextPages: 16,
      fetch: function (url, options) { return window.fetch(url, Object.assign({}, options, { signal: options.signal || abort.signal })); },
      render: render, log: function (text) { if (active && !disposed) status(text); }, downloadArtifactText: textDownload });
    function render() {
      if (!active || disposed) { rendered = false; return; }
      var artifact = artifactFor(target), format = readers.artifactViewerKind(artifact);
      if (format === "image" && Number(target.byteLength) > maximumImageBytes) {
        status("Изображение больше лимита просмотра 8 МБ. Оригинал можно скачать через меню."); retry.hidden = true; return;
      }
      savedScroll = content.scrollTop;
      unmount(); content.dataset.productViewer = format || "text";
      var value = actions.artifactViewerState(target.resourceUri);
      if (value && value.status === "error") {
        status(value.message || "Не удалось прочитать материал."); retry.hidden = false; rendered = true; return;
      }
      retry.hidden = true;
      status(value && value.status === "ready" ? "" : "Чтение сохранённого материала…");
      if (!readers.appendTypedArtifactViewer(content, artifact, actions)) {
        status("Просмотр этого формата пока недоступен. Оригинал можно скачать через меню."); rendered = true; return;
      }
      if (value && value.pages && value.pages[0] && value.pages[0].offset > 0)
        status("В памяти последние 16 страниц. Чтобы вернуться к началу, выберите «Повторить чтение» в меню.");
      rendered = !!value && value.status === "ready";
      content.scrollTop = savedScroll;
    }
    function stopReads() {
      generation++; abort.abort(); requests.forEach(function (id) { cancelBridgeRequest(id).catch(function () {}); }); requests.clear();
      if (actions.hasPending()) { actions.closeAll(); rendered = false; }
    }
    function reload() {
      stopReads(); actions.closeAll(); rendered = false; savedScroll = 0; content.scrollTop = 0;
      abort = new AbortController(); generation++; render();
    }
    function download() {
      var version = generation;
      return RNAssistantArtifactDownload.save(target, { send: send, fetch: window.fetch.bind(window), signal: abort.signal,
        isCurrent: function () { return current(version); }, status: status });
    }
    more.addEventListener("click", function () { RNAssistantActionMenu.open(more, [
      { label: "Скачать оригинал", icon: "download", onSelect: download },
      { label: "Копировать точную ссылку", icon: "link", onSelect: function () { copyTextResult(target.resourceUri).then(function () { status("Ссылка скопирована"); }).catch(function () { status("Не удалось скопировать ссылку"); }); } },
      { label: "Повторить чтение", icon: "refresh", onSelect: reload }
    ].concat(window.RNAssistantArtifactCatalog.commands(target))); }, { signal: events.signal });
    retry.addEventListener("click", reload, { signal: events.signal });
    if (target.head && window.RNAssistantArtifactVersions) RNAssistantArtifactVersions.append(buttons, target.head,
      { resourceUri: target.resourceUri, revision: target.revision || target.head.revision }, function (revision) {
        return RNAssistantDockWorkspace.openTarget(Object.assign({}, target, { resourceUri: revision.resourceUri,
          revision: revision.revision, title: revision.title || target.title }), { referencePanel: key(target) });
      }, { chatId: target.chatId, sessionRevision: target.sessionRevision, isCurrent: function () { return active && !disposed; }, send: scopedSend });
    return { element: root,
      activate: function () { if (disposed || active) return; active = true; abort = new AbortController(); generation++; if (!rendered) render(); },
      deactivate: function () { if (!active) return; active = false; stopReads(); RNAssistantActionMenu.closeWithin(root); },
      dispose: function () { if (disposed) return; active = false; disposed = true; stopReads(); actions.closeAll(); events.abort(); RNAssistantActionMenu.closeWithin(root); unmount(); root.replaceChildren(); }
    };
  }
  function register(dock) {
    dock.registerView("materials", { title: "Материалы", icon: "files", section: "artifacts", placement: "within",
      layout: function () { return document.getElementById("htmlWorkspaceLayout"); },
      element: function () { return document.getElementById("artifactCatalog"); },
      prepare: function () { RNAssistantArtifactCatalog.show(); renderHtmlWorkspace(); },
      onVisible: function (visible) { if (visible) renderHtmlWorkspace(); } });
    dock.registerView("material", { perObject: true, accepts: accepts, key: key, create: create,
      maximumInstances: maximumInstances, renderer: function (target) { return target.kind === "html_workspace" ? "onlyWhenVisible" : "always"; }, titleFor: function (target) { return target.title + (target.kind === "html_workspace" && target.revision ? " · v" + target.revision : ""); } });
  }
  window.RNAssistantMaterialView = { register: register, target: targetFor, accepts: accepts, key: key,
    openArtifact: function (artifact, context) {
      if (!readers.exactArtifactUri(artifact)) return false;
      var target = targetFor(artifact, context);
      if (!accepts(target)) return false;
      if (!RNAssistantDockWorkspace.sectionView(document.querySelector(".panel.active").id.slice(4))) switchTab("chat", { preserveChatOwner: true });
      return RNAssistantDockWorkspace.openTarget(target, { referencePanel: context && context.referencePanel || "conversation" });
    } };
}());
