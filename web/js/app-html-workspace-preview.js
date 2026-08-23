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
    return value.replace(/<\/script/gi, "<\\/script");
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

  function dataScript(dataSources) {
    var data = {};
    dataSources.forEach(function (source) {
      var name = prop(source, "Name", "name", prop(source, "Id", "id", ""));
      var json = prop(source, "Json", "json", "{}") || "{}";
      try {
        data[name] = JSON.parse(json);
      } catch (error) {
        data[name] = null;
      }
    });
    return "<script>window.RNAssistantData=" + escapeScriptJson(JSON.stringify(data)) + ";</script>";
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

  function previewViewportReset() {
    return "<style data-rn-preview-reset>html,body{min-height:100%;margin:0;}*,*::before,*::after{box-sizing:border-box;}</style>";
  }

  function previewContentSecurityPolicy() {
    return "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; connect-src 'none'; img-src data: blob:; font-src data:; media-src data: blob:; style-src 'unsafe-inline'; script-src 'unsafe-inline' blob: data:; frame-src 'none'; child-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none';\">";
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
    var files = options.files || [];
    var dataSources = options.dataSources || [];
    var file = activeHtmlFile(files, options.activeFileId || "");
    var html = file ? fileContent(file) : "";
    var headInject = previewContentSecurityPolicy() + "\n" + previewViewportReset() + "\n" + networkBridgeScript() + "\n" + dataScript(dataSources) + "\n" + cssBlock(files);
    var bodyInject = scriptBlock(files);
    if (!html.trim()) {
      html = "<div style=\"font-family:Segoe UI,Arial,sans-serif;padding:24px;color:#475467\">HTML workspace пуст.</div>";
    }
    if (/<html[\s>]/i.test(html)) {
      if (/<head[\s>]/i.test(html)) {
        html = html.replace(/<head([^>]*)>/i, function (match) { return match + "\n" + headInject; });
      } else {
        html = html.replace(/<html[^>]*>/i, function (match) { return match + "<head>" + headInject + "</head>"; });
      }
      if (!bodyInject) return html;
      if (/<\/body>/i.test(html)) return html.replace(/<\/body>/i, bodyInject + "\n</body>");
      if (/<\/html>/i.test(html)) return html.replace(/<\/html>/i, bodyInject + "\n</html>");
      return html + "\n" + bodyInject;
    }
    return "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" + headInject + "</head><body>" + html + "\n" + bodyInject + "</body></html>";
  }

  window.RNAssistantHtmlWorkspacePreview = { build: build };
}());
