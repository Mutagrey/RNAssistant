(function () {
  "use strict";
  var capabilities = null, current = null, busy = false, bound = false, readySent = false, initializing = false, pendingSwitch = false;
  function compact() { return current && document.documentElement.dataset.hostPresentation === "Compact"; }
  function notice(error) { RNAssistantShellLayout.notice(error.detail || error.message || String(error)); }
  function unavailable(name) {
    if (!capabilities || !current || busy || current.visibility === "ShuttingDown") return true;
    if (name === "ShowCompact") return !capabilities.compactPresentation || current.canCompact === false;
    if (name === "Park" || name === "SetAutoCollapse") return !capabilities.companion;
    if (name === "SetPinned") return !capabilities.pin;
    if (name === "Maximize") return current.canMaximize === false;
    if (name === "Restore") return current.canRestore === false;
    return false;
  }
  function control(id, label, icon, action) {
    var node = document.getElementById(id);
    node.title = label; node.setAttribute("aria-label", label);
    if (icon && node.dataset.icon !== icon) { node.dataset.icon = icon; node.innerHTML = RNAssistantIcons.render(icon); }
    node.disabled = unavailable(action);
    return node;
  }
  function render() {
    var controls = document.getElementById("hostWindowControls");
    if (!controls) return;
    controls.hidden = !capabilities || !current || !current.chromeReady;
    if (!capabilities || !current) return;
    control("hostWindowPresentation", compact() ? "Свернуть в помощник" : "Компактное окно",
      compact() ? "windowCapsule" : "windowCompact", compact() ? "Park" : "ShowCompact");
    control("hostWindowClose", "Закрыть RNAssistant", "close");
    control("hostWindowMenu", "Вид и окно", "more");
  }
  function apply(snapshot) {
    if (!snapshot || !Number.isSafeInteger(snapshot.revision) || current && snapshot.revision <= current.revision) return;
    var previousDiagnostic = current && current.diagnostic;
    current = snapshot;
    present();
    // Surface a new host error once, without turning the ordinary menu into a diagnostic view.
    if (current.diagnostic && current.diagnostic !== previousDiagnostic) notice(current.diagnostic);
  }
  function present() {
    if (!current) return;
    var presentation = initializing ? "FullNormal" : current.presentation;
    if (presentation !== "Companion") document.documentElement.dataset.hostPresentation = presentation;
    document.documentElement.classList.toggle("integrated-window-chrome", !!current.chromeReady);
    document.documentElement.classList.toggle("native-window-drag", !!(current.chromeReady && capabilities && capabilities.nativeDragRegions));
    RNAssistantShellLayout.refresh();
    RNAssistantDockWorkspace.setHostWindow(presentation, current.visibility === "Visible" && current.presentation !== "Companion");
    render();
  }
  async function command(name, value) {
    if (unavailable(name)) throw new Error("Оконная команда сейчас недоступна.");
    busy = true; render();
    try {
      var payload = { command: name }; if (typeof value === "boolean") payload.value = value;
      var response = await send("hostWindowCommand", payload);
      if (!response || !response.accepted || !response.state) throw new Error("Оконная команда не подтверждена.");
      apply(response.state); return response;
    } catch (error) {
      // Reconcile a timeout; never repeat the mutation.
      try { var actual = await send("getHostWindowState", {}); if (actual) apply(actual.state); } catch (ignored) { /* Alt+F4/launcher remain available. */ }
      throw error;
    } finally { busy = false; render(); if (pendingSwitch) { pendingSwitch = false; shutdown(true); } }
  }
  function request(name, value) { command(name, value).catch(notice); }
  function bindHeaderMove() {
    var header = document.querySelector(".workspace-topbar"), press = null;
    function draggable(event) {
      return capabilities && !capabilities.nativeDragRegions && current && current.chromeReady && !busy &&
        !event.target.closest('button, input, select, textarea, a, [role="button"], [contenteditable], [tabindex]');
    }
    header.addEventListener("pointerdown", function (event) {
      press = event.button === 0 && draggable(event) ? { x: event.clientX, y: event.clientY } : null;
    });
    header.addEventListener("pointermove", function (event) {
      if (!press) return;
      if (!(event.buttons & 1) || !draggable(event)) { press = null; return; }
      if (Math.abs(event.clientX - press.x) + Math.abs(event.clientY - press.y) < 4) return;
      press = null; event.preventDefault(); request("BeginMove");
    });
    function reset() { press = null; }
    document.addEventListener("pointerup", reset);
    document.addEventListener("pointercancel", reset);
    window.addEventListener("blur", reset);
    header.addEventListener("dblclick", function (event) {
      if (event.button === 0 && draggable(event)) { event.preventDefault(); request(current.canRestore ? "Restore" : "Maximize"); }
    });
  }
  function shutdown(replacement) {
    if (!capabilities || busy) { if (replacement) pendingSwitch = true; return; }
    if (replacement) pendingSwitch = false;
    if (!RNAssistantDockWorkspace.canCloseAll()) { notice(new Error("Сначала сохраните изменения редакторов.")); return; }
    RNAssistantDialog.open({ title: replacement ? "Открыть RNAssistant для другой книги?" : "Закрыть RNAssistant?",
      submitLabel: replacement ? "Переключить" : "Закрыть RNAssistant", danger: !replacement,
      message: replacement ? "Текущий сеанс будет закрыт, активные запросы остановлены. Черновики сохранятся перед открытием новой книги." :
        "Активные запросы будут остановлены. Черновики сохранятся перед закрытием.",
      onSubmit: async function () {
        if (!RNAssistantDockWorkspace.canCloseAll()) throw new Error("Сначала сохраните изменения редакторов.");
        var editing = false;
        RNAssistantConversationPanel.each(function (chat) { if (chat.composer.edit(chat.target)) editing = true; });
        if (editing) throw new Error("Сначала завершите или отмените редактирование сообщения.");
        var drafts = [];
        RNAssistantConversationPanel.each(function (chat) {
          if (!chat.target.sessionId) return;
          chat.captureDraft(false);
          drafts.push(chat.target.source === "office" ? RNAssistantChatDrafts.flushOffice(chat.target.sessionId) : RNAssistantChatDrafts.flushWorkspace(chat.target));
        });
        await Promise.all(drafts); await RNAssistantChatDrafts.flushAll();
        await command(replacement ? "SwitchDocument" : "Shutdown");
      } });
  }
  function toggleSetting(trigger, name, value) {
    // Checkbox menu items stay open by default. Close before the native command so
    // an old snapshot cannot expose another toggle while its ACK is pending.
    RNAssistantActionMenu.closeAll();
    trigger.focus({ preventScroll: true });
    request(name, value);
  }
  function windowSettings(trigger) {
    if (unavailable()) return;
    RNAssistantActionMenu.open(trigger, [
      { label: "Сворачивать при смене фокуса", checked: !!current.autoCollapse && !current.pinned,
        disabled: unavailable("SetAutoCollapse") || current.pinned,
        status: current.pinned ? "Недоступно, пока включено «Оставлять окно открытым»" : "Сворачивать окно в помощник при уходе фокуса",
        onSelect: function () { toggleSetting(trigger, "SetAutoCollapse", !current.autoCollapse); } },
      { label: "Вернуть окно на экран", icon: "fit", disabled: unavailable("ResetPlacement"),
        onSelect: function () { request("ResetPlacement"); } },
      { separator: true },
      { label: "Назад", icon: "back", onSelect: function () { menu(trigger); } }
    ], { label: "Настройки окна", compact: true, className: "host-window-menu" });
  }
  function menu(trigger) {
    if (unavailable()) return;
    function inFull(action) { if (!window.RNAssistantWindow.inFull(action)) action(); }
    function visible(id) {
      var node = document.getElementById(id);
      return !!(node && node.getClientRects().length && getComputedStyle(node).visibility !== "hidden");
    }
    var fromCompact = compact(), mode = fromCompact ? "ShowFull" : "Park", size = current.canRestore ? "Restore" : "Maximize";
    var entries = [
      { label: compact() ? "Полное окно" : "Свернуть в помощник", icon: compact() ? "windowRestore" : "windowCapsule",
        disabled: unavailable(mode), onSelect: function () { request(mode); } },
      { label: current.canRestore ? "Восстановить размер окна" : "Развернуть окно",
        icon: current.canRestore ? "windowRestore" : "windowMaximize", disabled: unavailable(size),
        onSelect: function () { request(size); } },
      { label: "Оставлять окно открытым", icon: "pin", checked: !!current.pinned, disabled: unavailable("SetPinned"),
        status: "Не сворачивать окно при переходе к документу; без закрепления поверх других окон",
        onSelect: function () { toggleSetting(trigger, "SetPinned", !current.pinned); } },
      { separator: true }
    ];
    // Wide Full has direct panel controls; native Compact/narrow hides them.
    // Use rendered visibility so CSS breakpoints and zoom remain the sole owner.
    if (!visible("productMobileMenuButton")) entries.push({ label: "Навигация", icon: "panelLeft",
      onSelect: function () { inFull(function () { RNAssistantShellLayout.setNavigationOpen(fromCompact || !RNAssistantShellLayout.isNavigationOpen()); }); } });
    if (!visible("toggleChatInspectorButton")) entries.push({ label: "Инспектор", icon: "panel",
      onSelect: function () { inFull(function () {
        if (fromCompact) RNAssistantDockWorkspace.show("inspector"); else RNAssistantDockWorkspace.toggleTools();
      }); } });
    if (compact()) entries.push({ label: "Открыть…", icon: "layers",
      onSelect: function () { inFull(function () { RNAssistantShellLayout.command("chooser", trigger); }); } });
    entries.push({ label: "Расположение панелей…", icon: "layout",
      onSelect: function () { inFull(function () { RNAssistantShellLayout.command("layout", trigger); }); } },
      { label: "Настройки окна…", icon: "settings", onSelect: function () { windowSettings(trigger); } });
    RNAssistantActionMenu.open(trigger, entries, { label: "Вид и окно", compact: true, className: "host-window-menu" });
  }
  function bootstrap(value) {
    if (!value || !value.capabilities || !value.state) return;
    capabilities = value.capabilities;
    if (!bound && state.initializePromise) {
      // Initial chat selection has a synchronous mount contract. Finish that existing bootstrap
      // before applying a saved transient layout; native visibility/controls remain responsive.
      initializing = true;
      var initialized = function () { initializing = false; present(); };
      Promise.resolve(state.initializePromise).then(initialized, initialized);
    }
    if (!bound) {
      bound = true;
      bindHeaderMove();
      document.getElementById("hostWindowPresentation").onclick = function () { request(compact() ? "Park" : "ShowCompact"); };
      document.getElementById("hostWindowClose").onclick = function () { shutdown(false); };
      document.getElementById("hostWindowMenu").onclick = function () { menu(this); };
    }
    apply(value.state); render();
    if (capabilities.integratedChrome && !readySent) {
      readySent = true;
      // Reveal the Web controls only after the Shell has mounted and painted.
      requestAnimationFrame(function () { requestAnimationFrame(function () { command("ChromeReady").catch(function (error) { readySent = false; notice(error); }); }); });
    }
    if (pendingSwitch && !busy) { pendingSwitch = false; shutdown(true); }
  }
  window.RNAssistantWindow = {
    bootstrap: bootstrap, apply: apply, command: command, shutdown: shutdown,
    switchDocument: function () { shutdown(true); }, isCompact: compact,
    inFull: function (action) {
      if (!compact()) return false;
      command("ShowFull").then(action).catch(notice); return true;
    }
  };
}());
