(function () {
    "use strict";

    const element = id => document.getElementById(id);
    const mode = element("mode"), status = element("status");
    let executor, input = "", sourceFile = null, result = null, cancellation = null, generation = 0, clock = null;
    mode.value = location.protocol === "file:" ? "cooperative" : "worker";
    const xlsx = () => element("operation").value === "extractXlsx";
    const ready = () => Boolean(input && (!xlsx() || sourceFile));

    function clearResult() {
        result = null;
        element("download").disabled = true;
        element("result").textContent = "Нет результата.";
    }

    function reset() {
        generation++;
        clearInterval(clock); clock = null;
        if (cancellation) cancellation.abort();
        cancellation = null;
        if (executor) executor.dispose();
        executor = RNTabularExecutor.create({ mode: mode.value, workerUrl: "worker.js" });
        element("modeNote").textContent = mode.value === "cooperative"
            ? "Работает при открытии index.html двойным кликом. Выполняется только поставляемое ядро с фиксированными лимитами."
            : "Требует доступного Worker. При file:// браузер может запретить запуск; автоматического переключения режима нет.";
        clearResult();
        element("cancel").disabled = true;
        element("run").disabled = !ready();
        element("progressView").hidden = true;
        element("sourcePicker").hidden = !xlsx();
    }

    element("capture").addEventListener("change", async event => {
        input = "";
        reset();
        const ticket = generation, file = event.target.files[0];
        if (!file) { status.textContent = "Выберите входной JSON."; return; }
        if (file.size > 1000000) { status.textContent = "INPUT_LIMIT: файл превышает 1 000 000 байт."; return; }
        try {
            const text = new TextDecoder("utf-8", { fatal: true }).decode(await file.arrayBuffer());
            if (ticket !== generation) return;
            input = text;
            element("run").disabled = !ready();
            status.textContent = "Загружен " + file.name + ". Готов к проверке.";
        } catch (_) {
            if (ticket === generation) status.textContent = "Не удалось прочитать файл как UTF-8.";
        }
    });

    element("sourceFile").addEventListener("change", event => {
        sourceFile = event.target.files[0] || null;
        if (sourceFile && (!sourceFile.size || sourceFile.size > RNTabularEngine.maxSourceBytes)) {
            sourceFile = null; reset(); status.textContent = "XLSX_INPUT_LIMIT: выберите непустой XLSX до 96 МиБ."; return;
        }
        reset(); status.textContent = sourceFile ? "Выбран " + sourceFile.name + ". Содержимое будет прочитано в Worker после запуска." : "Выберите книгу XLSX.";
    });

    element("run").addEventListener("click", async () => {
        if (!ready() || cancellation) return;
        const ticket = generation;
        cancellation = new AbortController();
        clearResult();
        element("run").disabled = true;
        element("cancel").disabled = false;
        const operation = element("operation").value, preview = [];
        const started = performance.now();
        element("progressView").hidden = false;
        element("progress").removeAttribute("value"); element("elapsed").textContent = "0 с";
        clock = setInterval(() => { element("elapsed").textContent = Math.floor((performance.now() - started) / 1000) + " с"; }, 500);
        status.textContent = operation === "inspect" ? "Проверка снимка…" : "Выполнение шаблона…";
        try {
            const settings = { signal: cancellation.signal,
                sink: {
                    writeBatch: async batch => { if (ticket === generation && !cancellation.signal.aborted) preview.push(...batch.records.slice(0, Math.max(0, 20 - preview.length))); },
                    complete: async () => {},
                    abort: () => { preview.length = 0; }
                },
                onProgress: progress => {
                    if (ticket !== generation) return;
                    let completed, total;
                    if (progress.phase === "reading-xlsx") {
                        completed = progress.partBytes; total = progress.totalPartBytes;
                        status.textContent = "Чтение XLSX · " + progress.part + " · распаковано " + progress.expandedBytes.toLocaleString("ru-RU") + " байт.";
                    } else if (progress.phase === "reading-records") {
                        completed = progress.completedItems; total = progress.totalItems;
                        status.textContent = "Прочитано " + completed + " из " + total + " исходных записей.";
                    } else {
                        completed = operation === "inspect" ? progress.completedCells : progress.completedRecords;
                        total = operation === "inspect" ? progress.totalCells : progress.totalRecords;
                        status.textContent = operation === "inspect" ? "Прочитано " + completed + " из " + total + " ячеек."
                            : "Подготовлено " + completed + " из " + total + " записей.";
                    }
                    if (total > 0) { element("progress").max = total; element("progress").value = completed; }
                    else element("progress").removeAttribute("value");
                }
            };
            const output = operation === "extractXlsx" ? await executor.extractXlsx(input, sourceFile, settings) : await executor[operation](input, settings);
            if (ticket !== generation) return;
            result = operation === "inspect" ? output : { ...output, preview: { records: preview, limit: 20, complete: preview.length === output.recordCount } };
            element("result").textContent = JSON.stringify(operation === "inspect" ? { ...output, cells: output.cells.slice(0, 20) } : result, null, 2);
            element("download").disabled = false;
            status.textContent = operation === "inspect" ? "Проверено " + output.cells.length + " ячеек. Это результат проверки, без публикации проекта."
                : "Получено " + output.recordCount + " записей. Отчёт содержит до 20 примеров; dataset не опубликован.";
        } catch (error) {
            if (ticket === generation) status.textContent = (error.code || "EXECUTION_FAILED") +
                (error.path ? " [" + error.path + "]" : "") + ": " + error.message;
        } finally {
            if (ticket === generation) {
                clearInterval(clock); clock = null; element("progressView").hidden = true;
                cancellation = null;
                element("run").disabled = !ready();
                element("cancel").disabled = true;
            }
        }
    });

    element("cancel").addEventListener("click", () => { if (cancellation) cancellation.abort(); });
    element("reset").addEventListener("click", () => { reset(); status.textContent = "Исполнитель перезапущен."; });
    mode.addEventListener("change", () => { reset(); status.textContent = "Режим изменён."; });
    element("operation").addEventListener("change", () => {
        input = ""; element("capture").value = "";
        mode.querySelector('[value="cooperative"]').disabled = xlsx();
        if (xlsx()) mode.value = "worker";
        reset(); status.textContent = "Выберите JSON для этой операции.";
    });
    element("download").addEventListener("click", () => {
        if (!result) return;
        const url = URL.createObjectURL(new Blob([JSON.stringify(result, null, 2)], { type: "application/json" }));
        const link = document.createElement("a");
        link.href = url; link.download = "tabular-probe-report.json";
        link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    });
    window.addEventListener("pagehide", () => { generation++; clearInterval(clock); executor.dispose(); });
    reset();
}());
