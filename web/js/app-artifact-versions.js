(function () {
  "use strict";

  function append(root, head, selectedRevision, onSelect, context) {
    var savedHtml = context && context.savedHtml;
    if (!savedHtml && (!head || head.historyCount < 2 ||
        ["versioned_document", "versioned_aggregate"].indexOf(head.resourceClass) < 0)) return;
    var aggregate = savedHtml || head.resourceClass === "versioned_aggregate";
    var chatId = context ? context.chatId : state.activeChatId, navigation = state.chatNavigationVersion;
    var sessionRevision = context ? context.sessionRevision : state.artifactLibrary.sessionRevision;
    var selected = selectedRevision || head;
    var label = (aggregate ? "Версии проекта" : "Версии") + (selected.revision ? " · v" + selected.revision : "");
    var trigger = RNAssistantActionMenu.createTrigger(label);
    trigger.classList.add("compact", "artifact-version-menu"); trigger.innerHTML = RNAssistantIcons.render("clock");
    if (!context || !context.compact) { trigger.className = "button compact artifact-version-menu"; trigger.appendChild(document.createTextNode(label)); }
    root.appendChild(trigger);
    var menu = null, loaded = false, pending = false, cursor = null, ids = new Set(), revisions = [], errorMessage = "";
    var generation = 0, currentHead = null, currentDocument = context && context.documentId;
    function reset() { loaded = false; pending = false; cursor = null; ids.clear(); revisions = []; errorMessage = ""; currentHead = null; currentDocument = context && context.documentId; }
    function current() {
      if (!trigger.isConnected || !menu || !menu.isOpen()) return false;
      if (context) return context.isCurrent();
      return chatId === state.activeChatId && navigation === state.chatNavigationVersion &&
        sessionRevision === (state.artifactLibrary || {}).sessionRevision && !state.chatDetailPending && !state.chatDetailError;
    }
    function entries() {
      var items = [{ label: aggregate ? "История проекта" : "История файла", disabled: true, icon: false,
        status: aggregate ? "HTML, CSS, JS и привязки данных — одна версия." : "Сохранённые версии этого файла." }];
      revisions.forEach(function (revision) {
        var date = new Date(revision.createdUtc);
        items.push({ key: revision.artifactId, label: "v" + revision.revision + (revision.isHead ? " · текущая" : revision.relation === "branch" ? " · другая ветка" : ""),
          status: revision.createdUtc && !isNaN(date.getTime()) ? date.toLocaleString() : "",
          checked: revision.resourceUri === selected.resourceUri, keepOpen: true, onSelect: function () {
            if (!current()) return;
            if (onSelect(revision) !== false) { menu.close(); if (trigger.isConnected) trigger.focus(); }
          } });
      });
      if (pending) items.push({ label: "Загрузка версий…", disabled: true, icon: false });
      else if (errorMessage || cursor) items.push({ label: errorMessage ? "Повторить загрузку" : "Показать ещё 50", status: errorMessage,
        icon: errorMessage ? "refresh" : "down", keepOpen: true, onSelect: function () { if (errorMessage) reset(); load(); } });
      return items;
    }
    async function load() {
      if (pending || !current()) return;
      var requestGeneration = generation;
      function accepted() { return requestGeneration === generation && current(); }
      pending = true; errorMessage = ""; menu.update(entries());
      try {
        var result = await (context ? context.send : send)(savedHtml ? "getSavedHtmlHistory" : "getArtifactLibraryHistory", savedHtml
          ? { chatId: chatId, documentId: context.documentId, resourceUri: selected.resourceUri, cursor: cursor }
          : { chatId: chatId, expectedSessionRevision: sessionRevision, headArtifactId: head.artifactId, cursor: cursor });
        if (!accepted()) return;
        if (result.chatId !== chatId || (savedHtml
          ? !result.documentId || (currentDocument && result.documentId !== currentDocument) || result.resourceUri !== selected.resourceUri || !result.headArtifactId || (currentHead && result.headArtifactId !== currentHead)
          : result.sessionRevision !== sessionRevision || result.headArtifactId !== head.artifactId))
          throw new Error("История изменилась. Обновите список артефактов.");
        currentHead = result.headArtifactId;
        if (savedHtml) currentDocument = result.documentId;
        (result.items || []).forEach(function (revision) {
          if (!revision.artifactId || !revision.resourceUri || ids.has(revision.artifactId)) return;
          ids.add(revision.artifactId); revisions.push(revision);
        });
        cursor = result.nextCursor || null; loaded = true;
      } catch (error) { if (accepted()) errorMessage = error.detail || error.message || "Не удалось загрузить версии."; }
      finally { if (requestGeneration === generation) { pending = false; if (current()) menu.update(entries()); } }
    }
    trigger.onclick = function () {
      if (menu && menu.isOpen()) { generation++; pending = false; menu.close(); return; }
      generation++;
      if (savedHtml || !loaded) reset();
      menu = RNAssistantActionMenu.open(trigger, entries());
      if (!loaded) load();
    };
    return trigger;
  }
  function mountBrowser(root, context) {
    var save = RNAssistantActionMenu.createTrigger("Сохранить версию проекта");
    save.classList.add("compact", "preview-surface-persistent"); save.removeAttribute("aria-haspopup"); save.removeAttribute("aria-expanded"); save.innerHTML = RNAssistantIcons.render("save"); root.appendChild(save);
    var versions = RNAssistantActionMenu.createTrigger("Версии проекта");
    versions.classList.add("compact"); versions.innerHTML = RNAssistantIcons.render("clock"); root.appendChild(versions);
    var apply = RNAssistantActionMenu.createTrigger("Применить сохранённую версию");
    apply.classList.add("compact"); apply.removeAttribute("aria-haspopup"); apply.removeAttribute("aria-expanded"); apply.innerHTML = RNAssistantIcons.render("upload"); root.appendChild(apply);
    var state = null, chosen = null, epoch = 0, busy = false, menu = null, rows = [], cursor = null, pending = null, disposed = false;
    function target() { return context.target(); }
    function controls() {
      var value = target(), caps = state && state.capabilities || value && value.capabilities || {};
      save.disabled = busy || !!pending || !state || !caps.saveVersion || !state.capabilities.saveVersion || !state.contentRevision;
      save.title = save.disabled ? "Сохранить версию: " + (busy ? "дождитесь операции" : !caps.saveVersion ?
        caps.saveReason === "ProjectDefinitionRequired" ? "задайте состав проекта" : "доступно для рабочей копии проекта" : (state && state.target && state.target.message || "обновите состояние исходников")) : "Сохранить версию проекта";
      versions.disabled = busy || !caps.versions || !value.pluginId; apply.hidden = !value || !value.pluginId;
      apply.disabled = busy || !!pending || !state || !(value && value.savedVersionId || chosen) || value.state === "Removed";
      apply.title = apply.disabled ? "Применить: сохраните или выберите версию" : "Применить сохранённую версию";
    }
    function valid(serial) { return !disposed && context.isCurrent() && epoch === serial; }
    async function refresh(force) {
      if (busy && !force) return;
      var serial = ++epoch, value = target(); state = null; controls();
      if (!value || !value.pluginId || !value.capabilities || !value.capabilities.versions) return;
      try {
        var result = await context.request("getBrowserVersionState", { target: value });
        if (!valid(serial)) return;
        state = result; controls();
        if (result.target && result.target.state === "Removed" && value.state !== "Removed") context.removed();
      } catch (error) { if (valid(serial)) { controls(); context.error(error); } }
    }
    function command(action, entry) {
      var value = target(), active = state && state.activeSavedVersion;
      return { action: action, target: value, versionId: entry && entry.versionId || value.savedVersionId || chosen,
        operationId: "version-" + crypto.randomUUID(), expectedStateRevision: state && state.stateRevision,
        expectedContentRevision: state && state.contentRevision, expectedDefinitionRevision: state && state.definitionRevision,
        expectedActiveVersionId: active && active.versionId, expectedActiveReleaseId: active && active.releaseId,
        expectedActiveRevision: state && state.activeStateRevision, confirmed: action !== "Save" };
    }
    async function execute(request) {
      if (busy) return; var serial = epoch, received = false; busy = true; controls();
      try {
        var result = await context.request("browserVersionCommand", request); received = true;
        if (!valid(serial)) return;
        if (result.outcome === "Unknown") { pending = request; context.status("Результат неизвестен. Проверьте исходную операцию через меню версий."); return; }
        pending = null;
        if (result.outcome === "Rejected") throw new Error(result.message || "Состояние изменилось. Обновите версии.");
        if (request.action === "Save") chosen = result.version.removed ? null : result.version.versionId;
        context.status(result.version.removed ? "Исходная операция подтверждена; сохранённая запись уже удалена" : result.outcome === "AlreadySaved" ? "Эта версия уже сохранена" : result.outcome === "Saved" ? "Сохранена версия " + result.version.ordinal :
          result.outcome === "Activated" ? "Сохранённая версия применена" : "Версия удалена");
        if (menu) menu.close();
        await refresh(true);
      } catch (error) { if (valid(serial)) { if (!received) { pending = request; context.status("Результат неизвестен. Проверьте исходную операцию через меню версий."); }
          else { context.error(error); await refresh(true); } } }
      finally { busy = false; controls(); }
    }
    save.onclick = function () { if (!save.disabled && !pending) execute(command("Save")); };
    apply.onclick = function () { if (!apply.disabled && !pending && window.confirm("Применить выбранную сохранённую версию плагина? Исходники останутся без изменений.")) execute(command("Activate")); };
    function locator(version) {
      var value = target(), base = value.pluginId ? "/plugins/" + encodeURIComponent(value.pluginId) : "/projects/" + encodeURIComponent(value.workspaceId);
      return base + (version ? "/versions/" + encodeURIComponent(version) : "/source") + "/" + (value.entryPath || value.relativePath).split("/").map(encodeURIComponent).join("/");
    }
    function entries() {
      var value = target(), items = [];
      if (pending) items.push({ label: "Проверить исходную операцию", icon: "refresh", onSelect: function () { return execute(pending); } });
      items.push({ label: "Рабочая копия", icon: "code", checked: !value.savedVersionId,
        onSelect: function () { return context.navigate(locator(null)); } });
      rows.forEach(function (entry) {
        items.push({ key: entry.versionId, label: "v" + entry.ordinal + (entry.label ? " · " + entry.label : ""), icon: "clock",
          checked: value.savedVersionId === entry.versionId, status: entry.active ? "Активная" : entry.current ? "Совпадает с рабочей копией" : new Date(entry.createdUtc).toLocaleString(),
          onSelect: function () { return context.navigate(locator(entry.versionId)); },
          secondary: { label: "Удалить версию " + entry.ordinal, icon: "trash", disabled: !entry.canDelete,
            status: entry.deleteBlockedReason === "ActiveVersion" ? "Активную версию удалить нельзя" : entry.deleteBlockedReason === "VersionInUse" ? "Версия используется операцией" : "",
            onSelect: function () { if (window.confirm("Удалить сохранённую версию " + entry.ordinal + "? Исторические ссылки и исходники сохранятся.")) return execute(command("Delete", entry)); } } });
      });
      if (cursor) items.push({ label: "Показать ещё 12", icon: "down", keepOpen: true, onSelect: function () { return load(cursor); } });
      if (!rows.length) items.push({ label: "Сохранённых версий нет", icon: false, disabled: true });
      return items;
    }
    async function load(next) {
      var serial = epoch, owner = menu;
      try {
        var page = await context.request("getBrowserVersions", { target: target(), cursor: next, limit: 12 });
        if (!valid(serial) || menu !== owner || !owner.isOpen()) return;
        if (next && state && page.stateRevision !== state.stateRevision) throw new Error("Список изменился. Откройте его заново.");
        if (state) state.stateRevision = page.stateRevision;
        rows = next ? rows.concat(page.items || []) : page.items || []; cursor = page.cursor; owner.update(entries());
      } catch (error) { if (valid(serial) && owner.isOpen()) owner.update([{ label: "Обновить список", status: error.message, icon: "refresh", onSelect: function () { return refresh(); } }]); }
    }
    versions.onclick = async function () {
      if (menu && menu.isOpen()) { menu.close(); return; }
      await refresh(); if (!context.isCurrent() || disposed) return;
      rows = []; cursor = null; menu = RNAssistantActionMenu.open(versions, entries()); await load(null);
    };
    controls();
    return { refresh: refresh, reset: function () { epoch++; state = null; chosen = null; pending = null; if (menu) menu.close(); controls(); },
      deactivate: function () { epoch++; if (menu) menu.close(); }, dispose: function () { disposed = true; epoch++; if (menu) menu.close(); save.remove(); versions.remove(); apply.remove(); } };
  }
  window.RNAssistantArtifactVersions = { append: append, mountBrowser: mountBrowser };
}());
