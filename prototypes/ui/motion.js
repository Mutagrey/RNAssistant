(function () {
  "use strict";
  var P = window.Prototype, media = matchMedia("(prefers-reduced-motion: reduce)");
  var disclosures = new WeakMap(), panels = new WeakMap(), animations = new Set();
  var selector = "[data-motion-key], .timeline-item[data-scroll-key], details[data-disclosure]";
  function reduced() { return media.matches || P.state && P.state.motion === "reduced"; }
  function options() {
    var style = getComputedStyle(document.documentElement);
    return { duration: parseFloat(style.getPropertyValue("--motion-duration")) || 240, easing: style.getPropertyValue("--motion-ease").trim() || "ease-out" };
  }
  function animate(node, frames, done) {
    var animation = node.animate(frames, options());
    animations.add(animation);
    animation.onfinish = function () { animations.delete(animation); if (done) done(); };
    animation.oncancel = function () { animations.delete(animation); };
    return animation;
  }
  function settle() { if (reduced()) Array.from(animations).forEach(function (animation) { animation.finish(); }); }
  function reveal(node, horizontal) {
    if (!node || !node.getClientRects().length || !node.getBoundingClientRect().height) return;
    animate(node, [{ opacity: 0, transform: horizontal ? "translateX(10px)" : "translateY(6px)" }, { opacity: 1, transform: "none" }]);
  }
  function key(node) { return node.dataset.motionKey || node.dataset.disclosure || node.dataset.scrollKey; }
  function panelState(root) {
    var node = root.querySelector(".inspector");
    if (!node) return null;
    var style = getComputedStyle(node), content = node.querySelector(".inspector-content");
    return { node: node, width: node.getBoundingClientRect().width, contentWidth: content.getBoundingClientRect().width,
      overlay: style.position === "absolute", opacity: style.opacity, transform: style.transform, closing: node.classList.contains("is-panel-closing") };
  }
  function slidePanel(root, before) {
    var current = root.querySelector(".inspector"), previous = before.pane;
    if (previous && previous.node === current) return;
    if (previous && panels.has(previous.node)) { panels.get(previous.node).cancel(); panels.delete(previous.node); }
    if (current && previous && !previous.closing) {
      if (root.querySelector(".inspector-head").textContent !== before.inspector) reveal(current.querySelector(".inspector-body"), true);
      return;
    }
    var node = current || previous && previous.node, body = root.querySelector(".chat-layout");
    if (!node || !body) return;
    var opening = !!current, state = opening ? panelState(root) : previous;
    if (!opening) {
      body.classList.add("with-inspector"); node.classList.add("is-panel-closing");
      node.inert = true; node.setAttribute("aria-hidden", "true"); body.appendChild(node);
    }
    node.classList.add("is-panel-transition"); node.style.setProperty("--panel-motion-width", state.contentWidth + "px");
    var start = previous ? { width: previous.width + "px", opacity: previous.opacity, transform: previous.transform } : { width: "0px", opacity: 0, transform: "none" };
    var end = { width: opening ? state.width + "px" : "0px", opacity: opening ? 1 : 0, transform: "none" };
    if (state.overlay) {
      start.width = end.width = state.width + "px";
      if (!previous) start.transform = "translateX(100%)";
      if (!opening) end.transform = "translateX(100%)";
    }
    panels.set(node, animate(node, [start, end], function () {
      panels.delete(node); node.classList.remove("is-panel-transition"); node.style.removeProperty("--panel-motion-width");
      if (!opening) { node.remove(); body.classList.remove("with-inspector"); }
    }));
  }
  P.motion = {
    finishPanel: function (node) { var animation = panels.get(node); if (animation) animation.finish(); },
    capture: function (root) {
      var inspector = root.querySelector(".inspector-head");
      var runs = new Map(Array.from(root.querySelectorAll(".assistant-message[data-motion-key]")).map(function (node) {
        return [key(node), { height: node.getBoundingClientRect().height, complete: !!node.querySelector(".run-history") }];
      }));
      return { scene: root.dataset.motionScene, inspector: inspector && inspector.textContent, pane: panelState(root), keys: new Set(Array.from(root.querySelectorAll(selector)).map(key)), runs: runs };
    },
    render: function (root, before, scene) {
      root.dataset.motionScene = scene;
      settle(); if (reduced()) return;
      if (before.scene !== scene) reveal(root.querySelector(".conversation-viewport, .editor, .content-scroll") || root);
      else {
        var added = Array.from(root.querySelectorAll(selector)).filter(function (node) { return !before.keys.has(key(node)); });
        added.forEach(function (node) { if (!added.some(function (parent) { return parent !== node && parent.contains(node); })) reveal(node); });
        root.querySelectorAll(".assistant-message[data-motion-key]").forEach(function (node) {
          var previous = before.runs.get(key(node)), history = node.querySelector(".run-history");
          if (!previous || previous.complete || !history || history.open) return;
          var height = node.getBoundingClientRect().height;
          if (previous.height <= height) return;
          node.classList.add("motion-collapsing");
          animate(node, [{ height: previous.height + "px" }, { height: height + "px" }], function () { node.classList.remove("motion-collapsing"); });
        });
      }
      if (before.scene === scene) slidePanel(root, before);
    },
    toggle: function (group, event) {
      var current = disclosures.get(group), target = !(current ? current.target : group.open);
      if (group.classList.contains("popover")) return target;
      event.preventDefault();
      var height = group.getBoundingClientRect().height;
      if (current) { current.animation.cancel(); disclosures.delete(group); group.classList.remove("is-disclosing"); }
      if (reduced()) { group.open = target; return target; }
      group.open = true;
      var style = getComputedStyle(group), summary = group.querySelector(":scope > summary");
      var end = target ? group.getBoundingClientRect().height : summary.getBoundingClientRect().height +
        parseFloat(style.paddingTop) + parseFloat(style.paddingBottom) + parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth);
      group.classList.add("is-disclosing");
      var animation = animate(group, [{ height: height + "px" }, { height: end + "px" }], function () {
        group.open = target; group.classList.remove("is-disclosing"); disclosures.delete(group);
      });
      disclosures.set(group, { target: target, animation: animation });
      return target;
    }
  };
  media.addEventListener("change", settle);
}());
