(function (root) {
  "use strict";
  const pageSize = 20, maximumTemplateBytes = 1000000;
  const node = (tag, className, value) => {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (value !== undefined) element.textContent = value;
    return element;
  };
  function create(container, processing, projectView, dictionaryController) {
    const panel = node("section", "dt-card dt-template-library");
    panel.setAttribute("aria-label", "Библиотека шаблонов проекта");
    panel.innerHTML = `<div class="dt-library-head"><div><p class="dt-eyebrow">БИБЛИОТЕКА ПРОЕКТА</p><h4>Шаблоны</h4><p id="dtLibraryProject">Откройте проект данных, чтобы хранить несколько шаблонов.</p></div><span id="dtLibraryCount">0</span></div>
      <div class="dt-library-actions"><input id="dtLibraryFiles" type="file" accept=".json,application/json" multiple hidden><button type="button" id="dtLibraryImport">Добавить JSON-шаблоны…</button><button type="button" class="dt-primary" id="dtLibrarySave">Сохранить текущий как версию</button><button type="button" id="dtLibraryRefresh">Обновить</button><button type="button" id="dtLibraryCancel" hidden>Отменить импорт</button></div>
      <p id="dtLibraryNotice" class="dt-library-notice" role="status" aria-live="polite"></p>
      <div class="dt-library-filter"><input id="dtLibrarySearch" type="search" placeholder="Найти по коду…" aria-label="Поиск шаблона"><label><input id="dtLibraryArchived" type="checkbox">Показать удалённые</label></div>
      <ul id="dtLibraryList" aria-label="Шаблоны проекта"></ul><div class="dt-library-paging" id="dtLibraryPaging" hidden><button type="button" id="dtLibraryPrevious">← Назад</button><span id="dtLibraryPage"></span><button type="button" id="dtLibraryNext">Далее →</button></div>`;
    container.querySelector("#dtTemplateDrop").insertAdjacentElement("beforebegin", panel);
    const get = id => panel.querySelector("#dtLibrary" + id), events = new AbortController();
    const listen = (element, type, handler) => element.addEventListener(type, handler, { signal: events.signal });
    const library = root.DataTemplatesBrowserTemplateLibrary;
    let project = null, items = [], validated = null, busy = false, disposed = false, abort = null;
    let page = 0, activeId = null, activeHead = null, seenGeneration = null;
    function notice(value) { get("Notice").textContent = value; }
    function render() {
      get("Project").textContent = project ? project.info.name + " · общий справочник · версии в templates/" :
        dictionaryController.active() ? "Загружаем общий справочник проекта…" :
          "Откройте проект данных в верхней карточке, чтобы хранить несколько шаблонов.";
      get("Count").textContent = items.filter(item => !item.archived).length + " активных";
      get("Import").disabled = get("Save").disabled = get("Refresh").disabled = busy || !project;
      get("Save").disabled = busy || !project || !validated;
      get("Cancel").hidden = !abort;
      const query = get("Search").value.trim().toLocaleLowerCase(), showArchived = get("Archived").checked;
      const matched = items.filter(item => (showArchived || !item.archived) && item.id.toLocaleLowerCase().includes(query));
      page = Math.max(0, Math.min(page, Math.ceil(matched.length / pageSize) - 1));
      get("List").replaceChildren(...matched.slice(page * pageSize, (page + 1) * pageSize).map(item => {
        const row = node("li", item.archived ? "dt-library-archived" : "");
        const info = node("div"); info.append(node("strong", "", item.id), node("small", "",
          (item.archived ? "Удалён · " : "") + item.versions + " версий · " + new Date(item.savedAt).toLocaleString("ru-RU")));
        const actions = node("div", "dt-library-row-actions");
        if (!item.archived) {
          const open = node("button", "dt-text-button", "Открыть"); open.type = "button";
          open.dataset.action = "open"; open.dataset.id = item.id; open.disabled = busy; actions.append(open);
        }
        const toggle = node("button", "dt-text-button", item.archived ? "Восстановить" : "Удалить");
        toggle.type = "button"; toggle.dataset.action = item.archived ? "restore" : "archive";
        toggle.dataset.id = item.id; toggle.disabled = busy; actions.append(toggle);
        row.append(info, actions); return row;
      }));
      if (!matched.length) get("List").append(node("li", "dt-library-empty", project ?
        "Шаблонов нет. Добавьте один или несколько JSON-файлов либо сохраните текущий проверенный черновик." :
        "Сначала откройте проект данных."));
      get("Paging").hidden = matched.length <= pageSize;
      get("Page").textContent = (page + 1) + " / " + Math.max(1, Math.ceil(matched.length / pageSize));
      get("Previous").disabled = page === 0;
      get("Next").disabled = (page + 1) * pageSize >= matched.length;
    }
    async function perform(work) {
      if (busy || disposed) return;
      if (processing.isRunning()) { notice("Дождитесь завершения обработки или записи общего справочника."); return; }
      if (processing.hasPendingEdit()) { notice("Сначала примените или отмените текущую правку."); return; }
      busy = true; render();
      try { await projectView.withProject(work); }
      catch (error) { if (!disposed) notice((error.code || "TEMPLATE_ERROR") + ": " + error.message); }
      finally { busy = false; abort = null; if (!disposed) render(); }
    }
    function refresh() {
      if (!project) { items = []; render(); return; }
      perform(async current => { await dictionaryController.assertCurrent(current);
        items = await library.list(current); if (!disposed) notice("Библиотека обновлена."); });
    }
    async function materialized(current, source, signal) {
      await dictionaryController.assertCurrent(current);
      let parsed;
      try { parsed = root.RNTabularJson.parse(source); }
      catch (error) { throw Object.assign(new Error("Некорректный JSON: " + error.message), { code: "TEMPLATE_INVALID" }); }
      if (parsed.kind === "data-templates-project-template") {
        const shared = dictionaryController.current();
        return root.DataTemplatesProjectTemplate.materialize(source, shared && shared.dictionary, current.info.projectId);
      }
      await root.DataTemplatesTemplateInspection.inspect(source, signal || new AbortController().signal);
      if (!root.confirm("Старый шаблон содержит собственный справочник. Объединить его с общим справочником проекта и продолжить?"))
        throw Object.assign(new Error("Импорт старого справочника отменён."), { code: "CANCELLED" });
      const full = root.DataTemplatesProjectTemplate.standalone(source);
      await dictionaryController.absorb(current, full.dictionaries, signal);
      const shared = dictionaryController.current();
      const stored = root.DataTemplatesProjectTemplate.detach(source, current.info.projectId);
      return root.DataTemplatesProjectTemplate.materialize(stored, shared.dictionary, current.info.projectId);
    }
    async function refreshAfterWrite(current, success) {
      try { items = await library.list(current); notice(success); }
      catch (error) { notice(success + " Список не обновился: " + error.message); }
    }
    listen(get("Refresh"), "click", refresh);
    listen(get("Search"), "input", () => { page = 0; render(); });
    listen(get("Archived"), "change", () => { page = 0; render(); });
    listen(get("Previous"), "click", () => { page--; render(); });
    listen(get("Next"), "click", () => { page++; render(); });
    listen(get("Import"), "click", () => get("Files").click());
    listen(get("Files"), "change", () => {
      const files = Array.from(get("Files").files); get("Files").value = "";
      if (!files.length) return;
      if (files.length > 50) { notice("За один раз можно добавить до 50 шаблонов."); return; }
      perform(async current => {
        abort = new AbortController(); render();
        let added = 0, rejected = 0, partial = 0; const errors = [];
        const knownIds = new Set((await library.list(current)).map(item => item.id));
        for (let index = 0; index < files.length; index++) {
          if (abort.signal.aborted) break;
          const file = files[index]; notice("Проверяем " + (index + 1) + " из " + files.length + ": " + file.name);
          let dictionaryChanged = false;
          try {
            if (!file.size || file.size > maximumTemplateBytes) throw Object.assign(new Error("Файл превышает 1 000 000 байт."), { code: "TEMPLATE_LIMIT" });
            const text = new TextDecoder("utf-8", { fatal: true }).decode(await file.arrayBuffer());
            const parsed = root.DataTemplatesProjectTemplate.standalone(text), id = parsed.template && parsed.template.id;
            if (knownIds.has(id))
              throw Object.assign(new Error("Код уже есть в библиотеке; откройте его и сохраните новую версию."), { code: "TEMPLATE_EXISTS" });
            await root.DataTemplatesTemplateInspection.inspect(text, abort.signal);
            const previous = dictionaryController.current();
            const absorbed = await dictionaryController.absorb(current, parsed.dictionaries, abort.signal);
            dictionaryChanged = !previous || previous.head !== absorbed.head;
            const shared = dictionaryController.current(), stored = root.DataTemplatesProjectTemplate.detach(text, current.info.projectId);
            const execution = root.DataTemplatesProjectTemplate.materialize(stored, shared.dictionary, current.info.projectId);
            await library.save(current, id, stored, null, { materialized: execution,
              dictionaryHead: shared.head, dictionaryText: shared.text, signal: abort.signal });
            knownIds.add(id); added++;
          } catch (error) {
            if (error.code === "effect_unknown") {
              error.message = "Уже добавлено: " + added + ". " +
                (dictionaryChanged ? "Общий справочник обновлён, шаблон мог не сохраниться. " : "") + error.message;
              throw error;
            }
            if (abort.signal.aborted) break;
            rejected++; if (dictionaryChanged) partial++;
            const detail = file.name + ": " + (dictionaryChanged ? "общий справочник обновлён; " : "") +
              (error.code || "TEMPLATE_INVALID") + " · " + error.message;
            if (errors.length < 3) errors.push(detail);
            notice(detail);
          }
        }
        await refreshAfterWrite(current, "Добавлено: " + added + ". Не принято: " + rejected +
          (partial ? ". Справочник обновлён без шаблона для " + partial + " файлов." : "") +
          (abort.signal.aborted ? " Импорт отменён." : ".") +
          (errors.length ? " Причины: " + errors.join("; ") : ""));
      });
    });
    listen(get("Cancel"), "click", () => { if (abort) abort.abort(); });
    listen(get("Save"), "click", () => {
      const draft = processing.currentValidated();
      if (!draft) { notice("Сначала завершите проверку текущего шаблона и правок."); return; }
      perform(async current => {
        const id = draft.inspection.template.id, existing = (await library.list(current)).find(item => item.id === id);
        if (existing && activeId === id && activeHead && existing.head !== activeHead)
          throw Object.assign(new Error("Версия изменилась с момента открытия. Сохраните черновик отдельно и обновите библиотеку."), { code: "TEMPLATE_CHANGED" });
        if (existing && (existing.archived || activeHead !== existing.head) &&
            !root.confirm("Шаблон «" + id + "» уже есть в проекте. Сохранить текущий JSON как новую версию?")) return;
        const full = root.DataTemplatesProjectTemplate.standalone(draft.input);
        const previous = dictionaryController.current();
        const absorbed = await dictionaryController.absorb(current, full.dictionaries);
        const shared = dictionaryController.current(), stored = root.DataTemplatesProjectTemplate.detach(draft.input, current.info.projectId);
        const execution = root.DataTemplatesProjectTemplate.materialize(stored, shared.dictionary, current.info.projectId);
        let saved;
        try { saved = await library.save(current, id, stored, existing ? existing.head : null,
          { materialized: execution, dictionaryHead: shared.head, dictionaryText: shared.text }); }
        catch (error) {
          if (!previous || previous.head !== absorbed.head)
            error.message = "Общий справочник сохранён, но версия шаблона не подтверждена. " + error.message;
          throw error;
        }
        processing.markDraftSaved(execution);
        if (processing.currentValidated() && processing.currentValidated().input === execution) {
          activeId = id; activeHead = saved.head;
        }
        await refreshAfterWrite(current, "Версия шаблона «" + id + "» сохранена и проверена чтением.");
      });
    });
    listen(get("List"), "click", event => {
      const button = event.target.closest("button[data-action]");
      if (!button || busy) return;
      const entry = items.find(item => item.id === button.dataset.id);
      if (!entry) return;
      if (button.dataset.action === "archive" && !root.confirm("Удалить «" + entry.id + "» из активной библиотеки? Версии останутся в templates/ для восстановления.")) return;
      perform(async current => {
        if (button.dataset.action === "open") {
          const raw = await library.open(current, entry);
          const text = await materialized(current, raw);
          if (await processing.loadDraft(entry.id + ".json", text)) {
            activeId = entry.id; activeHead = entry.head;
            container.querySelector("#dtTabTemplates").click();
            notice(processing.currentValidated() ? "Шаблон открыт для редактирования и проверен Worker." :
              "Файл открыт как черновик, но его текущие байты не прошли проверку. Исправьте JSON или восстановите файл.");
          }
        } else if (button.dataset.action === "archive") {
          await library.archive(current, entry);
          await refreshAfterWrite(current, "Шаблон убран из активной библиотеки; версии сохранены для восстановления.");
        } else if (button.dataset.action === "restore") {
          const raw = await library.restore(current, entry);
          const previous = dictionaryController.current();
          const text = await materialized(current, raw);
          const shared = dictionaryController.current();
          const stored = root.DataTemplatesProjectTemplate.detach(text, current.info.projectId);
          try {
            await library.save(current, entry.id, stored, entry.head,
              { materialized: text, dictionaryHead: shared.head, dictionaryText: shared.text });
          } catch (error) {
            if (!previous || previous.head !== shared.head)
              error.message = "Общий справочник сохранён, но восстановление шаблона не подтверждено. " + error.message;
            throw error;
          }
          await refreshAfterWrite(current, "Шаблон восстановлен как новая проверенная версия.");
        }
      });
    });
    dictionaryController.subscribe(value => { project = value; activeId = activeHead = null; refresh(); });
    processing.subscribeValidatedDraft((value, generation) => {
      if (seenGeneration !== generation) { seenGeneration = generation; activeId = activeHead = null; }
      validated = value;
      if (!disposed) get("Save").disabled = busy || !project || !validated;
    });
    render();
    return Object.freeze({ active: () => activeId,
      dispose: () => { disposed = true; if (abort) abort.abort(); events.abort(); panel.remove(); } });
  }
  root.DataTemplatesTemplateLibraryView = Object.freeze({ create });
}(window));
