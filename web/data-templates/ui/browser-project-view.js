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
      <div class="dt-project-drafts"><div class="dt-project-drafts-head"><div><strong>Черновики шаблонов</strong><p>Каждое сохранение создаёт отдельный файл в drafts/; исходные файлы не меняются.</p></div><div><button type="button" id="dtProjectRefresh">Обновить</button><button type="button" class="dt-primary" id="dtProjectSave">Сохранить черновик</button></div></div><p id="dtProjectNotice" role="status" aria-live="polite"></p><ul id="dtProjectDraftList" aria-label="Сохранённые черновики"></ul></div>
      <div class="dt-project-drafts"><div class="dt-project-drafts-head"><div><strong>Результаты извлечения</strong><p>Готовый файл сохраняется отдельно в results/ после проверки байтов. Это не публикация dataset.</p></div><div><button type="button" class="dt-primary" id="dtProjectSaveResult">Сохранить выбранный</button><button type="button" id="dtProjectCancelResult" hidden>Отменить</button></div></div><p id="dtProjectSelectedResult" class="dt-project-selection">Выберите готовый файл на вкладке «Результаты».</p><div id="dtProjectResultProgress" class="dt-project-transfer" hidden><progress id="dtProjectResultMeter" max="100" value="0" aria-label="Сохранение или проверка результата"></progress><span id="dtProjectResultStage"></span></div><p id="dtProjectResultNotice" role="status" aria-live="polite"></p><ul id="dtProjectResultList" aria-label="Сохранённые результаты"></ul></div></div>`;
    slot.append(panel);
    const get = name => panel.querySelector("#dtProject" + name), events = new AbortController();
    const listen = (element, type, handler) => element.addEventListener(type, handler, { signal: events.signal });
    let project = null, busy = false, disposed = false, drafts = [], results = [], incomplete = 0, limited = false;
    let selectedResult = null, transfer = null;
    const supported = typeof root.showDirectoryPicker === "function";
    const files = root.DataTemplatesBrowserProjectFiles;
    const resultFiles = root.DataTemplatesBrowserProjectResults;
    function message(value) { get("Notice").textContent = value; }
    function resultMessage(value) { get("ResultNotice").textContent = value; }
    function renderSelected() {
      get("SelectedResult").textContent = selectedResult ? selectedResult.path + " · " + selectedResult.recordCount + " записей" :
        "Выберите готовый файл после завершения очереди на вкладке «Результаты».";
      get("SaveResult").disabled = busy || !project || !selectedResult;
      get("CancelResult").hidden = !transfer;
    }
    function transferProgress(state) {
      if (disposed || !transfer) return;
      get("ResultProgress").hidden = false;
      get("ResultMeter").value = state.total ? Math.floor(state.done / state.total * 100) : 0;
      get("ResultStage").textContent = (state.phase === "copy" ? "Запись" : "Проверка") +
        " · " + Math.ceil(state.done / 1048576) + " / " + Math.ceil(state.total / 1048576) + " МиБ";
    }
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
      renderSelected();
      get("DraftList").replaceChildren(...drafts.map(item => {
        const row = node("li"), label = node("span", "", item.label + " · " + new Date(item.savedAt).toLocaleString("ru-RU"));
        const button = node("button", "dt-text-button", "Открыть"); button.type = "button";
        button.dataset.file = item.file; button.disabled = busy;
        row.append(label, button); return row;
      }));
      if (project && !drafts.length) get("DraftList").append(node("li", "dt-project-empty", "В проекте пока нет сохранённых черновиков."));
      get("ResultList").replaceChildren(...results.map(item => {
        const row = node("li"), label = node("span", "", item.sourcePath + " · " + item.recordCount +
          " записей · " + new Date(item.savedAt).toLocaleString("ru-RU"));
        const button = node("button", "dt-text-button", "Скачать"); button.type = "button";
        button.dataset.result = item.folder; button.disabled = busy;
        row.append(label, button); return row;
      }));
      if (project && !results.length) get("ResultList").append(node("li", "dt-project-empty", "Сохранённых результатов пока нет."));
      if (incomplete || limited) get("ResultList").append(node("li", "dt-project-empty",
        (incomplete ? "Незавершённых или повреждённых папок среди последних 100: " + incomplete + ". " : "") +
        (limited ? "Показаны последние 100 папок; старые доступны в results/." : "") +
        "Проверьте results/ вручную перед повторным сохранением."));
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
        const found = await files.listDrafts(opened), saved = await resultFiles.list(opened);
        project = opened; drafts = found; results = saved.entries; incomplete = saved.incomplete; limited = saved.limited;
      }, createNew ? "Проект создан в новой подпапке. Можно сохранить текущий черновик." : "Проект открыт.");
    }
    listen(get("Open"), "click", () => select(false));
    listen(get("Create"), "click", () => select(true));
    listen(get("Refresh"), "click", () => perform(async () => {
      drafts = await files.listDrafts(project);
      const saved = await resultFiles.list(project);
      results = saved.entries; incomplete = saved.incomplete; limited = saved.limited;
    }, "Списки проекта обновлены."));
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
    listen(get("SaveResult"), "click", async () => {
      if (busy || !project || !selectedResult) return;
      const currentProject = project, controller = new AbortController();
      busy = true; transfer = controller; resultMessage("Подготовка результата к сохранению…"); render();
      try {
        const receipt = await processing.withSelectedResult(result =>
          resultFiles.save(currentProject, result, controller.signal, transferProgress));
        if (disposed) return;
        resultMessage("Сохранено и проверено: results/" + receipt.folder + "/data.json");
        try {
          const saved = await resultFiles.list(currentProject);
          results = saved.entries; incomplete = saved.incomplete; limited = saved.limited;
        } catch (error) { resultMessage("Результат сохранён, но список не обновился: " + error.message); }
      } catch (error) {
        if (!disposed) resultMessage(error.code === "RESULT_CANCELLED" ? "Сохранение отменено; неполная папка удалена." :
          (error.code || "RESULT_ERROR") + ": " + error.message);
      } finally {
        transfer = null; busy = false; if (!disposed) { get("ResultProgress").hidden = true; render(); }
      }
    });
    listen(get("CancelResult"), "click", () => { if (transfer) transfer.abort(); });
    listen(get("ResultList"), "click", async event => {
      const button = event.target.closest("button[data-result]");
      if (!button || busy || !project) return;
      const currentProject = project, controller = new AbortController();
      busy = true; transfer = controller; resultMessage("Проверяем сохранённый результат…"); render();
      try {
        const saved = await resultFiles.open(currentProject, button.dataset.result, controller.signal, transferProgress);
        if (disposed) return;
        const url = URL.createObjectURL(saved.file), link = node("a");
        link.href = url; link.download = button.dataset.result.replace(/^result-\d+-[0-9a-f]+-/, "") + ".canonical.json";
        link.click(); setTimeout(() => URL.revokeObjectURL(url), 60000);
        resultMessage("Файл проверен и передан браузеру для скачивания.");
      } catch (error) {
        if (!disposed) resultMessage(error.code === "RESULT_CANCELLED" ? "Проверка отменена." :
          (error.code || "RESULT_ERROR") + ": " + error.message);
      } finally { transfer = null; busy = false; if (!disposed) { get("ResultProgress").hidden = true; render(); } }
    });
    processing.subscribeSelectedResult(value => { selectedResult = value; if (!disposed) renderSelected(); });
    if (!supported) {
      message("Выбор папки проекта недоступен в этом браузере. Черновик можно скачать во вкладке «Шаблоны».");
    } else message("Черновики сохраняются только после явного выбора папки проекта.");
    render();
    return Object.freeze({ dispose: () => { disposed = true; if (transfer) transfer.abort(); events.abort(); panel.remove(); } });
  }
  root.DataTemplatesBrowserProjectView = Object.freeze({ create: create });
}(window));
