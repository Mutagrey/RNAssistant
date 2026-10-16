(function () {
    "use strict";

    const element = id => document.getElementById(id);
    const mode = element("mode"), status = element("status");
    let executor, input = "", result = null, cancellation = null, generation = 0;
    mode.value = location.protocol === "file:" ? "cooperative" : "worker";

    function clearResult() {
        result = null;
        element("download").disabled = true;
        element("result").textContent = "Нет результата.";
    }

    function reset() {
        generation++;
        if (cancellation) cancellation.abort();
        cancellation = null;
        if (executor) executor.dispose();
        executor = RNTabularExecutor.create({ mode: mode.value, workerUrl: "worker.js" });
        element("modeNote").textContent = mode.value === "cooperative"
            ? "Работает при открытии index.html двойным кликом. Выполняется только поставляемое ядро с фиксированными лимитами."
            : "Требует доступного Worker. При file:// браузер может запретить запуск; автоматического переключения режима нет.";
        clearResult();
        element("cancel").disabled = true;
        element("run").disabled = !input;
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
            element("run").disabled = !input;
            status.textContent = "Загружен " + file.name + ". Готов к проверке.";
        } catch (_) {
            if (ticket === generation) status.textContent = "Не удалось прочитать файл как UTF-8.";
        }
    });

    element("run").addEventListener("click", async () => {
        if (!input || cancellation) return;
        const ticket = generation;
        cancellation = new AbortController();
        clearResult();
        element("run").disabled = true;
        element("cancel").disabled = false;
        status.textContent = "Проверка снимка…";
        try {
            const output = await executor.inspect(input, { signal: cancellation.signal,
                onProgress: progress => {
                    if (ticket === generation) status.textContent = "Прочитано " + progress.completedCells + " из " + progress.totalCells + " ячеек.";
                }
            });
            if (ticket !== generation) return;
            result = output;
            element("result").textContent = JSON.stringify({ ...output, cells: output.cells.slice(0, 20) }, null, 2);
            element("download").disabled = false;
            status.textContent = "Проверено " + output.cells.length + " ячеек. Это результат проверки, без публикации проекта.";
        } catch (error) {
            if (ticket === generation) status.textContent = (error.code || "EXECUTION_FAILED") +
                (error.path ? " [" + error.path + "]" : "") + ": " + error.message;
        } finally {
            if (ticket === generation) {
                cancellation = null;
                element("run").disabled = !input;
                element("cancel").disabled = true;
            }
        }
    });

    element("cancel").addEventListener("click", () => { if (cancellation) cancellation.abort(); });
    element("reset").addEventListener("click", () => { reset(); status.textContent = "Исполнитель перезапущен."; });
    mode.addEventListener("change", () => { reset(); status.textContent = "Режим изменён."; });
    element("download").addEventListener("click", () => {
        if (!result) return;
        const url = URL.createObjectURL(new Blob([JSON.stringify(result, null, 2)], { type: "application/json" }));
        const link = document.createElement("a");
        link.href = url; link.download = "tabular-probe-result.json";
        link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    });
    window.addEventListener("pagehide", () => { generation++; executor.dispose(); });
    reset();
}());
