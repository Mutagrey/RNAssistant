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
        var importPending = false, importAttempted = false, imported = null, importIssue = "";
        var publication = RNAssistantActionMenu.createTrigger("Сохранение проекта"); publication.classList.add("compact"); publication.innerHTML = RNAssistantIcons.render("upload"); root.appendChild(publication);
        async function createProject() {
          if (importPending || importAttempted || !context.isCurrent()) return;
          var isCurrent = context.captureCurrent();
          importPending = importAttempted = true; importIssue = ""; publication.disabled = true;
          context.status("Создание проекта из выбранного снимка…");
          try {
            var result = await send("createHtmlProjectFromArtifact", { chatId: target.chatId,
              documentId: target.documentId, resourceUri: target.resourceUri, backing: "Managed" });
            if (result.status === "Rejected") {
              importAttempted = false; importIssue = result.message || "Проект не создан.";
              context.error(new Error(importIssue));
              return;
            }
            if (result.status !== "Created" || !result.workspaceId) {
              importIssue = result.message || "Создание не подтверждено. Проверьте список проектов перед повторным созданием.";
              context.error(new Error(importIssue));
              return;
            }
            imported = result;
            context.status("Проект «" + result.name + "» создан.");
            if (isCurrent()) {
              try { await RNAssistantProjectChats.open(result.workspaceId); }
              catch (error) { context.error(new Error("Проект создан, но открыть его не удалось. " + (error.detail || error.message))); }
            }
          } catch (error) {
            importIssue = "Создание не подтверждено. Проверьте список проектов перед повторным созданием. " + (error.detail || error.message);
            context.error(new Error(importIssue));
          } finally { importPending = false; publication.disabled = false; }
        }
        publication.onclick = function () { RNAssistantActionMenu.open(publication, [{ label: "Скачать проект в файлы", status: "Архив выбранной версии с HTML, CSS, JS и данными", icon: "folder", onSelect: function () {
          return RNAssistantArtifactDownload.save(target, { send: send, fetch: window.fetch.bind(window), signal: context.signal(), isCurrent: context.isCurrent, status: context.status });
        } }, imported ? { label: "Открыть созданный проект", icon: "folder", onSelect: function () { return RNAssistantProjectChats.open(imported.workspaceId); } } :
          { label: "Создать проект из снимка", status: importIssue || (importAttempted ? "Проверьте результат в списке проектов" : "Копия исходников в приложении"),
            icon: "copy", disabled: importPending || importAttempted, onSelect: createProject }]); };
        var more = RNAssistantActionMenu.createTrigger("Действия с материалом"); more.classList.add("compact");
        more.onclick = function () { RNAssistantActionMenu.open(more, [{ label: "Копировать точную ссылку", icon: "link", onSelect: function () {
          return copyTextResult(target.resourceUri).then(function () { context.status("Ссылка скопирована"); }).catch(context.error);
        } }].concat(RNAssistantArtifactCatalog.commands(target))); };
        RNAssistantArtifactVersions.append(root, target.head, { resourceUri: target.resourceUri, revision: target.revision }, function (revision) {
          return RNAssistantDockWorkspace.openTarget(Object.assign({}, target, { resourceUri: revision.resourceUri, revision: revision.revision,
            title: revision.title || target.title }), { referencePanel: RNAssistantMaterialView.key(target) });
        }, { compact: true, savedHtml: true, chatId: target.chatId, documentId: target.documentId, isCurrent: context.isCurrent, send: send });
        root.appendChild(more);
      }
    });
  }
  window.RNAssistantHtmlArtifactView = { create: create };
}());
