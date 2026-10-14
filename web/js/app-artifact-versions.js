(function () {
  "use strict";

  function append(root, head, selectedRevision, onSelect) {
    if (!head || head.historyCount < 2 ||
        ["versioned_document", "versioned_aggregate"].indexOf(head.resourceClass) < 0) return;
    var chatId = state.activeChatId, navigation = state.chatNavigationVersion;
    var sessionRevision = state.artifactLibrary.sessionRevision;
    var selected = selectedRevision || head;
    var menu = document.createElement("details");
    menu.className = "html-workspace-menu artifact-version-menu";
    var summary = document.createElement("summary");
    summary.textContent = (head.resourceClass === "versioned_aggregate" ? "Версия проекта" : "Версия") +
      " · v" + selected.revision + " ▾";
    menu.appendChild(summary);
    var panel = document.createElement("div");
    panel.className = "html-workspace-menu-items artifact-version-options";
    var note = document.createElement("p");
    note.textContent = head.resourceClass === "versioned_aggregate"
      ? "HTML, CSS, JS и привязки данных — одна версия проекта."
      : "Просмотр сохранённых версий этого файла.";
    panel.appendChild(note);
    var rows = document.createElement("div"); panel.appendChild(rows);
    var message = document.createElement("p"); message.setAttribute("role", "status"); panel.appendChild(message);
    var more = document.createElement("button");
    more.type = "button"; more.textContent = "Показать ещё 50"; more.hidden = true; panel.appendChild(more);
    menu.appendChild(panel); root.appendChild(menu);
    var loaded = false, pending = false, cursor = null, ids = new Set();
    function current() {
      return menu.isConnected && chatId === state.activeChatId && navigation === state.chatNavigationVersion &&
        sessionRevision === (state.artifactLibrary || {}).sessionRevision && !state.chatDetailPending && !state.chatDetailError;
    }
    async function load() {
      if (pending || !current()) return;
      pending = true; more.disabled = true; message.textContent = "Загрузка версий…";
      try {
        var result = await send("getArtifactLibraryHistory", { chatId: chatId, expectedSessionRevision: sessionRevision,
          headArtifactId: head.artifactId, cursor: cursor });
        if (!current()) return;
        if (result.chatId !== chatId || result.sessionRevision !== sessionRevision || result.headArtifactId !== head.artifactId)
          throw new Error("История изменилась. Обновите список артефактов.");
        (result.items || []).forEach(function (revision) {
          if (!revision.artifactId || !revision.resourceUri || ids.has(revision.artifactId)) return;
          ids.add(revision.artifactId);
          var button = document.createElement("button"); button.type = "button";
          var label = document.createElement("strong");
          label.textContent = "v" + revision.revision + (revision.isHead ? " · текущая" :
            revision.relation === "branch" ? " · другая ветка" : "");
          button.appendChild(label);
          var date = new Date(revision.createdUtc);
          if (revision.createdUtc && !isNaN(date.getTime())) {
            var time = document.createElement("span"); time.textContent = date.toLocaleString(); button.appendChild(time);
          }
          button.setAttribute("aria-current", String(revision.resourceUri === selected.resourceUri));
          button.addEventListener("click", function () {
            if (!current()) return;
            if (onSelect(revision) !== false) {
              menu.open = false;
              var focus = root.querySelector(".artifact-version-menu summary");
              if (focus) focus.focus();
            }
          });
          rows.appendChild(button);
        });
        cursor = result.nextCursor || null; more.hidden = !cursor; loaded = true; message.textContent = "";
      } catch (error) {
        if (current()) { message.textContent = error.detail || error.message || "Не удалось загрузить версии.";
          more.hidden = false; more.textContent = "Повторить загрузку"; }
      } finally { pending = false; more.disabled = false; }
    }
    more.addEventListener("click", function () { more.textContent = "Показать ещё 50"; load(); });
    menu.addEventListener("toggle", function () { if (menu.open && !loaded) load(); });
    menu.addEventListener("keydown", function (event) {
      if (event.key === "Escape") { menu.open = false; summary.focus(); event.stopPropagation(); }
    });
  }

  document.addEventListener("click", function (event) {
    document.querySelectorAll(".artifact-version-menu[open]").forEach(function (menu) {
      if (!menu.contains(event.target)) menu.open = false;
    });
  });
  window.RNAssistantArtifactVersions = { append: append };
}());
