(function () {
  "use strict";

  function append(root, head, selectedRevision, onSelect, context) {
    if (!head || head.historyCount < 2 ||
        ["versioned_document", "versioned_aggregate"].indexOf(head.resourceClass) < 0) return;
    var chatId = context ? context.chatId : state.activeChatId, navigation = state.chatNavigationVersion;
    var sessionRevision = context ? context.sessionRevision : state.artifactLibrary.sessionRevision;
    var selected = selectedRevision || head;
    var label = (head.resourceClass === "versioned_aggregate" ? "Версии проекта" : "Версии") + " · v" + selected.revision;
    var trigger = RNAssistantActionMenu.createTrigger(label);
    trigger.classList.add("compact", "artifact-version-menu"); trigger.innerHTML = RNAssistantIcons.render("clock");
    if (!context || !context.compact) { trigger.className = "button compact artifact-version-menu"; trigger.appendChild(document.createTextNode(label)); }
    root.appendChild(trigger);
    var menu = null, loaded = false, pending = false, cursor = null, ids = new Set(), revisions = [], errorMessage = "";
    function current() {
      if (!trigger.isConnected || !menu || !menu.isOpen()) return false;
      if (context) return context.isCurrent();
      return chatId === state.activeChatId && navigation === state.chatNavigationVersion &&
        sessionRevision === (state.artifactLibrary || {}).sessionRevision && !state.chatDetailPending && !state.chatDetailError;
    }
    function entries() {
      var items = [{ label: head.resourceClass === "versioned_aggregate" ? "История проекта" : "История файла", disabled: true, icon: false,
        status: head.resourceClass === "versioned_aggregate" ? "HTML, CSS, JS и привязки данных — одна версия." : "Сохранённые версии этого файла." }];
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
        icon: errorMessage ? "refresh" : "down", keepOpen: true, onSelect: load });
      return items;
    }
    async function load() {
      if (pending || !current()) return;
      pending = true; errorMessage = ""; menu.update(entries());
      try {
        var result = await (context ? context.send : send)("getArtifactLibraryHistory", { chatId: chatId, expectedSessionRevision: sessionRevision,
          headArtifactId: head.artifactId, cursor: cursor });
        if (!current()) return;
        if (result.chatId !== chatId || result.sessionRevision !== sessionRevision || result.headArtifactId !== head.artifactId)
          throw new Error("История изменилась. Обновите список артефактов.");
        (result.items || []).forEach(function (revision) {
          if (!revision.artifactId || !revision.resourceUri || ids.has(revision.artifactId)) return;
          ids.add(revision.artifactId); revisions.push(revision);
        });
        cursor = result.nextCursor || null; loaded = true;
      } catch (error) { if (current()) errorMessage = error.detail || error.message || "Не удалось загрузить версии."; }
      finally { pending = false; if (current()) menu.update(entries()); }
    }
    trigger.onclick = function () {
      if (menu && menu.isOpen()) { menu.close(); return; }
      menu = RNAssistantActionMenu.open(trigger, entries());
      if (!loaded) load();
    };
  }
  window.RNAssistantArtifactVersions = { append: append };
}());
