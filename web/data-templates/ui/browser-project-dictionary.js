(function (root) {
  "use strict";
  const encoder = new TextEncoder(), decoder = new TextDecoder("utf-8", { fatal: true });
  const pattern = /^dictionary-(\d{13})-([0-9a-f]{32})\.json$/;
  const fail = (code, message) => { throw Object.assign(new Error(message), { code }); };
  const missing = error => error && error.name === "NotFoundError";
  async function folder(project, create) {
    try { return await project.directory.getDirectoryHandle("dictionaries", { create }); }
    catch (error) { if (!create && missing(error)) return null; throw error; }
  }
  async function scan(project, create) {
    await root.DataTemplatesBrowserProjectFiles.unchanged(project);
    const directory = await folder(project, create);
    if (!directory) return { directory: null, head: null };
    let head = null, count = 0;
    for await (const [name, handle] of directory.entries()) {
      if (++count > 1000) fail("DICTIONARY_LIMIT", "В dictionaries/ больше 1000 элементов.");
      if (handle.kind === "file" && pattern.test(name) && (!head || name > head)) head = name;
    }
    return { directory, head };
  }
  async function current(project) {
    const found = await scan(project, false);
    if (!found.head) return null;
    const body = await (await found.directory.getFileHandle(found.head)).getFile();
    if (!body.size || body.size > 1000000) { if (body.close) await body.close(); fail("DICTIONARY_LIMIT", "Версия справочника превышает 1 000 000 байт."); }
    let text, dictionary;
    try { text = decoder.decode(await body.arrayBuffer()); dictionary = JSON.parse(text); }
    catch (_) { fail("DICTIONARY_INVALID", "Версия общего справочника повреждена."); }
    finally { if (body.close) await body.close(); }
    await root.DataTemplatesTemplateInspection.inspectDictionary(text, new AbortController().signal);
    await root.DataTemplatesBrowserProjectFiles.unchanged(project);
    return Object.freeze({ head: found.head, dictionary, text });
  }
  async function save(project, dictionary, expectedHead, signal, expectedText) {
    const text = JSON.stringify(dictionary, null, 2) + "\n", bytes = encoder.encode(text);
    if (bytes.length > 1000000) fail("DICTIONARY_LIMIT", "Общий справочник превышает 1 000 000 байт.");
    await root.DataTemplatesTemplateInspection.inspectDictionary(text, signal || new AbortController().signal);
    const before = await current(project);
    if ((before && before.head || null) !== expectedHead ||
        before && before.text !== expectedText)
      fail("DICTIONARY_CHANGED", "Общий справочник изменился. Откройте проект заново перед правкой.");
    if (dictionary.revision !== (before ? before.dictionary.revision + 1 : 1))
      fail("DICTIONARY_REVISION", "Новая версия общего справочника должна увеличивать revision на один.");
    const directory = await folder(project, true);
    const timestamp = Math.max(Date.now(), before ? Number(pattern.exec(before.head)[1]) + 1 : 0);
    const name = "dictionary-" + timestamp + "-" + root.crypto.randomUUID().replace(/-/g, "") + ".json";
    try { await directory.getFileHandle(name); fail("target_exists", "Версия справочника уже существует."); }
    catch (error) { if (!missing(error)) throw error; }
    try {
      const handle = await directory.getFileHandle(name, { create: true });
      const writer = await handle.createWritable({ keepExistingData: false });
      await writer.write(text); await writer.close();
      const stored = await handle.getFile();
      try { if (stored.size !== bytes.length || decoder.decode(await stored.arrayBuffer()) !== text)
        fail("effect_unknown", "Байты справочника не прошли проверку чтением."); }
      finally { if (stored.close) await stored.close(); }
      const after = await current(project);
      if (!after || after.head !== name || after.text !== text)
        fail("effect_unknown", "Конкурирующая версия изменила общий справочник.");
      return after;
    } catch (error) {
      fail("effect_unknown", "Проверьте точный файл dictionaries/" + name + " перед повторением: " + error.message);
    }
  }
  root.DataTemplatesBrowserProjectDictionary = Object.freeze({ current, save });
}(window));
