(function () {
  "use strict";
  var mermaidLoad, renderQueue = Promise.resolve(), sequence = 0;
  var mounted = new Set();
  var observer;

  function button(label, icon, action) {
    var node = document.createElement("button");
    node.type = "button"; node.className = "icon-btn block-tool-button";
    buttonLabel(node, label, icon);
    node.addEventListener("click", action); return node;
  }
  function buttonLabel(node, label, icon) {
    node.title = label; node.setAttribute("aria-label", label);
    node.innerHTML = window.RNAssistantIcons.render(icon);
  }
  function loadMermaid() {
    if (!mermaidLoad) mermaidLoad = new Promise(function (resolve, reject) {
      var script = document.createElement("script");
      script.src = "js/vendor/mermaid.min.js";
      script.onload = function () { resolve(window.mermaid); };
      script.onerror = function () { script.remove(); mermaidLoad = null; reject(new Error("Mermaid не загрузился.")); };
      document.head.appendChild(script);
    });
    return mermaidLoad;
  }
  function dark() { return document.documentElement.getAttribute("data-theme") === "dark"; }
  function frameDocument(html, script, interactive) {
    // First policy cannot be relaxed by a second meta tag in authored HTML.
    var policy = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; " +
      "connect-src 'none'; font-src 'none'; media-src 'none'; frame-src 'none'; worker-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none';";
    // Never parse authored HTML in the host document: even detached DOMParser
    // documents may fetch images. The browser parses it only after the fixed CSP
    // header in the sandbox. Keeping the source intact preserves html/body attributes.
    var doc = document.implementation.createHTMLDocument("");
    var meta = doc.createElement("meta"); meta.setAttribute("http-equiv", "Content-Security-Policy"); meta.content = policy;
    doc.head.insertBefore(meta, doc.head.firstChild);
    var style = doc.createElement("style");
    style.textContent = ":root{color-scheme:" + (dark() ? "dark" : "light") + ";}body{margin:16px;font:14px system-ui;background:" +
      (dark() ? "transparent;color:#e5e7e8" : "transparent;color:#252b28") + ";}svg{max-width:100%}.diagram{display:flex;justify-content:center;align-items:center;min-height:80px}.diagram svg[aria-roledescription^=flowchart] .node rect{rx:14px;ry:14px}.diagram svg .cluster rect{rx:18px;ry:18px}*{box-sizing:border-box}";
    doc.head.appendChild(style);
    if (interactive) {
      var report = doc.createElement("script");
      report.textContent = "addEventListener('error',function(e){parent.postMessage({type:'rna.chatPreview.error',message:String(e.message).slice(0,500)},'*')});addEventListener('unhandledrejection',function(){parent.postMessage({type:'rna.chatPreview.error',message:'Ошибка асинхронного JavaScript'},'*')});";
      doc.head.insertBefore(report, meta.nextSibling);
    }
    if (script) {
      var vendor = doc.createElement("script"); vendor.textContent = script.replace(/<\/script/gi, "<\\/script");
      doc.head.insertBefore(vendor, meta.nextSibling);
    }
    return "<!doctype html><html>" + doc.head.outerHTML + html + "</html>";
  }
  function makeFrame(title, content, scripts) {
    var frame = document.createElement("iframe"); frame.className = "chat-visual-frame";
    frame.name = "rn-chat-preview-" + (++sequence);
    frame.title = title; frame.setAttribute("sandbox", scripts ? "allow-scripts" : "");
    frame.setAttribute("referrerpolicy", "no-referrer"); frame.srcdoc = content;
    return frame;
  }
  function mount(pre, language, source) {
    if (!pre.parentNode || pre.closest(".chat-visual")) return false;
    var wrap = document.createElement("section"); wrap.className = "chat-visual";
    var tools = document.createElement("div"); tools.className = "block-tools";
    wrap.setAttribute("aria-label", language === "mermaid" ? "Схема" : "Интерактивный пример");
    var surface = document.createElement("div"); surface.className = "chat-visual-surface";
    var status = document.createElement("div"); status.className = "chat-visual-status"; status.setAttribute("role", "status");
    var token = 0, disposed = false, seen = false, running = false, intersection;
    function stop() {
      token += 1; surface.replaceChildren();
      running = false; buttonLabel(play, "Запустить", "play");
    }
    function fail(error) { status.textContent = "Не удалось показать: " + (error.message || error); pre.hidden = false; }
    function showSource() { pre.hidden = !pre.hidden; codeButton.setAttribute("aria-expanded", String(!pre.hidden)); buttonLabel(codeButton, pre.hidden ? "Показать код" : "Скрыть код", "code"); }
    var codeButton = button("Показать код", "code", showSource); codeButton.setAttribute("aria-expanded", "false");
    var copyButton = button("Копировать", "copy", function () { window.copyText(source); });
    var zoom = button("Увеличить", "external", function () {
      var expanded = wrap.classList.toggle("chat-visual-expanded");
      buttonLabel(zoom, expanded ? "Свернуть" : "Увеличить", expanded ? "compress" : "external");
      zoom.setAttribute("aria-expanded", String(expanded));
    });
    zoom.setAttribute("aria-expanded", "false");
    wrap.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && wrap.classList.contains("chat-visual-expanded")) { zoom.click(); zoom.focus(); }
    });
    var play = button("Запустить", "play", function () {
      if (running) { stop(); status.textContent = "Просмотр остановлен."; }
      else startHtml();
    });
    if (language === "html") tools.appendChild(play);
    tools.appendChild(codeButton); tools.appendChild(copyButton); tools.appendChild(zoom);
    pre.parentNode.insertBefore(wrap, pre);
    wrap.appendChild(tools); wrap.appendChild(status); wrap.appendChild(surface); wrap.appendChild(pre); pre.hidden = true;
    status.textContent = language === "html" ? "Нажмите ▶ для просмотра" : "Схема ожидает просмотра…";
    async function startHtml() {
      stop(); var current = token; running = true; buttonLabel(play, "Остановить", "stop"); status.textContent = "Подготовка просмотра…";
      try {
        if (source.length > 200000) throw new Error("HTML превышает лимит 200 000 символов.");
        var js = "";
        // Only the already pinned ECharts package is admitted, never authored vendor directives.
        if (/\becharts\s*\./.test(source)) {
          var runtime = window.RNAssistantHtmlVendorRuntime;
          var files = [{ kind: "script", content: "echarts.init" }];
          await runtime.ensure(files); js = runtime.source(runtime.used(files)[0]).js;
        }
        if (disposed || current !== token) return;
        surface.appendChild(makeFrame("HTML-превью без доступа к сети", frameDocument(source, js, true), true));
        status.textContent = "";
      } catch (error) { if (!disposed && current === token) { stop(); fail(error); } }
    }
    function diagram() {
      var current = ++token; status.textContent = "Построение схемы…";
      renderQueue = renderQueue.catch(function () {}).then(async function () {
        if (disposed || current !== token) return;
        try {
          if (source.length > 30000) throw new Error("Схема превышает лимит 30 000 символов.");
          if (/%%\s*\{|^\s*---/m.test(source)) throw new Error("Конфигурация Mermaid внутри сообщения не поддерживается.");
          var mermaid = await loadMermaid();
          if (disposed || current !== token) return;
          mermaid.initialize({ startOnLoad: false, securityLevel: "strict", theme: "base", look: "classic",
            themeVariables: { fontFamily: "system-ui", fontSize: "14px",
              primaryColor: dark() ? "#2b2f34" : "#ffffff", primaryTextColor: dark() ? "#e5e7e8" : "#252b28",
              primaryBorderColor: dark() ? "#41484e" : "#d9dfdb", lineColor: dark() ? "#929d96" : "#849087" },
            htmlLabels: false, maxTextSize: 30000, maxEdges: 300, suppressErrorRendering: true, flowchart: { htmlLabels: false } });
          var result = await mermaid.render("rnaDiagram" + (++sequence), source);
          if (disposed || current !== token) return;
          // SVG lives in an inert frame: its styles/links cannot affect the transcript.
          var svg = DOMPurify.sanitize(result.svg, { USE_PROFILES: { svg: true, svgFilters: true }, FORBID_TAGS: ["foreignObject", "image", "a"] });
          var diagramFrame = makeFrame("Схема Mermaid", frameDocument('<div class="diagram">' + svg + "</div>", ""), false);
          var bounds = /viewBox="[^"]*?([\d.]+)\s+([\d.]+)"/.exec(svg);
          if (bounds && Number(bounds[1]) > 0) {
            var width = Math.max(200, surface.getBoundingClientRect().width - 32);
            diagramFrame.style.height = Math.min(420, Math.max(140, width * Number(bounds[2]) / Number(bounds[1]) + 40)) + "px";
          }
          surface.replaceChildren(diagramFrame);
          status.textContent = "";
        } catch (error) { if (!disposed && current === token) fail(error); }
      });
    }
    function frameError(event) {
      var frame = surface.querySelector("iframe");
      if (!disposed && frame && event.source === frame.contentWindow && event.data && event.data.type === "rna.chatPreview.error") {
        status.textContent = "Ошибка превью: " + String(event.data.message || "JavaScript").slice(0, 500);
      }
    }
    window.addEventListener("message", frameError);
    var item = { node: wrap, dispose: function () {
      if (disposed) return; disposed = true; window.removeEventListener("message", frameError); stop(); if (intersection) intersection.disconnect(); mounted.delete(item);
    }, theme: function () { if (language === "mermaid" && seen) diagram(); } };
    wrap.__disposeChatVisual = item.dispose;
    mounted.add(item);
    if (language === "mermaid") {
      intersection = new IntersectionObserver(function (entries) {
        if (entries.some(function (entry) { return entry.isIntersecting; })) { seen = true; intersection.disconnect(); diagram(); }
      });
      intersection.observe(wrap);
    }
    if (!observer) {
      observer = new MutationObserver(function (records) {
        mounted.forEach(function (entry) {
          if (!entry.node.isConnected) entry.dispose();
          else if (records.some(function (record) { return record.type === "attributes"; })) entry.theme();
        });
      });
      observer.observe(document.body, { childList: true, subtree: true });
      observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    }
    return true;
  }
  function clear(root) {
    root.querySelectorAll(".chat-visual").forEach(function (node) { if (node.__disposeChatVisual) node.__disposeChatVisual(); });
  }
  window.RNAssistantChatVisuals = Object.freeze({ mount: mount, clear: clear });
}());
