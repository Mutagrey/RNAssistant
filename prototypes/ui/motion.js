(function () {
  "use strict";
  var P = window.Prototype, media = matchMedia("(prefers-reduced-motion: reduce)");
  var disclosures = new WeakMap(), animations = new Set();
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
  P.motion = {
    capture: function (root) {
      var inspector = root.querySelector(".inspector-head");
      var runs = new Map(Array.from(root.querySelectorAll(".assistant-message[data-motion-key]")).map(function (node) {
        return [key(node), { height: node.getBoundingClientRect().height, complete: !!node.querySelector(".run-history") }];
      }));
      return { scene: root.dataset.motionScene, inspector: inspector && inspector.textContent, keys: new Set(Array.from(root.querySelectorAll(selector)).map(key)), runs: runs };
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
      var inspector = root.querySelector(".inspector"), heading = root.querySelector(".inspector-head");
      if (heading && heading.textContent !== before.inspector) reveal(inspector, true);
      else if (before.inspector && !heading) reveal(root.querySelector(".conversation-viewport"));
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
