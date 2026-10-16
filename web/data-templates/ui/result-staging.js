(function (root) {
  "use strict";
  // A provisional, per-file sink. Its bounded in-memory implementation can later be
  // replaced by durable browser storage without changing the queue or the Worker.
  const maximumBytes = 8 * 1024 * 1024, previewLimit = 20;
  const fail = (code, message) => Object.assign(new Error(message), { code: code });
  function create() {
    const parts = [], preview = [];
    let bytes = 0, count = 0, completed = false, closed = false, summary = null;
    const clear = () => { parts.length = 0; preview.length = 0; bytes = 0; count = 0; completed = false; summary = null; closed = true; };
    const sink = Object.freeze({
      writeBatch: async batch => {
        if (closed || completed) throw fail("RESULT_STAGING_CLOSED", "Приёмник результата закрыт.");
        const records = JSON.stringify(batch.records);
        const size = new TextEncoder().encode(records).byteLength;
        if (bytes + size > maximumBytes) throw fail("RESULT_STAGING_LIMIT", "Результат превышает лимит временного экспорта 8 МиБ.");
        parts.push(records.slice(1, -1)); bytes += size; count += batch.records.length;
        if (preview.length < previewLimit) preview.push(...batch.records.slice(0, previewLimit - preview.length));
      },
      complete: async result => {
        if (closed || completed || result.recordCount !== count)
          throw fail("RESULT_STAGING_INVALID", "Количество записей не совпало с результатом выполнения.");
        summary = result; completed = true;
      },
      abort: clear
    });
    function snapshot() {
      if (!completed) throw fail("RESULT_STAGING_INCOMPLETE", "Результат ещё не завершён.");
      return Object.freeze({ summary: summary, preview: Object.freeze(preview.slice()), bytes: bytes });
    }
    function blob(source, templateName) {
      if (!completed) throw fail("RESULT_STAGING_INCOMPLETE", "Экспорт недоступен до завершения обработки.");
      const header = JSON.stringify({ version: 1, kind: "data-templates-extraction", source: source,
        templateFile: templateName, summary: summary });
      return new Blob([header.slice(0, -1), ',"records":[', parts.join(","), "]}"], { type: "application/json" });
    }
    return Object.freeze({ sink: sink, snapshot: snapshot, blob: blob, discard: clear });
  }
  root.DataTemplatesResultStaging = Object.freeze({ create: create, maximumBytes: maximumBytes });
}(window));
