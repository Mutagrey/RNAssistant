(function () {
  "use strict";
  // One opt-in P5 surface. Runtime IDs are never saved in Dockview/localStorage.
  var available = false, active = false, root, content, surface, diagnostic, placeholder;
  var session = null, opening = false, closed = false, generation = 0, openingRequest;
  var queued = 0, pendingLayout = null, sendingLayout = null, lastLayout = "", pointer = false;
  var resize, overlays, dragging, listeners, nativeState = null;
  function sendCommand(action, extra) {
    return send("externalBrowserCommand", Object.assign({ action: action, sessionId: session }, extra || {}));
  }
  function fail(error) { if (surface) surface.fail({ title: "Внешний браузер" }, error.detail || error.message || String(error)); }
  function apply(value) {
    if (!value || value.sessionId !== session || nativeState && (value.revision < nativeState.revision ||
        value.revision === nativeState.revision && (value.layoutRevision || 0) < (nativeState.layoutRevision || 0))) return;
    if (!nativeState || (value.layoutRevision || 0) > (nativeState.layoutRevision || 0)) lastLayout = "";
    nativeState = value;
    diagnostic.textContent = value.diagnostic || "";
    if (value.status === "Closed") { closed = true; session = null; surface.clearDocument(); fail("Страница закрыта вместе с исходным scope. Закройте вкладку и откройте снова."); return; }
    surface.waiting({ title: value.title || "Внешняя страница", storage: { kind: "external" } }, value.message || " ");
    var ready = value.status === "Ready";
    surface.setDocument({ address: value.url, supportedModes: [],
      canGoBack: value.canGoBack, canGoForward: value.canGoForward,
      back: ready ? function () { return command("Back"); } : null, forward: ready ? function () { return command("Forward"); } : null,
      navigate: ready ? function (url) { return command("Navigate", { url: url }); } : null,
      details: [{ label: "P5a · экспериментальный WinForms WebView2", status: "Одна страница · временный профиль · без загрузок и новых окон", disabled: true }] });
    if (value.status === "Failed") fail(value.message);
    layout();
  }
  async function command(action, extra) {
    if (!session) throw new Error("Страница ещё не открыта.");
    var owner = session, version = generation;
    var result = await sendCommand(action, extra);
    if (session === owner && version === generation) apply(result);
  }
  async function open() {
    if (!available || !active || session || opening || closed) return;
    opening = true; var version = generation;
    surface.waiting({ title: "Внешний браузер" }, "Запуск WebView2…");
    var request = send("externalBrowserCommand", { action: "Open" }); openingRequest = request.requestId;
    try {
      var result = await request;
      if (version !== generation) {
        await send("externalBrowserCommand", { action: "Close", sessionId: result.sessionId }); return;
      }
      if (!result.sessionId) throw new Error("Host не вернул Browser session.");
      session = result.sessionId; nativeState = null; lastLayout = ""; apply(result);
    } catch (error) {
      if (error.resultUncertain) cancelBridgeRequest(request.requestId).catch(function () {});
      if (version === generation) fail(error);
    }
    finally { if (version === generation) { opening = false; openingRequest = null; surface.refresh(); } }
  }
  function blocked() {
    if (pointer || document.hidden || document.documentElement.classList.contains("workspace-dragging")) return true;
    return Array.from(document.querySelectorAll("dialog[open], .rna-action-menu-popup, .rail-hover-card")).some(function (node) {
      var box = node.getBoundingClientRect(); return box.width && box.height && getComputedStyle(node).visibility !== "hidden" && !node.hidden;
    });
  }
  function bounds() {
    var hidden = { visible: false };
    if (!active || !session || !root.isConnected || root.closest("[inert]") || blocked() || !nativeState || nativeState.status !== "Ready") return hidden;
    var rect = content.getBoundingClientRect();
    if (rect.width < 1 || rect.height < 1 || rect.left < 0 || rect.top < 0 || rect.right > innerWidth + 1 || rect.bottom > innerHeight + 1) return hidden;
    // Native HWND cannot be interleaved with HTML overlays. Hide it until the Shell owns the area again.
    var points = [[.05,.05],[.95,.05],[.5,.5],[.05,.95],[.95,.95]];
    if (points.some(function (point) { var node = document.elementFromPoint(rect.left + rect.width * point[0], rect.top + rect.height * point[1]); return !node || !content.contains(node); })) return hidden;
    return { visible: true, x: rect.left, y: rect.top, width: rect.width, height: rect.height, viewportWidth: innerWidth, viewportHeight: innerHeight };
  }
  function layout() { if (active && !queued) queued = requestAnimationFrame(place); }
  function place() {
    queued = 0;
    var next = bounds(), key = JSON.stringify(next);
    if (!session || key === lastLayout) return;
    placeholder.textContent = next.visible ? "Здесь отображается нативный WebView2. В обычном браузере доступна только проверка компоновки." : "Страница временно скрыта, пока открыт интерфейс Shell.";
    lastLayout = key; pendingLayout = next; flushLayout();
  }
  async function flushLayout() {
    if (sendingLayout || !pendingLayout || !session) return;
    var flight = {}; sendingLayout = flight;
    var version = generation, owner = session, next = pendingLayout; pendingLayout = null;
    try {
      var result = await sendCommand("Layout", { bounds: next });
      if (generation === version && session === owner) apply(result);
    } catch (error) { if (generation === version) { lastLayout = ""; fail(error); } }
    finally { if (sendingLayout === flight) { sendingLayout = null; if (pendingLayout) flushLayout(); } }
  }
  function listen() {
    if (listeners) return;
    listeners = new AbortController(); var signal = listeners.signal;
    resize = new ResizeObserver(layout); resize.observe(content);
    overlays = new MutationObserver(layout); overlays.observe(document.body, { childList: true });
    var hover = document.querySelector(".rail-hover-card"); if (hover) overlays.observe(hover, { attributes: true });
    dragging = new MutationObserver(layout); dragging.observe(document.documentElement, { attributes: true, attributeFilter: ["class"] });
    window.addEventListener("resize", layout, { signal: signal });
    document.addEventListener("scroll", layout, { capture: true, signal: signal });
    document.addEventListener("toggle", layout, { capture: true, signal: signal });
    document.addEventListener("visibilitychange", layout, { signal: signal });
    document.addEventListener("pointerdown", function () { pointer = true; place(); }, { capture: true, signal: signal });
    document.addEventListener("pointerup", function () { pointer = false; layout(); }, { capture: true, signal: signal });
    document.addEventListener("pointercancel", function () { pointer = false; layout(); }, { capture: true, signal: signal });
    window.addEventListener("blur", function () { pointer = false; layout(); }, { signal: signal });
    content.addEventListener("keydown", function (event) { if (event.key === "Enter" && session) { event.preventDefault(); command("Focus").catch(fail); } }, { signal: signal });
  }
  function unlisten() {
    cancelAnimationFrame(queued); queued = 0; pointer = false;
    if (listeners) { listeners.abort(); listeners = null; resize.disconnect(); overlays.disconnect(); dragging.disconnect(); }
  }
  function onVisible(shown) {
    active = shown;
    if (shown) {
      listen();
      if (!available) surface.waiting({ title: "Внешний браузер" }, "Пилот WebView2 отключён на этом host (RNA_BROWSER_PILOT=1).");
      open(); layout();
    }
    else { unlisten(); surface.collapse(); lastLayout = ""; pendingLayout = { visible: false }; flushLayout(); }
  }
  function close() {
    generation++; active = false; unlisten(); opening = false;
    if (openingRequest) { cancelBridgeRequest(openingRequest).catch(function (error) { notice(error.detail || error.message); }); openingRequest = null; }
    var owner = session; session = null; nativeState = null; pendingLayout = null; sendingLayout = null; closed = false; lastLayout = "";
    surface.clear(); diagnostic.textContent = "";
    if (owner) send("externalBrowserCommand", { action: "Close", sessionId: owner }).catch(function (error) { notice(error.detail || error.message); });
  }
  function register(dock) {
    root = document.createElement("section"); root.className = "browser-object-view browser-view external-browser-view"; root.setAttribute("aria-label", "Внешний браузер");
    content = document.createElement("div"); content.className = "browser-object-content"; content.tabIndex = 0;
    content.setAttribute("aria-label", "Страница WebView2. Enter — передать фокус странице.");
    placeholder = document.createElement("p"); content.appendChild(placeholder); root.appendChild(content);
    var details = document.createElement("details"), summary = document.createElement("summary");
    summary.textContent = "Диагностика встраивания · P5a"; summary.className = "rna-history-summary"; details.appendChild(summary);
    diagnostic = document.createElement("pre"); details.appendChild(diagnostic); details.className = "external-browser-diagnostics";
    root.insertBefore(details, content);
    surface = RNAssistantPreviewSurface.attach(root, { canReload: function () { return available && !opening && (!session || nativeState && nativeState.status === "Ready"); }, reload: function () { return session ? command("Reload") : open(); } });
    dock.registerView("external-browser", { title: "Внешний браузер · эксперимент", icon: "external", renderer: "onlyWhenVisible", available: function () { return available; },
      element: function () { return root; }, onVisible: onVisible, onLayout: layout, onClose: close });
    window.addEventListener("pagehide", function (event) { if (!event.persisted) close(); });
  }
  window.RNAssistantExternalBrowser = { register: register, apply: apply,
    bootstrap: function (supported) { available = supported === true; if (available && active) open(); } };
}());
