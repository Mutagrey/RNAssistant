(function () {
  var productNavigationKey = "rnassistant.ui.navigation.width";

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
    var maximum = Math.min(available * 0.42, available - 280);
    var next = Math.round(clamp(width, minimum, Math.max(minimum, maximum)));
    layout.style.setProperty("--split-left", next + "px");
    document.documentElement.style.setProperty("--nav-width", next + "px");
    if (persist) {
      Array.prototype.slice.call(document.querySelectorAll(".shell .split-layout[data-default-left]")).forEach(function (other) {
        other.style.setProperty("--split-left", next + "px");
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

    handle.addEventListener("mousedown", function (event) {
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
  };

  window.refreshSplitPanes = function () {
    Array.prototype.slice.call(document.querySelectorAll(".split-layout")).forEach(restoreSplitRatio);
  };

  window.addEventListener("resize", function () {
    if (typeof window.refreshSplitPanes === "function") window.refreshSplitPanes();
    if (typeof refreshCodeEditors === "function") {
      refreshCodeEditors();
    }
  });
  window.addEventListener("storage", function (event) {
    if (event.key === productNavigationKey && typeof window.refreshSplitPanes === "function") window.refreshSplitPanes();
  });
}());
