(function () {
  "use strict";
  // A stable iframe surface follows a Dock/inline placeholder. No provider APIs or navigation authority.
  var layer;
  function host(root) {
    if (!layer) {
      layer = document.createElement("div"); layer.className = "browser-surface-layer";
      root.appendChild(layer);
    }
    if (layer.parentElement !== root) throw new Error("Browser portal host changed.");
    return layer;
  }
  function create(options) {
    if (!options || !options.surface) throw new Error("Browser surface is required.");
    var anchor = document.createElement("div"); anchor.className = "browser-view-anchor";
    anchor.tabIndex = 0; anchor.setAttribute("role", "group");
    anchor.setAttribute("aria-label", "Просмотр страницы. Enter — перейти к содержимому.");
    var surface = options.surface, active = false, disposed = false, queued = 0, last = "";
    var listeners = new AbortController(), container = host(options.portalHost || document.body);
    surface.classList.add("browser-view-surface"); surface.hidden = true; surface.inert = true;
    container.appendChild(surface);
    function place() {
      queued = 0;
      if (disposed) return;
      var rect = anchor.getBoundingClientRect(), shown = active && anchor.isConnected && rect.width > 0 && rect.height > 0 && !anchor.closest("[inert]");
      surface.hidden = !shown; surface.inert = !shown;
      if (!shown) { last = ""; return; }
      var box = container.getBoundingClientRect(), scale = box.width / container.clientWidth || 1;
      var values = [(rect.left - box.left) / scale, (rect.top - box.top) / scale, rect.width / scale, rect.height / scale];
      var key = values.map(function (value) { return value.toFixed(2); }).join("|");
      if (key !== last) {
        ["left", "top", "width", "height"].forEach(function (name, i) { surface.style[name] = values[i] + "px"; });
        last = key;
      }
    }
    function layout() { if (!disposed && !queued) queued = requestAnimationFrame(place); }
    var observer = new ResizeObserver(layout); observer.observe(anchor);
    window.addEventListener("pagehide", function (event) {
      if (!event.persisted) { cancelAnimationFrame(queued); observer.disconnect(); listeners.abort(); }
    }, { signal: listeners.signal });
    window.addEventListener("resize", layout, { signal: listeners.signal });
    document.addEventListener("scroll", layout, { capture: true, signal: listeners.signal });
    anchor.addEventListener("keydown", function (event) {
      if (event.key !== "Enter" || !active) return;
      var control = surface.querySelector('button:not([disabled]):not([hidden]), summary, iframe');
      if (control) { event.preventDefault(); control.focus(); }
    }, { signal: listeners.signal });
    return { element: anchor, surface: surface, layout: layout,
      activate: function () { active = true; layout(); },
      deactivate: function () { active = false; surface.hidden = true; surface.inert = true; last = ""; },
      destroy: function () { if (disposed) return; disposed = true; cancelAnimationFrame(queued); observer.disconnect(); listeners.abort(); surface.remove(); anchor.remove(); }
    };
  }
  RNAssistantViewerRegistry.register("browser", create);
}());
