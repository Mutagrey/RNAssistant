(function () {
  "use strict";

  // Runs only inside a host-built preview. The page DOM is untrusted evidence;
  // this script grants no file, resource, or chat authority.
  function install(generation) {
    if (!Number.isSafeInteger(generation) || generation < 1) return;
    var active = false, disposed = false, hovered = null, selectedNode = null;
    var pointer = null, pointerInside = false;
    var overlay = null, tooltip = null, frame = 0, unsupportedFrame = false, observer = null;
    var accent = "#35684b", accentInk = "#ffffff";
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
    function palette(value) {
      if (!value || !/^#[0-9a-f]{6}$/i.test(value.accent || "") ||
          !/^#[0-9a-f]{6}$/i.test(value.ink || "")) return;
      accent = value.accent; accentInk = value.ink;
      if (hovered) show(hovered);
    }
    function show(node) {
      hovered = node && node.isConnected ? node : null;
      if (!hovered) {
        if (overlay) overlay.style.display = "none";
        if (tooltip) tooltip.style.display = "none";
        return;
      }
      if (!overlay) {
        overlay = document.createElement("div");
        overlay.dataset.rnaVisualHighlight = "true";
        overlay.setAttribute("aria-hidden", "true");
        overlay.style.cssText = "position:fixed;pointer-events:none;z-index:2147483646;box-sizing:border-box;display:none";
        document.documentElement.appendChild(overlay);
      }
      var rect = hovered.getBoundingClientRect();
      var rgb = [1, 3, 5].map(function (at) { return parseInt(accent.slice(at, at + 2), 16); }).join(",");
      overlay.style.border = "2px solid " + accent;
      overlay.style.background = "rgba(" + rgb + ",.08)";
      overlay.style.boxShadow = "0 0 0 3px rgba(" + rgb + ",.18)";
      overlay.style.borderRadius = getComputedStyle(hovered).borderRadius;
      overlay.style.left = rect.left + "px"; overlay.style.top = rect.top + "px";
      overlay.style.width = rect.width + "px"; overlay.style.height = rect.height + "px";
      var visible = rect.width > 0 && rect.height > 0 && rect.bottom > 0 && rect.right > 0 &&
        rect.top < innerHeight && rect.left < innerWidth;
      overlay.style.display = visible ? "block" : "none";
      if (!tooltip) {
        tooltip = document.createElement("div");
        tooltip.dataset.rnaVisualTooltip = "true";
        tooltip.setAttribute("aria-hidden", "true");
        tooltip.style.cssText = "position:fixed;pointer-events:none;z-index:2147483647;max-width:calc(100vw - 8px);padding:4px 7px;border-radius:5px;font:600 11px/1.35 system-ui,sans-serif;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;box-shadow:0 2px 8px #0003;display:none";
        document.documentElement.appendChild(tooltip);
      }
      var safe = attributes(hovered), label = hovered.localName.toLowerCase();
      if (safe.id) label += "#" + safe.id.slice(0, 48);
      else if (safe["data-ui-id"]) label += " [" + safe["data-ui-id"].slice(0, 48) + "]";
      tooltip.textContent = label + "  " + Math.round(rect.width) + " × " + Math.round(rect.height);
      tooltip.style.background = accent; tooltip.style.color = accentInk;
      tooltip.style.display = visible ? "block" : "none";
      tooltip.style.left = Math.max(4, Math.min(rect.left, innerWidth - tooltip.offsetWidth - 4)) + "px";
      tooltip.style.top = Math.max(4, Math.min(rect.top >= 30 ? rect.top - tooltip.offsetHeight - 5 : rect.bottom + 5,
        innerHeight - tooltip.offsetHeight - 4)) + "px";
    }
    function target(event) {
      var node = document.elementFromPoint(event.clientX, event.clientY);
      return node === overlay ? null : node;
    }
    function onMove(event) {
      pointer = { x: event.clientX, y: event.clientY }; pointerInside = true;
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
    function onOut(event) {
      if (!event.relatedTarget) { pointerInside = false; show(selectedNode); }
    }
    function onLeave() { pointerInside = false; show(selectedNode); }
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
      frame = requestAnimationFrame(function () {
        frame = 0;
        if (active) {
          var current = pointerInside && pointer ? document.elementFromPoint(pointer.x, pointer.y) : selectedNode;
          show(current === overlay || current === tooltip || current && current.localName === "iframe" ? null : current);
        }
        emitLocations();
      });
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
      document.addEventListener("pointerout", onOut, true);
      document.documentElement.addEventListener("pointerleave", onLeave);
      document.addEventListener("click", onClick, true);
      document.addEventListener("keydown", onKey, true);
      observer = new MutationObserver(function () {
        if (selectedNode && !selectedNode.isConnected) {
          selectedNode = null; show(null); emit("invalidated"); return;
        }
        if (hovered && !hovered.isConnected) show(null);
        else refresh();
      });
      observer.observe(document.documentElement, { childList: true, subtree: true });
    }
    function stop() {
      if (!active) return;
      active = false;
      document.removeEventListener("pointermove", onMove, true);
      document.removeEventListener("pointerover", onMove, true);
      document.removeEventListener("pointerout", onOut, true);
      document.documentElement.removeEventListener("pointerleave", onLeave);
      document.removeEventListener("click", onClick, true);
      document.removeEventListener("keydown", onKey, true);
      if (observer) { observer.disconnect(); observer = null; }
      selectedNode = null;
      if (frame) { cancelAnimationFrame(frame); frame = 0; }
      unsupportedFrame = false;
      pointer = null; pointerInside = false;
      show(null);
    }
    document.addEventListener("scroll", refresh, true);
    window.addEventListener("resize", refresh);
    function onControl(event) {
      var message = event.data;
      if (disposed || event.source !== window.parent || !message ||
          message.type !== "rnassistant-visual-inspector-control" || message.generation !== generation) return;
      if (message.operation === "probe") emit("ready");
      else if (message.operation === "activate") { palette(message.palette); start(); emit("activated"); }
      else if (message.operation === "palette") palette(message.palette);
      else if (message.operation === "pointer-exit") { pointerInside = false; show(selectedNode); }
      else if (message.operation === "deactivate") stop();
      else if (message.operation === "watch") {
        watches = Array.isArray(message.watches) ? message.watches.filter(function (item) {
          return item && typeof item.id === "string" && item.id.length <= 128 &&
            typeof item.selector === "string" && item.selector.length <= 1024 &&
            typeof item.tag === "string" && item.tag.length <= 32 &&
            (!item.stableUiId || typeof item.stableUiId === "string" && item.stableUiId.length <= 128);
        }).slice(0, 100) : [];
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
        if (tooltip) { tooltip.remove(); tooltip = null; }
      }
    }
    window.addEventListener("message", onControl);
    emit("ready");
  }

  function createHost(options) {
    var frame = options.frame, button = options.button, status = options.status;
    var generation = 0, ready = false, active = false, activating = false, selected = null, watched = [];
    var pendingReveal = null, lastParentPointerExit = 0;
    var themeObserver = null;
    if (!frame || !button || !status) throw new Error("Visual inspector host is incomplete.");
    function changed(reason) {
      if (typeof options.onSelectionChanged === "function")
        options.onSelectionChanged(!!(active && ready && selected), reason || "state");
    }
    function control(operation, id) {
      var palette = null;
      if (operation === "activate" || operation === "palette") {
        var style = getComputedStyle(document.documentElement);
        palette = { accent: style.getPropertyValue("--accent").trim() || "#35684b",
          ink: style.getPropertyValue("--accent-ink").trim() || "#ffffff" };
      }
      if (frame.contentWindow) frame.contentWindow.postMessage({
        type: "rnassistant-visual-inspector-control", generation: generation, operation: operation,
        watches: operation === "watch" ? watched : undefined, id: id, palette: palette }, "*");
    }
    function paint() {
      button.disabled = !ready || activating || typeof options.enabled === "function" && !options.enabled();
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
      if (!ready || activating || button.disabled) return;
      if (!active) {
        activating = true; control("activate"); paint();
        status.textContent = "Включаем выбор элемента…";
      } else deactivate();
    }
    function deactivate() {
      if (!active && !activating) return;
      active = false; activating = false; selected = null; control("deactivate"); paint(); changed("deactivate");
    }
    function onParentPointerMove() {
      var now = Date.now();
      if (!active || !ready || now - lastParentPointerExit < 120) return;
      lastParentPointerExit = now; control("pointer-exit");
    }
    function onWindowBlur() { if (active && ready) control("pointer-exit"); }
    function onVisibilityChange() { if (document.hidden) onWindowBlur(); }
    frame.addEventListener("load", onLoad);
    window.addEventListener("message", onMessage);
    window.addEventListener("pointermove", onParentPointerMove, true);
    window.addEventListener("blur", onWindowBlur);
    document.addEventListener("visibilitychange", onVisibilityChange);
    button.addEventListener("click", onClick);
    themeObserver = new MutationObserver(function () { if (ready && active) control("palette"); });
    themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "data-accent", "style"] });
    paint();
    return { reset: reset, deactivate: deactivate, refresh: paint, generation: function () { return generation; },
      watch: function (items) { watched = Array.isArray(items) ? items.slice(0, 100) : []; if (ready) control("watch"); },
      reveal: function (id) { if (ready) control("reveal", id); else pendingReveal = id; },
      selection: function () { return selected; },
      selectionRecord: function () { return active && ready && selected ? { generation: generation, selection: selected } : null; },
      dispose: function () {
        reset(); frame.removeEventListener("load", onLoad);
        window.removeEventListener("message", onMessage); button.removeEventListener("click", onClick);
        window.removeEventListener("pointermove", onParentPointerMove, true);
        window.removeEventListener("blur", onWindowBlur);
        document.removeEventListener("visibilitychange", onVisibilityChange);
        themeObserver.disconnect();
      } };
  }

  window.RNAssistantVisualInspector = {
    script: function (generation) { return "(" + install.toString() + ")(" + JSON.stringify(generation) + ");"; },
    install: install,
    createHost: createHost
  };
}());
