(function () {
  "use strict";
  if (!document.getElementById("artifactCatalog")) return;
  var generation = 0, previewGeneration = 0, pending = false, response = null, next = null, chat = null, previewUrl = null;
  var selected = null, selectedRevision = null, collapsed = {}, searchTimer, loading = false;
  function el(id) { return document.getElementById("artifactCatalog" + id); }
  function node(tag, text, className) {
    var result = document.createElement(tag); result.textContent = text || "";
    if (className) result.className = className;
    return result;
  }
  function status(text) { el("Status").textContent = text || ""; }
  function icon(kind) { return ({ html_workspace: "html", plan_document: "plan", attachment: "file" })[kind] || kind; }
  function current(id, nav) { return id === state.activeChatId && nav === state.chatNavigationVersion; }
  function operationId() { return Array.from(crypto.getRandomValues(new Uint8Array(16)), function (n) { return n.toString(16).padStart(2, "0"); }).join(""); }
  function render() { if (typeof renderHtmlWorkspace === "function") renderHtmlWorkspace(); }
  function clearPreview() {
    if (window.RNAssistantViewerRegistry) window.RNAssistantViewerRegistry.unmount(el("PreviewBody"));
    previewGeneration++; el("PreviewBody").replaceChildren(); el("Preview").hidden = true;
    $("htmlWorkspaceEditor").hidden = false;
    if (previewUrl) URL.revokeObjectURL(previewUrl); previewUrl = null;
  }
  function show(scope) {
    if (scope) el("Scope").value = scope;
    load();
  }
  function editor(artifact) {
    selectedRevision = null;
    clearPreview();
    if (response) {
      var selection = state.htmlWorkspaceSelection || {};
      var id = selection.type === "file" || selection.type === "data" ? state.activeHtmlArtifactId : selection.id;
      var uri = artifact && (artifact.ResourceUri || artifact.resourceUri);
      selected = response.items.find(function (item) {
        var local = localArtifact(item);
        return uri ? item.resourceUri === uri : local && (local.Id || local.id) === id;
      }) || null;
    }
    actions();
  }
  function sync() {
    if (chat !== state.activeChatId) {
      chat = state.activeChatId; generation++; response = null; selected = null; selectedRevision = null; next = null;
      clearPreview(); actions();
      if (typeof isPanelActive === "function" && isPanelActive("artifacts")) load();
    } else if (response && !loading && !pending && state.chatProjectionRevisions &&
        state.chatProjectionRevisions[chat] > response.sessionRevision &&
        typeof isPanelActive === "function" && isPanelActive("artifacts")) {
      load();
    }
  }
  async function load(cursor) {
    var id = state.activeChatId, nav = state.chatNavigationVersion, version = ++generation;
    if (!id) { response = null; selected = null; selectedRevision = null; clearPreview(); actions(); status("Откройте или создайте чат документа."); render(); return; }
    chat = id; loading = true; status("Загрузка…"); el("More").hidden = true;
    el("Document").hidden = el("Scope").value !== "all";
    try {
      var result = await send("listArtifactCatalog", { chatId: id, scope: el("Scope").value, query: $("htmlWorkspaceSearchInput").value,
        kind: el("Kind").value, documentId: el("Scope").value === "all" ? el("Document").value : "", cursor: cursor || null });
      if (!current(id, nav) || version !== generation) return;
      if (result.chatId !== id) throw new Error("Получено дерево другого чата.");
      if (cursor && response) result.items = response.items.concat(result.items);
      response = result; next = result.nextCursor;
      var selectedDocument = el("Document").value;
      el("Document").replaceChildren(new Option("Все документы", ""));
      result.documents.forEach(function (d) { el("Document").appendChild(new Option(d.title, d.id)); });
      el("Document").value = selectedDocument;
      if (selected) {
        selected = result.items.find(function (item) { return item.resourceUri === selected.resourceUri; }) || null;
        if (!selected) selectedRevision = null;
        if (!selected && !state.htmlWorkspaceDirty) {
          if (el("Preview").hidden) editor();
          if (!selected) { clearPreview(); emptyPreview(); }
        }
      }
      el("More").hidden = !next;
      status(result.items.length ? "" : "Артефакты не найдены.");
      if (!selected && el("Preview").hidden) editor();
      actions(); render();
    } catch (error) { if (current(id, nav) && version === generation) status(error.detail || error.message); }
    finally { if (version === generation) loading = false; }
  }
  function emptyPreview() {
    $("htmlWorkspaceEditor").hidden = true; el("Preview").hidden = false;
    el("PreviewTitle").textContent = "Выберите артефакт";
    el("PreviewBody").appendChild(node("p", "Выберите файл или проект в дереве слева."));
  }
  function canMutate() {
    if (pending) return false;
    if (state.htmlWorkspaceDirty) { status("Сохраните или отмените правки перед изменением артефактов."); return false; }
    return true;
  }
  async function mutate(command, args, button) {
    if (!canMutate()) return;
    var id = state.activeChatId, nav = state.chatNavigationVersion, edit = state.htmlWorkspaceEditVersion || 0;
    pending = true; actions(); if (button) button.disabled = true;
    try {
      var result = await send(command, args);
      if (!current(id, nav) || edit !== (state.htmlWorkspaceEditVersion || 0)) return;
      applyChatStateForChat(result, id); await load(); return true;
    } catch (error) {
      if (current(id, nav)) { await load(); status(error.detail || error.message); }
    }
    finally { pending = false; actions(); }
  }
  async function changeLink(id, revision, uri, detached, button) {
    if (id !== state.activeChatId) return;
    return mutate("changeArtifactLink", { chatId: id, expectedSessionRevision: revision, resourceUri: uri, detached: detached }, button);
  }
  async function copyItem(item, button, listed) {
    var copied = await mutate("copyArtifact", { chatId: listed.chatId, expectedSessionRevision: listed.sessionRevision,
      documentId: item.documentId, resourceUri: item.resourceUri, operationId: operationId() }, button);
    if (copied) { editor(); render(); }
    return copied;
  }
  function remove(item, listed, button) {
    if (!canMutate()) return;
    if (!window.confirm("Удалить «" + item.title + "» из документа «" + item.documentTitle + "»?\n\nАртефакт исчезнет из библиотеки и всех чатов этого документа. Его нельзя будет подключить или редактировать. Ссылки в истории и сохранённые старые версии останутся.")) return;
    return mutate("deleteDocumentArtifact", { chatId: listed.chatId, expectedSessionRevision: listed.sessionRevision,
      resourceUri: item.resourceUri, confirmed: true }, button);
  }
  function actions() {
    var root = el("Actions"); root.replaceChildren(); root.hidden = !selected;
    if (!selected || !response) return;
    var item = selected, listed = response, id = state.activeChatId, nav = state.chatNavigationVersion;
    function action(parent, label, fn, disabled, className) {
      var b = node("button", label, className); b.type = "button"; b.disabled = pending || !!disabled;
      b.addEventListener("click", function () { if (!b.disabled && current(id, nav)) fn(b); }); parent.appendChild(b);
    }
    var artifact = localArtifact(item);
    var head = artifact && window.RNAssistantArtifactVisuals && window.RNAssistantArtifactVisuals.libraryHead(artifact);
    if (window.RNAssistantArtifactVersions) window.RNAssistantArtifactVersions.append(root, head, selectedRevision, function (revision) {
      if (selected !== item || response !== listed || !current(id, nav) || !canMutate()) return false;
      if (revision.resourceUri === item.resourceUri) { select(item.resourceUri); return true; }
      selectedRevision = revision;
      actions();
      preview(Object.assign({}, item, { resourceUri: revision.resourceUri, title: revision.title || item.title }), revision);
      render();
      return true;
    });
    if (selectedRevision) {
      root.appendChild(node("span", "Только просмотр", "artifact-version-readonly"));
      action(root, "К текущей версии", function () { select(item.resourceUri); });
      if (item.canTransfer) action(root, "Скачать v" + selectedRevision.revision, function () {
        download(Object.assign({}, item, { resourceUri: selectedRevision.resourceUri }));
      });
      return;
    }
    if (item.documentId === listed.documentId) {
      action(root, item.linked ? "Убрать из чата" : "Подключить к чату", async function (b) {
        if (await changeLink(id, listed.sessionRevision, item.resourceUri, item.linked, b)) {
          if (selected && !item.linked) select(selected.resourceUri);
          else if (selected) preview(selected);
        }
      }, !item.linked && item.availabilityIssue);
    }
    var menu = node("details", "", "html-workspace-menu artifact-action-menu");
    menu.appendChild(node("summary", "Действия"));
    var options = node("div", "", "html-workspace-menu-items"); menu.appendChild(options);
    if (item.canTransfer) {
      action(options, "Независимая копия", function (b) { copyItem(item, b, listed); });
      action(options, item.kind === "html_workspace" ? "Экспорт проекта" : "Скачать", function () { download(item); });
    }
    if (item.canDelete) action(options, "Удалить из документа…", function (b) { remove(item, listed, b); }, false, "danger-soft");
    if (options.children.length) root.appendChild(menu);
  }
  function localArtifact(item) {
    return (state.artifacts || []).find(function (a) { return (a.ResourceUri || a.resourceUri) === item.resourceUri; });
  }
  async function select(uri) {
    if (!canMutate() || !response) return false;
    var item = response.items.find(function (value) { return value.resourceUri === uri; });
    if (!item) return false;
    selected = item; selectedRevision = null; clearPreview(); actions();
    if (item.availabilityIssue) {
      $("htmlWorkspaceEditor").hidden = true; el("Preview").hidden = false; el("PreviewTitle").textContent = item.title;
      el("PreviewBody").appendChild(node("p", "Артефакт недоступен. Можно убрать ссылку из чата или удалить артефакт из документа.")); render(); return true;
    }
    var id = state.activeChatId, nav = state.chatNavigationVersion;
    if (item.linked && item.documentId === response.documentId) {
      if ((item.kind === "html_workspace" || item.kind === "plan_document") && !item.selected) {
        if (!await changeLink(id, response.sessionRevision, uri, false) || !current(id, nav)) return false;
        item = selected || item;
      }
      var artifact = localArtifact(item);
      if (artifact && typeof window.openArtifactResource === "function") {
        state.htmlWorkspaceMode = "preview"; window.openArtifactResource(artifact); render(); return true;
      }
    }
    preview(item); render(); return true;
  }
  function tree(nodes, htmlChildren, options) {
    if (!response) return null;
    var groups = {}, byId = {}, chatGroup = null, documentGroup = null, otherGroup = null;
    function makeGroup(key, title) {
      return { key: key, groupKey: key, title: title, iconKind: "folder", expanded: !collapsed[key], children: [] };
    }
    function owner(item) {
      if (item.linked) return chatGroup || (chatGroup = makeGroup("catalog-owner::chat", "Этот чат"));
      if (item.documentId && item.documentId !== response.documentId) {
        if (!otherGroup) otherGroup = makeGroup("catalog-owner::all", "Все документы");
        var documentKey = "catalog-document::" + item.documentId;
        if (!groups[documentKey]) {
          groups[documentKey] = makeGroup(documentKey, item.documentTitle || item.documentId);
          otherGroup.children.push(groups[documentKey]);
        }
        return groups[documentKey];
      }
      return documentGroup || (documentGroup = makeGroup("catalog-owner::document", "В документе"));
    }
    response.items.forEach(function (item) {
      var artifact = localArtifact(item), artifactId = artifact && (artifact.Id || artifact.id);
      if (artifactId) byId[artifactId] = item.resourceUri;
      var entry = { key: "catalog::" + item.resourceUri, itemType: "artifact", itemId: item.resourceUri,
        title: item.title, iconKind: icon(item.kind), meta: item.availabilityIssue ? "Недоступен" : (item.linked ? "В чате" : ""),
        tooltip: item.title + " · " + item.documentTitle,
        expanded: !collapsed["catalog::" + item.resourceUri] };
      if (item.kind === "html_workspace" && item.documentId === response.documentId && item.selected) {
        entry.children = htmlChildren; entry.groupKey = "catalog::" + item.resourceUri;
      }
      owner(item).children.push(entry);
    });
    // Preserve chat-only generated/system artifacts and a new, unsaved workspace.
    if (el("Scope").value === "chat") nodes.forEach(function (group) {
      if (group.groupKey === "artifacts:html") {
        if (!response.items.some(function (item) { return item.kind === "html_workspace" && item.selected; })) {
          group.title = "Новый HTML-проект";
          (chatGroup || (chatGroup = makeGroup("catalog-owner::chat", "Этот чат"))).children.push(group);
        }
      } else {
        var children = (group.children || []).filter(function (child) { return !byId[child.itemId]; });
        if (children.length) {
          var target = chatGroup || (chatGroup = makeGroup("catalog-owner::chat", "Этот чат"));
          target.children.push.apply(target.children, children);
        }
      }
    });
    var roots = [chatGroup, documentGroup, otherGroup].filter(Boolean);
    var selection = options.selected || {}, selectedKey = selected ? "catalog::" + selected.resourceUri : null;
    if (el("Preview").hidden && selection.id) {
      selectedKey = byId[selection.id] ? "catalog::" + byId[selection.id] : null;
    }
    return { nodes: roots, selectedKey: selectedKey };
  }
  async function read(item, preview, id, nav, isCurrent) {
    var transfer;
    try {
      transfer = await send("exportArtifact", { chatId: id, documentId: item.documentId, resourceUri: item.resourceUri, preview: preview });
      var bytes = await window.RNAssistantResourceDownload.read(transfer.data, { maxBytes: 20 * 1024 * 1024,
        fetch: window.fetch.bind(window), isCurrent: isCurrent || function () { return current(id, nav); } });
      return { transfer: transfer, bytes: bytes };
    } finally {
      if (transfer && transfer.data) await send("closeArtifactTransfer", { chatId: id, leaseId: transfer.data.leaseId }).catch(function () {});
    }
  }
  async function download(item) {
    var id = state.activeChatId, nav = state.chatNavigationVersion;
    try {
      status("Подготовка файла…"); var result = await read(item, false, id, nav);
      if (!current(id, nav)) return;
      var url = URL.createObjectURL(new Blob([result.bytes], { type: result.transfer.contentType }));
      var a = document.createElement("a"); a.href = url; a.download = result.transfer.fileName; a.click();
      setTimeout(function () { URL.revokeObjectURL(url); }, 1000); status("Файл подготовлен для скачивания.");
    } catch (error) { if (current(id, nav)) status(error.detail || error.message); }
  }
  async function previewSnapshot(views) {
    var snapshot = { version: 2, resources: [], parts: [], generations: {} };
    for (var view of views) {
      var reference = view.Reference;
      var resource = { name: view.Name, descriptor: { reference: reference, title: view.Name }, view: view.View,
        path: view.Path, maxBatchItems: 32000, maxBatchBytes: 8 * 1024 * 1024, parts: [] };
      var total = view.Rows ? view.Rows.length : view.Text.length;
      var batchSize = view.Rows ? 500 : 32000;
      for (var offset = 0; offset < total || offset === 0;) {
        var end = Math.min(offset + batchSize, total);
        var batch = { resource: reference, view: view.View, offset: offset, nextOffset: end, done: end === total,
          coverage: { kind: view.Rows ? "record-range" : "character-range", start: offset, end: end, path: view.Path, fields: [] } };
        if (view.Rows) { batch.rows = view.Rows.slice(offset, end); batch.columns = view.Columns; }
        else batch.text = view.Text.slice(offset, end);
        var text = JSON.stringify(batch).replace(/</g, "\\u003c");
        var hash = Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text))), function (n) { return n.toString(16).padStart(2, "0"); }).join("");
        var part = { id: "rn-export-part-" + snapshot.parts.length, offset: offset, nextOffset: end,
          done: end === total, byteLength: new TextEncoder().encode(text).length, sha256: hash };
        snapshot.parts.push({ id: part.id, text: text }); resource.parts.push(part);
        if (end === total) break;
        offset = end;
      }
      snapshot.resources.push(resource);
    }
    return snapshot;
  }
  async function preview(item, revision) {
    var id = state.activeChatId, nav = state.chatNavigationVersion, version;
    var previousPicker = revision && el("PreviewBody").querySelector('select[aria-label="Файл проекта"]');
    var previousSource = revision && el("PreviewBody").querySelector(".artifact-catalog-source");
    var previousPath = previousPicker && previousPicker.value;
    var showSource = !!revision && (previousSource ? !previousSource.hidden : state.htmlWorkspaceMode === "edit");
    clearPreview(); version = previewGeneration;
    $("htmlWorkspaceEditor").hidden = true; el("Preview").hidden = false;
    var title = item.title + (revision ? " · v" + revision.revision : "");
    el("PreviewTitle").textContent = title; status("Загрузка предпросмотра…");
    try {
      var result = await read(item, true, id, nav, function () { return current(id, nav) && version === previewGeneration; });
      if (!current(id, nav) || version !== previewGeneration) return;
      el("PreviewTitle").textContent = title; el("Preview").hidden = false;
      var mime = result.transfer.contentType || "";
      if (item.kind === "html_workspace") {
        var envelope = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(result.bytes));
        var project = envelope.Project;
        var files = project.Files.map(function (f) { return { id: f.Path.toLowerCase(), path: f.Path, kind: f.Kind, content: f.Content }; });
        var picker = document.createElement("select"); picker.setAttribute("aria-label", "Файл проекта");
        files.forEach(function (f) { picker.appendChild(new Option(f.path, f.id)); });
        var pre = node("pre", "", "artifact-catalog-source");
        function source() { pre.textContent = files.find(function (f) { return f.id === picker.value; }).content; }
        var selection = state.htmlWorkspaceSelection || {};
        var currentFile = revision && selection.type === "file" && ((state.htmlWorkspace || {}).files || [])
          .find(function (f) { return (f.id || f.Id) === selection.id; });
        var path = previousPath || currentFile && (currentFile.path || currentFile.Path);
        var selectedFile = path && files.find(function (f) { return f.path.toLowerCase() === path.toLowerCase(); });
        picker.addEventListener("change", source); picker.value = (selectedFile || files[0]).id; source();
        if (path && !selectedFile) el("PreviewBody").appendChild(node("p", "Файл «" + path + "» отсутствует в этой версии проекта."));

        if (project.ExternalDependencies.length) el("PreviewBody").appendChild(node("p", "Внешние зависимости: " + project.ExternalDependencies.join(", ")));
        // Source inspection is always available; use the established sandbox renderer for HTML.
        {
          var snapshot = await previewSnapshot(envelope.Views);
          await window.RNAssistantHtmlWorkspacePreview.ensureVendors(files);
          if (!current(id, nav) || version !== previewGeneration) return;
          var frame = document.createElement("iframe"); frame.title = item.title; frame.setAttribute("sandbox", "allow-scripts");
          frame.srcdoc = window.RNAssistantHtmlWorkspacePreview.build({ files: files, activeFileId: project.EntryPath.toLowerCase(), dataSources: project.Data.map(function (d) { return { name: d.Name }; }), resourceSnapshot: snapshot, hostBridge: false });
          el("PreviewBody").appendChild(frame);
        }
        var modes = node("div", "", "mode-toggle");
        var viewButton = node("button", "Просмотр", "mode-button active"), sourceButton = node("button", "Код", "mode-button");
        function mode(code) {
          frame.hidden = code; picker.hidden = !code; pre.hidden = !code;
          viewButton.classList.toggle("active", !code); sourceButton.classList.toggle("active", code);
        }
        viewButton.addEventListener("click", function () { mode(false); }); sourceButton.addEventListener("click", function () { mode(true); });
        modes.appendChild(viewButton); modes.appendChild(sourceButton);
        el("PreviewBody").insertBefore(modes, el("PreviewBody").firstChild);
        el("PreviewBody").appendChild(picker); el("PreviewBody").appendChild(pre);
        mode(showSource);
      } else if (/^image\//.test(mime)) {
        previewUrl = URL.createObjectURL(new Blob([result.bytes], { type: mime }));
        var image = document.createElement("img"); image.src = previewUrl; image.alt = item.title; el("PreviewBody").appendChild(image);
      } else if (/text|json|javascript|xml/.test(mime)) {
        var text = new TextDecoder("utf-8", { fatal: true }).decode(result.bytes);
        var kind = item.kind === "markdown" || item.kind === "plan_document" || /markdown/.test(mime) ? "markdown" : /json/.test(mime) ? "json" : "text";
        window.RNAssistantViewerRegistry.mount(kind, el("PreviewBody"), { text: text, pageText: text, fullText: text, complete: true });
      } else el("PreviewBody").appendChild(node("p", "Файл: " + result.transfer.fileName + " · " + result.bytes.length + " байт. Используйте «Скачать» для открытия."));
      status("");
    } catch (error) { if (current(id, nav) && version === previewGeneration) status(error.detail || error.message); }
  }
  async function importFile(file) {
    if (!file || !canMutate()) return;
    if (file.size > 20 * 1024 * 1024) { status("Максимальный размер — 20 МиБ."); return; }
    var id = state.activeChatId, nav = state.chatNavigationVersion, lease;
    pending = true; actions();
    try {
      var catalogState = await send("listArtifactCatalog", { chatId: id, scope: "chat" });
      if (!current(id, nav)) return;
      status("Загрузка файла…");
      lease = await send("beginArtifactImport", { chatId: id, fileName: file.name, contentType: file.type || "application/octet-stream", byteLength: file.size });
      await window.RNAssistantResourceUpload.write(lease, file, { maxBytes: 20 * 1024 * 1024, isCurrent: function () { return current(id, nav); } });
      if (!current(id, nav) || state.htmlWorkspaceDirty) return;
      var edit = state.htmlWorkspaceEditVersion || 0;
      var result = await send("importArtifact", { chatId: id, expectedSessionRevision: catalogState.sessionRevision, uploadLeaseId: lease.leaseId, operationId: operationId() });
      if (!current(id, nav) || edit !== (state.htmlWorkspaceEditVersion || 0)) return;
      applyChatStateForChat(result, id); editor(); show("chat");
    } catch (error) { if (current(id, nav)) status(error.detail || error.message); }
    finally { pending = false; actions(); if (lease) await send("closeArtifactTransfer", { chatId: id, leaseId: lease.leaseId }).catch(function () {}); }
  }
  ["Scope", "Kind", "Document"].forEach(function (name) { el(name).addEventListener("change", function () { show(); }); });
  $("htmlWorkspaceSearchInput").addEventListener("input", function () { clearTimeout(searchTimer); searchTimer = setTimeout(function () { show(); }, 200); });
  el("Refresh").addEventListener("click", function () { show(); });
  el("More").addEventListener("click", function () { load(next); });
  el("Import").addEventListener("click", function () { if (canMutate()) el("File").click(); });
  el("File").addEventListener("change", function () { var file = this.files[0]; this.value = ""; importFile(file); });
  window.RNAssistantArtifactCatalog = { show: show, editor: editor, sync: sync, changeLink: changeLink,
    tree: tree, select: select, canSelect: canMutate, remove: function (uri) {
      var item = response && response.items.find(function (item) { return item.resourceUri === uri; });
      if (!item || !item.canDelete) { status("Обновите дерево и выберите актуальную версию артефакта для удаления."); return false; }
      return remove(item, response);
    }, toggle: function (key, expanded) { collapsed[key] = !expanded; } };
}());
