(function () {
  "use strict";

  var MAX_BYTES = Number.MAX_SAFE_INTEGER, MAX_PARTS = Number.MAX_SAFE_INTEGER, MAX_BATCH_BYTES = 8 * 1024 * 1024;

  function sameRef(left, right) {
    return !!left && !!right && left.uri === right.uri && left.revision === right.revision;
  }

  async function partHash(text) {
    if (!window.crypto || !window.crypto.subtle) throw new Error("RESOURCE_INTEGRITY_UNAVAILABLE");
    var bytes = new TextEncoder().encode(text);
    var digest = new Uint8Array(await window.crypto.subtle.digest("SHA-256", bytes));
    return Array.from(digest, function (value) { return value.toString(16).padStart(2, "0"); }).join("");
  }

  // Explicit export only. Bodies are pulled through the ordinary resource route,
  // never through control messages or back into the workspace/session projection.
  async function capture(resourceExport, options) {
    var bindings = resourceExport && resourceExport.bindings;
    if (!Array.isArray(bindings) || bindings.length > 32 || new Set(bindings.map(function (item) { return item.name; })).size !== bindings.length)
      throw new Error("RESOURCE_EXPORT_INVALID");
    var total = 0, parts = [], resources = [], maximum = options.maximumBytes || MAX_BYTES;
    function active() {
      if (options.signal && options.signal.aborted || !options.isCurrent()) throw new Error("RESOURCE_EXPORT_CANCELLED");
    }
    for (var binding of bindings) {
      active();
      var lease = binding.lease;
      if (!lease || !lease.descriptor || !lease.descriptor.reference || !lease.descriptor.reference.revision ||
          !/^https:\/\/rnassistant\.local-resource\/v1\/[a-f0-9]{64}$/.test(lease.url) ||
          !Number.isInteger(lease.maxBatchItems) || lease.maxBatchItems < 1 || lease.maxBatchItems > (lease.binary ? 256 * 1024 : 32000) ||
          !Number.isInteger(lease.maxBatchBytes) || lease.maxBatchBytes < 0 || lease.maxBatchBytes > MAX_BATCH_BYTES)
        throw new Error("RESOURCE_EXPORT_INVALID");
      if (lease.binary && (!lease.binary.payload || !Number.isInteger(lease.binary.payload.byteLength) ||
          lease.binary.payload.byteLength < 0 ||
          lease.maxBatchBytes > 256 * 1024 || lease.maxBatchItems !== lease.maxBatchBytes)) throw new Error("RESOURCE_EXPORT_INVALID");
      var resource = { name: binding.name, descriptor: lease.descriptor, view: lease.view, path: lease.path,
        binary: lease.binary, maxBatchBytes: lease.maxBatchBytes, maxBatchItems: lease.maxBatchItems, parts: [] };
      var offset = 0, done = false;
      while (!done) {
        active();
        if (parts.length >= MAX_PARTS) throw new Error("RESOURCE_EXPORT_BOUNDS");
        var abort = new AbortController();
        var cancel = function () { abort.abort(); };
        if (options.signal) options.signal.addEventListener("abort", cancel, { once: true });
        var timer = setTimeout(cancel, 30000);
        var bytes;
        try {
          var response = await options.fetch(lease.url + "?offset=" + offset + "&limit=" + lease.maxBatchItems,
            { credentials: "omit", cache: "no-store", signal: abort.signal });
          if (!response.ok) throw new Error("RESOURCE_EXPORT_READ_FAILED: " + response.status);
          // Bound consumption even if a broken route advertises/produces more bytes.
          var reader = response.body.getReader(), chunks = [], size = 0;
          try {
            while (true) {
              active();
              var item = await reader.read();
              if (item.done) break;
              size += item.value.byteLength;
              if (size > lease.maxBatchBytes || total + size > maximum) throw new Error("RESOURCE_EXPORT_BOUNDS");
              chunks.push(item.value);
            }
          } finally { await reader.cancel(); reader.releaseLock(); }
          bytes = new Uint8Array(size);
          var position = 0;
          chunks.forEach(function (chunk) { bytes.set(chunk, position); position += chunk.byteLength; });
        } finally {
          clearTimeout(timer);
          if (options.signal) options.signal.removeEventListener("abort", cancel);
        }
        active(); total += bytes.byteLength;
        var id = "rn-export-part-" + parts.length, text, next;
        if (lease.binary) {
          if (bytes.byteLength !== Math.min(lease.maxBatchItems, lease.binary.payload.byteLength - offset)) throw new Error("RESOURCE_SNAPSHOT_UNAVAILABLE");
          var strings = [];
          for (var start = 0; start < bytes.length; start += 8192)
            strings.push(String.fromCharCode.apply(null, bytes.subarray(start, start + 8192)));
          text = btoa(strings.join("")); next = offset + bytes.byteLength; done = next === lease.binary.payload.byteLength;
        } else {
          text = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
          var batch = JSON.parse(text), count = Array.isArray(batch.rows) ? batch.rows.length : typeof batch.text === "string" ? batch.text.length : -1;
          next = batch.nextOffset; done = batch.done;
          if (!sameRef(batch.resource, lease.descriptor.reference) || batch.view !== lease.view || batch.offset !== offset ||
              !Number.isSafeInteger(next) || count < 0 || count > lease.maxBatchItems || next !== offset + count ||
              typeof done !== "boolean" || !done && next === offset || !batch.coverage)
            throw new Error("RESOURCE_EXPORT_REVISION_MISMATCH");
        }
        // Hash the exact inert transport representation, not a logical revision.
        text = text.replace(/</g, "\\u003c");
        var sha256 = await partHash(text);
        parts.push({ id: id, text: text });
        resource.parts.push({ id: id, offset: offset, nextOffset: next, byteLength: bytes.byteLength, sha256: sha256, done: done });
        offset = next;
      }
      resources.push(resource);
    }
    active();
    return { version: 2, generations: resourceExport.generations, resources: resources, parts: parts };
  }

  // Exact retained views from the existing artifact transfer package. No live bindings.
  async function fromArtifactViews(views, options) {
    if (!Array.isArray(views) || views.length > 100) throw new Error("Некорректные данные сохранённого проекта.");
    var bytes = 0;
    var snapshot = { version: 2, resources: [], parts: [], generations: {} };
    for (var view of views) {
      var reference = view.Reference;
      var resource = { name: view.Name, descriptor: { reference: reference, title: view.Name }, view: view.View,
        path: view.Path, maxBatchItems: 32000, maxBatchBytes: 8 * 1024 * 1024, parts: [] };
      var total = view.Rows ? view.Rows.length : view.Text.length;
      var batchSize = view.Rows ? 500 : 32000;
      for (var offset = 0; offset < total || offset === 0;) {
        if (!options.isCurrent() || options.signal.aborted) throw new Error("Загрузка отменена.");
        if (snapshot.parts.length >= 2048) throw new Error("Данные проекта превышают лимит просмотра.");
        var end = Math.min(offset + batchSize, total);
        var batch = { resource: reference, view: view.View, offset: offset, nextOffset: end, done: end === total,
          coverage: { kind: view.Rows ? "record-range" : "character-range", start: offset, end: end, path: view.Path, fields: [] } };
        if (view.Rows) { batch.rows = view.Rows.slice(offset, end); batch.columns = view.Columns; }
        else batch.text = view.Text.slice(offset, end);
        var text = JSON.stringify(batch).replace(/</g, "\\u003c");
        var size = new TextEncoder().encode(text).length; bytes += size;
        if (size > MAX_BATCH_BYTES || bytes > 32 * 1024 * 1024) throw new Error("Данные проекта превышают лимит просмотра 32 МиБ.");
        var hash = await partHash(text);
        var part = { id: "rn-export-part-" + snapshot.parts.length, offset: offset, nextOffset: end,
          done: end === total, byteLength: size, sha256: hash };
        snapshot.parts.push({ id: part.id, text: text }); resource.parts.push(part);
        if (end === total) break;
        offset = end;
      }
      snapshot.resources.push(resource);
    }
    return snapshot;
  }

  // Read-only transport for an exported exact snapshot. The RN.resources handle,
  // stream/backpressure and selectors are the same as the hosted runtime.
  function installSnapshotTransport(manifest, hash) {
    if (manifest.version !== 2) throw new Error("RESOURCE_EXPORT_VERSION_UNSUPPORTED");
    var active = new Map(), sequence = 0;
    function copy(value) { return JSON.parse(JSON.stringify(value)); }
    function entry(lease) {
      var found = active.get(lease.leaseId);
      if (!found || Date.now() >= found.expires) {
        active.delete(lease.leaseId); throw new Error("RESOURCE_LEASE_EXPIRED");
      }
      return found.resource;
    }
    return {
      open: function (name) {
        active.forEach(function (item, key) { if (Date.now() >= item.expires) active.delete(key); });
        if (active.size >= 64) throw new Error("RESOURCE_LEASE_LIMIT");
        var resource = manifest.resources.find(function (item) { return item.name === name; });
        if (!resource) throw new Error("RESOURCE_SNAPSHOT_UNAVAILABLE");
        var id = "snapshot-" + (++sequence), expires = Date.now() + 600000;
        active.set(id, { resource: resource, expires: expires });
        return { leaseId: id, descriptor: copy(resource.descriptor), view: resource.view, path: resource.path,
          binary: resource.binary && copy(resource.binary), maxBatchItems: resource.maxBatchItems,
          maxBatchBytes: resource.maxBatchBytes };
      },
      close: function (leaseId) { active.delete(leaseId); },
      read: async function (lease, offset, limit, options) {
        if (options.signal && options.signal.aborted) throw new Error("RESOURCE_READ_CANCELLED");
        var resource = entry(lease);
        var part = resource.parts.find(function (item) {
          return offset >= item.offset && (offset < item.nextOffset || item.done && offset === item.offset);
        });
        var element = part && document.getElementById(part.id);
        if (!element) throw new Error("RESOURCE_SNAPSHOT_UNAVAILABLE");
        var content = element.textContent;
        if (content.length > 8 * 1024 * 1024 * 6) throw new Error("RESOURCE_BATCH_TOO_LARGE");
        if (await hash(content) !== part.sha256) throw new Error("RESOURCE_SNAPSHOT_UNAVAILABLE");
        entry(lease);
        if (options.signal && options.signal.aborted) throw new Error("RESOURCE_READ_CANCELLED");
        // Inert parts are decoded only on pull; no eager full-dataset JS object.
        if (resource.binary) {
          if (options.fields && options.fields.length) throw new Error("RESOURCE_VIEW_INVALID");
          var binary = atob(content), bytes = new Uint8Array(binary.length);
          if (bytes.length !== part.byteLength || bytes.length > resource.maxBatchBytes) throw new Error("RESOURCE_SNAPSHOT_UNAVAILABLE");
          for (var index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
          if (part.nextOffset !== part.offset + bytes.length || part.nextOffset > resource.binary.payload.byteLength)
            throw new Error("RESOURCE_SNAPSHOT_UNAVAILABLE");
          var start = offset - part.offset;
          return { ok: true, arrayBuffer: async function () { return bytes.slice(start, start + limit).buffer; } };
        }
        var batch = JSON.parse(content);
        if (!batch.resource || batch.resource.uri !== resource.descriptor.reference.uri ||
            batch.resource.revision !== resource.descriptor.reference.revision || batch.view !== resource.view ||
            batch.offset !== part.offset || batch.nextOffset !== part.nextOffset || batch.done !== part.done)
          throw new Error("RESOURCE_SNAPSHOT_UNAVAILABLE");
        var local = offset - part.offset, next = Math.min(offset + limit, part.nextOffset);
        if (Array.isArray(batch.rows)) {
          var fields = options.fields && options.fields.length ? options.fields : batch.columns.map(function (column) { return column.key; });
          if (!Array.isArray(fields) || fields.length > 1024 || new Set(fields).size !== fields.length ||
              fields.some(function (field) { return !batch.columns.some(function (column) { return column.key === field; }); }))
            throw new Error("RESOURCE_FIELD_UNAVAILABLE");
          batch.rows = batch.rows.slice(local, local + next - offset).map(function (row) {
            var result = Object.create(null);
            fields.forEach(function (field) { result[field] = row[field]; }); return result;
          });
          batch.columns = batch.columns.filter(function (column) { return fields.indexOf(column.key) >= 0; });
          batch.coverage = { kind: "record-range", start: offset, end: next, path: resource.path, fields: fields.slice() };
        } else {
          if (options.fields && options.fields.length) throw new Error("RESOURCE_VIEW_INVALID");
          batch.text = batch.text.slice(local, local + next - offset);
          if (local || next !== part.nextOffset)
            batch.coverage = { kind: "character-range", start: offset, end: next, fields: [] };
        }
        batch.offset = offset; batch.nextOffset = next; batch.done = part.done && next === part.nextOffset;
        return { ok: true, json: async function () { return batch; } };
      }
    };
  }

  function script(snapshot, installApi, names, escapeJson) {
    if (!snapshot || snapshot.version !== 2 || snapshot.resources.length !== names.length ||
        snapshot.resources.some(function (item) { return names.indexOf(item.name) < 0; }))
      throw new Error("RESOURCE_EXPORT_INVALID");
    var manifest = { version: snapshot.version, generations: snapshot.generations,
      workspace: snapshot.workspace, resources: snapshot.resources };
    var parts = snapshot.parts.map(function (part) {
      // Escape all '<' in JSON, including script/comment openers. Binary is base64.
      return '<script type="application/vnd.rnassistant.resource-part" id="' + part.id + '">' +
        part.text + "</script>";
    }).join("\n");
    return parts + "<script>(" + installApi.toString() + ")(" + escapeJson(JSON.stringify(names)) + ", (" +
      installSnapshotTransport.toString() + ")(" + escapeJson(JSON.stringify(manifest)) + ", " + partHash.toString() + "));</script>";
  }

  async function downloadTarget(target, options) {
    var prepared, writable;
    function current() { return options.isCurrent() && !(options.signal && options.signal.aborted); }
    try {
      if (typeof window.showSaveFilePicker === "function") {
        var handle = await window.showSaveFilePicker({ suggestedName: "rnassistant-project.html", types: [{ description: "HTML", accept: { "text/html": [".html"] } }] });
        if (!current()) return false; writable = await handle.createWritable();
      }
      options.status("Подготовка автономного HTML и снимков данных…");
      prepared = await options.send("prepareBrowserHtmlExport", { target: target });
      if (!current()) return false;
      var bytes = await RNAssistantResourceDownload.read(prepared.source, { maxBytes: 16 * 1024 * 1024,
        isCurrent: current, signal: options.signal, fetch: options.fetch });
      var workspace = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
      var files = workspace.Files || workspace.files, sources = workspace.DataSources || workspace.dataSources || [];
      if (!Array.isArray(files) || files.length > 512 || !Array.isArray(sources) || sources.length > 32) throw new Error("Некорректный снимок HTML workspace.");
      var entry = files.find(function (file) { return (file.Path || file.path) === prepared.target.entryPath; });
      if (!entry) throw new Error("В снимке отсутствует выбранная HTML-страница.");
      await RNAssistantHtmlWorkspacePreview.ensureVendors(files);
      var snapshot = await capture(prepared.resourceExport, { fetch: options.fetch, signal: options.signal, isCurrent: current, maximumBytes: 32 * 1024 * 1024 });
      snapshot.workspace = { resourceUri: prepared.target.resourceUri, contentSha256: prepared.source.payload.sha256 };
      var html = RNAssistantHtmlWorkspacePreview.build({ files: files, dataSources: sources,
        activeFileId: entry.Id || entry.id, hostBridge: false, resourceSnapshot: snapshot });
      var blob = new Blob([html], { type: "text/html;charset=utf-8" });
      if (blob.size > 64 * 1024 * 1024) throw new Error("Автономная страница превышает 64 МиБ; скачайте исходники проекта.");
      if (!current()) return false;
      if (writable) { await writable.write(blob); await writable.close(); writable = null; }
      else { var url = URL.createObjectURL(blob), link = document.createElement("a"); link.href = url; link.download = "rnassistant-project.html"; link.click();
        setTimeout(function () { URL.revokeObjectURL(url); }, 1000); }
      options.status("Автономный HTML подготовлен для скачивания. Данные включены как снимок.");
      return true;
    } catch (error) { if (current()) options.status(error.name === "AbortError" ? "Сохранение отменено." : error.detail || error.message); return false; }
    finally {
      if (writable) await writable.abort().catch(function () {});
      if (prepared) {
        var ids = (prepared.resourceExport && prepared.resourceExport.bindings || []).map(function (binding) { return binding.lease.leaseId; });
        if (prepared.source) ids.push(prepared.source.leaseId);
        await Promise.all(ids.map(function (leaseId) { return options.send("resourceDataClose", { chatId: prepared.chatId, workspaceId: "browser", leaseId: leaseId }).catch(function () {}); }));
      }
    }
  }
  window.RNAssistantHtmlResourceExport = { capture: capture, script: script, fromArtifactViews: fromArtifactViews, downloadTarget: downloadTarget };
}());
