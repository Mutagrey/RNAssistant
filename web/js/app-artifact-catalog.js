(function () {
  "use strict";
  var catalog = document.getElementById("artifactCatalog");
  if (!catalog) return;
  var generation = 0, pending = false, response = null, next = null, chat = null, previewUrl = null;
  function el(id) { return document.getElementById("artifactCatalog" + id); }
  function node(tag, text, className) {
    var result = document.createElement(tag); result.textContent = text || "";
    if (className) result.className = className;
    return result;
  }
  function status(text) { el("Status").textContent = text; }
  function kindLabel(kind) { return ({ html_workspace: "HTML-проект", markdown: "Markdown", file: "Файл", attachment: "Файл", image: "Изображение", plan_document: "План", chart: "График" })[kind] || kind; }
  function current(id, nav) { return id === state.activeChatId && nav === state.chatNavigationVersion; }
  function operationId() { return Array.from(crypto.getRandomValues(new Uint8Array(16)), function (n) { return n.toString(16).padStart(2, "0"); }).join(""); }
  function clearPreview() {
    el("PreviewBody").replaceChildren(); el("Preview").hidden = true; el("List").hidden = false;
    if (previewUrl) URL.revokeObjectURL(previewUrl); previewUrl = null;
  }
  function show(scope) {
    catalog.hidden = false; $("htmlWorkspaceLayout").classList.add("hidden");
    if (scope) el("Scope").value = scope;
    clearPreview(); load();
  }
  function editor() { generation++; clearPreview(); catalog.hidden = true; $("htmlWorkspaceLayout").classList.remove("hidden"); }
  function sync() {
    if (chat === state.activeChatId) return;
    chat = state.activeChatId; generation++; response = null; clearPreview(); el("List").replaceChildren();
    if (!catalog.hidden) load();
  }
  async function load(cursor) {
    var id = state.activeChatId, nav = state.chatNavigationVersion, version = ++generation;
    if (!id) { status("Откройте или создайте чат документа."); return; }
    chat = id; status("Загрузка…"); el("More").hidden = true;
    el("Document").hidden = el("Scope").value !== "all";
    try {
      var result = await send("listArtifactCatalog", { chatId: id, scope: el("Scope").value, query: el("Query").value,
        kind: el("Kind").value, documentId: el("Scope").value === "all" ? el("Document").value : "", cursor: cursor || null });
      if (!current(id, nav) || version !== generation) return;
      if (result.chatId !== id) throw new Error("Получен каталог другого чата.");
      response = result; next = result.nextCursor;
      var selectedDocument = el("Document").value;
      el("Document").replaceChildren(new Option("Все документы", ""));
      result.documents.forEach(function (d) { el("Document").appendChild(new Option(d.title, d.id)); });
      el("Document").value = selectedDocument;
      el("List").replaceChildren();
      result.items.forEach(function (item) {
        var row = node("article", "", "artifact-catalog-row"), copy = node("div", "", "artifact-catalog-copy");
        copy.appendChild(node("strong", item.title));
        copy.appendChild(node("span", item.documentTitle + " · " + kindLabel(item.kind) + " · " + new Date(item.updatedUtc).toLocaleString() +
          (item.linked ? " · В чате" : "") + (item.availabilityIssue ? " · Недоступен" : "")));
        row.appendChild(copy);
        var actions = node("div", "", "artifact-catalog-actions");
        function action(label, fn, disabled) { var b = node("button", label); b.type = "button"; b.disabled = !!disabled; b.addEventListener("click", function () { if (current(id, nav)) fn(b); }); actions.appendChild(b); }
        action("Просмотр", function () { preview(item); }, item.availabilityIssue);
        if (item.documentId === result.documentId) {
          action(item.linked ? "Убрать из чата" : "Подключить к чату", function (b) { changeLink(id, result.sessionRevision, item.resourceUri, item.linked, b); }, !item.linked && item.availabilityIssue);
          if (item.linked) action("Открыть в редакторе", async function (b) {
            if ((item.kind === "html_workspace" || item.kind === "plan_document") && !item.selected &&
                !await changeLink(id, result.sessionRevision, item.resourceUri, false, b)) return;
            if (!current(id, nav)) return;
            var artifacts = (state.artifacts || []).concat(window.artifactResourceHeads());
            var artifact = artifacts.find(function (a) { return (a.Id || a.id) === decodeURIComponent(item.resourceUri.split("/artifact/")[1].split("/")[0]); });
            if (artifact) { editor(); window.openArtifactResource(artifact); }
            else await preview(item);
          }, item.availabilityIssue);
        }
        if (item.canTransfer) {
          action("Независимая копия", function (b) { copyItem(item, b, result); });
          action(item.kind === "html_workspace" ? "Экспорт проекта" : "Скачать", function () { download(item); });
        }
        row.appendChild(actions); el("List").appendChild(row);
      });
      el("More").hidden = !next;
      status(result.items.length ? "Подключение внутри документа использует общий оригинал. Копия независима." : "Артефакты не найдены.");
    } catch (error) { if (current(id, nav) && version === generation) status(error.detail || error.message); }
  }
  function canMutate() {
    if (pending) return false;
    if (state.htmlWorkspaceDirty) { status("Сохраните или отмените правки в редакторе перед изменением артефактов."); return false; }
    return true;
  }
  async function mutate(command, args, button) {
    if (!canMutate()) return;
    var id = state.activeChatId, nav = state.chatNavigationVersion, edit = state.htmlWorkspaceEditVersion || 0;
    pending = true; if (button) button.disabled = true;
    try {
      var result = await send(command, args);
      if (!current(id, nav) || edit !== (state.htmlWorkspaceEditVersion || 0)) return;
      applyChatStateForChat(result, id); clearPreview(); await load(); return true;
    } catch (error) { if (current(id, nav)) status(error.detail || error.message); }
    finally { pending = false; if (button) button.disabled = false; }
  }
  async function changeLink(id, revision, uri, detached, button) {
    if (id !== state.activeChatId) return;
    return mutate("changeArtifactLink", { chatId: id, expectedSessionRevision: revision, resourceUri: uri, detached: detached }, button);
  }
  function copyItem(item, button, listed) {
    return mutate("copyArtifact", { chatId: listed.chatId, expectedSessionRevision: listed.sessionRevision,
      documentId: item.documentId, resourceUri: item.resourceUri, operationId: operationId() }, button);
  }
  async function read(item, preview, id, nav) {
    var transfer;
    try {
      transfer = await send("exportArtifact", { chatId: id, documentId: item.documentId, resourceUri: item.resourceUri, preview: preview });
      var bytes = await window.RNAssistantResourceDownload.read(transfer.data, { maxBytes: 20 * 1024 * 1024,
        fetch: window.fetch.bind(window), isCurrent: function () { return current(id, nav); } });
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
  async function preview(item) {
    var id = state.activeChatId, nav = state.chatNavigationVersion, version = ++generation;
    clearPreview(); status("Загрузка предпросмотра…");
    try {
      var result = await read(item, true, id, nav);
      if (!current(id, nav) || version !== generation) return;
      el("PreviewTitle").textContent = item.title; el("Preview").hidden = false; el("List").hidden = true;
      var mime = result.transfer.contentType || "";
      if (item.kind === "html_workspace") {
        var envelope = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(result.bytes));
        var project = envelope.Project;
        var files = project.Files.map(function (f) { return { id: f.Path.toLowerCase(), path: f.Path, kind: f.Kind, content: f.Content }; });
        var picker = document.createElement("select"); picker.setAttribute("aria-label", "Файл проекта");
        files.forEach(function (f) { picker.appendChild(new Option(f.path, f.id)); });
        var pre = node("pre", "", "artifact-catalog-source");
        function source() { pre.textContent = files.find(function (f) { return f.id === picker.value; }).content; }
        picker.addEventListener("change", source); picker.value = files[0].id; source();
        el("PreviewBody").appendChild(node("p", "Сохранённый проект: " + files.length + " файлов, " + project.Data.length + " снимков данных."));
        if (project.ExternalDependencies.length) el("PreviewBody").appendChild(node("p", "Внешние зависимости: " + project.ExternalDependencies.join(", ")));
        // Source inspection is always available; use the established sandbox renderer for HTML.
        {
          var snapshot = await previewSnapshot(envelope.Views);
          await window.RNAssistantHtmlWorkspacePreview.ensureVendors(files);
          if (!current(id, nav) || version !== generation) return;
          var frame = document.createElement("iframe"); frame.title = item.title; frame.setAttribute("sandbox", "allow-scripts");
          frame.srcdoc = window.RNAssistantHtmlWorkspacePreview.build({ files: files, activeFileId: project.EntryPath.toLowerCase(), dataSources: project.Data.map(function (d) { return { name: d.Name }; }), resourceSnapshot: snapshot, hostBridge: false });
          el("PreviewBody").appendChild(frame);
        }
        el("PreviewBody").appendChild(picker); el("PreviewBody").appendChild(pre);
      } else if (/^image\//.test(mime)) {
        previewUrl = URL.createObjectURL(new Blob([result.bytes], { type: mime }));
        var image = document.createElement("img"); image.src = previewUrl; image.alt = item.title; el("PreviewBody").appendChild(image);
      } else if (/text|json|javascript|xml/.test(mime)) {
        el("PreviewBody").appendChild(node("pre", new TextDecoder("utf-8", { fatal: true }).decode(result.bytes), "artifact-catalog-source"));
      } else el("PreviewBody").appendChild(node("p", "Файл: " + result.transfer.fileName + " · " + result.bytes.length + " байт. Используйте «Скачать» для открытия."));
      status("");
    } catch (error) { if (current(id, nav) && version === generation) status(error.detail || error.message); }
  }
  async function importFile(file) {
    if (!file || !canMutate()) return;
    if (file.size > 20 * 1024 * 1024) { status("Максимальный размер — 20 МиБ."); return; }
    var id = state.activeChatId, nav = state.chatNavigationVersion, lease;
    pending = true;
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
      applyChatStateForChat(result, id); show("chat");
    } catch (error) { if (current(id, nav)) status(error.detail || error.message); }
    finally { pending = false; if (lease) await send("closeArtifactTransfer", { chatId: id, leaseId: lease.leaseId }).catch(function () {}); }
  }
  ["Scope", "Kind", "Document"].forEach(function (name) { el(name).addEventListener("change", function () { show(); }); });
  el("Query").addEventListener("keydown", function (event) { if (event.key === "Enter") { event.preventDefault(); show(); } });
  el("Refresh").addEventListener("click", function () { show(); });
  el("Show").addEventListener("click", function () { show(); });
  el("Editor").addEventListener("click", editor);
  el("More").addEventListener("click", function () { load(next); });
  el("Back").addEventListener("click", function () { generation++; clearPreview(); });
  el("Import").addEventListener("click", function () { if (canMutate()) el("File").click(); });
  el("File").addEventListener("change", function () { var file = this.files[0]; this.value = ""; importFile(file); });
  window.RNAssistantArtifactCatalog = { show: show, editor: editor, sync: sync, changeLink: changeLink };
  $("htmlWorkspaceLayout").classList.add("hidden");
}());
