(function (root) {
  "use strict";
  function inspect(input, signal) {
    return new Promise((resolve, reject) => {
      if (signal.aborted) { reject(Object.assign(new Error("Проверка отменена."), { code: "EXECUTION_CANCELLED" })); return; }
      let worker;
      try { worker = new Worker("core/template-inspection-worker.js"); }
      catch (error) { reject(Object.assign(new Error("Не удалось запустить Worker для проверки шаблона: " + error.message),
        { code: "TEMPLATE_WORKER_UNAVAILABLE" })); return; }
      let settled = false;
      const finish = (error, result) => {
        if (settled) return;
        settled = true; clearTimeout(timer); signal.removeEventListener("abort", cancel); worker.terminate();
        if (error) reject(error); else resolve(result);
      };
      const cancel = () => finish(Object.assign(new Error("Проверка отменена."), { code: "EXECUTION_CANCELLED" }));
      const timer = setTimeout(() => finish(Object.assign(new Error("Проверка шаблона превысила 10 секунд."),
        { code: "TEMPLATE_TIMEOUT" })), 10000);
      signal.addEventListener("abort", cancel, { once: true });
      worker.onerror = event => finish(Object.assign(new Error(event.message || "Worker проверки недоступен."),
        { code: "TEMPLATE_WORKER_UNAVAILABLE" }));
      worker.onmessageerror = () => finish(Object.assign(new Error("Некорректный ответ Worker."),
        { code: "TEMPLATE_WORKER_INVALID" }));
      worker.onmessage = event => {
        const message = event.data;
        if (message && message.type === "result" && message.inspection) finish(null, message.inspection);
        else if (message && message.type === "error" && message.error)
          finish(Object.assign(new Error(message.error.message), { code: message.error.code, path: message.error.path }));
        else finish(Object.assign(new Error("Некорректный ответ Worker."), { code: "TEMPLATE_WORKER_INVALID" }));
      };
      if (signal.aborted) cancel(); else worker.postMessage(input);
    });
  }
  root.DataTemplatesTemplateInspection = Object.freeze({ inspect: inspect });
}(window));
