(function () {
  "use strict";
  // Presentation only: no conversation data, drafts, transport or run ownership.
  var views = new WeakMap(), positionLimit = 16;
  function emptyCache() {
    return { units: Object.create(null), liveBase: null, liveBaseSignature: null,
      liveSuppressed: false, pastActivities: Object.create(null), pastSteps: Object.create(null),
      signature: null, liveRunId: null };
  }
  function get(box, options) {
    if (views.has(box)) return views.get(box);
    options = options || {};
    var button = options.button, key = null, generation = 0, visible = true, dirty = false, disposed = false;
    var frames = Object.create(null), positions = new Map(), listeners = [], bound = false;
    var following = true, position = 0, interaction = 0, pendingPosition = null, resizeObserver, contentObserver;
    var view = { cache: emptyCache() };
    function cancelFrames() {
      Object.keys(frames).forEach(function (name) {
        var frame = frames[name];
        if (frame.raf) window.cancelAnimationFrame(frame.id); else window.clearTimeout(frame.id);
      });
      frames = Object.create(null);
    }
    function invalidate() { ++generation; cancelFrames(); }
    function schedule(name, action) {
      if (!visible || disposed) { dirty = true; return; }
      if (frames[name]) return;
      var stamp = generation, frame = { raf: !!window.requestAnimationFrame };
      var callback = function () {
        if (frames[name] !== frame) return;
        delete frames[name];
        if (!disposed && visible && stamp === generation) action();
      };
      frames[name] = frame;
      frame.id = frame.raf ? window.requestAnimationFrame(callback) : window.setTimeout(callback, 16);
    }
    function distance() { return Math.max(0, box.scrollHeight - box.scrollTop - box.clientHeight); }
    function shouldFollow() {
      var atBottom = distance() <= 2;
      if (box.scrollTop > position) following = atBottom;
      else if (box.scrollTop < position && !atBottom) following = false;
      return following;
    }
    function updateButton() {
      if (!button || disposed) return;
      var show = box.scrollHeight > box.clientHeight + 2 && (distance() > 64 || !following && distance() > 2);
      button.classList.toggle("is-visible", show);
      button.setAttribute("aria-hidden", show ? "false" : "true"); button.tabIndex = show ? 0 : -1;
    }
    function bottom(smooth) {
      following = true;
      if (smooth && typeof box.scrollTo === "function" &&
          !(window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches))
        box.scrollTo({ top: box.scrollHeight, behavior: "smooth" });
      else box.scrollTop = box.scrollHeight;
      position = box.scrollTop; updateButton();
    }
    function sync(follow, smooth) {
      if (!visible || disposed) return;
      pendingPosition = null;
      if (follow) bottom(smooth); else updateButton();
      var gesture = interaction, top = box.scrollTop;
      if (follow) schedule("scroll", function () {
        if (gesture === interaction && following && box.scrollTop === top) bottom(false);
      });
    }
    function capture() {
      var rect = box.getBoundingClientRect && box.getBoundingClientRect();
      var snapshot = { top: rect && !rect.height ? position : box.scrollTop };
      if (!rect || !rect.height) return snapshot;
      Array.prototype.some.call(box.children, function (node) {
        if (node.getBoundingClientRect().bottom <= rect.top) return false;
        snapshot.key = node.messageUnitKey;
        snapshot.offset = node.getBoundingClientRect().top - rect.top; return true;
      });
      return snapshot;
    }
    function restore(snapshot) {
      pendingPosition = null;
      var unit = snapshot.key && view.cache.units[snapshot.key];
      if (unit && unit.node && unit.node.getBoundingClientRect)
        box.scrollTop += unit.node.getBoundingClientRect().top - box.getBoundingClientRect().top - snapshot.offset;
      else box.scrollTop = snapshot.top;
      position = box.scrollTop;
    }
    function remember() {
      if (key === null || disposed) return;
      positions.delete(key); positions.set(key, pendingPosition || { following: following, viewport: capture() });
      if (positions.size > positionLimit) positions.delete(positions.keys().next().value);
    }
    function suspend() {
      // Capture before reparenting: hidden/new containers can clamp scrollTop.
      pendingPosition = pendingPosition || { following: following, viewport: capture() };
      remember(); invalidate();
    }
    function reset() {
      if (options.clear) options.clear();
      view.cache = emptyCache(); box.textContent = "";
    }
    function begin(target, renderOptions) {
      if (disposed) throw new Error("Conversation view is disposed");
      var next = JSON.stringify(target), changed = key !== next, saved;
      renderOptions = renderOptions || {};
      if (changed) {
        remember(); invalidate(); key = next; saved = positions.get(key); pendingPosition = saved || null; reset();
        following = saved ? saved.following : true; position = box.scrollTop; ++interaction;
      }
      var viewport = pendingPosition ? pendingPosition.viewport : changed ? null : capture();
      if (renderOptions.forceScroll) { following = true; pendingPosition = null; }
      return { changed: changed, viewport: viewport,
        follow: !renderOptions.preserveTop && (!!renderOptions.forceScroll || (changed ? following : shouldFollow())) };
    }
    function listen(node, name, handler, opts) {
      node.addEventListener(name, handler, opts);
      listeners.push(function () { node.removeEventListener(name, handler, opts); });
    }
    function observe() {
      if (!resizeObserver || !visible || disposed) return;
      resizeObserver.observe(box);
      Array.prototype.forEach.call(box.children, function (node) { resizeObserver.observe(node, { box: "border-box" }); });
      contentObserver.observe(box, { childList: true });
    }
    function disconnect() {
      if (resizeObserver) resizeObserver.disconnect();
      if (contentObserver) contentObserver.disconnect();
    }
    function bind() {
      if (bound || disposed) return;
      bound = true;
      listen(box, "scroll", function () {
        if (!visible) return;
        if (box.scrollTop !== position) { ++interaction; shouldFollow(); position = box.scrollTop; }
        updateButton();
      }, { passive: true });
      function interrupt(up) { ++interaction; if (up) following = false; }
      listen(box, "wheel", function (event) { interrupt(event.deltaY < 0); }, { passive: true });
      listen(box, "pointerdown", function () { interrupt(false); }, { passive: true });
      var touchY = 0;
      listen(box, "touchstart", function (event) { touchY = event.touches[0].clientY; interrupt(false); }, { passive: true });
      listen(box, "touchmove", function (event) {
        var next = event.touches[0].clientY; interrupt(next > touchY); touchY = next;
      }, { passive: true });
      listen(box, "keydown", function (event) {
        if (/^(INPUT|TEXTAREA|SELECT)$/.test(event.target.tagName) || event.target.isContentEditable) return;
        if (["ArrowUp", "PageUp", "Home", "ArrowDown", "PageDown", "End", " "].indexOf(event.key) >= 0)
          interrupt(event.key === "ArrowUp" || event.key === "PageUp" || event.key === "Home" || event.key === " " && event.shiftKey);
      });
      if (button) listen(button, "click", function () { bottom(false); });
      if (window.ResizeObserver) {
        resizeObserver = new window.ResizeObserver(function () {
          if (visible && box.getBoundingClientRect().height > 0) sync(shouldFollow(), false);
        });
        contentObserver = new window.MutationObserver(function (records) {
          records.forEach(function (record) {
            Array.prototype.forEach.call(record.removedNodes, function (node) {
              if (node.nodeType === 1 && node.parentNode !== box) resizeObserver.unobserve(node);
            });
            Array.prototype.forEach.call(record.addedNodes, function (node) {
              if (node.nodeType === 1 && node.parentNode === box) resizeObserver.observe(node, { box: "border-box" });
            });
          });
        });
        observe();
      }
      updateButton();
    }
    function setVisible(value, refresh) {
      if (disposed || visible === value) return;
      if (!value) { remember(); dirty = dirty || !!frames.stream; invalidate(); disconnect(); visible = false; return; }
      visible = true; observe();
      var previous = key, saved = positions.get(key);
      if (dirty) { dirty = false; refresh(); }
      if (saved && key === previous) schedule("activate", function () {
        following = saved.following;
        if (following) bottom(false); else { restore(saved.viewport); updateButton(); }
      });
    }
    Object.assign(view, { begin: begin, reset: reset, suspend: suspend, schedule: schedule,
      matches: function (target) { return !disposed && key === JSON.stringify(target); },
      canRender: function () { if (disposed) return false; if (!visible) dirty = true; return visible; },
      setVisible: setVisible, bind: bind, capture: capture, restore: restore, shouldFollow: shouldFollow,
      updateButton: updateButton, bottom: bottom, sync: sync,
      positionChanged: function () { position = box.scrollTop; },
      follow: function () { following = true; position = box.scrollTop; },
      dispose: function () {
        if (disposed) return;
        invalidate(); disconnect(); listeners.forEach(function (remove) { remove(); }); listeners = [];
        reset(); positions.clear(); disposed = true; views.delete(box);
      }
    });
    views.set(box, view); return view;
  }
  window.RNAssistantConversationView = { get: get };
})();
