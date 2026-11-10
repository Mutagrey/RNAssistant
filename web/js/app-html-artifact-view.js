(function () {
  "use strict";
  var pageSize = 32000, maximumBytes = 16 * 1024 * 1024;
  function node(tag, className, text) {
    var value = document.createElement(tag); value.className = className || ""; value.textContent = text || ""; return value;
  }
  async function load(target, context) {
    var transfer, bytes;
    try {
      transfer = await send("exportArtifact", { chatId: target.chatId, documentId: target.documentId, resourceUri: target.resourceUri, preview: true });
      if (!context.isCurrent()) return null;
      if (transfer.data && transfer.data.payload && transfer.data.payload.byteLength > maximumBytes)
        throw new Error("Проект больше лимита просмотра 16 МиБ. Экспорт доступен в меню материала.");
      bytes = await RNAssistantResourceDownload.read(transfer.data, { maxBytes: maximumBytes,
        fetch: window.fetch.bind(window), signal: context.signal, isCurrent: context.isCurrent, send: send });
    } finally {
      if (transfer && transfer.data) send("closeArtifactTransfer", { chatId: target.chatId, leaseId: transfer.data.leaseId }).catch(function () {});
    }
    if (!context.isCurrent()) return null;
    var envelope = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)), project = envelope.Project;
    if (!project || !Array.isArray(project.Files) || !project.Files.length || project.Files.length > 100)
      throw new Error("Сохранённый проект не содержит доступных файлов.");
    var files = project.Files.map(function (f) { return { id: f.Path.toLowerCase(), path: f.Path, kind: f.Kind, content: f.Content }; });
    var snapshot = await RNAssistantHtmlResourceExport.fromArtifactViews(envelope.Views, context);
    await RNAssistantHtmlWorkspacePreview.ensureVendors(files);
    if (!context.isCurrent()) return null;
    var root = node("div", "browser-html-document");
    var picker = node("select", "rna-form-control compact browser-document-file"); picker.setAttribute("aria-label", "Файл проекта");
    files.forEach(function (file) { picker.appendChild(new Option(file.path, file.id)); });
    picker.value = String(project.EntryPath).toLowerCase(); if (!picker.value) picker.selectedIndex = 0;
    root.appendChild(picker);
    var frame = node("iframe"); frame.title = target.title; frame.setAttribute("sandbox", "allow-scripts"); frame.referrerPolicy = "no-referrer";
    frame.srcdoc = RNAssistantHtmlWorkspacePreview.build({ files: files, activeFileId: String(project.EntryPath).toLowerCase(),
      dataSources: (project.Data || []).map(function (d) { return { name: d.Name }; }), resourceSnapshot: snapshot, hostBridge: false });
    var source = node("div", "browser-document-source product-viewer"); root.append(frame, source);
    var positions = Object.create(null), sourceFile = null;
    var presentation = { address: target.resourceUri, mode: "preview", supportedModes: ["preview", "source"] };
    if (project.ExternalDependencies && project.ExternalDependencies.length) presentation.details = [
      { label: "Внешние зависимости", status: project.ExternalDependencies.join(", "), icon: "external", disabled: true }];
    function renderSource() {
      var file = files.find(function (f) { return f.id === picker.value; }), offset = positions[file.id] || 0;
      RNAssistantViewerRegistry.mount("text", source, { text: file.content.slice(offset, offset + pageSize),
        code: true, path: file.path,
        offset: offset, totalCharacters: file.content.length, startLine: file.content.slice(0, offset).split("\n").length,
        hasPrevious: offset > 0, hasNext: offset + pageSize < file.content.length,
        onPrevious: function () { positions[file.id] = Math.max(0, offset - pageSize); renderSource(); },
        onNext: function () { positions[file.id] = offset + pageSize; renderSource(); },
        onCopy: function (text) { return copyTextResult(text); } });
      sourceFile = file.id;
    }
    function mode(selected) {
      var isSource = selected === "source";
      frame.hidden = isSource; source.hidden = !isSource; picker.hidden = !isSource;
      if (isSource && sourceFile !== picker.value) renderSource();
      presentation.mode = selected; return presentation;
    }
    picker.onchange = renderSource; mode("preview");
    return { element: root, frame: frame, presentation: presentation, selectMode: mode,
      dispose: function () { RNAssistantViewerRegistry.unmount(source); frame.remove(); root.replaceChildren(); } };
  }
  function create(target) {
    return RNAssistantBrowserObjectView.create({
      label: { title: target.title, address: target.resourceUri, path: [target.documentTitle, target.chatTitle, target.revision ? "Версия " + target.revision : "Сохранённая версия"].filter(Boolean).join(" · ") },
      storage: { kind: "managed" },
      details: [{ label: "Просмотр снимка", status: "Исходники и данные выбранной версии", icon: "clock", disabled: true }],
      annotationUnavailable: "Аннотации доступны в живом редакторе проекта. Здесь открыта сохранённая версия.",
      load: function (context) { return load(target, context); },
      discuss: function () { return RNAssistantConversationPanel.open({ source: "office", sessionId: target.chatId },
        { referencePanel: RNAssistantMaterialView.key(target), beside: true }); },
      actions: function (root, context) {
        var publication = RNAssistantActionMenu.createTrigger("Экспорт проекта"); publication.classList.add("compact"); publication.innerHTML = RNAssistantIcons.render("upload"); root.appendChild(publication);
        publication.onclick = function () { RNAssistantActionMenu.open(publication, [{ label: "Скачать проект в файлы", status: "Архив выбранной версии с HTML, CSS, JS и данными", icon: "folder", onSelect: function () {
          return RNAssistantArtifactDownload.save(target, { send: send, fetch: window.fetch.bind(window), signal: context.signal(), isCurrent: context.isCurrent, status: context.status });
        } }]); };
        var more = RNAssistantActionMenu.createTrigger("Действия с материалом"); more.classList.add("compact");
        more.onclick = function () { RNAssistantActionMenu.open(more, [{ label: "Копировать точную ссылку", icon: "link", onSelect: function () {
          return copyTextResult(target.resourceUri).then(function () { context.status("Ссылка скопирована"); }).catch(context.error);
        } }].concat(RNAssistantArtifactCatalog.commands(target))); };
        if (target.head) RNAssistantArtifactVersions.append(root, target.head, { resourceUri: target.resourceUri, revision: target.revision }, function (revision) {
          return RNAssistantDockWorkspace.openTarget(Object.assign({}, target, { resourceUri: revision.resourceUri, revision: revision.revision,
            title: revision.title || target.title }), { referencePanel: RNAssistantMaterialView.key(target) });
        }, { compact: true, chatId: target.chatId, sessionRevision: target.sessionRevision, isCurrent: context.isCurrent, send: send });
        root.appendChild(more);
      }
    });
  }
  window.RNAssistantHtmlArtifactView = { create: create };
}());
