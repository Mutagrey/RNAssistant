(function () {
  var productNavigationKey = "rnassistant.ui.navigation.width";
  var productNavigationOpenKey = "rnassistant.ui.navigation.open";
  var productNavigationPreference = true;
  var compactNavigationOpen = false;
  var productLayoutMode = null;
  var navigationTransitionTimer = null;
  var navigationToggle = null;
  var navigationSizes = null;
  var workspaceNoticeTimer = null;

  function renderWorkspaceChrome(model) {
    function text(id, value) { var node = document.getElementById(id); if (node.textContent !== value) node.textContent = value; }
    function icon(node, value, markup) { if (node.dataset.icon !== value) { node.dataset.icon = value; node.innerHTML = markup || RNAssistantIcons.render(value); } }
    if (model.context) {
      text("workspaceSectionTitle", model.context.title);
      icon(document.getElementById("workspaceContextIcon"), model.context.icon, model.context.iconHtml);
      text("workspaceHostStatus", model.context.hostStatus);
    }
    if (model.layout) {
      var layout = model.layout, max = document.getElementById("workspaceMaximize");
      max.disabled = !layout.canMaximize; max.setAttribute("aria-pressed", String(layout.maximized));
      max.title = layout.maximized ? "Восстановить компоновку" : "Развернуть группу";
      max.setAttribute("aria-label", max.title); icon(max, layout.maximized ? "compress" : "fit");
      max.hidden = !layout.visible;
      document.getElementById("workspaceViewActions").hidden = !layout.visible;
      document.getElementById("toggleChatInspectorButton").hidden = !layout.visible;
      text("workspaceLayoutStatus", layout.visible ? "Вкладки: " + layout.panels + " · группы: " + layout.groups : "");
      if (layout.visible) {
        ["settings", "chat"].forEach(function (section) {
          var selected = section === "settings" ? layout.activeView === "settings" : layout.section === "chat" && layout.activeView !== "settings";
          var button = document.querySelector('.section-rail [data-section="' + section + '"]');
          button.classList.toggle("active", selected);
          if (selected) button.setAttribute("aria-current", "page"); else button.removeAttribute("aria-current");
        });
      }
    }
  }

  function workspaceNotice(message) {
    document.getElementById("workspaceLayoutNotice").textContent = message;
    clearTimeout(workspaceNoticeTimer);
    workspaceNoticeTimer = setTimeout(function () { document.getElementById("workspaceLayoutNotice").textContent = ""; }, 5000);
  }

  function navigationMetrics() {
    if (!navigationSizes) {
      var styles = getComputedStyle(document.documentElement);
      function size(token) { return Number.parseFloat(styles.getPropertyValue(token)); }
      navigationSizes = { defaultWidth: size("--navigation-width-default"), min: size("--navigation-width-min"),
        max: size("--navigation-width-max"), step: size("--navigation-resize-step"),
        rail: size("--shell-rail-width"), workspace: size("--shell-workspace-min-width"), overlay: size("--navigation-overlay-inset") };
    }
    return navigationSizes;
  }

  try { productNavigationPreference = window.localStorage.getItem(productNavigationOpenKey) !== "0"; }
  catch (error) { /* Presentation preferences are optional. */ }

  function navigationMode() {
    var shell = document.querySelector(".shell");
    var width = shell ? shell.getBoundingClientRect().width : window.innerWidth;
    return width <= 640 ? "single" : width <= 1000 ? "compact" : "wide";
  }

  function navigationIsOpen() {
    return productLayoutMode === "wide" ? productNavigationPreference : compactNavigationOpen;
  }

  function syncProductNavigation() {
    var shell = document.querySelector(".shell");
    if (!shell) return;
    var mode = navigationMode();
    if ((mode === "wide") !== (productLayoutMode === "wide")) {
      compactNavigationOpen = false;
    }
    if (mode !== productLayoutMode) {
      productLayoutMode = mode;
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
    shell.classList.toggle("navigation-overlay", mode !== "wide");
    shell.classList.toggle("navigation-open", open);
    shell.classList.toggle("navigation-closed", !open);
    var button = document.getElementById("productMobileMenuButton");
    if (navigationToggle) navigationToggle.sync();
    shell.querySelectorAll(".split-layout[data-navigation] > .global-nav + .splitter").forEach(function (handle) {
      handle.hidden = mode !== "wide";
    });
    var active = shell.querySelector(".panel.active .global-nav");
    var focusedInNavigation = active && active.contains(document.activeElement);
    if (button) button.hidden = !active;
    if (active) {
      active.inert = !open;
      active.setAttribute("aria-hidden", String(!open));
    }
    shell.querySelectorAll(".panel.active :is(.chat-dock-host, .chat-browser, .plugin-workbench, .instruction-editor, .vba-editor, .html-workspace-editor, .product-dt-main, .plugin-content)").forEach(function (main) {
      main.inert = mode !== "wide" && open && !!active;
    });
    var scrim = document.getElementById("productNavigationScrim"), section = shell.querySelector(".panel.active");
    if (scrim) {
      // Keep the scrim and navigation in the same stacking context during section transitions.
      if (section && scrim.parentElement !== section) section.appendChild(scrim);
      scrim.hidden = mode === "wide" || !open || !active;
    }
    if (!open && focusedInNavigation && button) button.focus({ preventScroll: true });
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

  function dismissNavigation() {
    if (navigationMode() !== "wide" && navigationIsOpen()) {
      setProductNavigationOpen(false);
      if (document.activeElement === document.body) document.getElementById("productMobileMenuButton").focus({ preventScroll: true });
    }
  }

  window.RNAssistantShellLayout = {
    renderWorkspace: renderWorkspaceChrome,
    notice: workspaceNotice,
    bindWorkspace: function (actions) {
      [["workspacePanels", actions.panels], ["workspaceLayout", actions.layout], ["workspaceMaximize", actions.maximize]].forEach(function (entry) {
        document.getElementById(entry[0]).onclick = function () { entry[1](this); };
      });
    },
    isNavigationOpen: function () { return navigationIsOpen(); },
    mode: navigationMode,
    occupiesSpace: function () { return navigationMode() === "wide" && navigationIsOpen(); },
    setNavigationOpen: setProductNavigationOpen,
    dismissNavigation: dismissNavigation,
    selectSection: function (activate, repeated) {
      var wasOpen = navigationIsOpen();
      activate();
      if (document.querySelector(".panel.active .global-nav")) setProductNavigationOpen(!repeated || !wasOpen);
    },
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
      var value = options.getValue(), step = (options.step || (event.shiftKey ? 40 : 12)) * direction;
      if (event.key === "Home" && options.collapse && !options.homeResets) { event.preventDefault(); options.collapse(); return; }
      if (event.key === "Home") value = options.homeResets ? options.defaultValue() : options.bounds().min;
      else if (event.key === "End") value = options.bounds().max;
      else if (event.key === "Enter") value = options.defaultValue();
      else if (event.key === "ArrowLeft") value -= step;
      else if (event.key === "ArrowRight") value += step;
      else return;
      event.preventDefault(); apply(value, true);
    });
    setSplitterAria(handle, options.getValue(), options.bounds());
  }

  function rightPaneBounds(available) {
    var min = 280, main = 280;
    return { min: min, max: Math.max(min, available - main - 1), fits: available >= min + main + 1 };
  }

  window.RNAssistantSplitter = { bind: bindSplitter, update: setSplitterAria, rightPaneBounds: rightPaneBounds };

  function ratioAttr(layout, name, fallback) {
    var value = Number(layout.getAttribute(name));
    return isNaN(value) || value <= 0 ? fallback : value;
  }

  function isProductNavigation(layout) {
    return !!(typeof layout.closest === "function" && layout.closest(".shell") && layout.hasAttribute("data-navigation"));
  }

  function navigationBounds(layout) {
    var sizes = navigationMetrics();
    var available = layout.getBoundingClientRect().width || Math.max(0, window.innerWidth - sizes.rail);
    return { min: sizes.min, max: Math.max(sizes.min, Math.min(sizes.max, available - (navigationMode() === "wide" ? sizes.workspace : sizes.overlay))) };
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
      Array.prototype.slice.call(document.querySelectorAll(".shell .split-layout[data-navigation]")).forEach(function (other) {
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
      var width = navigationMetrics().defaultWidth;
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
        getValue: function () { return Number.parseFloat(getComputedStyle(layout).getPropertyValue("--split-left")) || navigationMetrics().defaultWidth; },
        setValue: function (value, persist) { setProductNavigationWidth(layout, value, persist); },
        bounds: function () { return navigationBounds(layout); },
        defaultValue: function () { return navigationMetrics().defaultWidth; },
        step: navigationMetrics().step, homeResets: true
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
      setOpen: function (open) {
        setProductNavigationOpen(open);
        if (open && navigationMode() !== "wide") {
          var target = document.querySelector(".panel.active .global-nav :is(button, summary, input):not([disabled]):not([hidden])");
          if (target) target.focus({ preventScroll: true });
        }
      }
    });
    document.getElementById("productNavigationScrim").addEventListener("click", dismissNavigation);
    document.getElementById("workspaceDiagnostics").addEventListener("click", function () { switchTab("diagnostics"); });
    document.addEventListener("rna-navigation-select", function (event) {
      if (event.target.closest(".global-nav")) dismissNavigation();
    });
    document.querySelector(".content").addEventListener("click", function (event) {
      if (event.target.closest(".global-nav .dt-tabs button, .global-nav .product-create-menu button")) dismissNavigation();
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
  window.addEventListener("pagehide", function () { clearTimeout(workspaceNoticeTimer); });
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape" && !event.defaultPrevented && navigationMode() !== "wide" && navigationIsOpen()) {
      event.preventDefault();
      dismissNavigation();
    }
  });
}());
