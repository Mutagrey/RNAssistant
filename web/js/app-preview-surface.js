(function () {
  "use strict";

  /** Browser presentation only. Providers own navigation, versions and publication. */
  function attach(root, options) {
    var bar = document.createElement("div"); bar.className = "preview-surface-bar"; bar.hidden = true;
    var toolbar = document.createElement("div"); toolbar.className = "preview-surface-toolbar";
    toolbar.setAttribute("role", "group"); toolbar.setAttribute("aria-label", "Браузер");
    var navigation = document.createElement("div"); navigation.className = "preview-surface-navigation";
    var location = document.createElement("div"); location.className = "rna-form-control compact preview-surface-location";
    var address = document.createElement("input"); address.type = "text"; address.readOnly = true;
    address.className = "preview-surface-address"; address.setAttribute("aria-label", "Адрес материала"); address.spellcheck = false;
    var actions = document.createElement("div"); actions.className = "preview-surface-actions";
    var status = document.createElement("div"); status.className = "preview-surface-status"; status.setAttribute("role", "status");
    var currentLabel = null, presentation = null, modeChange = null, infoMenu = null;
    var completion = document.createElement("div"); completion.className = "preview-surface-completion"; completion.hidden = true;
    completion.setAttribute("role", "listbox"); completion.id = "browser-completion-" + crypto.randomUUID();
    address.setAttribute("role", "combobox"); address.setAttribute("aria-autocomplete", "list"); address.setAttribute("aria-controls", completion.id);
    var suggestions = [], selectedSuggestion = -1, completionGeneration = 0;
    function closeCompletion() { completionGeneration++; suggestions = []; selectedSuggestion = -1; completion.hidden = true; completion.replaceChildren();
      address.setAttribute("aria-expanded", "false"); address.removeAttribute("aria-activedescendant"); }
    function selectSuggestion(index) { selectedSuggestion = index;
      Array.from(completion.children).forEach(function (item, i) { item.setAttribute("aria-selected", String(index === i)); });
      if (completion.children[index]) address.setAttribute("aria-activedescendant", completion.children[index].id); }
    address.oninput = async function () {
      closeCompletion(); var epoch = completionGeneration, owner = presentation;
      if (!owner || !owner.suggest) return;
      try {
        var result = await owner.suggest(address.value, epoch);
        if (epoch !== completionGeneration || owner !== presentation || document.activeElement !== address || result.requestGeneration !== epoch) return;
        suggestions = (result.items || []).slice(0, 20);
        suggestions.forEach(function (item, index) {
          var option = document.createElement("button"); option.type = "button"; option.setAttribute("role", "option"); option.id = completion.id + "-" + index;
          option.textContent = (item.title || item.displayLocator) + " · " + item.displayLocator; option.title = item.sourceLabel || "";
          option.onpointerdown = function (event) { event.preventDefault(); };
          option.onclick = function () { var locator = item.displayLocator; closeCompletion(); invoke(function () { return owner.navigate(locator); }); };
          completion.appendChild(option);
        });
        completion.hidden = !suggestions.length; address.setAttribute("aria-expanded", String(!!suggestions.length));
      } catch (error) { if (epoch === completionGeneration) closeCompletion(); }
    };
    address.onblur = function () { closeCompletion(); address.value = currentAddress(); };
    function iconButton(name, label) {
      var button = document.createElement("button"); button.type = "button"; button.className = "icon-btn compact";
      button.innerHTML = RNAssistantIcons.render(name); button.title = label; button.setAttribute("aria-label", label); return button;
    }
    function invoke(action) {
      try { Promise.resolve(action()).catch(function (error) { if (bar.isConnected) fail(currentLabel, error.detail || error.message); }); }
      catch (error) { fail(currentLabel, error.detail || error.message); }
    }
    async function navigateAddress(owner, value) {
      var epoch = completionGeneration;
      try { await owner.navigate(value); }
      finally { if (epoch === completionGeneration || document.activeElement !== address) address.value = currentAddress(); }
    }
    var back = iconButton("left", "Назад"), forward = iconButton("right", "Вперёд");
    back.disabled = forward.disabled = true;
    back.onclick = function () { if (presentation && presentation.back) invoke(presentation.back); };
    forward.onclick = function () { if (presentation && presentation.forward) invoke(presentation.forward); };
    var reload = options.reloadButton || iconButton("refresh", options.reloadLabel || "Обновить предпросмотр");
    reload.classList.add("compact"); reload.title = options.reloadLabel || "Обновить предпросмотр"; reload.setAttribute("aria-label", reload.title);
    if (!options.reloadButton) reload.onclick = function () { invoke(options.reload); };
    var annotate = iconButton("selection", "Аннотировать"); annotate.disabled = true;
    annotate.title = options.annotationUnavailable || "Выбор элементов недоступен для этого просмотра";
    var home = iconButton("home", "Главная"), stop = iconButton("stop", "Остановить загрузку");
    home.hidden = stop.hidden = true;
    home.onclick = function () { if (presentation && presentation.home) invoke(presentation.home); };
    stop.onclick = function () { if (presentation && presentation.stop) invoke(presentation.stop); };
    navigation.append(back, forward, reload, home, stop, annotate);

    var info = iconButton("info", "Сведения о странице"); info.setAttribute("aria-haspopup", "dialog"); info.setAttribute("aria-expanded", "false");
    var copyAddress = iconButton("copy", "Копировать адрес"); copyAddress.classList.add("preview-surface-copy-address");
    copyAddress.onpointerdown = function (event) { event.preventDefault(); };
    copyAddress.onclick = function () { invoke(function () { return copyTextResult(currentAddress()); }); };
    location.append(info, address, copyAddress, completion);
    var tools = iconButton("more", "Инструменты страницы"); tools.classList.add("preview-surface-tools"); tools.setAttribute("aria-expanded", "false");
    toolbar.append(navigation, location, actions, tools); bar.append(toolbar, status); root.insertBefore(bar, root.firstChild);

    function inOwnMenu(target) {
      return target && target.closest && target.closest(".rna-action-menu-popup") && toolbar.querySelector('[aria-haspopup][aria-expanded="true"]');
    }
    function outsidePointer(event) { if (!toolbar.contains(event.target) && !inOwnMenu(event.target)) expand(false); }
    function leaveWindow() { expand(false); }
    function expand(value) {
      if (!value) closeCompletion();
      toolbar.classList.toggle("is-address-focused", value); tools.setAttribute("aria-expanded", String(value));
      document.removeEventListener("pointerdown", outsidePointer, true); window.removeEventListener("blur", leaveWindow);
      if (value) { document.addEventListener("pointerdown", outsidePointer, true); window.addEventListener("blur", leaveWindow); }
    }
    address.onfocus = function () { expand(true); address.select(); };
    toolbar.addEventListener("focusout", function (event) { if (!toolbar.contains(event.relatedTarget) && !inOwnMenu(event.relatedTarget)) expand(false); });
    toolbar.addEventListener("keydown", function (event) {
      if (event.target === address && (event.key === "ArrowDown" || event.key === "ArrowUp") && suggestions.length) {
        event.preventDefault(); selectSuggestion((selectedSuggestion + (event.key === "ArrowDown" ? 1 : -1) + suggestions.length) % suggestions.length); return;
      }
      if (event.key === "Escape" && !toolbar.querySelector('[aria-haspopup][aria-expanded="true"]')) {
        expand(false); address.value = currentAddress(); reload.focus(); event.stopPropagation();
      }
      if (event.key === "Enter" && event.target === address && presentation && presentation.navigate && address.value.trim()) {
        var value = selectedSuggestion >= 0 ? suggestions[selectedSuggestion].displayLocator : address.value.trim();
        event.preventDefault(); expand(false); var owner = presentation; invoke(function () { return navigateAddress(owner, value); });
      }
    });
    tools.onclick = function () { address.focus(); };
    // Act on the original control before folding the optional tools back.
    actions.addEventListener("click", function (event) {
      if (!event.target.closest("button, summary") || actions.querySelector('[aria-expanded="true"]')) return;
      expand(false);
      if (actions.contains(document.activeElement) && !document.activeElement.getClientRects().length) tools.focus();
    });
    function currentAddress() { return presentation && presentation.address || currentLabel && (currentLabel.address || currentLabel.path || currentLabel.title) || options.address || ""; }
    function updateAddress() {
      if (document.activeElement !== address) address.value = currentAddress();
      address.title = currentAddress(); address.readOnly = !(presentation && presentation.navigate); copyAddress.disabled = !currentAddress();
    }
    function storage() { return currentLabel && currentLabel.storage || options.storage; }
    function infoEntries() {
      var value = currentLabel || {}, backing = storage();
      var entries = [{ label: value.title || "Страница", status: value.path || "", icon: "info", disabled: true }];
      if (backing) entries.push({ label: backing.kind === "directory" ? "Хранение · В папке" : backing.kind === "managed" ? "Хранение · В приложении" : "Внешняя страница",
        status: backing.detail || (backing.kind === "managed" ? "CAS · без отдельной папки исходников" : ""),
        icon: backing.kind === "directory" ? "folder" : backing.kind === "managed" ? "layers" : "external", disabled: true });
      var details = typeof options.details === "function" ? options.details() : options.details || [];
      return entries.concat(details, value.details || [], presentation && presentation.details || []).map(function (entry) { return Object.assign({}, entry, { informational: true }); });
    }
    function updateInfo() {
      var backing = storage(); info.innerHTML = RNAssistantIcons.render(backing && backing.kind === "directory" ? "folder" : backing && backing.kind === "managed" ? "layers" : "info");
      info.title = "Сведения о странице" + (backing ? backing.kind === "managed" ? " · В приложении" : backing.kind === "directory" ? " · В папке" : " · Внешняя страница" : "");
      if (infoMenu && infoMenu.isOpen()) infoMenu.update(infoEntries());
    }
    info.onclick = function () { infoMenu = RNAssistantActionMenu.open(info, infoEntries(), { align: "start", role: "dialog" }); };
    var discuss = null;
    if (options.discuss) {
      discuss = iconButton("chat", "Обсудить в чате рядом");
      discuss.onclick = function () { invoke(options.discuss); }; actions.appendChild(discuss);
    }
    var modes = null, modeButtons = {};
    function setDocument(value, onModeChange) {
      closeCompletion();
      presentation = value; modeChange = onModeChange;
      if (value.supportedModes && value.supportedModes.length && !modes) {
        modes = document.createElement("div"); modes.className = "view-switch"; modes.setAttribute("role", "group"); modes.setAttribute("aria-label", "Представление страницы");
        ["preview", "source", "diff"].forEach(function (mode) {
          var button = iconButton(mode === "preview" ? "play" : mode === "source" ? "code" : "diff", mode === "preview" ? "Страница" : mode === "source" ? "Исходник" : "Сравнение");
          button.onclick = function () { if (modeChange) modeChange(mode); }; modeButtons[mode] = button; modes.appendChild(button);
        });
        actions.prepend(modes);
      }
      if (modes) {
        modes.hidden = !(value.supportedModes && value.supportedModes.length);
        Object.keys(modeButtons).forEach(function (mode) {
          modeButtons[mode].hidden = !value.supportedModes || value.supportedModes.indexOf(mode) < 0;
          modeButtons[mode].setAttribute("aria-pressed", String(value.mode === mode));
        });
      }
      back.disabled = !value.back || !value.canGoBack; forward.disabled = !value.forward || !value.canGoForward;
      home.hidden = !value.home; stop.hidden = !value.stop;
      updateAddress(); updateInfo();
    }
    function clearDocument() {
      presentation = null; modeChange = null; back.disabled = forward.disabled = true;
      if (modes) modes.hidden = true;
      updateAddress();
    }
    var pending = null;
    function detach() {
      if (!pending) return;
      pending.frame.removeEventListener("load", pending.load); pending.frame.removeEventListener("error", pending.error); pending = null;
    }
    function setStatus(message, error) {
      status.textContent = message || ""; status.title = message || "";
      status.dataset.error = error ? "true" : "false"; status.setAttribute("role", error ? "alert" : "status");
    }
    function show(label, message) {
      bar.hidden = false; currentLabel = label && typeof label === "object" ? label : { title: label };
      updateAddress(); updateInfo(); setStatus(message, false); refresh();
    }
    function refresh() { reload.disabled = options.canReload ? !options.canReload() : false; }
    function fail(label, message) { detach(); show(label, ""); setStatus(String(message || "Предпросмотр недоступен.").slice(0, 500), true); }
    function watch(frame, label) {
      detach(); show(label, "Загрузка…");
      var current = { frame: frame,
        load: function () { if (pending === current) { detach(); setStatus("", false); } },
        error: function () { if (pending === current) { detach(); setStatus("Не удалось загрузить предпросмотр.", true); } }
      };
      pending = current; frame.addEventListener("load", current.load); frame.addEventListener("error", current.error);
    }
    return { actions: actions, discussButton: discuss, refresh: refresh, status: setStatus,
      setAnnotation: function (button) { annotate.replaceWith(button); annotate = button; button.classList.add("icon-btn", "compact"); },
      updateLabel: function (value) { currentLabel = Object.assign({}, currentLabel, value); updateAddress(); updateInfo(); },
      collapse: function () { expand(false); RNAssistantActionMenu.closeWithin(bar); },
      setDocument: setDocument, clearDocument: clearDocument, watch: watch,
      waiting: function (label, message) { detach(); show(label, message || "Загрузка…"); }, fail: fail,
      clear: function () { detach(); expand(false); RNAssistantActionMenu.closeWithin(bar); currentLabel = null; clearDocument(); bar.hidden = true; setStatus("", false); reload.disabled = true; }
    };
  }
  window.RNAssistantPreviewSurface = { attach: attach };
}());
