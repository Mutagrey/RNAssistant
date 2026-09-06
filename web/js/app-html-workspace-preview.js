(function () {
  "use strict";

  var ECHARTS_DEPENDENCY_ID = "runtime/echarts.min.js";
  var ECHARTS_VERSION = "5.6.0";

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
    window.addEventListener("message", function (event) {
      if (transport) return; // Exported snapshots have no mutable head subscriptions.
      if (event.source !== window.parent || !event.data || event.data.type !== "rnassistant-resource-changed") return;
      var changed = (event.data.names || []).filter(function (name) { return names.indexOf(name) >= 0; });
      if (changed.length) subscribers.forEach(function (listener) {
        try { listener(Object.freeze({ names: changed.slice(), generation: event.data.generation })); } catch (_) {}
      });
    });
    function control(operation, bindingName, leaseId) {
      if (transport) return Promise.resolve().then(function () {
        return operation === "open" ? transport.open(bindingName) : transport.close(leaseId);
      });
      return new Promise(function (resolve, reject) {
        if (window.parent === window) { reject(new Error("RESOURCE_HOST_REQUIRED: open this workspace in RNAssistant.")); return; }
        var channel = new MessageChannel();
        var timeout = setTimeout(function () { channel.port1.close(); reject(new Error("RESOURCE_CONTROL_TIMEOUT")); }, 15000);
        channel.port1.onmessage = function (event) {
          clearTimeout(timeout); channel.port1.close();
          if (!event.data || !event.data.ok) reject(new Error(String(event.data && event.data.error || "RESOURCE_ACCESS_DENIED")));
          else resolve(event.data.value);
        };
        window.parent.postMessage({ type: "rnassistant-resource-control", operation: operation,
          bindingName: bindingName, leaseId: leaseId }, "*", [channel.port2]);
      });
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
              lease.binary.payload.byteLength < 0 || lease.binary.payload.byteLength > 20 * 1024 * 1024 ||
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

  function usesECharts(files) {
    return files.some(function (file) {
      return (fileKind(file) === "html" || isScriptFile(file)) && /\becharts\b/.test(fileContent(file));
    });
  }

  function echartsReady() {
    return typeof window.RNAssistantEChartsFactory === "function" &&
      !!window.echarts && window.echarts.version === ECHARTS_VERSION;
  }

  function ensureECharts() {
    if (echartsReady()) return Promise.resolve(window.echarts);
    var runtime = window.RNAssistantEChartsSandboxRuntime;
    if (!runtime || typeof runtime.load !== "function") {
      return Promise.reject(new Error("Bundled ECharts " + ECHARTS_VERSION + " loader is unavailable."));
    }
    return runtime.load();
  }

  function dependencies(files) {
    if (!usesECharts(files || [])) return [];
    var loaded = echartsReady();
    return [{
      id: ECHARTS_DEPENDENCY_ID,
      path: ECHARTS_DEPENDENCY_ID,
      title: "echarts.min.js",
      kind: "script",
      version: ECHARTS_VERSION,
      loaded: loaded,
      readOnly: true,
      description: loaded
        ? "Встроенная зависимость preview/export; подключается перед скриптами workspace."
        : "Встроенная зависимость ECharts не загрузилась."
    }];
  }

  function echartsScript(files) {
    if (!usesECharts(files) || typeof window.RNAssistantEChartsFactory !== "function" ||
        !window.echarts || window.echarts.version !== ECHARTS_VERSION) {
      return "";
    }
    return "<script data-rn-vendor=\"echarts-" + ECHARTS_VERSION + "\">" +
      "/* Licensed to the Apache Software Foundation under the Apache License, Version 2.0. " +
      "https://www.apache.org/licenses/LICENSE-2.0 */\n(" +
      safeScript(window.RNAssistantEChartsFactory.toString()) +
      ")(window.echarts={});<\/script>";
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

  function networkBridgeScript() {
    return "<script>(function(){" +
      "var nativeFetch=window.fetch&&window.fetch.bind(window),seq=1,pending={};" +
      "window.addEventListener('message',function(e){var d=e.data||{};if(d.type!=='rnassistant-html-fetch-result'||!pending[d.requestId])return;var p=pending[d.requestId];delete pending[d.requestId];if(!d.ok){p.reject(new TypeError(String(d.value||'HTTP request failed')));return;}var v=d.value||{},h=v.headers||v.Headers||{};p.resolve(new Response(v.body||v.Body||'',{status:v.status||v.Status||200,statusText:v.statusText||v.StatusText||'',headers:h}));});" +
      "window.fetch=function(input,init){init=init||{};var url=typeof input==='string'?input:(input&&input.url)||'';if(!/^https?:\\/\\//i.test(url)){return nativeFetch?nativeFetch(input,init):Promise.reject(new TypeError('Only HTTP(S) URLs are supported'));}var headers={};try{new Headers(init.headers||(input&&input.headers)||{}).forEach(function(v,k){headers[k]=v;});}catch(ignore){}var id=String(seq++);return new Promise(function(resolve,reject){pending[id]={resolve:resolve,reject:reject};window.parent.postMessage({type:'rnassistant-html-fetch',requestId:id,request:{url:url,method:init.method||(input&&input.method)||'GET',headers:headers,body:typeof init.body==='string'?init.body:''}},'*');});};" +
      "}());<\/script>";
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
    if (options.hostBridge === false && usesECharts(files) &&
        (typeof window.RNAssistantEChartsFactory !== "function" ||
          !window.echarts || window.echarts.version !== ECHARTS_VERSION)) {
      throw new Error("Standalone HTML export requires the loaded bundled ECharts " + ECHARTS_VERSION + " dependency.");
    }
    var hostBridge = options.hostBridge === false ? "" : networkBridgeScript() + "\n";
    var chartRuntime = echartsScript(files);
    var headInject = '<meta charset="utf-8">' + previewContentSecurityPolicy(options.hostBridge === false) + "\n" + previewViewportReset() + "\n" +
      chartRuntime + (chartRuntime ? "\n" : "") + resourceScript(dataSources, options.resourceSnapshot) + hostBridge + "\n" + cssBlock(files);
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
    echartsReady: echartsReady,
    ensureECharts: ensureECharts,
    usesECharts: usesECharts
  };
}());
