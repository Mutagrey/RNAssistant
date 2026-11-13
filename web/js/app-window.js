(function () {
  "use strict";
  var capabilities = null, current = null, busy = false, bound = false, readySent = false, initializing = false, pendingSwitch = false;
  function compact() { return current && document.documentElement.dataset.hostPresentation === "Compact"; }
  function notice(error) { RNAssistantShellLayout.notice(error.detail || error.message || String(error)); }
  function control(id, label, icon) {
    var node = document.getElementById(id);
    node.title = label; node.setAttribute("aria-label", label);
    if (icon && node.dataset.icon !== icon) { node.dataset.icon = icon; node.innerHTML = RNAssistantIcons.render(icon); }
    node.disabled = busy || !current || current.visibility === "ShuttingDown";
    return node;
  }
  function render() {
    var controls = document.getElementById("hostWindowControls");
    if (!controls) return;
    controls.hidden = !capabilities || !current || !current.chromeReady;
    if (!capabilities || !current) return;
    control("hostWindowPresentation", compact() ? "Свернуть в помощник" : "Компактное окно", compact() ? "windowCapsule" : "windowCompact");
    control("hostWindowClose", "Закрыть RNAssistant", "close");
    control("hostWindowMenu", "Действия с окном", "more");
    var status = [current.presentation, current.visibility, current.pinned ? "pin" : "auto",
      current.chromeReady ? "web chrome" : "загрузка панели", current.anchorAvailable ? "Office anchor" : "нет Office anchor"];
    if (current.diagnostic) status.push(current.diagnostic);
    document.getElementById("hostWindowMenu").title = "Действия с окном · " + status.join(" · ");
  }
  function apply(snapshot) {
    if (!snapshot || !Number.isSafeInteger(snapshot.revision) || current && snapshot.revision <= current.revision) return;
    current = snapshot;
    present();
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
    if (!capabilities || busy) throw new Error("Оконная команда сейчас недоступна.");
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
  function menu(trigger) {
    function inFull(action) { if (!window.RNAssistantWindow.inFull(action)) action(); }
    var entries = [
      { label: compact() ? "Полное окно" : "Компактное окно", icon: "windowCompact", onSelect: function () { request(compact() ? "ShowFull" : "ShowCompact"); } },
      { label: "Свернуть в помощник", icon: "windowCapsule", onSelect: function () { request("Park"); } },
      { label: current.canRestore ? "Восстановить размер окна" : "Развернуть окно", icon: "fit", onSelect: function () { request(current.canRestore ? "Restore" : "Maximize"); } },
      { label: "Не сворачивать автоматически", icon: "pin", status: current.pinned ? "Включено" : "Выключено", onSelect: function () { request("SetPinned", !current.pinned); } },
      { label: "Автоматически убирать окно", icon: "compress", status: current.autoCollapse ? "Включено" : "Выключено", onSelect: function () { request("SetAutoCollapse", !current.autoCollapse); } },
      { label: "Вернуть окно на экран", icon: "fit", onSelect: function () { request("ResetPlacement"); } },
      { separator: true },
      { label: "Навигация", icon: "panel", onSelect: function () { inFull(function () { RNAssistantShellLayout.setNavigationOpen(!RNAssistantShellLayout.isNavigationOpen()); }); } },
      { label: "Инспектор", icon: "panel", onSelect: function () { inFull(function () { RNAssistantDockWorkspace.toggleTools(); }); } },
      { label: "Панели…", icon: "layers", onSelect: function () { inFull(function () { document.getElementById("workspacePanels").onclick.call(trigger); }); } },
      { label: "Компоновка…", icon: "panel", onSelect: function () { inFull(function () { document.getElementById("workspaceLayout").onclick.call(trigger); }); } },
      { separator: true },
      { label: current.presentation + " · " + current.visibility, informational: true }
    ];
    if (current.diagnostic) entries.push({ label: current.diagnostic, informational: true });
    RNAssistantActionMenu.open(trigger, entries);
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
