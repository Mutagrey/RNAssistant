(function () {
  var productNavigationKey = "rnassistant.ui.navigation.width";
  var productNavigationOpenKey = "rnassistant.ui.navigation.open";
  var productNavigationPreference = true;
  var compactNavigationOpen = false;
  var productLayoutMode = null;
  var navigationTransitionTimer = null;

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
    if (button) {
      var label = open ? "Свернуть навигацию" : "Открыть навигацию";
      button.setAttribute("aria-expanded", String(open));
      button.setAttribute("aria-label", label);
      button.title = label;
    }
    shell.querySelectorAll(".split-layout > .splitter:not(.chat-inspector-splitter)").forEach(function (handle) {
      handle.hidden = mode === "single";
    });
    var active = shell.querySelector(".panel.active .global-nav");
    if (active) {
      if (button) button.setAttribute("aria-controls", active.id);
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

  function ratioAttr(layout, name, fallback) {
    var value = Number(layout.getAttribute(name));
    return isNaN(value) || value <= 0 ? fallback : value;
  }

  function isProductNavigation(layout) {
    return !!(typeof layout.closest === "function" && layout.closest(".shell") && layout.hasAttribute("data-default-left"));
  }

  function setProductNavigationWidth(layout, width, persist) {
    var available = layout.getBoundingClientRect().width || Math.max(0, window.innerWidth - 52);
    var minimum = Math.max(240, numberAttr(layout, "data-min-left", 180));
    var maximum = Math.min(460, available - 280);
    var next = Math.round(clamp(width, minimum, Math.max(minimum, maximum)));
    layout.style.setProperty("--split-left", next + "px");
    document.documentElement.style.setProperty("--nav-width", next + "px");
    var handle = layout.querySelector(":scope > .splitter");
    if (handle) {
      handle.setAttribute("aria-valuemin", String(minimum));
      handle.setAttribute("aria-valuemax", String(Math.max(minimum, maximum)));
      handle.setAttribute("aria-valuenow", String(next));
      handle.setAttribute("aria-valuetext", next + " пикселей");
    }
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

    var minPx = numberAttr(layout, "data-min-left", 180);
    var minRatio = Math.max(ratioAttr(layout, "data-min-ratio", 0.16), minPx / box.width);
    var maxRatio = ratioAttr(layout, "data-max-ratio", 0.44);
    maxRatio = Math.min(maxRatio, Math.max(minRatio, (box.width - 280) / box.width));

    var next = clamp(ratio, minRatio, maxRatio);
    layout.style.setProperty("--split-left", (next * 100).toFixed(3) + "%");
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
    var handle = layout.querySelector(".splitter");
    if (!handle || handle.dataset.bound === "true") {
      return;
    }

    handle.dataset.bound = "true";
    restoreSplitRatio(layout);

    if (isProductNavigation(layout)) {
      var pane = layout.querySelector(".global-nav");
      if (pane) {
        pane.id = pane.id || layout.id + "Navigation";
        handle.setAttribute("aria-controls", pane.id);
      }
      handle.tabIndex = 0;
      handle.title = "Ширина дерева. Перетащите или используйте стрелки. Двойной щелчок — исходная ширина.";
      handle.addEventListener("keydown", function (event) {
        var width = Number.parseFloat(getComputedStyle(layout).getPropertyValue("--split-left")) || numberAttr(layout, "data-default-left", 310);
        var step = event.shiftKey ? 40 : 12;
        if (event.key === "Home") { event.preventDefault(); setProductNavigationOpen(false); return; }
        if (event.key === "Enter") { event.preventDefault(); setProductNavigationWidth(layout, numberAttr(layout, "data-default-left", 310), true); return; }
        if (event.key === "End") width = 460;
        else if (event.key === "ArrowLeft") width -= step;
        else if (event.key === "ArrowRight") width += step;
        else return;
        event.preventDefault();
        setProductNavigationWidth(layout, width, true);
        handle.setAttribute("aria-valuenow", String(Number.parseFloat(getComputedStyle(layout).getPropertyValue("--split-left"))));
      });
      handle.addEventListener("dblclick", function () {
        setProductNavigationWidth(layout, numberAttr(layout, "data-default-left", 310), true);
      });
      var drag = null;
      function finishProductDrag(commit) {
        if (!drag) return;
        var previous = drag;
        drag = null;
        document.body.classList.remove("resizing-pane");
        handle.classList.remove("dragging");
        if (!commit) setProductNavigationWidth(layout, previous.width, false);
        else setProductNavigationWidth(layout,
          Number.parseFloat(getComputedStyle(layout).getPropertyValue("--split-left")) || previous.width, true);
        if (handle.hasPointerCapture(previous.id)) handle.releasePointerCapture(previous.id);
        if (typeof refreshCodeEditors === "function") refreshCodeEditors();
      }
      handle.addEventListener("pointerdown", function (event) {
        if (event.button !== 0 || drag) return;
        event.preventDefault();
        if (document.activeElement === handle) handle.blur();
        drag = { id: event.pointerId, width: Number.parseFloat(getComputedStyle(layout).getPropertyValue("--split-left")) ||
          numberAttr(layout, "data-default-left", 310) };
        handle.setPointerCapture(event.pointerId);
        handle.classList.add("dragging");
        document.body.classList.add("resizing-pane");
      });
      handle.addEventListener("pointermove", function (event) {
        if (!drag || event.pointerId !== drag.id) return;
        if (!event.buttons) { finishProductDrag(true); return; }
        var next = event.clientX - layout.getBoundingClientRect().left;
        if (next < numberAttr(layout, "data-min-left", 240) - 12) {
          finishProductDrag(false);
          setProductNavigationOpen(false);
          return;
        }
        setProductNavigationWidth(layout, next, false);
      });
      handle.addEventListener("pointerup", function () { finishProductDrag(true); });
      handle.addEventListener("pointercancel", function () { finishProductDrag(false); });
      handle.addEventListener("lostpointercapture", function () { finishProductDrag(true); });
      window.addEventListener("blur", function () { finishProductDrag(true); });
      document.addEventListener("visibilitychange", function () { if (document.hidden) finishProductDrag(true); });
      document.addEventListener("keydown", function (event) {
        if (drag && event.key === "Escape") {
          event.preventDefault();
          event.stopImmediatePropagation();
          finishProductDrag(false);
        }
      }, true);
    }

    handle.addEventListener("mousedown", function (event) {
      if (isProductNavigation(layout)) return;
      event.preventDefault();
      var rect = layout.getBoundingClientRect();
      var latestRatio = null;
      var framePending = false;
      var dragging = true;
      document.body.classList.add("resizing-pane");

      function applyPendingRatio() {
        framePending = false;
        if (!dragging || latestRatio === null) {
          return;
        }
        setSplitRatio(layout, latestRatio, false);
      }

      function scheduleApplyRatio() {
        if (framePending) {
          return;
        }
        framePending = true;
        if (window.requestAnimationFrame) {
          window.requestAnimationFrame(applyPendingRatio);
        } else {
          window.setTimeout(applyPendingRatio, 16);
        }
      }

      function move(moveEvent) {
        latestRatio = (moveEvent.clientX - rect.left) / rect.width;
        if (isProductNavigation(layout) && moveEvent.clientX - rect.left < numberAttr(layout, "data-min-left", 240) - 12) {
          up();
          setProductNavigationOpen(false);
          return;
        }
        scheduleApplyRatio();
      }

      function up() {
        dragging = false;
        document.body.classList.remove("resizing-pane");
        document.removeEventListener("mousemove", move);
        document.removeEventListener("mouseup", up);
        if (latestRatio !== null) {
          setSplitRatio(layout, latestRatio, true);
        }
        if (typeof refreshCodeEditors === "function") {
          refreshCodeEditors();
        }
      }

      document.addEventListener("mousemove", move);
      document.addEventListener("mouseup", up);
    });
  }

  window.initializeSplitPanes = function () {
    Array.prototype.slice.call(document.querySelectorAll(".split-layout")).forEach(bindSplitLayout);
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
