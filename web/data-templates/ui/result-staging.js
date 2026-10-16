(function (root) {
  "use strict";
  // Provisional output is acknowledged only after its chunk has been accepted.
  // The browser project format and publication remain separate from this sink.
  const diskMaximumBytes = 256 * 1048576, memoryMaximumBytes = 8 * 1048576;
  const totalMaximumBytes = 1024 * 1048576, previewLimit = 20, folderName = "data-templates-provisional-v1";
  const encoder = new TextEncoder();
  const fail = (code, message) => Object.assign(new Error(message), { code: code });
  let directoryPromise = null;
  function mode() {
    return root.navigator.storage && typeof root.navigator.storage.getDirectory === "function" ? "disk" : "memory";
  }
  async function directory() {
    if (!directoryPromise) directoryPromise = (async () => {
      const origin = await root.navigator.storage.getDirectory();
      const folder = await origin.getDirectoryHandle(folderName, { create: true });
      // A crash can leave provisional files. Never prune a recent file another tab may own.
      for await (const [name] of folder.entries()) {
        const match = /^dt-(\d{13})-[0-9a-f-]+\.json$/.exec(name);
        if (match && Date.now() - Number(match[1]) > 24 * 60 * 60 * 1000) {
          try { await folder.removeEntry(name); } catch (_) { /* Quota errors surface on the next write. */ }
        }
      }
      return folder;
    })();
    return directoryPromise;
  }
  async function diskAdapter(prefix) {
    const folder = await directory(), name = "dt-" + Date.now() + "-" + root.crypto.randomUUID() + ".json";
    const handle = await folder.getFileHandle(name, { create: true });
    let writer = null, sealed = false;
    try {
      writer = await handle.createWritable({ keepExistingData: false });
      await writer.write(prefix);
    } catch (error) {
      if (writer) try { await writer.abort(); } catch (_) { /* Removal is authoritative. */ }
      try { await folder.removeEntry(name); } catch (_) {
        throw fail("RESULT_CLEANUP_FAILED", "Не удалось удалить незавершённый результат.");
      }
      throw error;
    }
    return Object.freeze({ mode: "disk",
      write: text => writer.write(text),
      finish: async text => { await writer.write(text); await writer.close(); sealed = true; },
      file: async () => { if (!sealed) throw fail("RESULT_STAGING_INCOMPLETE", "Файл результата не завершён."); return handle.getFile(); },
      discard: async () => {
        if (!sealed) try { await writer.abort(); } catch (_) { /* Removal is authoritative. */ }
        try { await folder.removeEntry(name); } catch (error) {
          if (error.name !== "NotFoundError") throw fail("RESULT_CLEANUP_FAILED", "Не удалось удалить временный результат.");
        }
      }
    });
  }
  function memoryAdapter(prefix) {
    const parts = [prefix]; let sealed = false;
    return Object.freeze({ mode: "memory",
      write: async text => { parts.push(text); },
      finish: async text => { parts.push(text); sealed = true; },
      file: async () => { if (!sealed) throw fail("RESULT_STAGING_INCOMPLETE", "Файл результата не завершён.");
        return new Blob(parts, { type: "application/json" }); },
      discard: async () => { parts.length = 0; sealed = false; }
    });
  }
  async function create(options) {
    if (!options || !options.source || typeof options.templateName !== "string" ||
        !Number.isSafeInteger(options.remainingBytes) || options.remainingBytes < 1)
      throw fail("RESULT_STAGING_INVALID", "Недопустимый бюджет или источник результата.");
    if (options.signal && options.signal.aborted) throw fail("EXECUTION_CANCELLED", "Обработка отменена.");
    const prefix = JSON.stringify({ version: 1, kind: "data-templates-extraction", source: options.source,
      templateFile: options.templateName }).slice(0, -1) + ',"records":[';
    let adapter;
    try { adapter = mode() === "disk" ? await diskAdapter(prefix) : memoryAdapter(prefix); }
    catch (error) { throw error.code ? error : fail("RESULT_STORAGE_UNAVAILABLE", "Хранилище браузера недоступно: " + error.message); }
    if (options.signal && options.signal.aborted) {
      await adapter.discard(); throw fail("EXECUTION_CANCELLED", "Обработка отменена.");
    }
    const maximumBytes = Math.min(adapter.mode === "disk" ? diskMaximumBytes : memoryMaximumBytes, options.remainingBytes);
    const preview = [];
    let bytes = 0, count = 0, summary = null, suffixBytes = 0, completed = false, finishing = false, revoked = false;
    let tail = Promise.resolve(), cleanup = null;
    const closed = () => fail("RESULT_STAGING_CLOSED", "Приёмник результата закрыт.");
    const sink = Object.freeze({
      writeBatch: batch => {
        if (revoked || finishing || completed) return Promise.reject(closed());
        const records = JSON.stringify(batch.records).slice(1, -1);
        tail = tail.then(async () => {
          if (revoked) throw closed();
          const text = (count ? "," : "") + records, size = encoder.encode(text).byteLength;
          if (bytes + size > maximumBytes)
            throw fail("RESULT_STAGING_LIMIT", "Превышен бюджет результата: " + Math.floor(maximumBytes / 1048576) + " МиБ.");
          await adapter.write(text);
          if (revoked) throw closed();
          bytes += size; count += batch.records.length;
          if (preview.length < previewLimit) preview.push(...batch.records.slice(0, previewLimit - preview.length));
          return Object.freeze({ records: count, bytes: bytes });
        });
        return tail;
      },
      complete: result => {
        if (revoked || finishing || completed) return Promise.reject(closed());
        finishing = true;
        tail = tail.then(async () => {
          if (revoked) throw closed();
          if (!result || result.recordCount !== count)
            throw fail("RESULT_STAGING_INVALID", "Количество записей не совпало с результатом выполнения.");
          const suffix = '],"summary":' + JSON.stringify(result) + "}";
          await adapter.finish(suffix);
          if (revoked) throw closed();
          suffixBytes = encoder.encode(suffix).byteLength;
          summary = result; completed = true;
        });
        return tail;
      },
      abort: () => { revoked = true; completed = false; summary = null; preview.length = 0; }
    });
    function snapshot() {
      if (!completed || revoked) throw fail("RESULT_STAGING_INCOMPLETE", "Результат ещё не завершён.");
      return Object.freeze({ summary: summary, preview: Object.freeze(preview.slice()), bytes: bytes, mode: adapter.mode });
    }
    async function file() {
      if (!completed || revoked) throw fail("RESULT_STAGING_INCOMPLETE", "Экспорт недоступен до завершения обработки.");
      const output = await adapter.file();
      if (revoked || output.size !== encoder.encode(prefix).byteLength + bytes + suffixBytes)
        throw fail("RESULT_STAGING_INVALID", "Размер результата не совпал с подтверждёнными порциями.");
      return output;
    }
    function discard() {
      if (!cleanup) {
        sink.abort();
        cleanup = tail.catch(() => {}).then(() => adapter.discard());
      }
      return cleanup;
    }
    return Object.freeze({ sink: sink, snapshot: snapshot, file: file, discard: discard });
  }
  root.DataTemplatesResultStaging = Object.freeze({ create: create, mode: mode,
    diskMaximumBytes: diskMaximumBytes, memoryMaximumBytes: memoryMaximumBytes, totalMaximumBytes: totalMaximumBytes });
}(window));
