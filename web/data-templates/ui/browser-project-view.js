(function (root) {
  "use strict";
  const node = (tag, className, value) => {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (value !== undefined) element.textContent = value;
    return element;
  };
  function create(container, processing) {
    const slot = container.querySelector('[data-dt-slot="workspace"]');
    const panel = node("details", "dt-project-card dt-card");
    panel.setAttribute("aria-label", "Проект данных");
    panel.innerHTML = `<summary class="dt-project-head" id="dtProjectToggle"><div><p class="dt-eyebrow">ПРОЕКТ ДАННЫХ</p><h3 id="dtProjectTitle">Без проекта</h3><p id="dtProjectDescription">Откройте папку с project.json или создайте новый проект.</p></div><span class="dt-inspection-badge" id="dtProjectState">Локально</span></summary><div class="dt-project-body">
      <div class="dt-project-controls"><button type="button" id="dtProjectOpen">Открыть папку проекта</button><label>Новый проект<input id="dtProjectName" maxlength="200" placeholder="Название проекта"></label><button type="button" id="dtProjectCreate">Создать подпапку…</button></div>
      <div class="dt-project-drafts"><div class="dt-project-drafts-head"><div><strong>Черновики шаблонов</strong><p>Каждое сохранение создаёт отдельный файл в drafts/; исходные файлы не меняются.</p></div><div><button type="button" id="dtProjectRefresh">Обновить</button><button type="button" class="dt-primary" id="dtProjectSave">Сохранить черновик</button></div></div><p id="dtProjectNotice" role="status" aria-live="polite"></p><ul id="dtProjectDraftList" aria-label="Сохранённые черновики"></ul></div></div>`;
    slot.append(panel);
    const get = name => panel.querySelector("#dtProject" + name), events = new AbortController();
    const listen = (element, type, handler) => element.addEventListener(type, handler, { signal: events.signal });
    let project = null, busy = false, disposed = false, drafts = [];
    const supported = typeof root.showDirectoryPicker === "function";
    const files = root.DataTemplatesBrowserProjectFiles;
    function message(value) { get("Notice").textContent = value; }
    function render() {
      get("Title").textContent = project ? project.info.name : "Без проекта";
      get("Description").textContent = project ? "project.json · источников: " + project.info.sources + " · черновики в drafts/" :
        supported ? "Откройте папку с project.json или создайте новый проект." :
          "Выбор папки недоступен; черновик можно скачать на вкладке «Шаблоны».";
      panel.title = project ? project.directory.name + "/project.json" : "";
      get("State").textContent = project ? "Открыт" : supported ? "Не выбран" : "Недоступно";
      for (const id of ["Open", "Create"]) get(id).disabled = busy || !supported;
      get("Name").disabled = busy || !supported;
      for (const id of ["Refresh", "Save"]) get(id).disabled = busy || !project;
      get("DraftList").replaceChildren(...drafts.map(item => {
        const row = node("li"), label = node("span", "", item.label + " · " + new Date(item.savedAt).toLocaleString("ru-RU"));
        const button = node("button", "dt-text-button", "Открыть"); button.type = "button";
        button.dataset.file = item.file; button.disabled = busy;
        row.append(label, button); return row;
      }));
      if (project && !drafts.length) get("DraftList").append(node("li", "dt-project-empty", "В проекте пока нет сохранённых черновиков."));
    }
    async function perform(work, success) {
      if (busy || disposed) return;
      busy = true; render();
      try {
        const result = await work();
        if (!disposed) message(typeof success === "function" ? success(result) : success);
      }
      catch (error) { if (!disposed && error.name !== "AbortError") message((error.code || "PROJECT_ERROR") + ": " + error.message); }
      finally { busy = false; if (!disposed) render(); }
    }
    async function select(createNew) {
      if (busy || disposed) return;
      let name;
      if (createNew) {
        name = get("Name").value.trim();
        if (!name) { message("Укажите название нового проекта."); get("Name").focus(); return; }
        try { root.DataTemplatesProjectManifest.create(name); }
        catch (error) { message(error.code + ": " + error.message); return; }
      }
      // The picker must run in the click gesture, before the first asynchronous operation.
      let selected;
      busy = true; render();
      try { selected = await root.showDirectoryPicker({ id: createNew ? "data-templates-parent" : "data-templates-project", mode: "readwrite" }); }
      catch (error) { if (error.name !== "AbortError") message((error.code || "PROJECT_ERROR") + ": " + error.message); }
      finally { busy = false; render(); }
      if (!selected) return;
      await perform(async () => {
        const opened = createNew ? await files.create(selected, name) : await files.open(selected);
        const found = await files.listDrafts(opened);
        project = opened; drafts = found;
      }, createNew ? "Проект создан в новой подпапке. Можно сохранить текущий черновик." : "Проект открыт.");
    }
    listen(get("Open"), "click", () => select(false));
    listen(get("Create"), "click", () => select(true));
    listen(get("Refresh"), "click", () => perform(async () => { drafts = await files.listDrafts(project); }, "Список черновиков обновлён."));
    listen(get("Save"), "click", () => {
      const draft = processing.currentDraft();
      if (!draft) { message("Сначала выберите шаблон и завершите текущую правку или обработку."); return; }
      perform(async () => {
        await files.saveDraft(project, draft);
        processing.markDraftSaved(draft.input);
        try { drafts = await files.listDrafts(project); return null; }
        catch (error) { return error; }
      }, error => error
        ? "Черновик сохранён и проверен, но список не обновился: " + error.message
        : "Черновик сохранён отдельным файлом и проверен чтением.");
    });
    listen(get("DraftList"), "click", event => {
      const button = event.target.closest("button[data-file]");
      if (!button || busy) return;
      perform(async () => {
        const draft = await files.openDraft(project, button.dataset.file);
        const accepted = await processing.loadDraft(draft.name, draft.input);
        if (!accepted) throw Object.assign(new Error("Текущий черновик оставлен без изменений."), { code: "CANCELLED" });
      }, "Черновик открыт. Проверка шаблона показана на вкладке «Шаблоны».");
    });
    if (!supported) {
      message("Выбор папки проекта недоступен в этом браузере. Черновик можно скачать во вкладке «Шаблоны».");
    } else message("Черновики сохраняются только после явного выбора папки проекта.");
    render();
    return Object.freeze({ dispose: () => { disposed = true; events.abort(); panel.remove(); } });
  }
  root.DataTemplatesBrowserProjectView = Object.freeze({ create: create });
}(window));
