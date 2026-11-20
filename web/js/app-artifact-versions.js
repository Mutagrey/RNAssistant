(function () {
  "use strict";

  function append(root, head, selectedRevision, onSelect, context) {
    if (!head || head.historyCount < 2 ||
        ["versioned_document", "versioned_aggregate"].indexOf(head.resourceClass) < 0) return;
    var aggregate = head.resourceClass === "versioned_aggregate";
    var chatId = context ? context.chatId : state.activeChatId, navigation = state.chatNavigationVersion;
    var sessionRevision = context ? context.sessionRevision : state.artifactLibrary.sessionRevision;
    var selected = selectedRevision || head;
    var label = (aggregate ? "Версии проекта" : "Версии") + (selected.revision ? " · v" + selected.revision : "");
    var trigger = RNAssistantActionMenu.createTrigger(label);
    trigger.classList.add("compact", "artifact-version-menu"); trigger.innerHTML = RNAssistantIcons.render("clock");
    if (!context || !context.compact) { trigger.className = "button compact artifact-version-menu"; trigger.appendChild(document.createTextNode(label)); }
    root.appendChild(trigger);
    var menu = null, loaded = false, pending = false, cursor = null, ids = new Set(), revisions = [], errorMessage = "";
    var generation = 0;
    function reset() { loaded = false; pending = false; cursor = null; ids.clear(); revisions = []; errorMessage = ""; }
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
        var result = await (context ? context.send : send)("getArtifactLibraryHistory",
          { chatId: chatId, expectedSessionRevision: sessionRevision, headArtifactId: head.artifactId, cursor: cursor });
        if (!accepted()) return;
        if (result.chatId !== chatId || result.sessionRevision !== sessionRevision || result.headArtifactId !== head.artifactId)
          throw new Error("История изменилась. Обновите список артефактов.");
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
      if (!loaded) reset();
      menu = RNAssistantActionMenu.open(trigger, entries());
      if (!loaded) load();
    };
    return trigger;
  }
  function mountBrowser(root, context) {
    var versions = RNAssistantActionMenu.createTrigger("Версии проекта");
    versions.classList.add("compact"); versions.innerHTML = RNAssistantIcons.render("clock"); root.appendChild(versions);
    var state = null, chosen = null, epoch = 0, busy = false, menu = null, rows = [], cursor = null, pending = null, disposed = false;
    var loadingState = false, loadingRows = false, stateError = "";
    function target() { return context.target(); }
    function controls() {
      var value = target(), caps = state && state.capabilities || value && value.capabilities || {};
      versions.disabled = busy || !supported(value);
      versions.title = value && value.savedVersionId ? "Версии проекта · " + value.savedVersionId : "Версии проекта";
    }
    function valid(serial) { return !disposed && context.isCurrent() && epoch === serial; }
    function supported(value) { return value && (value.pluginId || value.kind === "WorkspaceHtmlProject" || value.kind === "HtmlArtifact" || value.kind === "HtmlWorkspace"); }
    function paint() { if (menu && menu.isOpen()) menu.update(entries()); }
    async function refresh(force) {
      if (busy && !force) return;
      var serial = ++epoch, value = target(); state = null; stateError = ""; loadingState = true; controls(); paint();
      if (!supported(value) || !value.capabilities || !value.capabilities.versions) { loadingState = false; controls(); paint(); return; }
      try {
        var result = await context.request("getBrowserVersionState", { target: value });
        if (!valid(serial)) return;
        state = result; controls();
        if (result.target && result.target.state === "Removed" && value.state !== "Removed") context.removed();
      } catch (error) { if (valid(serial)) { stateError = error.detail || error.message || "Не удалось обновить версии."; controls(); context.error(error); } }
      finally { if (valid(serial)) { loadingState = false; controls(); paint(); } }
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
        if (result.outcome === "Unknown") { pending = request; if (menu) menu.close(); context.status("Результат неизвестен. Проверьте сохранение через меню версий."); return; }
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
    async function saveVersion() {
      if (pending || busy) return;
      if (context.beforeSave) {
        busy = true; controls();
        var changed;
        try { changed = await context.beforeSave(target()); }
        catch (error) { context.error(error); return; }
        finally { busy = false; controls(); }
        if (disposed || !context.isCurrent()) return;
        if (changed) await refresh();
        var value = target(), caps = state && state.capabilities || value && value.capabilities || {};
        if (pending || !state || !caps.saveVersion || !state.capabilities.saveVersion || !state.contentRevision) return;
      }
      return execute(command("Save"));
    }
    function applyVersion() {
      var value = target();
      if (busy || pending || !state || !(value && value.savedVersionId || chosen) || value.state === "Removed") return;
      if (window.confirm("Применить выбранную сохранённую версию плагина? Исходники останутся без изменений.")) return execute(command("Activate"));
    }
    function locator(version) {
      if (target().logicalId) return "/resources/" + encodeURIComponent(target().logicalId) + (version ? "/versions/" + encodeURIComponent(version) : "");
      var value = target(), base = value.pluginId ? "/plugins/" + encodeURIComponent(value.pluginId) : "/projects/" + encodeURIComponent(value.workspaceId);
      return base + (version ? "/versions/" + encodeURIComponent(version) : "/source") + "/" + (value.entryPath || value.relativePath).split("/").map(encodeURIComponent).join("/");
    }
    function entries() {
      var value = target(), items = [];
      var caps = state && state.capabilities || value && value.capabilities || {};
      var saveBlocked = busy || !!pending || !state || !caps.saveVersion || !state.capabilities.saveVersion || !state.contentRevision;
      var applyBlocked = busy || !!pending || !state || !(value && value.savedVersionId || chosen) || value.state === "Removed";
      if (loadingState) items.push({ label: "Обновление версий…", icon: false, disabled: true });
      else if (stateError) items.push({ label: "Повторить обновление версий", status: stateError, icon: "refresh", keepOpen: true, onSelect: async function () {
        await refresh(); if (!disposed && context.isCurrent() && menu && menu.isOpen() && state) await load(null);
      } });
      items.push({ label: pending ? "Проверить сохранение версии" : "Сохранить версию проекта", icon: pending ? "refresh" : "save",
        disabled: busy || !state || !caps.saveVersion || (!pending && saveBlocked),
        status: saveBlocked ? busy ? "Дождитесь операции" : !caps.saveVersion ?
          caps.saveReason === "ProjectDefinitionRequired" ? "Задайте состав проекта" : "Доступно для рабочей копии проекта" :
          state && state.target && state.target.message || "Обновите состояние исходников" : "",
        onSelect: function () { return pending ? execute(pending) : saveVersion(); } });
      items.push({ label: "Применить сохранённую версию", icon: "upload", disabled: applyBlocked,
        status: applyBlocked ? "Сохраните или выберите версию" : "Исходники останутся без изменений",
        onSelect: applyVersion });
      items.push({ separator: true });
      if (context.configure && value.kind === "WorkspaceHtmlProject" && !value.immutable)
        items.push({ label: "Состав проекта…", icon: "folder", onSelect: context.configure });
      items.push({ label: "Рабочая копия", icon: "code", checked: !value.savedVersionId,
        onSelect: function () { return context.navigate(locator(null)); } });
      rows.forEach(function (entry) {
        items.push({ key: entry.versionId, label: "v" + entry.ordinal + (entry.label && entry.label !== "v" + entry.ordinal ? " · " + entry.label : ""), icon: "clock",
          checked: value.savedVersionId === entry.versionId, status: entry.active ? "Активная" : entry.current ? "Совпадает с рабочей копией" : new Date(entry.createdUtc).toLocaleString(),
          onSelect: function () { return context.navigate(locator(entry.versionId)); },
          secondary: { label: "Удалить версию " + entry.ordinal, icon: "trash", disabled: !entry.canDelete,
            status: entry.deleteBlockedReason === "ActiveVersion" ? "Активную версию удалить нельзя" : entry.deleteBlockedReason === "VersionInUse" ? "Версия используется операцией" : "",
            onSelect: function () { if (window.confirm("Удалить сохранённую версию " + entry.ordinal + "? Исторические ссылки и исходники сохранятся.")) return execute(command("Delete", entry)); } } });
      });
      if (cursor) items.push({ label: "Показать ещё 12", icon: "down", keepOpen: true, onSelect: function () { return load(cursor); } });
      if (loadingRows) items.push({ label: "Загрузка версий…", icon: false, disabled: true });
      else if (!rows.length && !loadingState && !stateError) items.push({ label: "Сохранённых версий нет", icon: false, disabled: true });
      return items;
    }
    async function load(next) {
      var serial = epoch, owner = menu;
      loadingRows = true; stateError = ""; if (owner && owner.isOpen()) owner.update(entries());
      try {
        var page = await context.request("getBrowserVersions", { target: target(), cursor: next, limit: 12 });
        if (!valid(serial) || menu !== owner || !owner.isOpen()) return;
        if (next && state && page.stateRevision !== state.stateRevision) throw new Error("Список изменился. Откройте его заново.");
        if (state) state.stateRevision = page.stateRevision;
        rows = next ? rows.concat(page.items || []) : page.items || []; cursor = page.cursor; loadingRows = false; owner.update(entries());
      } catch (error) { if (valid(serial) && owner.isOpen()) { loadingRows = false; stateError = error.detail || error.message || "Не удалось загрузить версии."; owner.update(entries()); } }
      finally { if (valid(serial)) loadingRows = false; }
    }
    versions.onclick = async function () {
      if (menu && menu.isOpen()) { menu.close(); return; }
      rows = []; cursor = null; loadingRows = false; stateError = ""; loadingState = true;
      var owner = menu = RNAssistantActionMenu.open(versions, entries());
      await refresh(); if (!context.isCurrent() || disposed || menu !== owner || !owner.isOpen() || !state) return;
      await load(null);
    };
    controls();
    return { refreshOpen: function () { if (menu && menu.isOpen()) return refresh(); },
      checkSavedSelection: function () { var value = target(); if (value && value.savedVersionId && value.state === "Ready") return refresh(); },
      reset: function () { epoch++; state = null; chosen = null; pending = null; loadingState = false; loadingRows = false; stateError = ""; if (menu) menu.close(); controls(); },
      deactivate: function () { epoch++; if (menu) menu.close(); }, dispose: function () { disposed = true; epoch++; if (menu) menu.close(); versions.remove(); } };
  }
  window.RNAssistantArtifactVersions = { append: append, mountBrowser: mountBrowser };
}());
