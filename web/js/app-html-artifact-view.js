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
    var root = node("div", "browser-html-document"), toolbar = node("div", "browser-document-toolbar");
    var modes = node("div", "view-switch"), page = node("button", "button compact", "Страница"), code = node("button", "button compact", "Исходник");
    modes.setAttribute("role", "group"); modes.setAttribute("aria-label", "Представление HTML");
    page.type = code.type = "button"; modes.append(page, code);
    var picker = node("select", "rna-form-control compact"); picker.setAttribute("aria-label", "Файл проекта");
    files.forEach(function (file) { picker.appendChild(new Option(file.path, file.id)); });
    picker.value = String(project.EntryPath).toLowerCase(); if (!picker.value) picker.selectedIndex = 0;
    toolbar.append(modes, picker); root.appendChild(toolbar);
    if (project.ExternalDependencies && project.ExternalDependencies.length)
      root.appendChild(node("p", "browser-object-note", "Внешние зависимости: " + project.ExternalDependencies.join(", ")));
    var frame = node("iframe"); frame.title = target.title; frame.setAttribute("sandbox", "allow-scripts"); frame.referrerPolicy = "no-referrer";
    frame.srcdoc = RNAssistantHtmlWorkspacePreview.build({ files: files, activeFileId: String(project.EntryPath).toLowerCase(),
      dataSources: (project.Data || []).map(function (d) { return { name: d.Name }; }), resourceSnapshot: snapshot, hostBridge: false });
    var source = node("div", "browser-document-source product-viewer"); root.append(frame, source);
    var positions = Object.create(null), sourceFile = null;
    function renderSource() {
      var file = files.find(function (f) { return f.id === picker.value; }), offset = positions[file.id] || 0;
      RNAssistantViewerRegistry.mount("text", source, { text: file.content.slice(offset, offset + pageSize),
        offset: offset, totalCharacters: file.content.length, startLine: file.content.slice(0, offset).split("\n").length,
        hasPrevious: offset > 0, hasNext: offset + pageSize < file.content.length,
        onPrevious: function () { positions[file.id] = Math.max(0, offset - pageSize); renderSource(); },
        onNext: function () { positions[file.id] = offset + pageSize; renderSource(); },
        onCopy: function (text) { return copyTextResult(text); } });
      sourceFile = file.id;
    }
    function mode(isSource) {
      frame.hidden = isSource; source.hidden = !isSource; picker.hidden = !isSource;
      page.classList.toggle("active", !isSource); code.classList.toggle("active", isSource);
      page.setAttribute("aria-pressed", String(!isSource)); code.setAttribute("aria-pressed", String(isSource));
      if (isSource && sourceFile !== picker.value) renderSource();
    }
    page.onclick = function () { mode(false); }; code.onclick = function () { mode(true); }; picker.onchange = renderSource; mode(false);
    return { element: root, frame: frame, dispose: function () { RNAssistantViewerRegistry.unmount(source); frame.remove(); root.replaceChildren(); } };
  }
  function create(target) {
    return RNAssistantBrowserObjectView.create({
      label: { title: target.title, path: [target.documentTitle, target.chatTitle, target.revision ? "Версия " + target.revision : "Сохранённая версия"].filter(Boolean).join(" · ") },
      note: "Сохранённый проект · данные на момент открытия · без связи с Office. Правки и аннотации — в редакторе.",
      load: function (context) { return load(target, context); },
      actions: function (root, context) {
        var more = RNAssistantActionMenu.createTrigger("Действия с материалом"); root.appendChild(more);
        more.onclick = function () { RNAssistantActionMenu.open(more, [{ label: "Скачать оригинал", icon: "download", onSelect: function () {
          return RNAssistantArtifactDownload.save(target, { send: send, fetch: window.fetch.bind(window), signal: context.signal(), isCurrent: context.isCurrent, status: context.status });
        } }, { label: "Копировать точную ссылку", icon: "link", onSelect: function () {
          return copyTextResult(target.resourceUri).then(function () { context.status("Ссылка скопирована"); }).catch(context.error);
        } }].concat(RNAssistantArtifactCatalog.commands(target))); };
        if (target.head) RNAssistantArtifactVersions.append(root, target.head, { resourceUri: target.resourceUri, revision: target.revision }, function (revision) {
          return RNAssistantDockWorkspace.openTarget(Object.assign({}, target, { resourceUri: revision.resourceUri, revision: revision.revision,
            title: revision.title || target.title }), { referencePanel: RNAssistantMaterialView.key(target) });
        }, { chatId: target.chatId, sessionRevision: target.sessionRevision, isCurrent: context.isCurrent, send: send });
      }
    });
  }
  window.RNAssistantHtmlArtifactView = { create: create };
}());
