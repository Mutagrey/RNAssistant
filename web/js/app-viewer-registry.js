(function () {
  "use strict";

  var allowedKinds = { image: true, json: true, markdown: true, task_list: true, text: true };
  var factories = Object.create(null);
  var mounted = typeof WeakMap === "function" ? new WeakMap() : null;

  function normalizeKind(kind) {
    return String(kind || "").trim().toLowerCase();
  }

  function register(kind, factory) {
    kind = normalizeKind(kind);
    if (!allowedKinds[kind]) throw new Error("Viewer kind is not allowlisted: " + kind);
    if (typeof factory !== "function") throw new Error("Viewer factory must be a function.");
    if (factories[kind] && factories[kind] !== factory) throw new Error("Viewer kind is already registered: " + kind);
    factories[kind] = factory;
  }

  function unmount(target) {
    if (!target) return;
    var current = mounted ? mounted.get(target) : target.__rnViewerController;
    if (current && typeof current.destroy === "function") current.destroy();
    if (mounted) mounted.delete(target); else delete target.__rnViewerController;
    if (typeof target.replaceChildren === "function") target.replaceChildren();
  }

  function mount(kind, target, options) {
    kind = normalizeKind(kind);
    if (!target || typeof target.replaceChildren !== "function") throw new Error("Viewer target is required.");
    if (!allowedKinds[kind] || !factories[kind]) throw new Error("Viewer is not registered: " + kind);
    unmount(target);
    var controller = factories[kind](options || {});
    if (!controller || !controller.element) throw new Error("Viewer factory returned an invalid controller.");
    target.appendChild(controller.element);
    if (mounted) mounted.set(target, controller); else target.__rnViewerController = controller;
    return controller;
  }

  window.RNAssistantViewerRegistry = {
    register: register,
    mount: mount,
    unmount: unmount,
    has: function (kind) { return !!factories[normalizeKind(kind)]; },
    kinds: function () { return Object.keys(factories).sort(); }
  };
}());
