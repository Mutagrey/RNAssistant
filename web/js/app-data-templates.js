(function () {
  "use strict";
  const intake = window.RNAssistantSourceIntake;
  let view = null, sources = [], nextId = 0, job = null, activity = null;
  let contextKey = "", contextBusy = false, notice = { text: "", error: false };

  // The owner retains File handles and operation lifetime; views receive metadata only.
  function activitySnapshot() {
    if (!activity) return null;
    return Object.freeze({ state: activity.state, progress: activity.progress,
      elapsedMs: activity.elapsedMs === null ? performance.now() - activity.started : activity.elapsedMs });
  }
  function renderActivity() { if (view) view.renderActivity(activitySnapshot()); }
  function forgetActivity() {
    if (activity) clearInterval(activity.timer);
    activity = null; renderActivity();
  }
  function message(text, error) {
    notice = Object.freeze({ text: text, error: !!error });
    if (view) view.renderNotice(notice);
  }
  function signature(source) { return JSON.stringify([source.path, source.file.size, source.file.lastModified]); }
  function eligible(source) { return !!source.format && source.file.size > 0; }
  function render() {
    if (!view) return;
    const counts = new Map();
    sources.forEach(source => counts.set(signature(source), (counts.get(signature(source)) || 0) + 1));
    view.render(Object.freeze({ busy: !!job || contextBusy, notice: notice, activity: activitySnapshot(),
      hasWorkspaceSources: !!window.RNAssistantWorkspaceSources && window.RNAssistantWorkspaceSources.hasSources(),
      sources: Object.freeze(sources.map(source => Object.freeze({ id: source.id, name: source.file.name,
        path: source.path, size: source.file.size, format: source.format, included: source.included,
        available: eligible(source), duplicate: counts.get(signature(source)) > 1 }))) }));
  }
  async function add(start) {
    if (job || contextBusy) return;
    if (view) view.showSources();
    forgetActivity();
    const operation = { abort: new AbortController(), context: contextKey, started: performance.now(),
      elapsedMs: null, state: "running", progress: Object.freeze({ phase: "enumerating", completedFiles: 0, totalFiles: null, sourceBytes: 0, currentPath: "" }) };
    job = activity = operation;
    operation.timer = setInterval(renderActivity, 500);
    message("Получаем список файлов…", false); render();
    try {
      // Invoked synchronously in the browser event so drop handles are captured before await.
      const result = await start({ signal: operation.abort.signal, maximum: intake.limits.files - sources.length,
        progress: progress => { if (job === operation) { operation.progress = progress; renderActivity(); } } });
      if (job !== operation || contextKey !== operation.context) return;
      result.forEach(item => {
        const format = intake.formatFor(item.file.name);
        sources.push({ id: "source-" + (++nextId), file: item.file, path: item.path,
          format: format, included: !!format && item.file.size > 0 });
      });
      const unsupported = result.filter(item => !intake.formatFor(item.file.name)).length;
      operation.state = "completed";
      message(result.length ? "Добавлено файлов: " + result.length + (unsupported ? ". Неподдерживаемых: " + unsupported + ". Они не включены в подборку." : ". Состав подборки можно изменить ниже.")
        : "Файлов не найдено. Выберите непустую папку или файлы.", false);
    } catch (error) {
      if (job === operation) {
        operation.state = error.code === "cancelled" ? "cancelled" : "failed";
        message(error.message || "Не удалось добавить файлы. Список не изменён.", error.code !== "cancelled");
      }
    } finally {
      clearInterval(operation.timer);
      operation.elapsedMs = performance.now() - operation.started;
      if (job === operation) { job = null; render(); }
    }
  }
  function setContext(state, busy) {
    if (window.RNAssistantWorkspaceSources) window.RNAssistantWorkspaceSources.setContext(state, busy);
    const workspace = state && state.workspace, project = state && state.project;
    const key = workspace ? JSON.stringify([workspace.bindingId, project ? project.manifestPath : null]) : "";
    if (key !== contextKey) {
      const hadSources = sources.length || job;
      if (job) job.abort.abort();
      forgetActivity();
      job = null; sources = []; contextKey = key;
      if (view) view.resetSelection();
      if (hadSources) message("Контекст проекта изменился. Выберите источники для текущего проекта.", false);
    }
    contextBusy = busy;
    render();
  }
  function bind(container) {
    if (view) return;
    container = container || document.querySelector("#tab-workspace .workspace-projects");
    if (!container) return;
    view = window.RNAssistantDataTemplatesView.create(container, intake.formats, Object.freeze({
      files: files => add(options => intake.fromFiles(files, options)),
      drop: transfer => add(options => intake.fromDrop(transfer, options)),
      cancel: () => { if (job) job.abort.abort(); },
      dismiss: forgetActivity,
      include: (ids, included) => {
        if (job || contextBusy) return;
        const selected = new Set(ids);
        sources.forEach(source => { if (selected.has(source.id) && eligible(source)) source.included = included; });
        render();
      },
      remove: id => { if (!job && !contextBusy) { sources = sources.filter(source => source.id !== id); render(); } },
      clear: () => {
        if (job || contextBusy) return;
        forgetActivity(); sources = []; view.resetSelection();
        message("Список очищен. Исходные файлы не изменены.", false); render();
      }
    }));
    if (window.RNAssistantWorkspaceSources) window.RNAssistantWorkspaceSources.bind(container);
    render();
  }
  // Detaching presentation never cancels accepted work or loses the temporary selection.
  function unbind() {
    if (view) view.dispose(); view = null;
    if (window.RNAssistantWorkspaceSources) window.RNAssistantWorkspaceSources.unbind();
  }
  window.RNAssistantDataTemplates = Object.freeze({ bind: bind, unbind: unbind, setContext: setContext, workspaceSourcesChanged: render });
}());
