(function () {
  "use strict";

  var owner = null, open = false, expanded = false, bound = false, transitionTimer = null;
  var ratio = 0.5, savedRatioKey = "rnassistant.ui.htmlBrowser.ratio";
  try {
    var saved = Number(window.localStorage.getItem(savedRatioKey));
    if (saved > 0 && saved < 1) ratio = saved;
  } catch (_) { /* Pane geometry is optional. */ }

  function el(id) { return document.getElementById(id); }
  function previewOwner() { return window.RNAssistantHtmlWorkspaceSurface; }
  function sameBinding(binding) {
    return !!owner && !!binding && owner.chatId === binding.chatId && owner.artifactId === binding.artifactId;
  }
  function current() { return sameBinding(previewOwner().binding()); }
  function availableWidth() {
    var layout = el("chatLayout"), navigation = layout.querySelector(".chat-sidebar");
    var nav = window.RNAssistantShellLayout && window.RNAssistantShellLayout.isNavigationOpen()
      ? navigation.getBoundingClientRect().width + 1 : 0;
    return Math.max(0, layout.clientWidth - nav);
  }
  function bounds() {
    return window.RNAssistantSplitter.rightPaneBounds(availableWidth());
  }
  function overlayMode() {
    return window.RNAssistantShellLayout.mode() === "single" || !bounds().fits;
  }
  function paneWidth() {
    var width = availableWidth(), limit = bounds();
    return Math.max(limit.min, Math.min(limit.max, width * (expanded ? 0.6 : ratio)));
  }
  function refresh() {
    if (!bound) return;
    var layout = el("chatLayout"), overlay = overlayMode();
    layout.classList.toggle("browser-overlay", overlay);
    var toggle = el("toggleChatInspectorButton");
    var toggleTarget = open && overlay ? el("htmlWorkspacePreview").querySelector(".preview-surface-panel-head") :
      document.querySelector("#chatDropZone > .chatbar .chatbar-actions");
    if (toggleTarget && toggle.parentElement !== toggleTarget) toggleTarget.appendChild(toggle);
    layout.style.setProperty("--browser-width", Math.round(paneWidth()) + "px");
    el("chatBrowserSplitter").hidden = !open || overlay;
    window.RNAssistantSplitter.update(el("chatBrowserSplitter"), paneWidth(), bounds());
    el("chatBrowserExpand").disabled = overlay;
  }
  function animate() {
    var layout = el("chatLayout");
    if (!document.querySelector(".shell.layout-ready") ||
        window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;
    clearTimeout(transitionTimer);
    layout.classList.add("browser-transition");
    transitionTimer = setTimeout(function () { layout.classList.remove("browser-transition"); }, 240);
  }
  function paint() {
    var layout = el("chatLayout"), panel = el("chatBrowser");
    layout.classList.toggle("with-browser", open);
    panel.inert = !open;
    panel.setAttribute("aria-hidden", String(!open));
    var expand = el("chatBrowserExpand");
    expand.setAttribute("aria-pressed", String(expanded));
    expand.setAttribute("aria-label", expanded ? "Вернуть размер браузера" : "Развернуть браузер");
    expand.title = expand.getAttribute("aria-label");
    expand.innerHTML = window.RNAssistantIcons.render(expanded ? "compress" : "fit");
    refresh();
    if (window.RNAssistantChatInspector) {
      window.RNAssistantChatInspector.syncToggle();
      window.RNAssistantChatInspector.syncSwitcher();
    }
  }
  function setOpen(value) {
    if (value && !current()) return false;
    if (open === !!value) return true;
    if (value && window.RNAssistantChatInspector) window.RNAssistantChatInspector.setOpen(false);
    var returnFocus = !value && el("chatBrowser").contains(document.activeElement);
    if (!value) {
      if (window.RNAssistantHtmlWorkspaceAnnotations) window.RNAssistantHtmlWorkspaceAnnotations.collapseEditor();
      if (typeof window.deactivateHtmlWorkspaceInspector === "function") window.deactivateHtmlWorkspaceInspector();
    }
    animate(); open = !!value; paint();
    if (returnFocus) el("toggleChatInspectorButton").focus({ preventScroll: true });
    return true;
  }
  function reloadAfterFrameMove(isCurrent) {
    el("htmlWorkspacePreviewFrame").addEventListener("load", function () {
      if (isCurrent()) previewOwner().reload();
    }, { once: true });
  }
  function restoreHome() {
    var root = el("htmlWorkspacePreview"), editor = el("htmlWorkspaceEditor");
    root.classList.remove("preview-side-panel");
    if (root.parentElement === el("chatBrowserFrameMount")) {
      reloadAfterFrameMove(function () { return !owner && root.parentElement === editor; });
      editor.insertBefore(root, el("htmlWorkspaceEdit"));
    }
    var toolbar = editor.querySelector(".html-workspace-toolbar");
    var status = el("htmlWorkspaceInspectStatus");
    ["inspectHtmlWorkspaceButton", "attachHtmlWorkspaceSelectionButton"].forEach(function (id) {
      toolbar.insertBefore(el(id), el("openHtmlWorkspaceRightButton"));
    });
    el("attachHtmlWorkspaceSelectionButton").hidden = true;
    toolbar.insertBefore(status, toolbar.querySelector(".resource-editor-actions"));
    el("inspectHtmlWorkspaceButton").dataset.browserControl = "true";
    previewOwner().refreshInspectorControl();
    el("chatBrowserExpand").hidden = true;
    el("chatBrowserMenu").hidden = true;
  }
  function release() {
    if (!owner) return;
    var focusWasInside = el("chatBrowser").contains(document.activeElement);
    setOpen(false);
    previewOwner().suspend();
    owner = null; expanded = false;
    restoreHome();
    paint();
    if (focusWasInside && el("tab-chat").classList.contains("active")) el("chatInput").focus({ preventScroll: true });
  }
  function mount() {
    var root = el("htmlWorkspacePreview"), bar = root.querySelector(".preview-surface-bar");
    root.classList.add("preview-side-panel");
    var actions = bar.querySelector(".preview-surface-actions");
    el("chatBrowserFrameMount").appendChild(root);
    bar.querySelector(".preview-surface-panel-head").appendChild(el("inspectHtmlWorkspaceButton"));
    el("attachHtmlWorkspaceSelectionButton").hidden = true;
    el("inspectHtmlWorkspaceButton").dataset.browserControl = "true";
    previewOwner().refreshInspectorControl();
    el("chatBrowser").appendChild(el("htmlWorkspaceInspectStatus"));
    actions.append(el("chatBrowserExpand"), el("chatBrowserMenu"));
    el("chatBrowserExpand").hidden = false;
    el("chatBrowserMenu").hidden = false;
  }
  function openCurrent() {
    var binding = previewOwner().prepare();
    if (!binding) return false;
    if (owner && !sameBinding(binding)) release();
    var reused = !!owner;
    if (!reused) {
      reloadAfterFrameMove(function () { return owner === binding; });
      owner = binding; mount();
    }
    if (window.RNAssistantProjectChats && window.RNAssistantProjectChats.scope() !== "office")
      window.RNAssistantProjectChats.setScope("office");
    switchTab("chat");
    setOpen(true);
    if (window.RNAssistantHtmlWorkspaceAnnotations) window.RNAssistantHtmlWorkspaceAnnotations.sync();
    return true;
  }
  function openArtifact(artifact) {
    var binding = previewOwner().binding();
    var id = artifact && (artifact.Id || artifact.id);
    if (!binding || String(id || "") !== String(binding.artifactId)) return false;
    return openCurrent();
  }
  function syncSource() { if (owner && !current()) release(); }
  function setWidth(value, persist) {
    var limit = bounds(), available = availableWidth();
    if (overlayMode()) return;
    expanded = false;
    ratio = Math.max(limit.min, Math.min(limit.max, value)) / available;
    if (persist) try { window.localStorage.setItem(savedRatioKey, String(ratio)); } catch (_) { /* Optional. */ }
    paint();
  }
  function bind() {
    if (bound) return;
    bound = true;
    var bar = el("htmlWorkspacePreview").querySelector(".preview-surface-actions");
    ["Expand"].forEach(function (kind) {
      var button = document.createElement("button");
      button.type = "button"; button.id = "chatBrowser" + kind; button.className = "icon-btn";
      button.hidden = true;
      bar.appendChild(button);
    });
    var menu = document.createElement("details");
    menu.id = "chatBrowserMenu"; menu.className = "chat-browser-menu"; menu.hidden = true;
    menu.innerHTML = '<summary class="icon-btn" aria-label="Действия с браузером" title="Действия с браузером">' +
      window.RNAssistantIcons.render("more") + '</summary><div class="popover-menu">' +
      '<button type="button" id="chatBrowserOpenMaterials">Открыть в Материалах</button>' +
      '<button type="button" id="chatBrowserClose">Закрыть просмотр</button></div>';
    bar.appendChild(menu);
    el("chatBrowserExpand").addEventListener("click", function () { expanded = !expanded; animate(); paint(); });
    el("chatBrowserClose").addEventListener("click", release);
    el("chatBrowserOpenMaterials").addEventListener("click", function () {
      var artifact = (state.artifacts || []).find(function (item) {
        return owner && String(item.Id || item.id) === String(owner.artifactId);
      });
      release();
      if (artifact && typeof window.openArtifactResource === "function") window.openArtifactResource(artifact);
      else switchTab("artifacts");
    });
    el("openHtmlWorkspaceRightButton").addEventListener("click", openCurrent);
    window.RNAssistantSplitter.bind(el("chatBrowserSplitter"), {
      getValue: paneWidth, setValue: setWidth, bounds: bounds,
      defaultValue: function () { return availableWidth() * 0.5; },
      direction: -1, enabled: function () { return open && !overlayMode(); }
    });
    window.addEventListener("resize", refresh);
    paint();
  }

  window.RNAssistantChatBrowser = {
    bind: bind, hasSource: function () { return !!owner; }, isOpen: function () { return open; },
    setOpen: setOpen, syncSource: syncSource,
    openCurrent: openCurrent, openArtifact: openArtifact, hide: function () { setOpen(false); }, refresh: refresh,
    onTabChange: function (name) { if (name === "artifacts") release(); else if (name !== "chat") setOpen(false); }
  };
}());
