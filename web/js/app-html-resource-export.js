(function () {
  "use strict";

  var MAX_BYTES = 32 * 1024 * 1024, MAX_PARTS = 1024, MAX_BATCH_BYTES = 8 * 1024 * 1024;

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
    var total = 0, parts = [], resources = [];
    function active() {
      if (options.signal && options.signal.aborted || !options.isCurrent()) throw new Error("RESOURCE_EXPORT_CANCELLED");
    }
    for (var binding of bindings) {
      active();
      var lease = binding.lease;
      if (!lease || !lease.descriptor || !lease.descriptor.reference || !lease.descriptor.reference.revision ||
          !/^https:\/\/rnassistant\.local-resource\/v1\/[a-f0-9]{64}$/.test(lease.url) ||
          !Number.isInteger(lease.maxBatchItems) || lease.maxBatchItems < 1 || lease.maxBatchItems > 32000 ||
          !Number.isInteger(lease.maxBatchBytes) || lease.maxBatchBytes < 0 || lease.maxBatchBytes > MAX_BATCH_BYTES)
        throw new Error("RESOURCE_EXPORT_INVALID");
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
              if (size > lease.maxBatchBytes || total + size > MAX_BYTES) throw new Error("RESOURCE_EXPORT_BOUNDS");
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
          if (bytes.byteLength !== lease.binary.payload.byteLength) throw new Error("RESOURCE_SNAPSHOT_UNAVAILABLE");
          var strings = [];
          for (var start = 0; start < bytes.length; start += 8192)
            strings.push(String.fromCharCode.apply(null, bytes.subarray(start, start + 8192)));
          text = btoa(strings.join("")); next = 1; done = true;
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
    return { version: 1, generations: resourceExport.generations, resources: resources, parts: parts };
  }

  // Read-only transport for an exported exact snapshot. The RN.resources handle,
  // stream/backpressure and selectors are the same as the hosted runtime.
  function installSnapshotTransport(manifest, hash) {
    if (manifest.version !== 1) throw new Error("RESOURCE_EXPORT_VERSION_UNSUPPORTED");
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
          return { ok: true, arrayBuffer: async function () { return bytes.buffer; } };
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
    if (!snapshot || snapshot.version !== 1 || snapshot.resources.length !== names.length ||
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

  window.RNAssistantHtmlResourceExport = { capture: capture, script: script };
}());
