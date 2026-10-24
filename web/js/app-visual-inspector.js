(function () {
  "use strict";

  // Runs only inside a host-built preview. The page DOM is untrusted evidence;
  // this script grants no file, resource, or chat authority.
  function install(generation) {
    if (!Number.isSafeInteger(generation) || generation < 1) return;
    var active = false, disposed = false, hovered = null, selectedNode = null;
    var overlay = null, frame = 0, unsupportedFrame = false, observer = null;
    var allowed = ["id", "class", "role", "data-ui-id", "data-testid"];
    var sensitive = "input,textarea,select,option,[contenteditable],[data-private],[data-sensitive],script,style,template,noscript";

    function emit(kind, selection) {
      window.parent.postMessage({ type: "rnassistant-visual-inspector-" + kind,
        generation: generation, selection: selection || null }, "*");
    }
    function safeValue(value) {
      value = String(value || "").trim();
      return value.length <= 128 && !/(password|secret|token|api.?key|bearer|credential|authorization|eyJ[A-Za-z0-9_-]{16,}|sk-[A-Za-z0-9_-]{12,})/i.test(value)
        ? value : "";
    }
    function attributes(node) {
      if (node.matches(sensitive)) return {};
      var result = {};
      allowed.forEach(function (name) {
        var value = safeValue(node.getAttribute(name));
        if (value) result[name] = value;
      });
      return result;
    }
    function textExcerpt(node) {
      if (node.matches(sensitive)) return "";
      var result = "", visited = 0;
      var walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
      while (visited++ < 256 && result.length < 512 && walker.nextNode()) {
        var parent = walker.currentNode.parentElement;
        if (parent && parent.closest(sensitive)) continue;
        result += " " + String(walker.currentNode.nodeValue || "");
      }
      return result.replace(/\s+/g, " ").trim().slice(0, 512);
    }
    function selector(node) {
      var parts = [], current = node;
      while (current && current.nodeType === 1 && parts.length < 64) {
        var tag = current.localName.toLowerCase(), index = 1, scanned = 0, sibling = current.previousElementSibling;
        while (sibling && scanned++ < 10000) {
          if (sibling.localName === current.localName) index++;
          sibling = sibling.previousElementSibling;
        }
        if (sibling) return "";
        parts.unshift(tag + ":nth-of-type(" + index + ")");
        current = current.parentElement;
      }
      if (current) return "";
      var path = parts.join(" > ");
      return path.length <= 1024 ? path : "";
    }
    function number(value) { return Number.isFinite(value) ? Math.round(value * 10) / 10 : 0; }
    function snapshot(node) {
      if (!node || node.getRootNode() !== document || node.localName === "iframe" || node.shadowRoot) return null;
      var rect = node.getBoundingClientRect(), ancestors = [], current = node.parentElement;
      while (current && ancestors.length < 8) {
        ancestors.push({ tag: current.localName.toLowerCase(), attributes: attributes(current) });
        current = current.parentElement;
      }
      var selectedAttributes = attributes(node);
      var result = { tag: node.localName.toLowerCase(), stableUiId: selectedAttributes["data-ui-id"] || "",
        attributes: selectedAttributes, selector: selector(node), ancestry: ancestors,
        textExcerpt: textExcerpt(node), rect: { x: number(rect.x), y: number(rect.y),
          width: number(rect.width), height: number(rect.height) },
        viewport: { width: number(window.innerWidth), height: number(window.innerHeight),
          dpr: number(window.devicePixelRatio || 1) } };
      return new TextEncoder().encode(JSON.stringify(result)).length <= 16384 ? result : null;
    }
    function show(node) {
      hovered = node && node.isConnected ? node : null;
      if (!hovered) { if (overlay) overlay.style.display = "none"; return; }
      if (!overlay) {
        overlay = document.createElement("div");
        overlay.setAttribute("aria-hidden", "true");
        overlay.style.cssText = "position:fixed;pointer-events:none;z-index:2147483647;border:2px solid #3b82f6;background:rgba(59,130,246,.10);box-sizing:border-box;display:none";
        document.documentElement.appendChild(overlay);
      }
      var rect = hovered.getBoundingClientRect();
      overlay.style.left = rect.left + "px"; overlay.style.top = rect.top + "px";
      overlay.style.width = rect.width + "px"; overlay.style.height = rect.height + "px";
      overlay.style.display = rect.width && rect.height ? "block" : "none";
    }
    function target(event) {
      var node = document.elementFromPoint(event.clientX, event.clientY);
      return node === overlay ? null : node;
    }
    function onMove(event) {
      var node = target(event);
      if (node && node.localName === "iframe") {
        show(null);
        if (!unsupportedFrame) { selectedNode = null; emit("unsupported"); }
        unsupportedFrame = true;
        return;
      }
      unsupportedFrame = false;
      show(node);
    }
    function onClick(event) {
      var node = target(event);
      event.preventDefault(); event.stopImmediatePropagation();
      var selection = snapshot(node);
      selectedNode = selection ? node : null;
      emit(selection ? "selected" : "unsupported", selection);
      show(selection ? node : null);
    }
    function refresh() {
      if (frame) return;
      frame = requestAnimationFrame(function () { frame = 0; show(hovered); });
    }
    function onKey(event) {
      if (event.key !== "Escape") return;
      event.preventDefault(); event.stopImmediatePropagation();
      stop(); emit("stopped");
    }
    function start() {
      if (active || disposed) return;
      active = true;
      document.addEventListener("pointermove", onMove, true);
      document.addEventListener("pointerover", onMove, true);
      document.addEventListener("click", onClick, true);
      document.addEventListener("keydown", onKey, true);
      document.addEventListener("scroll", refresh, true);
      window.addEventListener("resize", refresh);
      observer = new MutationObserver(function () {
        if (!selectedNode || selectedNode.isConnected) return;
        selectedNode = null; show(null); emit("invalidated");
      });
      observer.observe(document.documentElement, { childList: true, subtree: true });
    }
    function stop() {
      if (!active) return;
      active = false;
      document.removeEventListener("pointermove", onMove, true);
      document.removeEventListener("pointerover", onMove, true);
      document.removeEventListener("click", onClick, true);
      document.removeEventListener("keydown", onKey, true);
      document.removeEventListener("scroll", refresh, true);
      window.removeEventListener("resize", refresh);
      if (observer) { observer.disconnect(); observer = null; }
      selectedNode = null;
      if (frame) { cancelAnimationFrame(frame); frame = 0; }
      unsupportedFrame = false;
      show(null);
    }
    function onControl(event) {
      var message = event.data;
      if (disposed || event.source !== window.parent || !message ||
          message.type !== "rnassistant-visual-inspector-control" || message.generation !== generation) return;
      if (message.operation === "probe") emit("ready");
      else if (message.operation === "activate") start();
      else if (message.operation === "deactivate") stop();
      else if (message.operation === "dispose") {
        stop(); disposed = true; window.removeEventListener("message", onControl);
        if (overlay) { overlay.remove(); overlay = null; }
      }
    }
    window.addEventListener("message", onControl);
  }

  function createHost(options) {
    var frame = options.frame, button = options.button, status = options.status;
    var generation = 0, ready = false, active = false, selected = null;
    if (!frame || !button || !status) throw new Error("Visual inspector host is incomplete.");
    function changed() { if (typeof options.onSelectionChanged === "function") options.onSelectionChanged(!!(active && ready && selected)); }
    function control(operation) {
      if (frame.contentWindow) frame.contentWindow.postMessage({
        type: "rnassistant-visual-inspector-control", generation: generation, operation: operation }, "*");
    }
    function paint() {
      button.disabled = !ready;
      button.setAttribute("aria-pressed", active ? "true" : "false");
      button.textContent = active ? "Выключить выбор" : "Выбрать элемент";
      if (!active) status.textContent = "";
    }
    function reset() {
      if (ready) control("dispose");
      generation++; ready = false; active = false; selected = null; paint(); changed();
      return generation;
    }
    function onLoad() {
      if (!generation) return;
      ready = false; active = false; selected = null; paint(); changed();
      control("probe");
    }
    function onMessage(event) {
      var message = event.data;
      if (!message || event.source !== frame.contentWindow || message.generation !== generation) return;
      if (message.type === "rnassistant-visual-inspector-ready") { ready = true; paint(); return; }
      if (message.type === "rnassistant-visual-inspector-stopped") { active = false; selected = null; paint(); changed(); return; }
      if (!active || !ready) return;
      if (message.type === "rnassistant-visual-inspector-unsupported") {
        selected = null;
        changed();
        status.textContent = "Этот элемент недоступен для выбора."; return;
      }
      if (message.type === "rnassistant-visual-inspector-invalidated") {
        selected = null;
        changed();
        status.textContent = "Выбранный элемент исчез из просмотра."; return;
      }
      if (message.type !== "rnassistant-visual-inspector-selected") return;
      var value = message.selection;
      if (!value || typeof value.tag !== "string" || value.tag.length > 32 ||
          typeof value.textExcerpt !== "string" || value.textExcerpt.length > 512 ||
          typeof value.selector !== "string" || value.selector.length > 1024 ||
          !Array.isArray(value.ancestry) || value.ancestry.length > 8 ||
          !value.attributes || Object.keys(value.attributes).length > 12) return;
      try { if (new TextEncoder().encode(JSON.stringify(value)).length > 16384) return; }
      catch (_) { return; }
      selected = value;
      changed();
      status.textContent = "Элемент (только просмотр): " + value.tag +
        (value.stableUiId ? " · " + value.stableUiId.slice(0, 128) : "") +
        (value.textExcerpt ? " · " + value.textExcerpt.slice(0, 80) : "");
    }
    function onClick() {
      if (!ready) return;
      active = !active;
      if (!active) selected = null;
      control(active ? "activate" : "deactivate");
      paint();
      changed();
      if (active) status.textContent = "Наведите и нажмите на элемент. Escape — выход.";
    }
    function deactivate() {
      if (!active) return;
      active = false; selected = null; control("deactivate"); paint(); changed();
    }
    frame.addEventListener("load", onLoad);
    window.addEventListener("message", onMessage);
    button.addEventListener("click", onClick);
    paint();
    return { reset: reset, deactivate: deactivate, generation: function () { return generation; },
      selection: function () { return selected; },
      selectionRecord: function () { return active && ready && selected ? { generation: generation, selection: selected } : null; },
      dispose: function () {
        reset(); frame.removeEventListener("load", onLoad);
        window.removeEventListener("message", onMessage); button.removeEventListener("click", onClick);
      } };
  }

  window.RNAssistantVisualInspector = {
    script: function (generation) { return "(" + install.toString() + ")(" + JSON.stringify(generation) + ");"; },
    createHost: createHost
  };
}());
