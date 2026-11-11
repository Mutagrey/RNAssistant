(function () {
  "use strict";
  var capabilities = null, current = null, busy = false, bound = false, readySent = false, initializing = false;
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
    controls.hidden = !capabilities;
    if (!capabilities || !current) return;
    controls.dataset.integrated = String(capabilities.integratedChrome);
    control("hostWindowPin", "Не сворачивать автоматически", "pin").setAttribute("aria-pressed", String(current.pinned));
    control("hostWindowPresentation", compact() ? "В помощника" : "Компактная панель", "compress");
    control("hostWindowMaximize", current.canRestore ? "Восстановить окно" : "Развернуть окно", current.canRestore ? "compress" : "fit");
    control("hostWindowDismiss", "Закрыть окно — работа продолжится", "close");
    control("hostWindowMenu", "Действия с окном", "more");
    var status = [current.presentation, current.visibility, current.pinned ? "pin" : "auto",
      current.chromeReady ? "web chrome" : "native caption", current.anchorAvailable ? "Office anchor" : "нет Office anchor"];
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
      try { var actual = await send("getHostWindowState", {}); if (actual) apply(actual.state); } catch (ignored) { /* Native caption/launcher remain available. */ }
      throw error;
    } finally { busy = false; render(); }
  }
  function request(name, value) { command(name, value).catch(notice); }
  function shutdown() {
    if (!capabilities || busy) return;
    if (!RNAssistantDockWorkspace.canCloseAll()) return;
    RNAssistantDialog.open({ title: "Завершить сеанс RNAssistant?", submitLabel: "Завершить сеанс", danger: true,
      message: "Окно и сеанс будут закрыты. При активных запросах сначала остановите их и дождитесь завершения операций.",
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
        await command("Shutdown");
      } });
  }
  function menu(trigger) {
    function inFull(action) { if (!window.RNAssistantWindow.inFull(action)) action(); }
    var entries = [
      { label: compact() ? "Полное окно" : "Компактная панель", icon: "panel", onSelect: function () { request(compact() ? "ShowFull" : "ShowCompact"); } },
      { label: "В помощника", icon: "compress", onSelect: function () { request("Park"); } },
      { label: "Свернуть на панель задач", icon: "minus", onSelect: function () { request("SystemMinimize"); } },
      { label: "Не сворачивать автоматически", icon: "pin", status: current.pinned ? "Включено" : "Выключено", onSelect: function () { request("SetPinned", !current.pinned); } },
      { label: "Автоматически убирать окно", icon: "compress", status: current.autoCollapse ? "Включено" : "Выключено", onSelect: function () { request("SetAutoCollapse", !current.autoCollapse); } },
      { label: "Вернуть окно на экран", icon: "fit", onSelect: function () { request("ResetPlacement"); } },
      { separator: true },
      { label: "Навигация", icon: "panel", onSelect: function () { inFull(function () { RNAssistantShellLayout.setNavigationOpen(!RNAssistantShellLayout.isNavigationOpen()); }); } },
      { label: "Инспектор", icon: "panel", onSelect: function () { inFull(function () { RNAssistantDockWorkspace.toggleTools(); }); } },
      { label: "Панели…", icon: "layers", onSelect: function () { inFull(function () { document.getElementById("workspacePanels").onclick.call(trigger); }); } },
      { label: "Компоновка…", icon: "panel", onSelect: function () { inFull(function () { document.getElementById("workspaceLayout").onclick.call(trigger); }); } },
      { separator: true },
      { label: "Завершить сеанс…", icon: "close", onSelect: shutdown },
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
      document.getElementById("hostWindowPin").onclick = function () { request("SetPinned", !current.pinned); };
      document.getElementById("hostWindowPresentation").onclick = function () { request(compact() ? "Park" : "ShowCompact"); };
      document.getElementById("hostWindowMaximize").onclick = function () { request(current.canRestore ? "Restore" : "Maximize"); };
      document.getElementById("hostWindowDismiss").onclick = function () { request("Dismiss"); };
      document.getElementById("hostWindowMenu").onclick = function () { menu(this); };
    }
    apply(value.state); render();
    if (capabilities.integratedChrome && !readySent) {
      readySent = true;
      // Shell/Dock controls are mounted and painted before removing the recovery caption.
      requestAnimationFrame(function () { requestAnimationFrame(function () { command("ChromeReady").catch(function (error) { readySent = false; notice(error); }); }); });
    }
  }
  window.RNAssistantWindow = {
    bootstrap: bootstrap, apply: apply, command: command, shutdown: shutdown, isCompact: compact,
    inFull: function (action) {
      if (!compact()) return false;
      command("ShowFull").then(action).catch(notice); return true;
    }
  };
}());
