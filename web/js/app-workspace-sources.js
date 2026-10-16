(function (root) {
  "use strict";
  // Application projections and transport lifetime. Views never own queue work.
  let view = null, context = null, key = "", lastState = null, epoch = 0, contextBusy = false;
  let chosen = new Map(), files = null, browsing = false, browseTicket = 0;
  let queueId = null, queue = null, rows = null, busy = false, uncertain = false;
  let reading = false, readAgain = false, timer = null, queryTicket = 0;
  let filters = { offset: 0, query: "", state: null, sort: "Added" }, notice = "", error = false;
  let publishedPresence = false;
  const natural = value => Number.isSafeInteger(value) && value >= 0;
  const path = value => typeof value === "string" && value.length > 0 && value.length <= 4096 && !/[\x00-\x1f]/.test(value);
  const states = ["Pending", "Running", "Prepared", "Failed", "Cancelled"];
  const stages = ["Hashing", "Compressing", "Retaining", "Verifying"];
  function invalid() { return new Error("Ответ приложения не удалось проверить. Обновите состояние очереди."); }
  function unfinished() { return busy || uncertain || !!queueId && (!queue || queue.state !== "Finished"); }
  function hasSources() { return chosen.size > 0 || !!queue; }
  function render() {
    if (view) view.render({ context: context, contextBusy: contextBusy, chosen: Array.from(chosen.keys()),
      files: files, browsing: browsing, queue: queue, rows: rows, filters: filters,
      busy: busy, unfinished: unfinished(), uncertain: uncertain, notice: notice, error: error });
    if (root.RNAssistantWorkspace && root.RNAssistantWorkspace.sourceWorkChanged) root.RNAssistantWorkspace.sourceWorkChanged();
    if (publishedPresence !== hasSources()) {
      publishedPresence = hasSources();
      if (root.RNAssistantDataTemplates) root.RNAssistantDataTemplates.workspaceSourcesChanged();
    }
  }
  function message(text, failed) { notice = text; error = !!failed; }
  function validItem(item) {
    return item && path(item.id) && path(item.path) && states.includes(item.state) &&
      (item.byteLength === null || natural(item.byteLength)) &&
      [null, "WaitingForReader", "CheckingProject", "Capturing"].includes(item.phase) &&
      (!item.progress || stages.includes(item.progress.stage) && natural(item.progress.completedBytes) &&
        natural(item.progress.totalBytes) && item.progress.completedBytes <= item.progress.totalBytes);
  }
  function accept(response, page) {
    if (!response || response.type !== "rnassistant.workspaceSources" || response.contractVersion !== 1 ||
      response.bindingId !== context.workspace.bindingId || response.projectRevision !== context.revision ||
      response.manifestPath !== context.project.manifestPath || !path(response.queueId) || queueId && response.queueId !== queueId ||
      !["Running", "Pausing", "Paused", "Finished"].includes(response.state) ||
      !["revision", "total", "pending", "running", "prepared", "failed", "cancelled", "capturedBytes", "maximumFileBytes", "maximumBatchBytes", "matched", "offset"].every(name => natural(response[name])) ||
      response.total < 1 || response.total > 500 || response.maximumFileBytes < 1 || response.maximumBatchBytes < response.maximumFileBytes ||
      response.running > 1 || response.pending + response.running + response.prepared + response.failed + response.cancelled !== response.total ||
      response.matched > response.total || response.offset > response.matched || response.capturedBytes > response.maximumBatchBytes ||
      !Array.isArray(response.items) || response.items.length > 50 || !response.items.every(validItem) ||
      response.items.length !== Math.min(50, response.matched - response.offset) ||
      new Set(response.items.map(item => item.id)).size !== response.items.length ||
      (response.running === 1 ? !validItem(response.currentItem) || response.currentItem.state !== "Running" : response.currentItem !== null) ||
      response.state === "Finished" && (response.pending || response.running)) throw invalid();
    if (queue && response.revision < queue.revision) return;
    queueId = response.queueId; queue = response; uncertain = false;
    if (page) { rows = response; filters = { ...filters, offset: response.offset }; }
  }
  function schedule() {
    clearTimeout(timer);
    if (queueId && !uncertain && queue && ["Running", "Pausing"].includes(queue.state)) timer = setTimeout(readQueue, 500);
  }
  async function readQueue() {
    clearTimeout(timer);
    if (!queueId || !context || !context.project) return;
    if (reading) { readAgain = true; return; }
    reading = true; readAgain = false;
    const acceptedEpoch = epoch, ticket = queryTicket, id = queueId;
    try {
      const response = await send("getWorkspaceSourceQueue", { queueId: id, limit: 50, ...filters });
      if (acceptedEpoch !== epoch || id !== queueId) return;
      accept(response, ticket === queryTicket);
      if (!uncertain) message("", false);
    } catch (failure) {
      if (acceptedEpoch === epoch) { uncertain = true; message(failure.message || "Не удалось обновить очередь.", true); }
    } finally {
      reading = false;
      if (acceptedEpoch === epoch) { render(); if (readAgain && !uncertain) readQueue(); else schedule(); }
      else if (queueId) readQueue();
    }
  }
  async function browse(directory, query, offset) {
    if (!context || !context.workspace || contextBusy) return;
    const ticket = ++browseTicket, acceptedEpoch = epoch;
    browsing = true; render();
    try {
      const response = await send("browseWorkspaceFiles", { bindingId: context.workspace.bindingId,
        directory: directory || "", query: query || "", offset: offset || 0 });
      if (ticket !== browseTicket || acceptedEpoch !== epoch) return;
      if (!response || response.type !== "rnassistant.workspaceFiles" || response.contractVersion !== 1 ||
        response.bindingId !== context.workspace.bindingId || response.directory !== (directory || "") ||
        !natural(response.offset) || !natural(response.matched) || response.matched > 5000 || typeof response.scanLimitReached !== "boolean" ||
        !Array.isArray(response.items) || response.items.length > 50 || !response.items.every(item => item && path(item.name) && path(item.path) &&
          item.path === (directory ? directory + "/" : "") + item.name && typeof item.isDirectory === "boolean" &&
          (item.isDirectory ? item.byteLength === null : natural(item.byteLength)))) throw invalid();
      files = { ...response, query: query || "" }; message("", false);
    } catch (failure) {
      if (ticket === browseTicket && acceptedEpoch === epoch) { files = null; message(failure.message || "Не удалось прочитать папку.", true); }
    } finally { if (ticket === browseTicket) { browsing = false; render(); } }
  }
  async function prepare(fileMiB, batchMiB) {
    if (unfinished() || contextBusy || !context || !context.project || !chosen.size) return;
    if (!Number.isInteger(fileMiB) || fileMiB < 1 || fileMiB > 4096 || !Number.isInteger(batchMiB) || batchMiB < fileMiB || batchMiB > 65536) {
      message("Лимит файла: 1–4096 МБ. Лимит подборки: от лимита файла до 65536 МБ.", true); render(); return;
    }
    const acceptedEpoch = epoch;
    busy = true; message("Запускаем подготовку файлов…", false); render();
    try {
      const response = await send("prepareWorkspaceSources", { bindingId: context.workspace.bindingId, projectRevision: context.revision,
        paths: Array.from(chosen.keys()), maximumFileBytes: fileMiB * 1048576, maximumBatchBytes: batchMiB * 1048576 });
      if (acceptedEpoch !== epoch) return;
      if (!response || response.maximumFileBytes !== fileMiB * 1048576 || response.maximumBatchBytes !== batchMiB * 1048576) throw invalid();
      queueId = null; queue = rows = null; filters = { offset: 0, query: "", state: null, sort: "Added" };
      accept(response, true); message("", false);
      if (view) view.prepared();
    } catch (failure) {
      if (acceptedEpoch === epoch) { uncertain = true; message("Запуск не подтверждён. Обновите состояние перед следующим действием. " + (failure.message || ""), true); }
    } finally {
      if (acceptedEpoch === epoch) { busy = false; render(); schedule(); }
    }
  }
  async function control(command, itemIds) {
    if (!queueId || busy || uncertain || contextBusy) return;
    const acceptedEpoch = epoch;
    busy = true; render();
    try {
      const response = await send("controlWorkspaceSourceQueue", { queueId: queueId, command: command, itemIds: itemIds || null });
      if (acceptedEpoch !== epoch) return;
      accept(response, false); message("", false); readAgain = true;
    } catch (failure) {
      if (acceptedEpoch === epoch) { uncertain = true; message(failure.message || "Действие не подтверждено. Обновите состояние.", true); }
    } finally {
      if (acceptedEpoch === epoch) { busy = false; render(); if (!uncertain) readQueue(); }
    }
  }
  function setContext(state, blocked) {
    const nextKey = state && state.workspace ? JSON.stringify([state.workspace.bindingId, state.revision, state.project && state.project.manifestPath]) : "";
    if (nextKey !== key) {
      epoch++; key = nextKey; chosen = new Map(); files = queue = rows = null; queueId = null;
      busy = uncertain = false; browseTicket++; browsing = false; clearTimeout(timer);
      filters = { offset: 0, query: "", state: null, sort: "Added" }; message("", false);
      if (view) view.reset();
    }
    context = state; contextBusy = !!blocked;
    if (state !== lastState && !blocked) {
      lastState = state;
      const reported = state && state.sourceQueueId;
      if (reported && state.project) {
        if (queueId !== reported) queue = rows = null;
        queueId = reported; readQueue();
      }
      else if (!busy) { queueId = null; queue = rows = null; uncertain = false; message("", false); }
    }
    render();
  }
  function bind(container) {
    if (view) return;
    view = root.RNAssistantWorkspaceSourcesView.create(container, {
      browse: browse, prepare: prepare, control: control,
      refresh: () => root.RNAssistantWorkspace.show(),
      choose: (paths, include) => {
        if (unfinished() || contextBusy || !files) return;
        const candidates = files.items.filter(item => !item.isDirectory && item.byteLength > 0 && paths.includes(item.path));
        if (include && new Set([...chosen.keys(), ...candidates.map(item => item.path)]).size > 500) {
          message("В одной подборке допускается до 500 файлов.", true); render(); return;
        }
        candidates.forEach(item => { if (include) chosen.set(item.path, true); else chosen.delete(item.path); }); render();
      },
      clear: () => { if (!unfinished()) { chosen.clear(); render(); } },
      filter: value => { filters = { ...filters, ...value }; queryTicket++; readQueue(); }
    });
    render();
  }
  function unbind() { if (view) view.dispose(); view = null; }
  root.RNAssistantWorkspaceSources = Object.freeze({ bind: bind, unbind: unbind, setContext: setContext, hasUnfinished: unfinished, hasSources: hasSources });
}(window));
