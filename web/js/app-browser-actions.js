(function () {
  "use strict";
  // Domain operations only. Browser owns target/lifetime; existing services own effects.
  function mount(root, context) {
    var copies = new Map(), disposed = false, abort = new AbortController();
    var create = document.createElement("button"); create.type = "button"; create.className = "icon-btn compact preview-surface-persistent";
    create.innerHTML = RNAssistantIcons.render("copy"); create.setAttribute("aria-label", "Создать проект");
    var more = RNAssistantActionMenu.createTrigger("Действия страницы"); more.classList.add("compact"); root.append(create, more);
    function key(target) { return target && (target.resourceUri || target.workspaceId + ":" + target.savedVersionId); }
    function capture(target) { var generation = context.generation(); return function () { return !disposed && context.isCurrent() &&
      context.generation() === generation && key(context.target()) === key(target); }; }
    function operation(target) { return copies.get(key(target)); }
    function refresh() {
      if (disposed) return;
      var target = context.target(), caps = target && target.capabilities || {}, copy = operation(target);
      create.hidden = !target || target.kind !== "HtmlResource";
      create.disabled = !caps.createProject || !!copy;
      create.title = copy ? copy.issue || (copy.pending ? "Проект создаётся…" : "Проект уже создан: откройте его в действиях страницы") : caps.createProject ?
        "Создать проект из точного HTML-источника" : "Создание проекта недоступно для этого источника";
      more.hidden = !target || target.kind === "Home"; more.disabled = !target || target.state !== "Ready";
    }
    async function createProject(target) {
      if (!context.isCurrent() || !target || !target.capabilities.createProject || operation(target)) return;
      if (copies.size >= 64) throw new Error("Закройте эту вкладку перед созданием ещё одного проекта.");
      var accepted = capture(target), copy = { pending: true }; copies.set(key(target), copy); refresh();
      context.status("Создание проекта из точного источника…");
      try {
        var result = target.kind === "WorkspaceHtmlProject" ? await send("copyBrowserProject", { target: target }) :
          await send("createHtmlProjectFromArtifact", { chatId: target.chatId, documentId: target.documentId, resourceUri: target.resourceUri, backing: "Managed" });
        if (result.status === "Rejected") { copies.delete(key(target)); throw new Error(result.message || "Проект не создан."); }
        if (result.status !== "Created" || !result.workspaceId) throw new Error(result.message || "Создание проекта не подтверждено.");
        copy.result = result;
        if (accepted()) {
          context.status("Проект «" + result.name + "» создан.");
          try { await RNAssistantProjectChats.open(result.workspaceId); }
          catch (error) { if (accepted()) context.error(new Error("Проект создан, но открыть его не удалось. " + (error.detail || error.message))); }
        }
      } catch (error) {
        copy.issue = copies.has(key(target)) ? "Результат не подтверждён. Проверьте список проектов. " + (error.detail || error.message) : error.detail || error.message;
        if (accepted()) context.error(new Error(copy.issue));
      } finally { copy.pending = false; refresh(); }
    }
    create.onclick = function () { createProject(context.target()).catch(context.error); };
    more.onclick = function () {
      var target = context.target(); if (!target || target.state !== "Ready") return;
      var caps = target.capabilities, copy = operation(target), items = [];
      if (context.canFind && context.canFind()) items.push({ label: 'Найти на странице', icon: 'search', onSelect: context.find });
      if (context.canScreenshot) items.push({ label: "Скачать снимок страницы", icon: "image", disabled: !context.canScreenshot(), onSelect: function () {
        var request = context.screenshotRequest(), accepted = capture(target);
        return RNAssistantArtifactDownload.save({ title: "rnassistant-page.png" }, { send: send, fetch: window.fetch.bind(window), signal: abort.signal,
          isCurrent: function () { return accepted() && context.canScreenshot(); }, status: context.status, prepareMessage: "Подготовка снимка…",
          open: function () { return context.captureScreenshot(request); },
          close: function (lease) { return send("closeBrowserScreenshot", Object.assign({}, request, { leaseId: lease })); } });
      } });
      if (caps.edit && target.kind === "HtmlWorkspace") items.push({ label: "Редактировать исходники", icon: "code", onSelect: function () {
        return RNAssistantHtmlWorkspaceSurface.open(target.chatId, { mode: "edit" }); } });
      if (caps.exportProject && ["HtmlWorkspace", "HtmlArtifact"].includes(target.kind)) items.push({ label: "Скачать автономный HTML", icon: "download", onSelect: function () {
        return RNAssistantHtmlResourceExport.downloadTarget(target, { send: send, fetch: window.fetch.bind(window), signal: abort.signal,
          isCurrent: capture(target), status: context.status }); } });
      if (caps.exportProject) items.push({ label: "Скачать исходники проекта (ZIP)", icon: "folder", onSelect: function () {
        return RNAssistantArtifactDownload.save(target, { send: send, fetch: window.fetch.bind(window), signal: abort.signal, isCurrent: capture(target), status: context.status,
          open: target.kind === "WorkspaceHtmlProject" ? function () { return send("exportBrowserProject", { target: target }); } : null,
          close: target.kind === "WorkspaceHtmlProject" ? function (lease) { return send("closeBrowserProjectTransfer", { target: target, leaseId: lease }); } : null }); } });
      if (caps.createProject || copy) items.push(copy && copy.result ? { label: "Открыть созданный проект", icon: "folder", onSelect: function () { return RNAssistantProjectChats.open(copy.result.workspaceId); } } :
        { label: "Создать проект из снимка", icon: "copy", status: copy && copy.issue || (copy && copy.pending ? "Проект создаётся…" : "Копия точных исходников в приложении"), disabled: !!copy || !caps.createProject,
          onSelect: function () { return createProject(target); } });
      if (target.resourceUri) items.push({ label: "Копировать точную ссылку", icon: "link", onSelect: function () { return copyTextResult(target.resourceUri); } });
      if (target.pluginId) items.push({ label: "Чат исходников", icon: "chat", onSelect: context.discuss });
      if (!items.length) items.push({ label: "Нет дополнительных действий", disabled: true, icon: false });
      RNAssistantActionMenu.open(more, items);
    };
    refresh();
    return { refresh: refresh, deactivate: function () { abort.abort(); abort = new AbortController(); RNAssistantActionMenu.closeWithin(root); },
      dispose: function () { disposed = true; abort.abort(); RNAssistantActionMenu.closeWithin(root); create.remove(); more.remove(); } };
  }
  window.RNAssistantBrowserActions = { mount: mount };
}());
