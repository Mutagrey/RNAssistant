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
  }
  window.RNAssistantArtifactVersions = { append: append };
}());
