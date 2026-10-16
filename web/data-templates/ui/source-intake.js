(function (root) {
  "use strict";

  // Selection metadata only. An extension is a format hint, never a parsing or publication receipt.
  const formats = Object.freeze([
    Object.freeze({ id: "xlsx", label: "Excel", extension: ".xlsx", shape: "Листы и диапазоны",
      description: "Сохраняем адреса ячеек, структуру листов и доступность значений формул." }),
    Object.freeze({ id: "csv", label: "CSV", extension: ".csv", shape: "Строки и колонки",
      description: "Явно задаём кодировку, разделитель и строку заголовков. Коды остаются текстом." }),
    Object.freeze({ id: "json", label: "JSON", extension: ".json", shape: "Объекты и массивы",
      description: "Выбираем массив записей и поля. Для значения сохраняем путь внутри JSON." })
  ]);
  const limits = Object.freeze({ files: 500, entries: 2000, depth: 16, milliseconds: 15000 });
  function fail(code, message) { return Object.assign(new Error(message), { code: code }); }
  function formatFor(name) {
    const lower = String(name).toLowerCase();
    return formats.find(format => lower.endsWith(format.extension)) || null;
  }

  function collect(roots, options) {
    options = options || {};
    const signal = options.signal;
    const maximum = Math.min(limits.files, options.maximum === undefined ? limits.files : options.maximum);
    const totalFiles = roots.every(item => item.file || item.entry.isFile) ? roots.length : null;
    return new Promise((resolve, reject) => {
      let active = true, timer, progressTimer, visited = 0, sourceBytes = 0, currentPath = "", reportedAt = -Infinity;
      const result = [];
      function report(force) {
        if (!active || !options.progress) return;
        const now = performance.now();
        if (!force && now - reportedAt < 100) {
          if (!progressTimer) progressTimer = setTimeout(() => {
            progressTimer = null;
            try { report(true); } catch (error) { finish(error); }
          }, 100 - (now - reportedAt));
          return;
        }
        clearTimeout(progressTimer); progressTimer = null;
        reportedAt = now;
        options.progress(Object.freeze({ phase: "enumerating", completedFiles: result.length,
          totalFiles: totalFiles, sourceBytes: sourceBytes, currentPath: currentPath }));
      }
      function finish(error) {
        if (!active) return;
        // Final counters are exact even when intermediate updates were coalesced.
        try { report(true); } catch (observerError) { error = error || observerError; }
        active = false;
        clearTimeout(timer);
        clearTimeout(progressTimer);
        if (signal) signal.removeEventListener("abort", abort);
        if (error) reject(error); else resolve(result);
      }
      function abort() { finish(fail("cancelled", "Добавление отменено. Предыдущий список сохранён.")); }
      function guard() {
        if (!active || signal && signal.aborted) throw fail("cancelled", "Добавление отменено.");
      }
      async function checkpoint() {
        guard();
        if (++visited > limits.entries) throw fail("entry_limit", "В папке слишком много элементов. Выберите меньшую папку.");
        if (visited % 40 === 0) await new Promise(done => setTimeout(done, 0));
        guard();
      }
      function add(file, path) {
        guard();
        if (!file || typeof file.name !== "string" || !Number.isSafeInteger(file.size) || file.size < 0)
          throw fail("file_unavailable", "Один из файлов недоступен. Выберите его ещё раз.");
        if (result.length >= maximum)
          throw fail("file_limit", "В подборке может быть не больше 500 файлов. Добавление не выполнено; выберите меньше файлов.");
        if (path.length > 4096) throw fail("path_limit", "Слишком длинный путь. Выберите вложенную папку отдельно.");
        if (!Number.isSafeInteger(sourceBytes + file.size)) throw fail("size_limit", "Общий объём файлов слишком велик для этой подборки.");
        result.push({ file: file, path: path });
        sourceBytes += file.size;
        currentPath = path;
        report(false);
      }
      async function walk(item, parent, depth) {
        await checkpoint();
        if (depth > limits.depth) throw fail("depth_limit", "Слишком много вложенных папок. Выберите нужную папку отдельно.");
        if (item.file) { add(item.file, item.file.webkitRelativePath || item.file.name); return; }
        const entry = item.entry;
        const path = parent + entry.name;
        currentPath = path;
        report(false);
        if (entry.isFile) {
          const file = await new Promise((done, failed) => entry.file(done, failed));
          add(file, path);
        } else if (entry.isDirectory) {
          const reader = entry.createReader();
          while (true) {
            guard();
            const batch = await new Promise((done, failed) => reader.readEntries(done, failed));
            guard();
            if (!batch.length) break;
            for (const child of batch) await walk({ entry: child }, path + "/", depth + 1);
          }
        } else throw fail("entry_unavailable", "Этот элемент нельзя прочитать как файл или папку.");
      }
      if (signal && signal.aborted) { abort(); return; }
      if (signal) signal.addEventListener("abort", abort, { once: true });
      timer = setTimeout(() => finish(fail("intake_timeout", "Получение списка заняло слишком много времени. Выберите меньшую папку.")), limits.milliseconds);
      (async () => {
        report(true);
        for (const item of roots) await walk(item, "", 0);
        finish();
      })().catch(error => finish(typeof error.code === "string" ? error : fail("read_failed", "Не удалось прочитать состав папки. Список не изменён; попробуйте выбор файлов.")));
    });
  }

  function fromFiles(files, options) {
    if (files.length > limits.files)
      return Promise.reject(fail("file_limit", "Можно добавить до 500 файлов. Выберите меньшую папку или набор файлов."));
    return collect(Array.from(files, file => ({ file: file })), options);
  }
  function fromDrop(transfer, options) {
    // Capture browser handles during the drop event; DataTransfer becomes protected afterwards.
    const roots = [];
    if (transfer.items && transfer.items.length) {
      for (const item of transfer.items) {
        if (item.kind !== "file") continue;
        if (roots.length >= limits.entries) return Promise.reject(fail("entry_limit", "Слишком много элементов. Выберите меньший набор."));
        const entry = item.webkitGetAsEntry ? item.webkitGetAsEntry() : null;
        const file = entry ? null : item.getAsFile();
        if (entry) roots.push({ entry: entry });
        else if (file) roots.push({ file: file });
        else return Promise.reject(fail("drop_unavailable", "Не удалось получить этот источник. Используйте кнопку выбора файла или папки."));
      }
    } else {
      return fromFiles(transfer.files || [], options);
    }
    return collect(roots, options);
  }
  root.DataTemplatesSourceIntake = Object.freeze({ formats: formats, limits: limits,
    formatFor: formatFor, fromFiles: fromFiles, fromDrop: fromDrop });
}(window));
