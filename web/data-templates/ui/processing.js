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
    let view, template = null, draft = null, draftDirty = false, draftGeneration = 0,
      rulePending = false, mappingPending = false,
      templateLoading = false, templateTicket = 0, templateAbort = null, validationTimer = null, run = null,
      queue = [], selectedId = null, lastActivity = null, templateNotice = "", runNotice = "", disposed = false;
    const count = status => queue.filter(item => item.status === status).length;
    function snapshot() {
      const sources = loader.selectedSources(), active = run && queue.find(item => item.status === "running");
      const completed = count("completed"), failed = count("failed"), cancelled = count("cancelled");
      return Object.freeze({ templateName: template && template.name, templateInspection: template && template.inspection,
        draftName: draft && draft.name, draftText: draft && draft.input, draftDirty: draftDirty,
        draftGeneration: draftGeneration,
        rulePending: rulePending, mappingPending: mappingPending, templateLoading: templateLoading,
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
      if ((draftDirty || rulePending || mappingPending) &&
        !root.confirm("Текущий черновик изменён. Заменить его выбранным файлом?")) return;
      const ticket = ++templateTicket;
      draftGeneration++;
      clearTimeout(validationTimer);
      if (templateAbort) templateAbort.abort();
      templateAbort = file ? new AbortController() : null;
      template = null; draft = null; draftDirty = false; rulePending = false; mappingPending = false;
      templateLoading = !!file; templateNotice = ""; render();
      if (!file) return;
      try {
        if (!file.size || file.size > 1000000) throw new Error("Нужен непустой JSON-шаблон до 1 000 000 байт.");
        const input = new TextDecoder("utf-8", { fatal: true }).decode(await file.arrayBuffer());
        if (ticket !== templateTicket || disposed) return;
        draft = Object.freeze({ name: file.name, input: input }); render();
        const inspection = await root.DataTemplatesTemplateInspection.inspect(input, templateAbort.signal);
        if (ticket !== templateTicket || disposed) return;
        template = Object.freeze({ name: file.name, input: input, inspection: inspection });
        templateNotice = "Структура шаблона проверена. Данные источника и значения маппинга проверяются при обработке каждого файла.";
      } catch (error) {
        if (ticket === templateTicket && !disposed) templateNotice = "Шаблон «" + file.name + "» не принят: " + errorText(error);
      } finally { if (ticket === templateTicket) { templateLoading = false; render(); } }
    }
    function changeDraft(input) {
      if (!draft || run || mappingPending || disposed) return;
      draft = Object.freeze({ name: draft.name, input: input }); draftDirty = true; template = null;
      const ticket = ++templateTicket;
      clearTimeout(validationTimer); if (templateAbort) templateAbort.abort();
      templateAbort = new AbortController(); templateLoading = true;
      templateNotice = "Проверяем изменённый JSON…"; render();
      if (!input || input.length > 1000000) {
        templateLoading = false; templateNotice = "INPUT_LIMIT: JSON-черновик должен содержать от 1 до 1 000 000 символов.";
        render(); return;
      }
      const validationAbort = templateAbort;
      validationTimer = setTimeout(async () => {
        try {
          const inspection = await root.DataTemplatesTemplateInspection.inspect(input, validationAbort.signal);
          if (ticket !== templateTicket || disposed) return;
          template = Object.freeze({ name: draft.name, input: input, inspection: inspection });
          templateNotice = "Черновик проверен. Значения источника будут проверены при обработке.";
        } catch (error) {
          if (ticket === templateTicket && !disposed) templateNotice = "Черновик требует исправления: " + errorText(error);
        } finally { if (ticket === templateTicket) { templateLoading = false; render(); } }
      }, 300);
    }
    function ruleDirty(value) { if (rulePending !== value) { rulePending = value; render(); } }
    function mappingDirty(value) { if (mappingPending !== value) { mappingPending = value; render(); } }
    async function commitCandidate(candidate, successNotice, kind) {
      if (candidate.length > 1000000) return { ok: false, message: "JSON-черновик превысит 1 000 000 символов." };
      const ticket = ++templateTicket;
      if (templateAbort) templateAbort.abort();
      templateAbort = new AbortController(); templateLoading = true;
      templateNotice = "Проверяем изменённый шаблон…"; render();
      const validationAbort = templateAbort;
      try {
        const nextInspection = await root.DataTemplatesTemplateInspection.inspect(candidate, validationAbort.signal);
        if (ticket !== templateTicket || disposed) return { ok: false, message: "Проверка прервана." };
        draft = Object.freeze({ name: draft.name, input: candidate }); draftDirty = true;
        if (kind === "rule") rulePending = false; else mappingPending = false;
        template = Object.freeze({ name: draft.name, input: candidate, inspection: nextInspection });
        templateNotice = successNotice;
        return { ok: true };
      } catch (error) {
        if (ticket === templateTicket && !disposed) templateNotice = "Правка не принята: " + errorText(error);
        return { ok: false, message: "Правка не принята: " + errorText(error) };
      } finally { if (ticket === templateTicket) { templateLoading = false; render(); } }
    }
    async function applyRule(path, expression) {
      if (!draft || !template || run || mappingPending || disposed)
        return { ok: false, message: "Сначала закончите текущую правку JSON или маппинга." };
      let candidate;
      try { candidate = root.RNTabularTemplateDraft.replaceExpression(draft.input, template.inspection, path, expression); }
      catch (error) { return { ok: false, message: errorText(error) }; }
      return commitCandidate(candidate, "Правило применено; полный черновик проверен.", "rule");
    }
    async function applyMapping(edit) {
      if (!draft || !template || run || rulePending || disposed)
        return { ok: false, message: "Сначала закончите текущую правку JSON или правила." };
      let candidate;
      try { candidate = root.RNTabularTemplateDraft.upsertMapping(draft.input, template.inspection, edit); }
      catch (error) { return { ok: false, message: errorText(error) }; }
      return commitCandidate(candidate, "Соответствие применено; полный черновик проверен.", "mapping");
    }
    function downloadDraft() {
      if (!draft || rulePending || mappingPending || disposed) return;
      const file = new Blob([draft.input], { type: "application/json;charset=utf-8" });
      const url = URL.createObjectURL(file), link = document.createElement("a");
      link.href = url;
      link.download = (draft.name.replace(/\.json$/i, "").replace(/[^\p{L}\p{N}._-]+/gu, "_").slice(0, 80) || "template") + ".draft.json";
      link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
    function cancel() { if (run) run.abort.abort(); }
    async function start() {
      if (run || templateLoading || !template || rulePending || mappingPending || disposed) return;
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
      select: id => { selectedId = id; render(); }, download: download,
      changeDraft: changeDraft, ruleDirty: ruleDirty, applyRule: applyRule, mappingDirty: mappingDirty,
      applyMapping: applyMapping, downloadDraft: downloadDraft
    });
    render();
    return Object.freeze({ sourcesChanged: render, dispose: () => {
      disposed = true; templateTicket++; clearTimeout(validationTimer); if (run) { run.abort.abort(); clearInterval(run.timer); }
      if (templateAbort) templateAbort.abort();
      releaseResults().catch(() => {}); if (view) view.dispose(); loader.endProcessing();
    } });
  }
  root.DataTemplatesProcessing = Object.freeze({ create: create });
}(window));
