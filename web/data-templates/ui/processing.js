(function (root) {
  "use strict";
  const sourceLimit = 96 * 1024 * 1024;
  const errorText = error => (error.code || "EXECUTION_FAILED") + (error.path ? " [" + error.path + "]" : "") + ": " + error.message;
  function progressText(progress) {
    if (!progress) return "Ожидание Worker";
    if (progress.phase === "reading-xlsx") return "Чтение " + (progress.part || "XLSX") +
      (progress.totalPartBytes ? " · " + Math.floor(progress.partBytes / progress.totalPartBytes * 100) + "% части" : "");
    if (progress.phase === "reading-records") return "Исходные записи: " + progress.completedItems + " из " + progress.totalItems;
    if (progress.phase === "staging-results") return "Сохранено " + progress.records + " записей · " +
      Math.ceil(progress.bytes / 1048576) + " МиБ результата";
    if (progress.phase === "verifying-result") return "Проверка сохранённого результата";
    if (progress.totalRecords) return "Записи: " + progress.completedRecords + " из " + progress.totalRecords;
    return "Обработка в Worker";
  }
  function create(container, loader) {
    let view, template = null, templateLoading = false, templateTicket = 0, run = null,
      queue = [], selectedId = null, lastActivity = null, templateNotice = "", runNotice = "", disposed = false;
    const count = status => queue.filter(item => item.status === status).length;
    function snapshot() {
      const sources = loader.selectedSources(), active = run && queue.find(item => item.status === "running");
      const completed = count("completed"), failed = count("failed"), cancelled = count("cancelled");
      return Object.freeze({ templateName: template && template.name, templateLoading: templateLoading,
        selectedCount: sources.length, selectedXlsx: sources.filter(source => source.format === "xlsx").length,
        sourceBusy: loader.isBusy(), running: !!run, templateNotice: templateNotice, runNotice: runNotice,
        storageMode: root.DataTemplatesResultStaging.mode(),
        queue: Object.freeze(queue.map(item => Object.freeze({ id: item.id, path: item.path, status: item.status,
          error: item.error, recordCount: item.result ? item.result.summary.recordCount : null, progress: progressText(item.progress) }))),
        selectedId: selectedId, selected: (() => {
          const item = queue.find(value => value.id === selectedId);
          return item && item.result ? Object.freeze({ path: item.path, status: item.status,
            summary: item.result.summary, preview: item.result.preview, bytes: item.result.bytes, mode: item.result.mode })
            : item ? Object.freeze({ path: item.path, status: item.status, error: item.error,
              progress: progressText(item.progress) }) : null;
        })(),
        totals: Object.freeze({ total: queue.length, completed: completed, failed: failed, cancelled: cancelled,
          records: queue.reduce((sum, item) => sum + (item.result ? item.result.summary.recordCount : 0), 0) }),
        activity: run ? Object.freeze({ state: "running", stage: "Обработка XLSX",
          title: "Файлы выполняются по очереди", currentItem: active ? active.path : "Подготовка ядра",
          elapsedMs: performance.now() - run.started,
          measure: { completed: completed + failed + cancelled, total: queue.length, label: "Файлов завершено" },
          detail: active ? progressText(active.progress) : "Ожидание Worker",
          note: "Один Worker и один файл одновременно. Отмена прерывает текущий файл и оставшуюся очередь." }) : lastActivity });
    }
    function render() { if (view && !disposed) view.render(snapshot()); }
    function releaseResults() {
      const previous = queue; queue = []; selectedId = null;
      return Promise.all(previous.filter(item => item.store).map(item => item.store.discard()));
    }
    async function chooseTemplate(file) {
      if (run || disposed) return;
      const ticket = ++templateTicket;
      template = null; templateLoading = !!file; templateNotice = ""; render();
      if (!file) return;
      try {
        if (!file.size || file.size > 1000000) throw new Error("Нужен непустой JSON-шаблон до 1 000 000 байт.");
        const input = new TextDecoder("utf-8", { fatal: true }).decode(await file.arrayBuffer());
        const parsed = JSON.parse(input);
        if (parsed.version !== 2 || parsed.kind !== "xlsx-extraction")
          throw new Error("Для текущего чтения нужен шаблон xlsx-extraction версии 2.");
        if (ticket !== templateTicket || disposed) return;
        template = Object.freeze({ name: file.name, input: input });
        templateNotice = "Шаблон загружен. Проверка правил выполняется при запуске на каждом файле.";
      } catch (error) {
        if (ticket === templateTicket && !disposed) templateNotice = "Шаблон не загружен: " + error.message;
      } finally { if (ticket === templateTicket) { templateLoading = false; render(); } }
    }
    function cancel() { if (run) run.abort.abort(); }
    async function start() {
      if (run || templateLoading || !template || disposed) return;
      const selection = loader.beginProcessing();
      if (!selection) { templateNotice = "Дождитесь окончания подбора источников."; render(); return; }
      if (!selection.length) { loader.endProcessing(); templateNotice = "Выберите хотя бы один источник."; render(); return; }
      const cleanup = releaseResults(); lastActivity = null; runNotice = "";
      const runningTemplate = template;
      queue = selection.map(source => ({ id: source.id, file: source.file, path: source.path, format: source.format,
        templateName: runningTemplate.name, status: "queued", error: "", progress: null, result: null, store: null }));
      selectedId = queue[0].id;
      const operation = { abort: new AbortController(), started: performance.now(), timer: null };
      run = operation; operation.timer = setInterval(render, 500); render(); view.showResults();
      let executor = null;
      try {
        await cleanup;
        if (operation.abort.signal.aborted) return;
        // Loading the fixed core also respects cancellation before the first Worker starts.
        if (queue.some(item => item.format === "xlsx" && item.file.size <= sourceLimit)) {
          const executorFactory = await root.DataTemplatesBrowserCore.load();
          if (operation.abort.signal.aborted) return;
          if (root.RNTabularEngine.maxSourceBytes !== sourceLimit)
            throw new Error("Лимит XLSX не совпал с поставляемым ядром.");
          executor = executorFactory.create({ mode: "worker", workerUrl: "core/worker.js" });
        }
        for (const item of queue) {
          if (operation.abort.signal.aborted) break;
          if (item.format !== "xlsx") {
            item.status = "failed"; item.error = "READER_UNAVAILABLE: чтение " + item.format.toUpperCase() + " пока не подключено."; render(); continue;
          }
          if (!item.file.size || item.file.size > sourceLimit) {
            item.status = "failed"; item.error = "XLSX_INPUT_LIMIT: нужен непустой XLSX до 96 МиБ."; render(); continue;
          }
          item.status = "running"; render();
          try {
            const retained = queue.reduce((sum, entry) => sum + (entry.result ? entry.result.bytes : 0), 0);
            const remainingBytes = root.DataTemplatesResultStaging.totalMaximumBytes - retained;
            if (remainingBytes < 1) throw Object.assign(new Error("Общий бюджет результатов 1 ГиБ исчерпан."), { code: "RESULT_STAGING_LIMIT" });
            item.store = await root.DataTemplatesResultStaging.create({
              source: { name: item.file.name, path: item.path, size: item.file.size },
              templateName: item.templateName, remainingBytes: remainingBytes, signal: operation.abort.signal
            });
            if (operation.abort.signal.aborted) throw Object.assign(new Error("Обработка отменена."), { code: "EXECUTION_CANCELLED" });
            const summary = await executor.extractXlsx(runningTemplate.input, item.file, {
              signal: operation.abort.signal,
              sink: {
                writeBatch: async batch => {
                  const receipt = await item.store.sink.writeBatch(batch);
                  if (run === operation && !operation.abort.signal.aborted) {
                    item.progress = { phase: "staging-results", records: receipt.records, bytes: receipt.bytes }; render();
                  }
                },
                complete: item.store.sink.complete,
                abort: item.store.sink.abort
              },
              onProgress: progress => { if (run === operation) { item.progress = progress; render(); } }
            });
            if (operation.abort.signal.aborted) throw Object.assign(new Error("Обработка отменена."), { code: "EXECUTION_CANCELLED" });
            const staged = item.store.snapshot();
            if (staged.summary !== summary) throw new Error("Результат приёмника не совпал с исполнителем.");
            item.progress = { phase: "verifying-result" }; render();
            await item.store.file(); // Read back size/availability before showing a successful result.
            if (operation.abort.signal.aborted) throw Object.assign(new Error("Обработка отменена."), { code: "EXECUTION_CANCELLED" });
            item.result = staged;
            item.status = "completed";
          } catch (error) {
            let cleanupError = null;
            if (item.store) try { await item.store.discard(); } catch (failure) { cleanupError = failure; }
            item.result = null;
            item.status = cleanupError ? "failed" : operation.abort.signal.aborted ? "cancelled" : "failed";
            item.error = cleanupError ? errorText(cleanupError) : operation.abort.signal.aborted ? "Обработка отменена." : errorText(error);
            if (operation.abort.signal.aborted) break;
          }
          render();
        }
      } catch (error) {
        if (!operation.abort.signal.aborted)
          queue.filter(item => item.status === "queued").forEach(item => { item.status = "failed"; item.error = errorText(error); });
      } finally {
        if (executor) executor.dispose();
        clearInterval(operation.timer);
        if (operation.abort.signal.aborted) queue.filter(item => item.status === "queued" || item.status === "running")
          .forEach(item => { item.status = "cancelled"; item.error = "Обработка отменена."; });
        const completed = count("completed"), failed = count("failed"), cancelled = count("cancelled");
        lastActivity = Object.freeze({ state: cancelled ? "cancelled" : failed ? "failed" : "completed",
          stage: "Обработка XLSX", title: cancelled ? "Очередь остановлена" : failed ? "Завершено с ошибками" : "Обработка завершена",
          currentItem: "", elapsedMs: performance.now() - operation.started,
          measure: { completed: completed + failed + cancelled, total: queue.length, label: "Файлов завершено" },
          detail: "Готово: " + completed + " · Ошибки: " + failed + " · Отменено: " + cancelled,
          note: "" });
        runNotice = cancelled ? "Очередь остановлена. Незавершённые результаты отброшены; уже готовые файлы доступны для просмотра и экспорта."
          : completed ? "Доступен просмотр и полный JSON-экспорт каждого успешного файла. Результаты временные и не опубликованы."
            : "Готовых результатов нет. Проверьте ошибки в очереди.";
        run = null; loader.endProcessing(); render();
      }
    }
    async function download(id) {
      const item = queue.find(value => value.id === id);
      if (!item || item.status !== "completed" || !item.store) return;
      try {
        const file = await item.store.file();
        if (disposed || !queue.includes(item) || item.status !== "completed") return;
        const url = URL.createObjectURL(file), link = document.createElement("a");
        link.href = url; link.download = (item.file.name.replace(/[^\p{L}\p{N}._-]+/gu, "_").slice(0, 80) || "result") + ".canonical.json";
        link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
      } catch (error) {
        if (disposed || !queue.includes(item) || item.status !== "completed") return;
        item.result = null; item.status = "failed"; item.error = errorText(error);
        try { await item.store.discard(); } catch (failure) { item.error = errorText(failure); }
        lastActivity = null;
        runNotice = "Не удалось подготовить полный экспорт. Результат этого файла помечен ошибкой."; render();
      }
    }
    view = root.DataTemplatesProcessingView.create(container, {
      chooseTemplate: chooseTemplate, start: start, cancel: cancel, dismiss: () => { lastActivity = null; render(); },
      select: id => { selectedId = id; render(); }, download: download
    });
    render();
    return Object.freeze({ sourcesChanged: render, dispose: () => {
      disposed = true; templateTicket++; if (run) { run.abort.abort(); clearInterval(run.timer); }
      releaseResults().catch(() => {}); if (view) view.dispose(); loader.endProcessing();
    } });
  }
  root.DataTemplatesProcessing = Object.freeze({ create: create });
}(window));
