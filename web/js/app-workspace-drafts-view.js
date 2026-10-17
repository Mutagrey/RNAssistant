(function (root) {
  "use strict";
  const node = (tag, className, text) => {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
  };
  function create(container, actions) {
    const section = container.querySelector("#dtViewTemplates");
    section.querySelector(".dt-empty-state").remove();
    section.querySelector(".dt-phase").textContent = "Черновики проекта";
    section.querySelector(".dt-section-heading p").textContent =
      "Исходный JSON хранится отдельными снимками в папке проекта. Проверка и запуск выполняются в автономном приложении.";
    section.insertAdjacentHTML("beforeend", `<div class="dt-process-intro dt-card" id="dtTemplateDrop" role="region" aria-label="Выбор или перетаскивание JSON-шаблона"><div><p class="dt-eyebrow">01 / ИСХОДНЫЙ ШАБЛОН</p><h4>Откройте JSON-шаблон</h4><p>Выберите файл или перетащите его сюда. Правьте исходный JSON в общем редакторе; при сохранении исходный файл не изменится.</p></div><div class="dt-template-picker"><input type="file" id="dtNativeTemplateFile" accept=".json,application/json" hidden><button type="button" id="dtNativeChooseTemplate">Выбрать JSON</button><span id="dtNativeTemplateName">Файл не выбран</span><small>До 1 000 000 байт UTF-8</small></div></div>
      <p id="dtNativeTemplateNotice" class="dt-process-note" role="status" aria-live="polite"></p>
      <section class="dt-project-drafts dt-card dt-native-drafts" aria-label="Черновики проекта"><div class="dt-project-drafts-head"><div><p class="dt-eyebrow">02 / ПРОЕКТ</p><h4 id="dtNativeProjectTitle">Откройте проект</h4><p id="dtNativeProjectHint">После выбора рабочей папки здесь появятся сохранённые черновики.</p></div><div><button type="button" id="dtNativeRefreshDrafts">Обновить</button><button type="button" class="dt-primary" id="dtNativeSaveDraft">Сохранить снимок</button></div></div><p id="dtNativeDraftNotice" role="status" aria-live="polite"></p><ul id="dtProjectDraftList" aria-label="Сохранённые черновики"></ul></section>`);
    const get = id => section.querySelector("#dtNative" + id), events = new AbortController();
    const listen = (element, type, handler) => element.addEventListener(type, handler, { signal: events.signal });
    const editor = root.DataTemplatesTemplateEditor.create(container, {
      changeDraft: actions.changeDraft, ruleDirty: actions.ruleDirty, applyRule: actions.applyRule,
      downloadDraft: actions.downloadDraft, loadCodeMirror: () => root.CodeMirror
        ? Promise.resolve(root.CodeMirror) : Promise.reject(new Error("Локальный CodeMirror недоступен."))
    });
    let state = null;
    listen(get("ChooseTemplate"), "click", () => get("TemplateFile").click());
    listen(get("TemplateFile"), "change", () => {
      actions.chooseFile(get("TemplateFile").files[0] || null); get("TemplateFile").value = "";
    });
    for (const type of ["dragenter", "dragover"]) listen(section.querySelector("#dtTemplateDrop"), type, event => {
      if (!event.dataTransfer || !Array.from(event.dataTransfer.types).includes("Files")) return;
      event.preventDefault(); event.stopPropagation();
      event.dataTransfer.dropEffect = state && state.busy ? "none" : "copy";
      section.querySelector("#dtTemplateDrop").dataset.dragging = "true";
    });
    listen(section.querySelector("#dtTemplateDrop"), "dragleave", event => {
      event.stopPropagation(); section.querySelector("#dtTemplateDrop").dataset.dragging = "false";
    });
    listen(section.querySelector("#dtTemplateDrop"), "drop", event => {
      event.preventDefault(); event.stopPropagation(); section.querySelector("#dtTemplateDrop").dataset.dragging = "false";
      if (state && !state.busy && event.dataTransfer.files.length) actions.chooseFile(event.dataTransfer.files[0]);
    });
    listen(get("RefreshDrafts"), "click", actions.refresh);
    listen(get("SaveDraft"), "click", actions.save);
    listen(section.querySelector("#dtProjectDraftList"), "click", event => {
      const button = event.target.closest("button[data-file]");
      if (button && state && !state.busy) actions.open(button.dataset.file);
    });
    function render(next) {
      state = next;
      const draft = next.draft;
      get("ChooseTemplate").disabled = next.busy;
      get("TemplateName").textContent = draft.draftName || "Файл не выбран";
      get("TemplateNotice").textContent = draft.templateNotice || "";
      get("ProjectTitle").textContent = next.project ? next.project.name : "Откройте проект";
      get("ProjectHint").textContent = next.project
        ? next.project.manifestPath + (next.readOnly ? " · только чтение" : " · снимки в drafts/")
        : draft.draftName ? "Черновик остаётся в памяти. Откройте проект, чтобы сохранить его." :
          "После выбора рабочей папки здесь появятся сохранённые черновики.";
      get("RefreshDrafts").disabled = !next.project || next.busy;
      get("SaveDraft").disabled = !next.canSave;
      get("DraftNotice").textContent = next.notice || "";
      get("DraftNotice").dataset.error = next.error ? "true" : "false";
      const list = section.querySelector("#dtProjectDraftList");
      list.replaceChildren(...next.items.map(item => {
        const row = node("li"), label = node("span", "", item.label + " · " + new Date(item.savedAt).toLocaleString("ru-RU"));
        const button = node("button", "dt-text-button", "Открыть");
        button.type = "button"; button.dataset.file = item.file; button.disabled = next.busy;
        row.append(label, button); return row;
      }));
      if (!next.items.length) list.append(node("li", "dt-project-empty", next.project
        ? "Сохранённых черновиков пока нет." : "Сначала откройте проект в рабочей папке."));
      editor.render({ ...draft, running: next.busy });
    }
    return Object.freeze({ render, openEditor: () => editor.open(), dispose: () => { events.abort(); editor.dispose();
      section.querySelector("#dtTemplateDrop").remove(); get("TemplateNotice").remove();
      section.querySelector(".dt-native-drafts").remove();
    } });
  }
  root.RNAssistantWorkspaceDraftsView = Object.freeze({ create });
}(window));
