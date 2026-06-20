(function () {
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

  function setSplitRatio(layout, ratio, persist) {
    var box = layout.getBoundingClientRect();
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
      document.body.classList.add("resizing-pane");

      function move(moveEvent) {
        setSplitRatio(layout, (moveEvent.clientX - rect.left) / rect.width, true);
        if (typeof refreshCodeEditors === "function") {
          refreshCodeEditors();
        }
      }

      function up() {
        document.body.classList.remove("resizing-pane");
        document.removeEventListener("mousemove", move);
        document.removeEventListener("mouseup", up);
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
    if (typeof refreshCodeEditors === "function") {
      refreshCodeEditors();
    }
  });
}());
