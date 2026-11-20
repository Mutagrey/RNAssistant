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
  function staticHtml(html) {
    var value = String(html || "");
    value = value.replace(/<meta\b(?=[^>]*\bhttp-equiv\s*=\s*(?:"refresh"|'refresh'|refresh\b))[^>]*>/gi, "");
    return value.replace(/<(a|area)\b([^>]*)>/gi, function (tag, name, attributes) {
      attributes = attributes.replace(/\s(?:href|xlink:href|target|download)\s*=\s*(?:"[^"]*"|'[^']*'|[^\s>]+)/gi, "");
      return "<" + name + attributes + " aria-disabled=\"true\" tabindex=\"-1\">";
    });
  }
  function frameDocument(html) {
    // First policy cannot be relaxed by a second meta tag in authored HTML.
    var policy = "default-src 'none'; script-src 'none'; style-src 'unsafe-inline'; img-src data:; navigate-to 'none'; " +
      "connect-src 'none'; font-src 'none'; media-src 'none'; frame-src 'none'; worker-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none';";
    // Never parse authored HTML in the host document: even detached DOMParser
    // documents may fetch images. The browser parses it only after the fixed CSP
    // header in the sandbox. Keeping the source intact preserves html/body attributes.
    var doc = document.implementation.createHTMLDocument("");
    var meta = doc.createElement("meta"); meta.setAttribute("http-equiv", "Content-Security-Policy"); meta.content = policy;
    doc.head.insertBefore(meta, doc.head.firstChild);
    var style = doc.createElement("style");
    style.textContent = ":root{color-scheme:" + (dark() ? "dark" : "light") + ";}body{margin:16px;font:14px system-ui;background:" +
      (dark() ? "transparent;color:#e5e7e8" : "transparent;color:#252b28") + ";}svg{max-width:100%}.diagram{display:flex;justify-content:center;align-items:center;min-height:80px}.diagram svg[aria-roledescription^=flowchart] .node rect{rx:14px;ry:14px}.diagram svg .cluster rect{rx:18px;ry:18px}*{box-sizing:border-box}" +
      "*{animation:none!important;transition:none!important;scroll-behavior:auto!important}a,area{pointer-events:none!important}";
    doc.head.appendChild(style);
    return "<!doctype html><html>" + doc.head.outerHTML + html + "</html>";
  }
  function makeFrame(title, content) {
    var frame = document.createElement("iframe"); frame.className = "chat-visual-frame";
    frame.name = "rn-chat-preview-" + (++sequence); frame.title = title; frame.setAttribute("sandbox", "");
    frame.setAttribute("referrerpolicy", "no-referrer"); frame.srcdoc = content;
    return frame;
  }
  function mount(pre, language, source, blockId) {
    if (!pre.parentNode || pre.closest(".chat-visual")) return false;
    var wrap = document.createElement("section"); wrap.className = "chat-visual";
    if (language === "html") wrap.classList.add("kind-html");
    var tools = document.createElement("div"); tools.className = "block-tools";
    wrap.setAttribute("aria-label", language === "mermaid" ? "Схема" : "Интерактивный пример");
    var surface = document.createElement("div"); surface.className = "chat-visual-surface";
    var status = document.createElement("div"); status.className = "chat-visual-status"; status.setAttribute("role", "status");
    var token = 0, disposed = false, seen = false, htmlStarted = false, intersection;
    function resetHtml() {
      token += 1;
      Array.prototype.forEach.call(surface.querySelectorAll("iframe"), function (frame) {
        try { frame.removeAttribute("srcdoc"); frame.src = "about:blank"; } catch (ignore) {}
      });
      surface.replaceChildren(); surface.style.height = "";
      wrap.classList.remove("is-static");
      htmlStarted = false;
      if (play) { play.disabled = false; buttonLabel(play, "Запустить", "play"); }
    }
    function showCode(value) {
      pre.hidden = !value;
      if (language === "html") surface.hidden = value;
      codeButton.setAttribute("aria-expanded", String(value));
      buttonLabel(codeButton, value && language === "html" ? "Показать HTML" : value ? "Скрыть код" : "Показать код",
        value && language === "html" ? "eye" : "code");
      if (!value && language === "html" && !htmlStarted) startStaticHtml();
    }
    function fail(error) { resetHtml(); status.textContent = "Не удалось показать: " + (error.message || error); showCode(true); }
    function showSource() { showCode(pre.hidden); }
    var play = language === "html" ? button("Запустить", "play", openInteractiveHtml) : null;
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
    if (play) tools.appendChild(play);
    tools.appendChild(codeButton); tools.appendChild(copyButton); tools.appendChild(zoom);
    pre.parentNode.insertBefore(wrap, pre);
    wrap.appendChild(tools); wrap.appendChild(status); wrap.appendChild(surface); wrap.appendChild(pre); pre.hidden = true;
    status.textContent = language === "html" ? "Подготовка статического просмотра…" : "Схема ожидает просмотра…";
    function renderStaticHtml(reason) {
      if (disposed) return;
      resetHtml(); var current = token; htmlStarted = true;
      wrap.classList.add("is-static"); showCode(false);
      try {
        if (source.length > 200000) throw new Error("HTML превышает лимит 200 000 символов.");
        surface.appendChild(makeFrame("Статический HTML без выполнения скриптов", frameDocument(staticHtml(source))));
        status.textContent = reason || "Статический просмотр. Скрипты не выполняются.";
      } catch (error) { if (!disposed && current === token) fail(error); }
    }
    function startStaticHtml() { renderStaticHtml(null); }
    function openInteractiveHtml() {
      if (disposed) return;
      if (source.length > 200000) { fail(new Error("HTML превышает лимит 200 000 символов.")); return; }
      if (!window.RNAssistantBrowser || typeof window.RNAssistantBrowser.openHtmlPreview !== "function") {
        status.textContent = "Изолированный исполнитель Browser недоступен. Статический просмотр остаётся доступен.";
        return;
      }
      showCode(false); play.disabled = true; status.textContent = "Открываю изолированный Browser…";
      var conversation = wrap.closest(".conversation-panel");
      var reference = conversation ? RNAssistantConversationPanel.key(JSON.parse(conversation.dataset.conversation)) :
        window.RNAssistantDockWorkspace && RNAssistantDockWorkspace.activePanelId();
      if (!blockId) blockId = crypto.randomUUID();
      Promise.resolve().then(function () { return window.RNAssistantBrowser.openHtmlPreview(source, { title: "HTML из чата",
        referencePanel: reference, blockId: JSON.stringify([reference, blockId]) }); })
        .then(function (opened) {
          if (disposed) return; play.disabled = false;
          status.textContent = opened === false
            ? "Не удалось открыть изолированный Browser. Статический просмотр остаётся доступен."
            : "HTML передан в изолированный Browser. Статус выполнения показан во вкладке Browser.";
        }, function (error) {
          if (disposed) return; play.disabled = false;
          status.textContent = "Не удалось запустить скрипты: " + (error.detail || error.message || error);
        });
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
          var diagramFrame = makeFrame("Схема Mermaid", frameDocument('<div class="diagram">' + svg + "</div>"));
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
    var item = { node: wrap, dispose: function () {
      if (disposed) return; disposed = true; resetHtml(); if (intersection) intersection.disconnect(); mounted.delete(item);
    }, theme: function () { if (language === "mermaid" && seen) diagram(); } };
    wrap.__disposeChatVisual = item.dispose;
    mounted.add(item);
    if (language === "mermaid" || language === "html") {
      intersection = new IntersectionObserver(function (entries) {
        if (entries.some(function (entry) { return entry.isIntersecting; })) {
          seen = true; intersection.disconnect();
          if (language === "html") startStaticHtml(); else diagram();
        }
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
