(function (root) {
  "use strict";
  // A receipt is written last. Incomplete folders stay visible as recovery evidence.
  const chunkBytes = 1048576, maximumBytes = 300 * chunkBytes, maximumReceiptBytes = 65536;
  const pattern = /^result-(\d{13})-([0-9a-f]{32})-([\p{L}\p{N}._-]{1,48})$/u;
  const decoder = new TextDecoder("utf-8", { fatal: true });
  const fail = (code, message) => { throw Object.assign(new Error(message), { code: code }); };
  const missing = error => error && error.name === "NotFoundError";
  const cancelled = signal => { if (signal && signal.aborted) fail("RESULT_CANCELLED", "Сохранение результата отменено."); };
  const progress = (notify, phase, done, total) => { if (notify) notify(Object.freeze({ phase, done, total })); };
  const hex = buffer => Array.from(new Uint8Array(buffer), byte => byte.toString(16).padStart(2, "0")).join("");
  const digest = buffer => root.crypto.subtle.digest("SHA-256", buffer).then(hex);
  const label = path => Array.from(String(path || "result").split("/").pop()
    .replace(/[^\p{L}\p{N}._-]+/gu, "_").replace(/^[_\-.]+|[_\-.]+$/g, ""))
    .slice(0, 48).join("") || "result";
  async function directory(project, create) {
    try { return await project.directory.getDirectoryHandle("results", { create }); }
    catch (error) { if (!create && missing(error)) return null; throw error; }
  }
  function validReceipt(value, project, name) {
    return value && typeof value === "object" && !Array.isArray(value) &&
      value.kind === "data-templates-result-receipt" && value.version === 1 &&
      value.projectId === project.info.projectId && value.folder === name &&
      Number.isSafeInteger(value.savedAt) && value.savedAt === Number(pattern.exec(name)[1]) &&
      typeof value.sourcePath === "string" && value.sourcePath.length > 0 && value.sourcePath.length <= 4096 &&
      typeof value.templateFile === "string" && value.templateFile.length > 0 && value.templateFile.length <= 4096 &&
      Number.isSafeInteger(value.recordCount) && value.recordCount >= 0 &&
      Number.isSafeInteger(value.bytes) && value.bytes > 0 && value.bytes <= maximumBytes &&
      value.chunkBytes === chunkBytes && Array.isArray(value.chunkHashes) &&
      value.chunkHashes.length === Math.ceil(value.bytes / chunkBytes) &&
      value.chunkHashes.every(hash => typeof hash === "string" && /^[0-9a-f]{64}$/.test(hash));
  }
  async function readReceipt(project, folder, name) {
    const handle = await folder.getFileHandle("receipt.json"), file = await handle.getFile();
    if (file.size > maximumReceiptBytes) fail("RESULT_RECEIPT_INVALID", "Квитанция результата превышает лимит.");
    let value;
    try { value = JSON.parse(decoder.decode(await file.arrayBuffer())); }
    catch (_) { fail("RESULT_RECEIPT_INVALID", "Квитанция результата повреждена."); }
    if (!validReceipt(value, project, name)) fail("RESULT_RECEIPT_INVALID", "Квитанция результата не соответствует проекту.");
    return value;
  }
  async function verify(file, receipt, signal, notify) {
    if (file.size !== receipt.bytes) fail("RESULT_VERIFY_FAILED", "Размер результата изменился.");
    progress(notify, "verify", 0, file.size);
    for (let offset = 0, index = 0; offset < file.size; offset += chunkBytes, index++) {
      cancelled(signal);
      const hash = await digest(await file.slice(offset, offset + chunkBytes).arrayBuffer());
      if (hash !== receipt.chunkHashes[index]) fail("RESULT_VERIFY_FAILED", "Байты результата не прошли проверку.");
      progress(notify, "verify", Math.min(offset + chunkBytes, file.size), file.size);
    }
    cancelled(signal);
  }
  async function save(project, result, signal, notify) {
    if (!result || !result.file || typeof result.file.slice !== "function" ||
        !Number.isSafeInteger(result.file.size) || result.file.size < 1 || result.file.size > maximumBytes ||
        typeof result.sourcePath !== "string" || !result.sourcePath || result.sourcePath.length > 4096 ||
        typeof result.templateFile !== "string" || !result.templateFile || result.templateFile.length > 4096 ||
        !Number.isSafeInteger(result.recordCount) || result.recordCount < 0)
      fail("RESULT_INVALID", "Недопустимый размер или метаданные результата.");
    if (!root.crypto || !root.crypto.subtle) fail("RESULT_UNSUPPORTED", "Браузер не поддерживает проверку SHA-256.");
    cancelled(signal);
    await root.DataTemplatesBrowserProjectFiles.unchanged(project);
    const parent = await directory(project, true);
    const name = "result-" + Date.now() + "-" + root.crypto.randomUUID().replace(/-/g, "") + "-" + label(result.sourcePath);
    try { await parent.getDirectoryHandle(name); fail("target_exists", "Папка результата уже существует."); }
    catch (error) { if (!missing(error)) throw error; }
    cancelled(signal);
    let folder = null, writer = null;
    try {
      folder = await parent.getDirectoryHandle(name, { create: true });
      const handle = await folder.getFileHandle("data.json", { create: true });
      writer = await handle.createWritable({ keepExistingData: false });
      const chunkHashes = [];
      progress(notify, "copy", 0, result.file.size);
      for (let offset = 0; offset < result.file.size; offset += chunkBytes) {
        cancelled(signal);
        const bytes = await result.file.slice(offset, offset + chunkBytes).arrayBuffer();
        chunkHashes.push(await digest(bytes));
        await writer.write(bytes);
        progress(notify, "copy", Math.min(offset + chunkBytes, result.file.size), result.file.size);
      }
      cancelled(signal);
      await writer.close(); writer = null;
      const receipt = { kind: "data-templates-result-receipt", version: 1,
        projectId: project.info.projectId, folder: name, savedAt: Number(pattern.exec(name)[1]),
        sourcePath: result.sourcePath, templateFile: result.templateFile,
        recordCount: result.recordCount, bytes: result.file.size, chunkBytes, chunkHashes };
      await verify(await handle.getFile(), receipt, signal, notify);
      await root.DataTemplatesBrowserProjectFiles.unchanged(project);
      cancelled(signal);
      const receiptText = JSON.stringify(receipt) + "\n";
      if (new TextEncoder().encode(receiptText).byteLength > maximumReceiptBytes)
        fail("RESULT_INVALID", "Квитанция результата превысила лимит.");
      const receiptHandle = await folder.getFileHandle("receipt.json", { create: true });
      writer = await receiptHandle.createWritable({ keepExistingData: false });
      await writer.write(receiptText); await writer.close(); writer = null;
      if (decoder.decode(await (await receiptHandle.getFile()).arrayBuffer()) !== receiptText)
        fail("RESULT_VERIFY_FAILED", "Квитанция результата не прошла проверку чтением.");
      return Object.freeze({ ...receipt });
    } catch (error) {
      if (writer) try { await writer.abort(); } catch (_) { /* Folder removal decides recovery. */ }
      if (folder) try { await parent.removeEntry(name, { recursive: true }); }
      catch (_) { fail("effect_unknown", "Неясно, сохранён ли результат. Проверьте results/" + name + " перед повторением."); }
      throw error;
    }
  }
  async function list(project) {
    await root.DataTemplatesBrowserProjectFiles.unchanged(project);
    const parent = await directory(project, false);
    if (!parent) return Object.freeze({ entries: Object.freeze([]), incomplete: 0, limited: false });
    const names = []; let scanned = 0;
    for await (const [name, handle] of parent.entries()) {
      if (++scanned > 1000) fail("RESULT_LIST_LIMIT", "В папке results/ слишком много элементов.");
      if (handle.kind === "directory" && pattern.test(name)) names.push(name);
    }
    names.sort().reverse();
    const entries = []; let incomplete = 0;
    for (const name of names.slice(0, 100)) {
      try {
        const folder = await parent.getDirectoryHandle(name);
        const receipt = await readReceipt(project, folder, name);
        const file = await (await folder.getFileHandle("data.json")).getFile();
        if (file.size !== receipt.bytes) fail("RESULT_RECEIPT_INVALID", "Размер файла не совпал с квитанцией.");
        if (entries.length < 50) entries.push(Object.freeze({ ...receipt }));
      } catch (error) {
        if (!missing(error) && error.code !== "RESULT_RECEIPT_INVALID") throw error;
        incomplete++;
      }
    }
    return Object.freeze({ entries: Object.freeze(entries), incomplete, limited: names.length > 100 });
  }
  async function open(project, name, signal, notify) {
    if (typeof name !== "string" || !pattern.test(name)) fail("RESULT_INVALID", "Неизвестный результат проекта.");
    await root.DataTemplatesBrowserProjectFiles.unchanged(project);
    const parent = await directory(project, false);
    if (!parent) fail("RESULT_MISSING", "Папка результатов отсутствует.");
    const folder = await parent.getDirectoryHandle(name), receipt = await readReceipt(project, folder, name);
    const file = await (await folder.getFileHandle("data.json")).getFile();
    await verify(file, receipt, signal, notify);
    return Object.freeze({ file, receipt });
  }
  root.DataTemplatesBrowserProjectResults = Object.freeze({ save, list, open });
}(window));
