(function (root) {
  "use strict";
  let view = null, context = null, pending = false, busy = false, acceptingOpen = false;
  let items = [], notice = "", error = false, key = "", epoch = 0, needsRefresh = false,
    disposed = false, lastSourceBusy = null;
  const session = root.DataTemplatesDraftSession.create({
    onChange: render, busy: () => disposed || busy && !acceptingOpen
  });
  const identity = state => state && state.workspace && state.project
    ? state.workspace.bindingId + "\n" + state.project.manifestPath : "";
  function render() {
    if (!view) return;
    const project = context && context.project, workspace = context && context.workspace;
    const sourceBusy = !!(root.RNAssistantWorkspaceSources && root.RNAssistantWorkspaceSources.hasUnfinished());
    lastSourceBusy = sourceBusy;
    view.render({ draft: session.snapshot(), project, readOnly: !!(workspace && workspace.readOnly),
      items, notice, error, busy: busy || pending,
      canSave: !!project && !!workspace && !workspace.readOnly && !busy && !pending &&
        !sourceBusy && !!session.current() });
  }
  function sourcesChanged() {
    if (view && !!root.RNAssistantWorkspaceSources.hasUnfinished() !== lastSourceBusy) render();
  }
  function setContext(state, blocked) {
    const next = identity(state);
    if (next !== key) {
      key = next; epoch++; items = []; needsRefresh = !!next;
      notice = next ? "Читаем черновики выбранного проекта…" : ""; error = false;
    }
    context = state; pending = !!blocked;
    render();
    if (needsRefresh && !pending && !busy && view) {
      needsRefresh = false;
      Promise.resolve().then(() => { if (!disposed && view && !busy && identity(context) === key) refresh(); });
    }
  }
  function message(value, failed) { notice = value; error = !!failed; render(); }
  async function refresh() {
    if (!context || !context.project || busy || pending || disposed) return;
    const current = epoch;
    busy = true; message("Обновляем список черновиков…", false);
    try {
      const response = await root.RNAssistantWorkspace.projectDraft("list");
      if (current === epoch) { items = response.items; message(items.length ? "Снимки проекта обновлены." : "Снимков пока нет.", false); }
    } catch (failure) {
      if (current === epoch || !context || !context.project)
        message((failure.code || "DRAFT_ERROR") + ": " + failure.message, true);
    } finally { busy = false; render(); }
  }
  async function save() {
    if (!context || !context.project || !context.workspace || context.workspace.readOnly || busy || pending || disposed ||
        root.RNAssistantWorkspaceSources.hasUnfinished()) return;
    const draft = session.current();
    if (!draft) { message("Сначала выберите или откройте черновик.", true); return; }
    const current = epoch, file = root.DataTemplatesDraftSession.newFileName(draft.name);
    const manifest = context.project.manifestPath;
    const target = manifest.slice(0, manifest.lastIndexOf("/") + 1) + "drafts/" + file;
    busy = true; message("Сохраняем отдельный снимок в drafts/…", false);
    try {
      const response = await root.RNAssistantWorkspace.projectDraft("save", { file, input: draft.input });
      if (current === epoch) {
        items = [response.draft, ...items.filter(item => item.file !== file)].slice(0, 50);
        session.markSaved(draft.input);
        message("Снимок сохранён и проверен чтением: " + file, false);
      }
    } catch (failure) {
      if (current === epoch || !context || !context.project)
        message(!failure.code || ["effect_unknown", "bridge_response_timeout", "bridge_transport_failed", "workspace_response_invalid"].includes(failure.code)
          ? "Запись не подтверждена. Проверьте файл " + target + " кнопкой «Проверить состояние файла». Повторное сохранение не запускалось."
          : (failure.code || "DRAFT_ERROR") + ": " + failure.message, true);
    } finally { busy = false; render(); }
  }
  async function open(file) {
    if (!context || !context.project || busy || pending || disposed) return;
    const current = epoch;
    busy = true; message("Читаем сохранённый снимок…", false);
    try {
      const response = await root.RNAssistantWorkspace.projectDraft("open", { file });
      if (current !== epoch) return;
      acceptingOpen = true;
      try {
        const loaded = await session.load(response.draft.label + ".json", response.input);
        if (loaded) {
          message("Снимок открыт в редакторе. Правила здесь не проверяются.", false);
          if (view) view.openEditor();
        }
        else message("Текущий черновик оставлен без изменений.", false);
      } finally { acceptingOpen = false; }
    } catch (failure) {
      if (current === epoch || !context || !context.project)
        message((failure.code || "DRAFT_ERROR") + ": " + failure.message, true);
    } finally { busy = false; render(); }
  }
  function bind(container) {
    if (view || disposed) return;
    view = root.RNAssistantWorkspaceDraftsView.create(container, {
      chooseFile: session.chooseFile, changeDraft: session.changeDraft,
      ruleDirty: session.ruleDirty, applyRule: session.applyRule,
      downloadDraft: session.download, refresh, save, open
    });
    render();
    if (needsRefresh && !pending && !busy) { needsRefresh = false; Promise.resolve().then(refresh); }
  }
  function unbind() { if (view) { view.dispose(); view = null; } }
  function dispose() { disposed = true; unbind(); session.dispose(); }
  root.RNAssistantWorkspaceDrafts = Object.freeze({ bind, unbind, setContext, sourcesChanged, dispose });
}(window));
