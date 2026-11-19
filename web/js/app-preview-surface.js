(function () {
  "use strict";

  /** Browser presentation only. Providers own navigation, versions and publication. */
  function attach(root, options) {
    var bar = document.createElement("div"); bar.className = "preview-surface-bar"; bar.hidden = true;
    var toolbar = document.createElement("div"); toolbar.className = "preview-surface-toolbar";
    if (options.singleOverflow) toolbar.classList.add("is-single-overflow");
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
    var suggestions = [], selectedSuggestion = -1, completionGeneration = 0, suggestTimer = 0;
    function overlayChanged() { document.dispatchEvent(new CustomEvent("rnassistant:overlay-change")); }
    function invalidateCompletion() {
      if (suggestTimer) { clearTimeout(suggestTimer); suggestTimer = 0; }
      completionGeneration++; suggestions = []; selectedSuggestion = -1;
      address.removeAttribute("aria-activedescendant");
      if (!completion.hidden) { completion.setAttribute("aria-busy", "true");
        Array.from(completion.children).forEach(function (item) { item.disabled = true; }); }
    }
    function closeCompletion() { invalidateCompletion();
      var changed = !completion.hidden; completion.hidden = true; completion.replaceChildren(); completion.removeAttribute("aria-busy");
      address.setAttribute("aria-expanded", "false"); if (changed) overlayChanged(); }
    function selectSuggestion(index) { selectedSuggestion = index;
      Array.from(completion.children).forEach(function (item, i) { item.setAttribute("aria-selected", String(index === i)); });
      if (completion.children[index]) address.setAttribute("aria-activedescendant", completion.children[index].id); }
    function showCompletion(items, epoch, owner, documentRevision, documentAddress) {
      if (epoch !== completionGeneration || !presentation || presentation.addressLocked || presentation.documentRevision !== documentRevision ||
          presentation.address !== documentAddress || document.activeElement !== address || !completion.isConnected) return;
      var seen = new Set();
      suggestions = (items || []).filter(function (item) {
        if (!item) return false;
        if (item.disabledReason && !["None", "NativeUnavailable", "ProjectDefinitionRequired"].includes(item.disabledReason)) return false;
        if (seen.has(item.displayLocator)) return false;
        seen.add(item.displayLocator); return true;
      }).slice(0, 20);
      completion.replaceChildren(); completion.scrollTop = 0; completion.removeAttribute("aria-busy");
      suggestions.forEach(function (item, index) {
        var option = document.createElement("button"); option.type = "button"; option.setAttribute("role", "option"); option.id = completion.id + "-" + index;
        option.textContent = (item.title || item.displayLocator) + " · " + item.displayLocator; option.title = item.sourceLabel || "";
        option.onpointerdown = function (event) { event.preventDefault(); };
        option.onclick = function () { if (!presentation || presentation.addressLocked ||
          presentation.documentRevision !== documentRevision || presentation.address !== documentAddress) return;
          var locator = item.displayLocator; closeCompletion(); invoke(function () { return owner.navigate(locator); }); };
        completion.appendChild(option);
      });
      var changed = completion.hidden !== !suggestions.length; completion.hidden = !suggestions.length;
      address.setAttribute("aria-expanded", String(!!suggestions.length)); if (changed) overlayChanged();
    }
    function requestSuggestions(prefix, immediate) {
      var owner = presentation;
      if (!owner || !owner.suggest || owner.addressLocked) { closeCompletion(); return; }
      invalidateCompletion(); var epoch = completionGeneration;
      var documentRevision = owner.documentRevision, documentAddress = owner.address;
      var local = typeof owner.localSuggest === "function" ? owner.localSuggest(prefix) : [];
      if (local && local.length) showCompletion(local, epoch, owner, documentRevision, documentAddress);
      // Focus is a history affordance. Show known pages immediately; a catalog
      // query starts only after the user actually types.
      if (immediate) return;
      function query() {
        suggestTimer = 0;
        owner.suggest(prefix, epoch).then(function (result) {
          if (!result || result.requestGeneration !== epoch) return;
          showCompletion(result.items || [], epoch, owner, documentRevision, documentAddress);
        }, function () { if (epoch === completionGeneration && presentation && presentation.documentRevision === documentRevision &&
          presentation.address === documentAddress) closeCompletion(); });
      }
      suggestTimer = setTimeout(query, 120);
    }
    address.oninput = function () {
      requestSuggestions(address.value, false);
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
    var annotate = iconButton("selection", "Аннотировать"); annotate.disabled = true; annotate.classList.add("preview-surface-annotation");
    annotate.title = options.annotationUnavailable || "Выбор элементов недоступен для этого просмотра";
    var home = iconButton("home", "Главная"), stop = iconButton("stop", "Остановить загрузку");
    home.hidden = stop.hidden = true;
    home.onclick = function () { if (presentation && presentation.home) invoke(presentation.home); };
    stop.onclick = function () { if (presentation && presentation.stop) invoke(presentation.stop); };
    navigation.append(back, forward, reload, stop, annotate);

    var info = iconButton("info", "Сведения о странице"); info.setAttribute("aria-haspopup", "dialog"); info.setAttribute("aria-expanded", "false");
    location.append(info, address, completion);
    var tools = iconButton("more", "Инструменты страницы"); tools.classList.add("preview-surface-tools"); tools.setAttribute("aria-expanded", "false");
    if (options.singleOverflow) toolbar.append(navigation, location, actions);
    else toolbar.append(navigation, location, actions, tools);
    bar.append(toolbar, status); root.insertBefore(bar, root.firstChild);

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
    address.onfocus = function () {
      address.select();
      if (presentation && presentation.addressLocked) return;
      expand(true); requestSuggestions("", true);
    };
    toolbar.addEventListener("focusout", function (event) { if (!toolbar.contains(event.relatedTarget) && !inOwnMenu(event.relatedTarget)) expand(false); });
    toolbar.addEventListener("keydown", function (event) {
      if (event.target === address && (event.key === "ArrowDown" || event.key === "ArrowUp") && suggestions.length) {
        event.preventDefault(); selectSuggestion((selectedSuggestion + (event.key === "ArrowDown" ? 1 : -1) + suggestions.length) % suggestions.length); return;
      }
      if (event.key === "Escape" && !toolbar.querySelector('[aria-haspopup][aria-expanded="true"]')) {
        expand(false); address.value = currentAddress(); reload.focus(); event.stopPropagation();
      }
      if (event.key === "Enter" && event.target === address && presentation && presentation.navigate && !presentation.addressLocked && address.value.trim()) {
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
      var locked = !!(presentation && (presentation.addressLocked || !presentation.navigate));
      address.title = currentAddress(); address.readOnly = locked; address.setAttribute("aria-readonly", String(locked));
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
      discuss.classList.add("preview-surface-discuss");
      discuss.onclick = function () { invoke(options.discuss); }; actions.appendChild(discuss);
    }
    var modes = null, modeButtons = {};
    function setDocument(value, onModeChange) {
      var previous = presentation, documentChanged = !previous || value.documentRevision !== previous.documentRevision;
      if (document.activeElement !== address || documentChanged || previous &&
          (value.address !== previous.address || value.addressLocked !== previous.addressLocked)) closeCompletion();
      presentation = value; modeChange = onModeChange;
      if (discuss) discuss.hidden = value.allowDiscuss === false;
      if (value.supportedModes && value.supportedModes.length && !modes) {
        modes = document.createElement("div"); modes.className = "view-switch"; modes.setAttribute("role", "group"); modes.setAttribute("aria-label", "Представление страницы");
        ["preview", "source"].forEach(function (mode) {
          var button = iconButton(mode === "preview" ? "play" : "code", mode === "preview" ? "Страница" : "Исходник");
          button.onclick = function () { if (modeChange) modeChange(mode); }; modeButtons[mode] = button; modes.appendChild(button);
        });
        if (options.modesHost) { options.modesHost.appendChild(modes); options.modesHost.hidden = false; }
        else actions.prepend(modes);
      }
      if (modes) {
        modes.hidden = !(value.supportedModes && value.supportedModes.length);
        Object.keys(modeButtons).forEach(function (mode) {
          modeButtons[mode].hidden = !value.supportedModes || value.supportedModes.indexOf(mode) < 0;
          modeButtons[mode].setAttribute("aria-pressed", String(value.mode === mode));
        });
      }
      back.disabled = !value.back || !value.canGoBack; forward.disabled = !value.forward || !value.canGoForward;
      reload.hidden = !!value.loading;
      home.hidden = !value.home; stop.hidden = !value.loading;
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
      if (typeof options.onStatus === "function") options.onStatus(message || "", !!error);
    }
    function show(label, message) {
      bar.hidden = false; currentLabel = label && typeof label === "object" ? label : { title: label };
      if (typeof options.onLabel === "function") options.onLabel(currentLabel);
      updateAddress(); updateInfo(); setStatus(message, false); refresh();
    }
    function refresh() { reload.disabled = options.canReload ? !options.canReload() : false; }
    function fail(label, message) { detach(); closeCompletion(); show(label, ""); setStatus(String(message || "Предпросмотр недоступен.").slice(0, 500), true); }
    function watch(frame, label) {
      detach(); show(label, "Загрузка…");
      var current = { frame: frame,
        load: function () { if (pending === current) { detach(); setStatus("", false); } },
        error: function () { if (pending === current) { detach(); setStatus("Не удалось загрузить предпросмотр.", true); } }
      };
      pending = current; frame.addEventListener("load", current.load); frame.addEventListener("error", current.error);
    }
    function closeMenus() { RNAssistantActionMenu.closeWithin(bar); RNAssistantActionMenu.closeWithin(actions); }
    return { actions: actions, discussButton: discuss, refresh: refresh, status: setStatus,
      annotationButton: function () { return annotate; },
      canHome: function () { return !!(presentation && presentation.home); },
      goHome: function () { if (presentation && presentation.home) return presentation.home(); },
      setAnnotation: function (button) { annotate.replaceWith(button); annotate = button; button.classList.add("icon-btn", "compact", "preview-surface-annotation"); },
      updateLabel: function (value) { currentLabel = Object.assign({}, currentLabel, value); if (typeof options.onLabel === "function") options.onLabel(currentLabel); updateAddress(); updateInfo(); },
      bar: bar, toolbar: toolbar, statusElement: status,
      collapse: function () { expand(false); closeMenus(); },
      setDocument: setDocument, clearDocument: clearDocument, watch: watch,
      waiting: function (label, message) { detach(); show(label, message || "Загрузка…"); }, fail: fail,
      clear: function () { detach(); expand(false); closeMenus(); currentLabel = null; clearDocument(); bar.hidden = true; setStatus("", false); reload.hidden = false; reload.disabled = true; stop.hidden = true; }
    };
  }
  window.RNAssistantPreviewSurface = { attach: attach };
}());
