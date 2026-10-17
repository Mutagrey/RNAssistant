(function () {
  "use strict";
  var P = window.Prototype, shell = document.getElementById("app-shell");
  var storageKey = "rnassistant.prototype.panel-widths.v3", widths = {}, entries = [], drag, frame;
  var keys = ["navigation", "inspector"];
  function readWidths() {
    try {
      var saved = JSON.parse(localStorage.getItem(storageKey) || "{}"); widths = {};
      keys.forEach(function (key) { if (saved && Number.isFinite(saved[key]) && saved[key] > 0) widths[key] = saved[key]; });
    } catch (_) { /* Presentation preferences are optional in restricted browsers. */ }
  }
  readWidths();
  window.addEventListener("storage", function (event) { if (event.key === storageKey && !drag) { readWidths(); sync(); } });

  function persist() { try { localStorage.setItem(storageKey, JSON.stringify(widths)); } catch (_) {} }
  function visible(element) { return element && getComputedStyle(element).display !== "none"; }
  function active(entry) { return entry.container.getBoundingClientRect().width > 0 && visible(entry.pane) && visible(entry.peer) && getComputedStyle(entry.pane).position !== "absolute"; }
  function limits(entry) {
    var reserve = 280;
    if (entry.pane.id === "global-nav") {
      reserve += document.getElementById("section-rail").getBoundingClientRect().width;
      entries.forEach(function (other) { if (other !== entry && active(other)) reserve += other.min + other.handle.getBoundingClientRect().width; });
    }
    var max = Math.max(1, Math.min(entry.max, entry.container.clientWidth - reserve - entry.handle.getBoundingClientRect().width));
    return { min: Math.min(entry.min, max), max: max };
  }
  function clamp(value, range) { return Math.max(range.min, Math.min(range.max, value)); }
  function sync() {
    entries.forEach(function (entry) {
      entry.handle.hidden = !active(entry);
      entry.pane.style.removeProperty("width");
      if (entry.handle.hidden) return;
      var range = limits(entry), desired = widths[entry.key];
      var width = Math.round(clamp(desired == null ? entry.pane.getBoundingClientRect().width : desired, range));
      entry.pane.style.width = width + "px";
      entry.handle.setAttribute("aria-valuemin", Math.ceil(range.min));
      entry.handle.setAttribute("aria-valuemax", Math.floor(range.max));
      entry.handle.setAttribute("aria-valuenow", width);
      entry.handle.setAttribute("aria-valuetext", width + " пикселей");
    });
  }
  function schedule() {
    if (!frame) frame = requestAnimationFrame(function () { frame = null; sync(); });
  }
  function resize(entry, width) { widths[entry.key] = clamp(width, limits(entry)); sync(); }
  function finish(commit) {
    if (!drag) return;
    var previous = drag; drag = null;
    if (!commit) {
      if (previous.preference == null) delete widths[previous.entry.key];
      else widths[previous.entry.key] = previous.preference;
    }
    previous.entry.handle.classList.remove("dragging");
    document.body.classList.remove("resizing-panels");
    if (previous.entry.handle.hasPointerCapture(previous.pointerId)) previous.entry.handle.releasePointerCapture(previous.pointerId);
    if (commit) persist();
    sync();
  }
  function add(key, pane, peer, direction, min, max, label) {
    if (!pane || !peer) return;
    var handle = document.createElement("div");
    var entry = { key: key, pane: pane, peer: peer, direction: direction, min: min, max: max, container: pane.parentElement, handle: handle };
    pane.id = pane.id || "panel-" + key;
    pane.classList.add("resizable-pane");
    handle.className = "split-handle";
    handle.dataset.split = key;
    handle.tabIndex = 0;
    handle.setAttribute("role", "separator");
    handle.setAttribute("aria-orientation", "vertical");
    handle.setAttribute("aria-label", label);
    handle.setAttribute("aria-controls", pane.id);
    handle.title = label + ". Перетащите или используйте стрелки. Двойной щелчок — исходная ширина.";
    handle.hidden = true;
    pane.insertAdjacentElement(direction === 1 ? "afterend" : "beforebegin", handle);
    entries.push(entry);
  }
  function targetEntry(event) { return entries.find(function (entry) { return entry.handle === event.target; }); }
  shell.addEventListener("pointerdown", function (event) {
    var entry = targetEntry(event);
    if (!entry || event.button !== 0 || drag) return;
    event.preventDefault();
    // A pointer resize must not leave the keyboard focus ring on the full divider.
    if (document.activeElement === entry.handle) entry.handle.blur();
    drag = { entry: entry, pointerId: event.pointerId, x: event.clientX, width: entry.pane.getBoundingClientRect().width, preference: widths[entry.key] };
    entry.handle.setPointerCapture(event.pointerId);
    entry.handle.classList.add("dragging");
    document.body.classList.add("resizing-panels");
  });
  shell.addEventListener("pointermove", function (event) {
    if (!drag || drag.pointerId !== event.pointerId) return;
    if (!event.buttons) { finish(true); return; }
    resize(drag.entry, drag.width + (event.clientX - drag.x) * drag.entry.direction);
  });
  shell.addEventListener("pointerup", function (event) { if (drag && drag.pointerId === event.pointerId) finish(true); });
  shell.addEventListener("pointercancel", function (event) { if (drag && drag.pointerId === event.pointerId) finish(false); });
  shell.addEventListener("lostpointercapture", function (event) { if (drag && drag.pointerId === event.pointerId && drag.entry.handle === event.target) finish(true); });
  window.addEventListener("blur", function () { finish(true); });
  document.addEventListener("visibilitychange", function () { if (document.hidden) finish(true); });
  document.addEventListener("keydown", function (event) {
    if (drag && event.key === "Escape") { event.preventDefault(); event.stopImmediatePropagation(); finish(false); }
  }, true);
  shell.addEventListener("dblclick", function (event) {
    var entry = targetEntry(event);
    if (entry) { delete widths[entry.key]; sync(); persist(); }
  });
  shell.addEventListener("keydown", function (event) {
    var entry = targetEntry(event);
    if (!entry) return;
    var range = limits(entry), width = entry.pane.getBoundingClientRect().width, step = event.shiftKey ? 40 : 12;
    if (event.key === "ArrowLeft") width -= step * entry.direction;
    else if (event.key === "ArrowRight") width += step * entry.direction;
    else if (event.key === "Home") width = range.min;
    else if (event.key === "End") width = range.max;
    else if (event.key === "Enter") { event.preventDefault(); delete widths[entry.key]; sync(); persist(); return; }
    else return;
    event.preventDefault(); resize(entry, width); persist();
  });
  P.layout = {
    refresh: function () {
      finish(true);
      entries.forEach(function (entry) { entry.handle.remove(); });
      entries = [];
      var main = document.getElementById("main"), body = main.querySelector(".body-split");
      add("navigation", document.getElementById("global-nav"), main, 1, 240, 460, "Ширина дерева раздела");
      if (body) {
        add("inspector", body.querySelector(".inspector"), body.querySelector(".conversation"), -1, 220, 480, "Ширина правой панели");
      }
      sync();
    }
  };
  var observer = new ResizeObserver(schedule);
  observer.observe(document.getElementById("stage"));
  observer.observe(document.getElementById("main"));
}());
