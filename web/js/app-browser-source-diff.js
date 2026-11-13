(function () {
  "use strict";
  // Read-only presentation over the common checkpoint catalog and existing source owners.
  function create(target) {
    var root = document.createElement("section"); root.className = "browser-source-diff"; root.hidden = true;
    root.setAttribute("aria-label", "Сравнение сохранённых исходников");
    var controls = document.createElement("div"); controls.className = "browser-source-diff-controls";
    var choose = RNAssistantActionMenu.createTrigger("Сравнить с версией");
    choose.className = "button compact"; choose.textContent = "Сравнить с версией…";
    var pair = document.createElement("div"); pair.className = "browser-source-diff-pair";
    var files = document.createElement("select"); files.className = "rna-form-control compact"; files.hidden = true;
    files.setAttribute("aria-label", "Файл для сравнения");
    var note = document.createElement("p"); note.className = "browser-source-diff-note";
    note.textContent = "Сохранённые исходники. Привязки и значения данных предпросмотра не сравниваются.";
    var output = document.createElement("div"); output.className = "browser-source-diff-output";
    output.tabIndex = 0; output.setAttribute("aria-label", "Изменения исходников");
    var status = document.createElement("div"); status.className = "browser-source-diff-status"; status.setAttribute("role", "status");
    status.textContent = "Выберите вторую сохранённую версию.";
    controls.append(choose, files); root.append(controls, pair, note, status, output);
    var afterId = target.savedVersionId || target.resourceUri;
    var active = false, disposed = false, epoch = 0, before = null, ready = false, menu = null, menuEpoch = 0;
    var pending = new Set(), filePath = null, head = null;
    var changes = { Added: "добавлен", Removed: "удалён", Changed: "изменён", Unchanged: "без изменений" };
    async function request(type, payload) {
      var task = send(type, payload); pending.add(task);
      try { return await task; } finally { pending.delete(task); }
    }
    function stop() {
      epoch++; menuEpoch++;
      pending.forEach(function (task) { if (task.requestId) cancelBridgeRequest(task.requestId).catch(function () {}); });
      pending.clear(); if (menu) menu.close(); menu = null;
    }
    function current(version) { return active && !disposed && epoch === version; }
    function compare(path) {
      return request("compareBrowserSources", { target: target, beforeId: before.id, afterId: afterId, path: path });
    }

    function accept(value, path) {
      if (!value || value.beforeId !== before.id || value.afterId !== afterId || value.path !== path ||
          !Array.isArray(value.files) || value.files.length > 1024 || value.files.some(function (f) { return !f.path || !changes[f.change]; }))
        throw new Error("Ответ сравнения не соответствует выбранной паре.");
      return value;
    }
    async function load(path) {
      if (!active || disposed || !before) return;
      stop(); var version = epoch; ready = false; filePath = path; files.disabled = true;
      output.replaceChildren(); status.textContent = "Читаю выбранные исходники…";
      try {
        var value = await compare(path);
        if (!current(version)) return;
        accept(value, path);
        pair.textContent = value.beforeLabel + " → " + value.afterLabel;
        files.replaceChildren();
        value.files.forEach(function (file) { files.appendChild(new Option(file.path + " · " + changes[file.change], file.path)); });
        files.hidden = !value.files.length;
        if (!value.files.length) { status.textContent = "В выбранных версиях нет файлов."; ready = true; return; }
        if (path === null) {
          path = (value.files.find(function (f) { return f.change !== "Unchanged"; }) || value.files[0]).path;
          filePath = path; files.value = path;
          value = await compare(path);
          if (!current(version)) return;
          accept(value, path);
        }
        files.value = path;
        if (!value.complete) status.textContent = value.message || "Полное текстовое сравнение недоступно.";
        else {
          if (typeof value.beforeText !== "string" || typeof value.afterText !== "string" || value.beforeText.length + value.afterText.length > 512000)
            throw new Error("Содержимое сравнения недоступно или превышает лимит.");
          var diff = RNAssistantTextDiff.format(value.beforeText, value.afterText);
          if (value.beforeText === "" && value.afterText === "") {
            var change = value.files.find(function (f) { return f.path === path; }).change;
            if (change === "Added" || change === "Removed") diff.summary = change === "Added" ? "Добавлен пустой файл." : "Удалён пустой файл.";
          }
          status.textContent = ""; RNAssistantTextDiff.render(output, diff);
        }
        ready = true;
      } catch (error) { if (current(version)) status.textContent = error.detail || error.message || "Не удалось прочитать исходники."; }
      finally { if (current(version)) files.disabled = false; }
    }
    async function history(cursor) {
      var version = ++menuEpoch, owner = menu;
      function accepted() { return active && !disposed && menuEpoch === version && menu === owner && owner.isOpen(); }
      owner.update([{ label: "Загрузка версий…", disabled: true, icon: false }]);
      try {
        var value = await request("getBrowserVersions", { target: target, cursor: cursor, limit: 12 });
        if (!accepted()) return;
        if (head && value.stateRevision !== head) throw new Error("Каталог изменился. Откройте выбор версий снова.");
        head = value.stateRevision;
        var entries = (value.items || []).map(function (item) {
          var id = item.versionId;
          var label = "v" + item.ordinal, date = new Date(item.createdUtc || item.capturedUtc);
          return { key: id, label: label, icon: "clock", disabled: id === afterId,
            status: id === afterId ? "Открытая версия" : !isNaN(date.getTime()) ? date.toLocaleString() : "",
            checked: before && before.id === id, onSelect: function () {
              if (!active || disposed) return;
              before = { id: id, label: label }; choose.textContent = "Сравнить с " + label;
              pair.textContent = ""; files.hidden = true; return load(null);
            } };
        });
        if (!entries.length) entries.push({ label: "Сохранённых версий нет.", disabled: true, icon: false });
        if (value.cursor) entries.push({ label: "Следующая страница версий", icon: "right", keepOpen: true, onSelect: function () { history(value.cursor); } });
        if (cursor) entries.push({ label: "В начало истории", icon: "left", keepOpen: true, onSelect: function () { history(null); } });
        owner.update(entries);
      } catch (error) {
        if (accepted()) owner.update([{ label: "Не удалось загрузить версии", status: error.detail || error.message, disabled: true, icon: false }]);
      }
    }
    choose.onclick = function () {
      if (!active || disposed) return;
      if (menu && menu.isOpen()) { menuEpoch++; menu.close(); return; }
      head = null; menu = RNAssistantActionMenu.open(choose, []); if (menu) history(null);
    };
    files.onchange = function () { load(files.value); };
    return { element: root,
      activate: function () { if (disposed) return; active = true; root.hidden = false; if (before && !ready) load(filePath); },
      deactivate: function () { active = false; stop(); root.hidden = true; },
      dispose: function () { disposed = true; active = false; stop(); before = null; root.replaceChildren(); } };
  }
  window.RNAssistantBrowserSourceDiff = { create: create };
}());
