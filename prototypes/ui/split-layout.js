(function () {
  "use strict";
  var P = window.Prototype, shell = document.getElementById("app-shell");
  var storageKey = "rnassistant.prototype.panel-widths.v3", widths = {}, entries = [], drag, frame, navigationOpen = null;
  var layoutMode, compactNavigationOpen = false, navigationTransition;
  var keys = ["navigation", "inspector"];
  function readWidths() {
    try {
      var saved = JSON.parse(localStorage.getItem(storageKey) || "{}"); widths = {};
      keys.forEach(function (key) { if (saved && Number.isFinite(saved[key]) && saved[key] > 0) widths[key] = saved[key]; });
      navigationOpen = saved && typeof saved.navigationOpen === "boolean" ? saved.navigationOpen : null;
    } catch (_) { /* Presentation preferences are optional in restricted browsers. */ }
  }
  readWidths();
  window.addEventListener("storage", function (event) { if (event.key === storageKey && !drag) { readWidths(); sync(); } });

  function persist() { try { localStorage.setItem(storageKey, JSON.stringify(Object.assign({}, widths, { navigationOpen: navigationOpen }))); } catch (_) {} }
  function mode() {
    var width = document.getElementById("stage").clientWidth;
    return width <= 620 ? "single" : width <= 820 ? "compact" : "wide";
  }
  function setNavigationPreference(open) {
    if (layoutMode === "wide") navigationOpen = open;
    else compactNavigationOpen = open;
  }
  function syncNavigation() {
    var nav = document.getElementById("global-nav"), focused = document.activeElement;
    var nextMode = mode();
    // A temporary compact pane must never hide the chat after a window resize.
    if (nextMode !== layoutMode) { compactNavigationOpen = false; layoutMode = nextMode; }
    var open = !nav.hidden && (layoutMode === "wide" ? navigationOpen !== false : compactNavigationOpen);
    // Animate open/close, not every width update while resizing the window.
    if (shell.classList.contains("layout-ready") && shell.classList.contains("navigation-open") !== open) {
      clearTimeout(navigationTransition); shell.classList.add("navigation-transition");
      var duration = parseFloat(getComputedStyle(shell).getPropertyValue("--motion-duration")) || 240;
      navigationTransition = setTimeout(function () { shell.classList.remove("navigation-transition"); }, duration + 40);
    }
    shell.classList.toggle("navigation-single", layoutMode === "single");
    shell.classList.toggle("navigation-compact", layoutMode !== "wide");
    shell.classList.toggle("navigation-open", open); shell.classList.toggle("navigation-closed", !open);
    nav.inert = !open; nav.setAttribute("aria-hidden", String(!open));
    var main = document.getElementById("main"), replacesMain = layoutMode === "single" && open;
    main.inert = replacesMain; main.setAttribute("aria-hidden", String(replacesMain));
    var rail = document.getElementById("section-rail"), hidesRail = layoutMode === "single" && !open;
    rail.inert = hidesRail; rail.setAttribute("aria-hidden", String(hidesRail));
    document.querySelectorAll(".menu-btn").forEach(function (button) {
      var label = open ? "Свернуть навигацию" : "Открыть навигацию";
      button.setAttribute("aria-expanded", String(open)); button.setAttribute("aria-controls", "global-nav");
      button.setAttribute("aria-label", label); button.title = label;
    });
    if (!open && (nav.contains(focused) || focused.matches('[data-split="navigation"]'))) {
      var button = document.querySelector(".menu-btn"); if (button) button.focus({ preventScroll: true });
    }
  }
  function visible(element) { return element && getComputedStyle(element).display !== "none"; }
  function active(entry) {
    if (entry.key === "navigation" && (layoutMode === "single" || shell.classList.contains("navigation-closed"))) return false;
    return !document.getElementById("main").inert && entry.container.getBoundingClientRect().width > 0 && visible(entry.pane) && visible(entry.peer) && getComputedStyle(entry.pane).position !== "absolute";
  }
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
    if (drag && mode() !== layoutMode) { finish(false); return; }
    syncNavigation();
    entries.forEach(function (entry) {
      entry.handle.hidden = !active(entry);
      var range = limits(entry), desired = widths[entry.key];
      var width;
      if (entry.key === "navigation") {
        var defaultWidth = parseFloat(getComputedStyle(entry.pane).getPropertyValue("--pane-default"));
        width = layoutMode === "single" ? shell.clientWidth - 52 : Math.round(clamp(desired == null ? defaultWidth : desired, range));
        // Keep the content at its final width while its outer pane slides closed.
        // Reading the animated width here would feed it back into ResizeObserver.
        entry.pane.style.setProperty("--pane-width", width + "px");
      } else {
        if (entry.handle.hidden) return;
        width = Math.round(clamp(desired == null ? parseFloat(getComputedStyle(entry.pane).getPropertyValue("--pane-default")) : desired, range));
        entry.pane.style.setProperty("--pane-width", width + "px");
      }
      if (entry.handle.hidden) return;
      entry.handle.setAttribute("aria-valuemin", Math.ceil(range.min));
      entry.handle.setAttribute("aria-valuemax", Math.floor(range.max));
      entry.handle.setAttribute("aria-valuenow", width);
      entry.handle.setAttribute("aria-valuetext", width + " пикселей");
    });
    if (!shell.classList.contains("layout-ready")) requestAnimationFrame(function () { shell.classList.add("layout-ready"); });
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
    document.body.classList.add("resizing-panels"); P.motion.finishPanel(entry.pane);
    drag = { entry: entry, pointerId: event.pointerId, x: event.clientX, width: entry.pane.getBoundingClientRect().width, preference: widths[entry.key] };
    entry.handle.setPointerCapture(event.pointerId);
    entry.handle.classList.add("dragging");
  });
  shell.addEventListener("pointermove", function (event) {
    if (!drag || drag.pointerId !== event.pointerId) return;
    if (!event.buttons) { finish(true); return; }
    var width = drag.width + (event.clientX - drag.x) * drag.entry.direction;
    if (drag.entry.key === "navigation" && width < drag.entry.min - 12) {
      widths.navigation = drag.preference || drag.width; setNavigationPreference(false); finish(true); return;
    }
    resize(drag.entry, width);
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
    if (entry) { delete widths[entry.key]; if (entry.key === "navigation") setNavigationPreference(true); sync(); persist(); }
  });
  shell.addEventListener("keydown", function (event) {
    var entry = targetEntry(event);
    if (!entry) return;
    var range = limits(entry), width = Number(entry.handle.getAttribute("aria-valuenow")), step = event.shiftKey ? 40 : 12;
    if (event.key === "ArrowLeft") width -= step * entry.direction;
    else if (event.key === "ArrowRight") width += step * entry.direction;
    else if (event.key === "Home") width = range.min;
    else if (event.key === "End") width = range.max;
    else if (event.key === "Enter") { event.preventDefault(); delete widths[entry.key]; sync(); persist(); return; }
    else return;
    if (entry.key === "navigation" && (event.key === "Home" || width < range.min)) {
      event.preventDefault(); P.layout.setNavigation(false, true); return;
    }
    event.preventDefault(); resize(entry, width); persist();
  });
  P.layout = {
    setNavigation: function (open, focus) {
      sync(); setNavigationPreference(open); persist(); sync();
      if (focus) requestAnimationFrame(function () {
        var target = open ? document.querySelector('#global-nav [role="treeitem"][tabindex="0"]') || document.querySelector('#global-nav button, #global-nav summary') : document.querySelector('.menu-btn');
        if (target && target.focus) target.focus({ preventScroll: true });
      });
    },
    closeCompactNavigation: function () {
      if (mode() === "single" && shell.classList.contains("navigation-open")) P.layout.setNavigation(false);
    },
    refresh: function () {
      finish(true);
      entries.forEach(function (entry) { entry.handle.remove(); });
      entries = [];
      var main = document.getElementById("main"), body = main.querySelector(".body-split");
      add("navigation", document.getElementById("global-nav"), main, 1, 240, 460, "Ширина дерева раздела");
      if (body) {
        add("inspector", body.querySelector(".inspector"), body.querySelector(".chat-main"), -1, 220, 480, "Ширина правой панели");
      }
      sync();
    }
  };
  var observer = new ResizeObserver(schedule);
  observer.observe(document.getElementById("stage"));
  observer.observe(document.getElementById("main"));
}());
