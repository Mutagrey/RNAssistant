(function () {
  "use strict";

  // Runs only inside a host-built preview. The page DOM is untrusted evidence;
  // this script grants no file, resource, or chat authority.
  function install(generation) {
    if (!Number.isSafeInteger(generation) || generation < 1) return;
    var active = false, disposed = false, hovered = null, selectedNode = null;
    var overlay = null, frame = 0, unsupportedFrame = false, observer = null;
    var watches = [], watchObserver = null;
    var allowed = ["id", "class", "role", "data-ui-id", "data-testid"];
    var sensitive = "input,textarea,select,option,[contenteditable],[data-private],[data-sensitive],script,style,template,noscript";

    function emit(kind, selection) {
      window.parent.postMessage({ type: "rnassistant-visual-inspector-" + kind,
        generation: generation, selection: selection || null }, "*");
    }
    function emitLocations() {
      if (!watches.length) return;
      var locations = watches.map(function (item) {
        var nodes;
        try { nodes = document.querySelectorAll(item.selector); } catch (_) { return { id: item.id, status: "missing" }; }
        if (nodes.length !== 1 || nodes[0].localName !== item.tag ||
            (item.stableUiId && nodes[0].getAttribute("data-ui-id") !== item.stableUiId))
          return { id: item.id, status: "missing" };
        var rect = nodes[0].getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0 || rect.bottom <= 0 || rect.right <= 0 ||
            rect.top >= innerHeight || rect.left >= innerWidth) return { id: item.id, status: "outside" };
        return { id: item.id, status: "visible",
          rect: { x: rect.x, y: rect.y, width: rect.width, height: rect.height } };
      });
      window.parent.postMessage({ type: "rnassistant-visual-inspector-locations",
        generation: generation, locations: locations }, "*");
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
        overlay.style.cssText = "position:fixed;pointer-events:none;z-index:2147483647;border:2px solid #438b61;background:rgba(67,139,97,.10);box-sizing:border-box;display:none";
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
      frame = requestAnimationFrame(function () { frame = 0; if (active) show(hovered); emitLocations(); });
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
      if (observer) { observer.disconnect(); observer = null; }
      selectedNode = null;
      if (frame) { cancelAnimationFrame(frame); frame = 0; }
      unsupportedFrame = false;
      show(null);
    }
    document.addEventListener("scroll", refresh, true);
    window.addEventListener("resize", refresh);
    function onControl(event) {
      var message = event.data;
      if (disposed || event.source !== window.parent || !message ||
          message.type !== "rnassistant-visual-inspector-control" || message.generation !== generation) return;
      if (message.operation === "probe") emit("ready");
      else if (message.operation === "activate") { start(); emit("activated"); }
      else if (message.operation === "deactivate") stop();
      else if (message.operation === "watch") {
        watches = Array.isArray(message.watches) ? message.watches.filter(function (item) {
          return item && typeof item.id === "string" && item.id.length <= 128 &&
            typeof item.selector === "string" && item.selector.length <= 1024 &&
            typeof item.tag === "string" && item.tag.length <= 32 &&
            (!item.stableUiId || typeof item.stableUiId === "string" && item.stableUiId.length <= 128);
        }).slice(0, 10) : [];
        if (watchObserver) { watchObserver.disconnect(); watchObserver = null; }
        if (watches.length) {
          watchObserver = new MutationObserver(function (changes) {
            if (changes.some(function (change) { return change.target !== overlay; })) refresh();
          });
          watchObserver.observe(document.documentElement, { childList: true, subtree: true, attributes: true });
        }
        refresh();
      } else if (message.operation === "reveal") {
        var watched = watches.find(function (item) { return item.id === message.id; });
        var found = null;
        if (watched) try {
          var matches = document.querySelectorAll(watched.selector);
          if (matches.length === 1 && matches[0].localName === watched.tag &&
              (!watched.stableUiId || matches[0].getAttribute("data-ui-id") === watched.stableUiId)) found = matches[0];
        } catch (_) { /* The selector no longer resolves. */ }
        if (found) { found.scrollIntoView({ block: "center", inline: "nearest" }); refresh(); }
        emit("revealed", { id: message.id, found: !!found });
      } else if (message.operation === "dispose") {
        stop(); disposed = true; window.removeEventListener("message", onControl);
        document.removeEventListener("scroll", refresh, true);
        window.removeEventListener("resize", refresh);
        if (watchObserver) { watchObserver.disconnect(); watchObserver = null; }
        if (overlay) { overlay.remove(); overlay = null; }
      }
    }
    window.addEventListener("message", onControl);
  }

  function createHost(options) {
    var frame = options.frame, button = options.button, status = options.status;
    var generation = 0, ready = false, active = false, activating = false, selected = null, watched = [];
    var pendingReveal = null;
    if (!frame || !button || !status) throw new Error("Visual inspector host is incomplete.");
    function changed(reason) {
      if (typeof options.onSelectionChanged === "function")
        options.onSelectionChanged(!!(active && ready && selected), reason || "state");
    }
    function control(operation, id) {
      if (frame.contentWindow) frame.contentWindow.postMessage({
        type: "rnassistant-visual-inspector-control", generation: generation, operation: operation,
        watches: operation === "watch" ? watched : undefined, id: id }, "*");
    }
    function paint() {
      button.disabled = !ready || activating;
      button.setAttribute("aria-pressed", active ? "true" : "false");
      if (button.dataset.browserControl === "true" && window.RNAssistantIcons) {
        button.classList.add("chat-browser-annotate");
        button.innerHTML = window.RNAssistantIcons.render("selection") + "<span>Аннотировать</span>";
        button.setAttribute("aria-label", active ? "Выключить выбор элемента" : "Аннотировать: выбрать элемент");
        button.title = button.getAttribute("aria-label");
      } else {
        button.classList.remove("chat-browser-annotate");
        button.textContent = active ? "Выключить выбор" : "Выбрать элемент";
        button.removeAttribute("aria-label");
        button.removeAttribute("title");
      }
      if (!active) status.textContent = "";
    }
    function reset() {
      if (ready) control("dispose");
      generation++; ready = false; active = false; activating = false; selected = null; paint(); changed("reset");
      return generation;
    }
    function onLoad() {
      if (!generation) return;
      ready = false; active = false; activating = false; selected = null; paint(); changed("load");
      control("probe");
    }
    function onMessage(event) {
      var message = event.data;
      if (!message || event.source !== frame.contentWindow || message.generation !== generation) return;
      if (message.type === "rnassistant-visual-inspector-ready") {
        ready = true; paint(); if (watched.length) control("watch");
        if (pendingReveal) { control("reveal", pendingReveal); pendingReveal = null; }
        return;
      }
      if (message.type === "rnassistant-visual-inspector-revealed") {
        if (typeof options.onReveal === "function") options.onReveal(message.selection);
        return;
      }
      if (message.type === "rnassistant-visual-inspector-activated") {
        if (activating) {
          activating = false; active = true; paint(); changed();
          status.textContent = "Наведите и нажмите на элемент. Escape — выход.";
        }
        return;
      }
      if (message.type === "rnassistant-visual-inspector-locations") {
        if (ready && Array.isArray(message.locations) && typeof options.onLocations === "function")
          options.onLocations(message.locations);
        return;
      }
      if (message.type === "rnassistant-visual-inspector-stopped") { active = false; activating = false; selected = null; paint(); changed("stopped"); return; }
      if (!active || !ready) return;
      if (message.type === "rnassistant-visual-inspector-unsupported") {
        selected = null;
        changed("unsupported");
        status.textContent = "Этот элемент недоступен для выбора."; return;
      }
      if (message.type === "rnassistant-visual-inspector-invalidated") {
        selected = null;
        changed("invalidated");
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
      changed("selected");
      status.textContent = button.dataset.browserControl === "true" ? "" :
        "Элемент (только просмотр): " + value.tag +
        (value.stableUiId ? " · " + value.stableUiId.slice(0, 128) : "") +
        (value.textExcerpt ? " · " + value.textExcerpt.slice(0, 80) : "");
    }
    function onClick() {
      if (!ready || activating) return;
      if (!active) {
        activating = true; control("activate"); paint();
        status.textContent = "Включаем выбор элемента…";
      } else deactivate();
    }
    function deactivate() {
      if (!active && !activating) return;
      active = false; activating = false; selected = null; control("deactivate"); paint(); changed("deactivate");
    }
    frame.addEventListener("load", onLoad);
    window.addEventListener("message", onMessage);
    button.addEventListener("click", onClick);
    paint();
    return { reset: reset, deactivate: deactivate, refresh: paint, generation: function () { return generation; },
      watch: function (items) { watched = Array.isArray(items) ? items.slice(0, 10) : []; if (ready) control("watch"); },
      reveal: function (id) { if (ready) control("reveal", id); else pendingReveal = id; },
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
