(function () {
  "use strict";

  function prop(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function fileId(file) {
    return prop(file, "Id", "id", prop(file, "Path", "path", ""));
  }

  function filePath(file) {
    return prop(file, "Path", "path", fileId(file));
  }

  function fileKind(file) {
    return String(prop(file, "Kind", "kind", "") || "").toLowerCase();
  }

  function fileContent(file) {
    return prop(file, "Content", "content", "") || "";
  }

  function isScriptFile(file) {
    var kind = fileKind(file);
    return kind === "script" || kind === "js" || /\.js$/i.test(filePath(file));
  }

  function activeHtmlFile(files, activeFileId) {
    var active = null;
    files.forEach(function (file) {
      if (!active && fileId(file) === activeFileId && fileKind(file) === "html") active = file;
    });
    if (active) return active;
    files.forEach(function (file) {
      if (!active && fileKind(file) === "html") active = file;
    });
    return active;
  }

  function escapeScriptJson(value) {
    return String(value || "")
      .replace(/</g, "\\u003c")
      .replace(/\u2028/g, "\\u2028")
      .replace(/\u2029/g, "\\u2029");
  }

  function safeStyle(value) {
    return String(value || "").replace(/<\/style/gi, "<\\/style");
  }

  function safeScript(value) {
    return String(value || "").replace(/<\/script/gi, "<\\/script");
  }

  function encodeHtml(value) {
    return String(value || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  // The application sees only bounded resource handles, never an injected dataset.
  function installResourceApi(names, transport) {
    var nativeFetch = window.fetch && window.fetch.bind(window);
    var handles = new Set();
    var subscribers = new Set();
    var native = !transport && window.chrome && window.chrome.webview;
    var nativePending = new Map(), nativeSequence = 0;
    if (native) native.addEventListener("message", function (event) {
      var value = event.data;
      if (value && value.type === "rnassistant-resource-changed") {
        var changed = (value.names || []).filter(function (name) { return names.indexOf(name) >= 0; });
        if (changed.length) subscribers.forEach(function (listener) { try { listener(Object.freeze({ names: changed, generation: value.generation })); } catch (_) {} });
      }
      var pending = value && value.type === "rnassistant-resource-result" && nativePending.get(value.requestId);
      if (!pending) return;
      nativePending.delete(value.requestId); clearTimeout(pending.timer);
      if (value.ok) pending.resolve(value.value); else pending.reject(new Error(value.error || "RESOURCE_ACCESS_DENIED"));
    });
    function control(operation, bindingName, leaseId) {
      if (transport) return Promise.resolve().then(function () {
        return operation === "open" ? transport.open(bindingName) : transport.close(leaseId);
      });
      if (native) return new Promise(function (resolve, reject) {
        if (nativePending.size >= 64) { reject(new Error("RESOURCE_CONTROL_LIMIT")); return; }
        var id = "resource-" + (++nativeSequence), timer = setTimeout(function () { nativePending.delete(id); reject(new Error("RESOURCE_CONTROL_TIMEOUT")); }, 15000);
        nativePending.set(id, { resolve: resolve, reject: reject, timer: timer });
        native.postMessage(JSON.stringify({ type: "rnassistant-resource-control", requestId: id, operation: operation, bindingName: bindingName, leaseId: leaseId }));
      });
      return Promise.reject(new Error("RESOURCE_HOST_REQUIRED: open this workspace in RNAssistant."));
    }

    async function open(name) {
      if (names.indexOf(name) < 0) throw new Error("RESOURCE_BINDING_UNKNOWN: " + name);
      var lease = await control("open", name, null);
      var closed = false, busy = false, offset = 0, done = false;
      var handle = {
        descriptor: Object.freeze(lease.descriptor),
        async read(options) {
          options = options || {};
          if (closed || done) throw new Error("RESOURCE_LEASE_CLOSED");
          if (busy) throw new Error("RESOURCE_BACKPRESSURE");
          if (options.view && options.view !== lease.view) throw new Error("RESOURCE_VIEW_UNSUPPORTED");
          if (options.path && options.path !== lease.path) throw new Error("RESOURCE_VIEW_PATH_UNSUPPORTED");
          if (options.page !== undefined && String(options.page) !== lease.path) throw new Error("RESOURCE_VIEW_PATH_UNSUPPORTED");
          var limit = options.limit !== undefined ? options.limit :
            options.batchRows !== undefined ? options.batchRows : lease.maxBatchItems;
          var requestedOffset = options.offset === undefined ? offset : options.offset;
          if (!Number.isInteger(limit) || limit < 1 || limit > lease.maxBatchItems || requestedOffset !== offset)
            throw new Error("RESOURCE_BATCH_BOUNDS");
          if (lease.binary && (options.fields && options.fields.length || !Number.isInteger(lease.binary.payload.byteLength) ||
              lease.binary.payload.byteLength < 0 ||
              lease.maxBatchBytes > 256 * 1024 || limit > lease.maxBatchBytes)) throw new Error("RESOURCE_BATCH_BOUNDS");
          busy = true;
          try {
            if (options.signal && options.signal.aborted) throw new Error("RESOURCE_READ_CANCELLED");
            var response = transport ? await transport.read(lease, offset, limit, options) : await nativeFetch(lease.url + "?offset=" + offset + "&limit=" + limit +
              (options.fields ? "&fields=" + encodeURIComponent(JSON.stringify(options.fields)) : ""),
              { method: "GET", credentials: "omit", cache: "no-store", signal: options.signal });
            if (response.ok && lease.binary) {
              var expected = Math.min(limit, lease.binary.payload.byteLength - offset), bytes;
              if (transport) bytes = await response.arrayBuffer();
              else {
                if (response.headers.get("Content-Type") !== lease.binary.payload.contentType) throw new Error("RESOURCE_VIEW_INVALID");
                var reader = response.body.getReader(), collected = new Uint8Array(expected), received = 0;
                try {
                  while (true) {
                    var chunk = await reader.read();
                    if (closed || options.signal && options.signal.aborted) throw new Error("RESOURCE_READ_CANCELLED");
                    if (chunk.done) break;
                    if (received + chunk.value.byteLength > expected) throw new Error("RESOURCE_BATCH_TOO_LARGE");
                    collected.set(chunk.value, received); received += chunk.value.byteLength;
                  }
                  if (received !== expected) throw new Error("RESOURCE_SNAPSHOT_UNAVAILABLE");
                } finally { await reader.cancel().catch(function () {}); reader.releaseLock(); }
                bytes = collected.buffer;
              }
              if (closed) throw new Error("RESOURCE_LEASE_CLOSED");
              if (options.signal && options.signal.aborted) throw new Error("RESOURCE_READ_CANCELLED");
              if (bytes.byteLength > expected || expected > 0 && bytes.byteLength === 0)
                throw new Error("RESOURCE_BATCH_TOO_LARGE");
              var start = offset; offset += bytes.byteLength; done = offset === lease.binary.payload.byteLength;
              return { resource: lease.descriptor.reference, view: lease.view, bytes: bytes, offset: start, nextOffset: offset,
                mimeType: lease.binary.payload.contentType, width: lease.binary.width, height: lease.binary.height,
                pageIndex: lease.binary.pageIndex, done: done };
            }
            var batch = await response.json();
            if (closed) throw new Error("RESOURCE_LEASE_CLOSED");
            if (options.signal && options.signal.aborted) throw new Error("RESOURCE_READ_CANCELLED");
            if (!response.ok) throw new Error(batch.code + ": " + batch.message);
            offset = batch.nextOffset; done = batch.done;
            return batch;
          } finally { busy = false; }
        },
        async *stream(options) {
          var streamOptions = Object.assign({}, options || {});
          if (streamOptions.offset !== undefined && streamOptions.offset !== offset) throw new Error("RESOURCE_BATCH_BOUNDS");
          delete streamOptions.offset;
          try { while (!done && !closed) yield await handle.read(streamOptions); }
          finally { await handle.close(); }
        },
        async close() {
          if (closed) return;
          closed = true; handles.delete(handle);
          await control("close", name, lease.leaseId);
        }
      };
      handles.add(handle);
      return Object.freeze(handle);
    }
    window.addEventListener("pagehide", function () {
      handles.forEach(function (handle) { handle.close().catch(function () {}); });
    });
    Object.defineProperty(window, "RN", { value: Object.freeze({
      resources: Object.freeze({ open: open, names: function () { return names.slice(); },
        subscribe: function (listener) {
          if (typeof listener !== "function") throw new Error("RESOURCE_LISTENER_INVALID");
          subscribers.add(listener); return function () { subscribers.delete(listener); };
        } })
    }), writable: false, configurable: false });
  }

  function resourceScript(dataSources, snapshot) {
    var names = dataSources.map(function (source) { return prop(source, "Name", "name", ""); });
    if (snapshot) {
      if (!window.RNAssistantHtmlResourceExport) throw new Error("RESOURCE_EXPORT_RUNTIME_UNAVAILABLE");
      return window.RNAssistantHtmlResourceExport.script(snapshot, installResourceApi, names, escapeScriptJson);
    }
    return "<script>(" + safeScript(installResourceApi.toString()) + ")(" +
      escapeScriptJson(JSON.stringify(names)) + ");</script>";
  }

  function cssBlock(files) {
    return files.filter(function (file) {
      return fileKind(file) === "css";
    }).map(function (file) {
      return "<style data-rn-path=\"" + encodeHtml(filePath(file)) + "\">\n" + safeStyle(fileContent(file)) + "\n</style>";
    }).join("\n");
  }

  function scriptBlock(files) {
    return files.filter(isScriptFile).map(function (file) {
      return "<script data-rn-path=\"" + encodeHtml(filePath(file)) + "\">\n" + safeScript(fileContent(file)) + "\n</script>";
    }).join("\n");
  }

  function dependencies(files) {
    var runtime = window.RNAssistantHtmlVendorRuntime;
    return runtime ? runtime.dependencies(files || []) : [];
  }

  function vendorHead(files) {
    var runtime = window.RNAssistantHtmlVendorRuntime;
    if (!runtime) throw new Error("Bundled HTML vendor registry is unavailable.");
    return runtime.used(files).map(function (vendor) {
      var source = runtime.source(vendor);
      var label = encodeHtml(vendor.id + "-" + vendor.version);
      return (source.css ? "<style data-rn-vendor=\"" + label + "\">" + safeStyle(source.css) + "</style>\n" : "") +
        "<script data-rn-vendor=\"" + label + "\">" + safeScript(source.js) + "<\/script>";
    }).join("\n");
  }

  function injectBeforeLastClosingTag(html, tagName, content) {
    var pattern = new RegExp("<\\/" + tagName + "\\s*>", "ig");
    var match = null;
    var candidate;
    while ((candidate = pattern.exec(html)) !== null) match = candidate;
    if (!match) return null;
    return html.slice(0, match.index) + content + "\n" + html.slice(match.index);
  }

  function previewViewportReset() {
    return "<style data-rn-preview-reset>html,body{min-height:100%;margin:0;}*,*::before,*::after{box-sizing:border-box;}</style>";
  }

  function previewContentSecurityPolicy(standalone) {
    return "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; connect-src " +
      (standalone ? "'none'" : "https://rnassistant.local-resource/v1/") + "; img-src data: blob:" +
      (standalone ? "" : " https://rnassistant.local-resource/v1/") +
      "; font-src data:; media-src data: blob:; style-src 'unsafe-inline'; script-src 'unsafe-inline' blob: data:; frame-src 'none'; child-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none';\">";
  }

  function build(options) {
    options = options || {};
    if ((options.files || []).some(function (file) { return typeof prop(file, "Content", "content", null) !== "string"; }))
      throw new Error("RESOURCE_SOURCE_REQUIRED: load every exact workspace source before assembly.");
    var files = options.files || [];
    var dataSources = options.dataSources || [];
    if (options.hostBridge === false && dataSources.length && !options.resourceSnapshot)
      throw new Error("RESOURCE_EXPORT_REQUIRED: capture exact resource bindings before standalone export.");
    var file = activeHtmlFile(files, options.activeFileId || "");
    var html = file ? fileContent(file) : "";
    var inspector = options.inspectGeneration && options.hostBridge !== false
      ? "<script>" + safeScript(window.RNAssistantVisualInspector.script(options.inspectGeneration)) + "<\/script>\n" : "";
    var vendorRuntime = vendorHead(files);
    var headInject = '<meta charset="utf-8">' + (options.nativeHost ? "" : previewContentSecurityPolicy(options.hostBridge === false)) + "\n" + previewViewportReset() + "\n" +
      inspector + vendorRuntime + (vendorRuntime ? "\n" : "") + resourceScript(dataSources, options.resourceSnapshot) + "\n" + cssBlock(files);
    var bodyInject = scriptBlock(files);
    if (!html.trim()) {
      html = "<div style=\"font-family:Segoe UI,Arial,sans-serif;padding:24px;color:#475467\">HTML workspace пуст.</div>";
    }
    if (/<html[\s>]/i.test(html)) {
      if (bodyInject) {
        var withBodyScripts = injectBeforeLastClosingTag(html, "body", bodyInject) ||
          injectBeforeLastClosingTag(html, "html", bodyInject);
        html = withBodyScripts || html + "\n" + bodyInject;
      }
      if (/<head[\s>]/i.test(html)) {
        html = html.replace(/<head([^>]*)>/i, function (match) { return match + "\n" + headInject; });
      } else {
        html = html.replace(/<html[^>]*>/i, function (match) { return match + "<head>" + headInject + "</head>"; });
      }
      return html;
    }
    return "<!doctype html><html><head><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" + headInject + "</head><body>" + html + "\n" + bodyInject + "</body></html>";
  }

  window.RNAssistantHtmlWorkspacePreview = {
    build: build,
    dependencies: dependencies,
    missingVendors: function (files) { return window.RNAssistantHtmlVendorRuntime.missing(files); },
    ensureVendors: function (files) { return window.RNAssistantHtmlVendorRuntime.ensure(files); }
  };
}());
