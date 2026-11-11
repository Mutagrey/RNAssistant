(function () {
  "use strict";
  // Immutable source views share UI lifetime, not data/protocol interpretation.
  function create(options) {
    var root = document.createElement("section"); root.className = "browser-object-view";
    root.setAttribute("aria-label", options.label.title);
    var content = document.createElement("div"); content.className = "browser-object-content";
    root.appendChild(content);
    var active = false, disposed = false, generation = 0, pending = false, value = null, abort, comparison = null, selectedMode = null;
    var surface = RNAssistantPreviewSurface.attach(root, { reload: reload,
      address: options.address, storage: options.storage, details: options.details, annotationUnavailable: options.annotationUnavailable,
      discuss: options.discuss && function () { if (!disposed) return options.discuss({ isCurrent: function () { return !disposed; } }); },
      reloadLabel: "Повторить загрузку", canReload: function () { return active && !pending; } });
    var host = document.createElement("div"); host.className = "browser-view";
    var renderer = RNAssistantViewerRegistry.mount("browser", host, { surface: root, portalHost: document.querySelector(".shell") });
    function current(version) { return active && !disposed && version === generation; }
    function disposeValue(item) { if (item && item.dispose) item.dispose(); }
    function stop() { generation++; pending = false; if (abort) abort.abort(); if (!value && !comparison) surface.clearDocument(); }
    function status(message) { if (!disposed) surface.waiting(options.label, message || " "); }
    function selectMode(mode) {
      if (!active || disposed || !value && mode !== "diff") return;
      if (mode === "diff" && !comparison) return;
      var presentation = !value ? { supportedModes: [] } : mode === "diff" || !value.selectMode ? value.presentation : value.selectMode(mode);
      selectedMode = mode;
      content.hidden = mode === "diff";
      if (comparison) comparison[mode === "diff" ? "activate" : "deactivate"]();
      surface.setDocument(Object.assign({}, presentation, { mode: mode,
        supportedModes: (presentation.supportedModes || ["preview"]).concat(comparison ? ["diff"] : []) }), selectMode);
    }
    function mountComparison(config) {
      if (!config) return;
      comparison = RNAssistantBrowserSourceDiff.create(config); root.appendChild(comparison.element);
    }
    async function load() {
      if (!active || disposed || pending || value || comparison) return;
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
        mountComparison(value.comparison);
        if (value.presentation) selectMode(value.presentation.mode || "preview");
      } catch (error) {
        if (current(version)) {
          pending = false; surface.fail(options.label, error.detail || error.message);
          // Preview/data failure must not prevent an explicit read of immutable sources.
          mountComparison(options.comparison);
          if (comparison) surface.setDocument({ supportedModes: ["diff"] }, selectMode);
        }
      }
    }
    function clearComparison() { if (comparison) { comparison.dispose(); comparison.element.remove(); comparison = null; } selectedMode = null; content.hidden = false; }
    function reload() { stop(); clearComparison(); surface.clearDocument(); disposeValue(value); value = null; content.replaceChildren(); return load(); }
    if (options.actions) options.actions(surface.actions, { status: status, error: function (error) { if (!disposed) surface.fail(options.label, error.detail || error.message); }, signal: function () { return abort && abort.signal; }, isCurrent: function () { return active && !disposed; },
      captureCurrent: function () { var version = generation; return function () { return current(version); }; } });
    surface.waiting(options.label, "Откройте вкладку для загрузки.");
    var events = new AbortController();
    window.addEventListener("pagehide", function (event) { if (!event.persisted) result.dispose(); }, { signal: events.signal });
    var result = { element: host, layout: renderer.layout,
      activate: function () { if (disposed || active) return; active = true; renderer.activate(); if (comparison && selectedMode === "diff") comparison.activate(); load(); },
      deactivate: function () { if (!active) return; active = false; stop(); if (comparison) comparison.deactivate(); surface.collapse(); renderer.deactivate(); RNAssistantActionMenu.closeWithin(root); },
      dispose: function () { if (disposed) return; disposed = true; active = false; events.abort(); stop(); disposeValue(value); value = null;
        clearComparison(); surface.clear(); RNAssistantActionMenu.closeWithin(root); RNAssistantViewerRegistry.unmount(host); root.replaceChildren(); }
    };
    return result;
  }
  window.RNAssistantBrowserObjectView = { create: create };
}());
