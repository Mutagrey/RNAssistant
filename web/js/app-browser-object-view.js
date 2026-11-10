(function () {
  "use strict";
  // Immutable source views share UI lifetime, not data/protocol interpretation.
  function create(options) {
    var root = document.createElement("section"); root.className = "browser-object-view";
    root.setAttribute("aria-label", options.label.title);
    var content = document.createElement("div"); content.className = "browser-object-content";
    root.appendChild(content);
    var active = false, disposed = false, generation = 0, pending = false, value = null, abort;
    var surface = RNAssistantPreviewSurface.attach(root, { reload: reload,
      address: options.address, storage: options.storage, details: options.details, annotationUnavailable: options.annotationUnavailable,
      discuss: options.discuss && function () { if (!disposed) return options.discuss({ isCurrent: function () { return !disposed; } }); },
      reloadLabel: "Повторить загрузку", canReload: function () { return active && !pending; } });
    var host = document.createElement("div"); host.className = "browser-view";
    var renderer = RNAssistantViewerRegistry.mount("browser", host, { surface: root, portalHost: document.querySelector(".shell") });
    function current(version) { return active && !disposed && version === generation; }
    function disposeValue(item) { if (item && item.dispose) item.dispose(); }
    function stop() { generation++; pending = false; if (abort) abort.abort(); if (!value) surface.clearDocument(); }
    function status(message) { if (!disposed) surface.waiting(options.label, message || " "); }
    function selectMode(mode) {
      if (!active || disposed || !value || !value.presentation) return;
      surface.setDocument(value.selectMode(mode), selectMode);
    }
    async function load() {
      if (!active || disposed || pending || value) return;
      var version = ++generation; abort = new AbortController(); pending = true;
      surface.clearDocument();
      surface.waiting(options.label, "Загрузка сохранённой версии…");
      try {
        var loaded = await options.load({ signal: abort.signal, isCurrent: function () { return current(version); }, status: function (message) { if (current(version)) status(message); } });
        if (!current(version)) { disposeValue(loaded); return; }
        if (!loaded) throw new Error("Содержимое сохранённой версии недоступно.");
        value = loaded; pending = false;
        if (value.frame) surface.watch(value.frame, options.label); else status("");
        content.replaceChildren(value.element);
        if (value.presentation) surface.setDocument(value.presentation, selectMode);
      } catch (error) {
        if (current(version)) { pending = false; surface.fail(options.label, error.detail || error.message); }
      }
    }
    function reload() { stop(); surface.clearDocument(); disposeValue(value); value = null; content.replaceChildren(); return load(); }
    if (options.actions) options.actions(surface.actions, { status: status, error: function (error) { if (!disposed) surface.fail(options.label, error.detail || error.message); }, signal: function () { return abort && abort.signal; }, isCurrent: function () { return active && !disposed; },
      captureCurrent: function () { var version = generation; return function () { return current(version); }; } });
    surface.waiting(options.label, "Откройте вкладку для загрузки.");
    var events = new AbortController();
    window.addEventListener("pagehide", function (event) { if (!event.persisted) result.dispose(); }, { signal: events.signal });
    var result = { element: host, layout: renderer.layout,
      activate: function () { if (disposed || active) return; active = true; renderer.activate(); load(); },
      deactivate: function () { if (!active) return; active = false; stop(); surface.collapse(); renderer.deactivate(); RNAssistantActionMenu.closeWithin(root); },
      dispose: function () { if (disposed) return; disposed = true; active = false; events.abort(); stop(); disposeValue(value); value = null;
        surface.clear(); RNAssistantActionMenu.closeWithin(root); RNAssistantViewerRegistry.unmount(host); root.replaceChildren(); }
    };
    return result;
  }
  window.RNAssistantBrowserObjectView = { create: create };
}());
