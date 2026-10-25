(function () {
  var productNavigationKey = "rnassistant.ui.navigation.width";
  var productNavigationOpenKey = "rnassistant.ui.navigation.open";
  var productNavigationPreference = true;
  var compactNavigationOpen = false;
  var productLayoutMode = null;
  var navigationTransitionTimer = null;
  var navigationToggle = null;

  try { productNavigationPreference = window.localStorage.getItem(productNavigationOpenKey) !== "0"; }
  catch (error) { /* Presentation preferences are optional. */ }

  function navigationMode() {
    var shell = document.querySelector(".shell");
    var width = shell ? shell.getBoundingClientRect().width : window.innerWidth;
    return width <= 620 ? "single" : width <= 820 ? "compact" : "wide";
  }

  function navigationIsOpen() {
    return productLayoutMode === "wide" ? productNavigationPreference : compactNavigationOpen;
  }

  function syncProductNavigation() {
    var shell = document.querySelector(".shell");
    if (!shell) return;
    var mode = navigationMode();
    if (mode !== productLayoutMode) {
      productLayoutMode = mode;
      compactNavigationOpen = false;
    }
    var open = navigationIsOpen();
    if (shell.classList.contains("layout-ready") && shell.classList.contains("navigation-open") !== open) {
      clearTimeout(navigationTransitionTimer);
      shell.classList.add("navigation-transition");
      navigationTransitionTimer = setTimeout(function () { shell.classList.remove("navigation-transition"); }, 280);
    }
    shell.classList.toggle("navigation-wide", mode === "wide");
    shell.classList.toggle("navigation-compact", mode === "compact");
    shell.classList.toggle("navigation-single", mode === "single");
    shell.classList.toggle("navigation-open", open);
    shell.classList.toggle("navigation-closed", !open);
    var button = document.getElementById("productMobileMenuButton");
    if (navigationToggle) navigationToggle.sync();
    shell.querySelectorAll(".split-layout[data-default-left] > .splitter:not(.chat-inspector-splitter)").forEach(function (handle) {
      handle.hidden = mode === "single";
    });
    var active = shell.querySelector(".panel.active .global-nav");
    if (active) {
      active.inert = !open;
      active.setAttribute("aria-hidden", String(!open));
    }
    var main = shell.querySelector(".panel.active :is(.chat-main, .instruction-editor, .vba-editor, .html-workspace-editor, .settings-pages, .product-dt-main, .plugin-content)");
    if (main) main.inert = mode === "single" && open;
    var rail = shell.querySelector(".section-rail");
    if (rail) {
      rail.inert = mode === "single" && !open;
      rail.setAttribute("aria-hidden", String(mode === "single" && !open));
    }
    if (!open && active && active.contains(document.activeElement) && button) button.focus({ preventScroll: true });
    if (window.RNAssistantChatBrowser) window.RNAssistantChatBrowser.refresh();
  }

  function setProductNavigationOpen(open) {
    if (navigationMode() === "wide") {
      productNavigationPreference = !!open;
      try { window.localStorage.setItem(productNavigationOpenKey, open ? "1" : "0"); }
      catch (error) { /* Storage may be disabled. */ }
    } else compactNavigationOpen = !!open;
    syncProductNavigation();
    if (typeof refreshCodeEditors === "function") refreshCodeEditors();
  }

  window.RNAssistantShellLayout = {
    isNavigationOpen: function () { return navigationIsOpen(); },
    mode: navigationMode,
    setNavigationOpen: setProductNavigationOpen,
    refresh: syncProductNavigation
  };

  function numberAttr(node, name, fallback) {
    var value = Number(node.getAttribute(name));
    return isNaN(value) || value <= 0 ? fallback : value;
  }

  function storageKey(layout) {
    return "rnassistant.split.v3." + (layout.getAttribute("data-split-key") || layout.id || "layout");
  }

  function clamp(value, min, max) {
    return Math.max(min, Math.min(max, value));
  }

  function setSplitterAria(handle, value, bounds) {
    if (!handle) return;
    handle.setAttribute("aria-valuemin", String(Math.round(bounds.min)));
    handle.setAttribute("aria-valuemax", String(Math.round(bounds.max)));
    handle.setAttribute("aria-valuenow", String(Math.round(value)));
    handle.setAttribute("aria-valuetext", Math.round(value) + " пикселей");
  }

  // Shared interaction for navigation, inspector and local preview/chat panes.
  // Owners supply their dimensions and persistence; the handle owns drag/keyboard state.
  function bindSplitter(handle, options) {
    if (!handle || handle.dataset.bound === "true") return;
    handle.dataset.bound = "true";
    handle.tabIndex = 0;
    handle.title = "Перетащите или используйте стрелки. Enter или двойной щелчок — исходная ширина.";
    var drag = null, pending = null, frame = null, direction = options.direction || 1;
    function enabled() { return !options.enabled || options.enabled(); }
    function apply(value, persist) {
      options.setValue(value, persist);
      setSplitterAria(handle, options.getValue(), options.bounds());
    }
    function finish(commit) {
      if (!drag) return;
      if (frame !== null) window.cancelAnimationFrame(frame);
      frame = null;
      if (commit && pending !== null) apply(pending, false);
      pending = null;
      var previous = drag; drag = null;
      document.body.classList.remove("resizing-pane");
      handle.classList.remove("dragging");
      apply(commit ? options.getValue() : previous.value, commit);
      if (handle.hasPointerCapture(previous.id)) handle.releasePointerCapture(previous.id);
      if (typeof refreshCodeEditors === "function") refreshCodeEditors();
    }
    handle.addEventListener("pointerdown", function (event) {
      if (event.button !== 0 || drag || !enabled()) return;
      event.preventDefault();
      if (document.activeElement === handle) handle.blur();
      drag = { id: event.pointerId, x: event.clientX, value: options.getValue() };
      handle.setPointerCapture(event.pointerId);
      handle.classList.add("dragging");
      document.body.classList.add("resizing-pane");
    });
    handle.addEventListener("pointermove", function (event) {
      if (!drag || event.pointerId !== drag.id) return;
      if (!event.buttons) { finish(true); return; }
      var next = drag.value + (event.clientX - drag.x) * direction;
      if (options.collapse && next < options.bounds().min - 12) {
        finish(false); options.collapse(); return;
      }
      pending = next;
      if (frame === null) frame = window.requestAnimationFrame(function () {
        frame = null;
        if (drag && pending !== null) { var value = pending; pending = null; apply(value, false); }
      });
    });
    handle.addEventListener("pointerup", function () { finish(true); });
    handle.addEventListener("pointercancel", function () { finish(false); });
    handle.addEventListener("lostpointercapture", function () { finish(true); });
    window.addEventListener("blur", function () { finish(true); });
    document.addEventListener("visibilitychange", function () { if (document.hidden) finish(true); });
    document.addEventListener("keydown", function (event) {
      if (drag && event.key === "Escape") {
        event.preventDefault(); event.stopImmediatePropagation(); finish(false);
      }
    }, true);
    handle.addEventListener("dblclick", function () { if (enabled()) apply(options.defaultValue(), true); });
    handle.addEventListener("keydown", function (event) {
      if (!enabled()) return;
      var value = options.getValue(), step = (event.shiftKey ? 40 : 12) * direction;
      if (event.key === "Home" && options.collapse) { event.preventDefault(); options.collapse(); return; }
      if (event.key === "Home") value = options.bounds().min;
      else if (event.key === "End") value = options.bounds().max;
      else if (event.key === "Enter") value = options.defaultValue();
      else if (event.key === "ArrowLeft") value -= step;
      else if (event.key === "ArrowRight") value += step;
      else return;
      event.preventDefault(); apply(value, true);
    });
    setSplitterAria(handle, options.getValue(), options.bounds());
  }

  window.RNAssistantSplitter = { bind: bindSplitter, update: setSplitterAria };

  function ratioAttr(layout, name, fallback) {
    var value = Number(layout.getAttribute(name));
    return isNaN(value) || value <= 0 ? fallback : value;
  }

  function isProductNavigation(layout) {
    return !!(typeof layout.closest === "function" && layout.closest(".shell") && layout.hasAttribute("data-default-left"));
  }

  function navigationBounds(layout) {
    var available = layout.getBoundingClientRect().width || Math.max(0, window.innerWidth - 52);
    var minimum = Math.max(240, numberAttr(layout, "data-min-left", 180));
    return { min: minimum, max: Math.max(minimum, Math.min(460, available - 280)) };
  }

  function splitBounds(layout) {
    var width = layout.getBoundingClientRect().width;
    var minimum = Math.min(width / 3, Math.max(numberAttr(layout, "data-min-left", 180), ratioAttr(layout, "data-min-ratio", 0.16) * width));
    return { min: minimum, max: Math.max(minimum, Math.min(ratioAttr(layout, "data-max-ratio", 0.44) * width,
      width - numberAttr(layout, "data-min-right", 280))) };
  }

  function setProductNavigationWidth(layout, width, persist) {
    var bounds = navigationBounds(layout);
    var next = Math.round(clamp(width, bounds.min, bounds.max));
    layout.style.setProperty("--split-left", next + "px");
    document.documentElement.style.setProperty("--nav-width", next + "px");
    var handle = layout.querySelector(":scope > .splitter");
    setSplitterAria(handle, next, bounds);
    if (persist) {
      Array.prototype.slice.call(document.querySelectorAll(".shell .split-layout[data-default-left]")).forEach(function (other) {
        other.style.setProperty("--split-left", next + "px");
        var otherHandle = other.querySelector(":scope > .splitter");
        if (otherHandle) {
          otherHandle.setAttribute("aria-valuenow", String(next));
          otherHandle.setAttribute("aria-valuetext", next + " пикселей");
        }
      });
      try { window.localStorage.setItem(productNavigationKey, String(next)); } catch (error) { /* Storage may be disabled. */ }
    }
  }

  function setSplitRatio(layout, ratio, persist) {
    var box = layout.getBoundingClientRect();
    if (isProductNavigation(layout)) {
      setProductNavigationWidth(layout, ratio * box.width, persist);
      return;
    }
    if (box.width <= 0) {
      layout.style.setProperty("--split-left", (ratio * 100).toFixed(3) + "%");
      return;
    }

    var bounds = splitBounds(layout);
    var next = clamp(ratio * box.width, bounds.min, bounds.max) / box.width;
    layout.style.setProperty("--split-left", (next * 100).toFixed(3) + "%");
    setSplitterAria(layout.querySelector(":scope > .splitter"), next * box.width, bounds);
    if (persist === false) {
      return;
    }
    try {
      window.localStorage.setItem(storageKey(layout), String(next));
    } catch (error) {
      /* localStorage can be disabled in constrained WebView sessions. */
    }
  }

  function restoreSplitRatio(layout) {
    if (isProductNavigation(layout)) {
      var width = numberAttr(layout, "data-default-left", 310);
      // Match the prototype's compact default while preserving a dragged width.
      if (window.innerWidth - 24 <= 1170) width = Math.min(width, 290);
      try { width = Number(window.localStorage.getItem(productNavigationKey)) || width; } catch (error) { /* Use default. */ }
      setProductNavigationWidth(layout, width, false);
      return;
    }
    var fallback = ratioAttr(layout, "data-default-ratio", 0.25);
    var saved = fallback;
    try {
      saved = Number(window.localStorage.getItem(storageKey(layout))) || fallback;
    } catch (error) {
      saved = fallback;
    }
    setSplitRatio(layout, saved, false);
  }

  function bindSplitLayout(layout) {
    var handle = layout.querySelector(":scope > .splitter");
    if (!handle || handle.dataset.bound === "true") return;
    restoreSplitRatio(layout);
    if (isProductNavigation(layout)) {
      var pane = layout.querySelector(":scope > .global-nav");
      if (pane) {
        pane.id = pane.id || layout.id + "Navigation";
        handle.setAttribute("aria-controls", pane.id);
      }
      bindSplitter(handle, {
        getValue: function () { return Number.parseFloat(getComputedStyle(layout).getPropertyValue("--split-left")) || numberAttr(layout, "data-default-left", 310); },
        setValue: function (value, persist) { setProductNavigationWidth(layout, value, persist); },
        bounds: function () { return navigationBounds(layout); },
        defaultValue: function () { return numberAttr(layout, "data-default-left", 310); },
        collapse: function () { setProductNavigationOpen(false); }
      });
    } else {
      bindSplitter(handle, {
        getValue: function () { return Number.parseFloat(getComputedStyle(layout).getPropertyValue("--split-left")) / 100 * layout.getBoundingClientRect().width; },
        setValue: function (value, persist) { var width = layout.getBoundingClientRect().width; if (width) setSplitRatio(layout, value / width, persist); },
        bounds: function () { return splitBounds(layout); },
        defaultValue: function () { return ratioAttr(layout, "data-default-ratio", 0.25) * layout.getBoundingClientRect().width; }
      });
      new ResizeObserver(function () { if (layout.getBoundingClientRect().width) restoreSplitRatio(layout); }).observe(layout);
    }
  }

  window.initializeSplitPanes = function () {
    Array.prototype.slice.call(document.querySelectorAll(".split-layout")).forEach(bindSplitLayout);
    navigationToggle = window.RNAssistantPanelToggle.bind(document.getElementById("productMobileMenuButton"), {
      side: "left",
      controls: function () { var panel = document.querySelector(".panel.active .global-nav"); return panel && panel.id; },
      isOpen: navigationIsOpen,
      setOpen: setProductNavigationOpen
    });
    syncProductNavigation();
    requestAnimationFrame(function () {
      var shell = document.querySelector(".shell");
      if (shell) shell.classList.add("layout-ready");
    });
  };

  window.refreshSplitPanes = function () {
    Array.prototype.slice.call(document.querySelectorAll(".split-layout")).forEach(restoreSplitRatio);
    syncProductNavigation();
  };

  window.addEventListener("resize", function () {
    if (typeof window.refreshSplitPanes === "function") window.refreshSplitPanes();
    if (typeof refreshCodeEditors === "function") {
      refreshCodeEditors();
    }
  });
  window.addEventListener("storage", function (event) {
    if (event.key === productNavigationKey && typeof window.refreshSplitPanes === "function") window.refreshSplitPanes();
    if (event.key === productNavigationOpenKey) {
      productNavigationPreference = event.newValue !== "0";
      syncProductNavigation();
    }
  });
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape" && !event.defaultPrevented && navigationMode() !== "wide" && navigationIsOpen() &&
        document.querySelector(".panel.active .global-nav")?.contains(document.activeElement)) {
      event.preventDefault();
      setProductNavigationOpen(false);
    }
  });
}());
