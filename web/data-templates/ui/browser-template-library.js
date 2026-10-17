(function (root) {
  "use strict";
  // The catalog is derived from immutable project files; project.json stays format 2.
  const encoder = new TextEncoder(), decoder = new TextDecoder("utf-8", { fatal: true });
  const limit = 1000000, idPattern = /^[A-Za-z][A-Za-z0-9_.-]{0,127}$/;
  const filePattern = /^template-(.+)-(\d{13})-([0-9a-f]{32})\.(json|removed)$/;
  const fail = (code, message) => { throw Object.assign(new Error(message), { code }); };
  const missing = error => error && error.name === "NotFoundError";
  async function folder(project, create) {
    try { return await project.directory.getDirectoryHandle("templates", { create }); }
    catch (error) { if (!create && missing(error)) return null; throw error; }
  }
  function parse(name) {
    const match = filePattern.exec(name);
    return match && idPattern.test(match[1]) ? { file: name, id: match[1], savedAt: Number(match[2]),
      archived: match[4] === "removed" } : null;
  }
  async function scan(project, create) {
    await root.DataTemplatesBrowserProjectFiles.unchanged(project);
    const directory = await folder(project, create);
    if (!directory) return { directory: null, entries: [] };
    const groups = new Map(); let count = 0;
    for await (const [name, handle] of directory.entries()) {
      if (++count > 1000) fail("TEMPLATE_LIBRARY_LIMIT", "В templates/ больше 1000 элементов.");
      const event = handle.kind === "file" && parse(name);
      if (!event) continue;
      const group = groups.get(event.id) || [];
      group.push(event); groups.set(event.id, group);
    }
    const entries = Array.from(groups, ([id, events]) => {
      events.sort((a, b) => a.file.localeCompare(b.file));
      const head = events[events.length - 1];
      const content = events.filter(item => !item.archived).at(-1);
      return Object.freeze({ id, head: head.file, savedAt: head.savedAt, archived: head.archived,
        contentFile: content && content.file, versions: events.filter(item => !item.archived).length });
    });
    entries.sort((a, b) => b.savedAt - a.savedAt || a.id.localeCompare(b.id));
    return { directory, entries: Object.freeze(entries) };
  }
  async function list(project) { return (await scan(project, false)).entries; }
  async function read(project, file, expectedId) {
    const parsed = parse(file);
    if (!parsed || parsed.archived || parsed.id !== expectedId) fail("TEMPLATE_INVALID", "Неизвестный файл шаблона.");
    await root.DataTemplatesBrowserProjectFiles.unchanged(project);
    const directory = await folder(project, false);
    if (!directory) fail("TEMPLATE_MISSING", "Папка шаблонов отсутствует.");
    const body = await (await directory.getFileHandle(file)).getFile();
    if (!body.size || body.size > limit) fail("TEMPLATE_LIMIT", "Шаблон превышает лимит 1 000 000 байт.");
    try { return decoder.decode(await body.arrayBuffer()); }
    catch (_) { fail("TEMPLATE_ENCODING", "Шаблон должен быть в UTF-8."); }
  }
  async function open(project, entry) {
    const current = (await list(project)).find(item => item.id === entry.id);
    if (!current || current.head !== entry.head || current.archived || !current.contentFile)
      fail("TEMPLATE_CHANGED", "Шаблон изменён. Обновите библиотеку перед открытием.");
    return read(project, current.contentFile, current.id);
  }
  async function append(project, id, text, expectedHead, archived, signal) {
    if (!idPattern.test(id)) fail("TEMPLATE_INVALID", "Нужен допустимый устойчивый код шаблона.");
    const bytes = encoder.encode(text);
    if (!bytes.length || bytes.length > limit || decoder.decode(bytes) !== text)
      fail("TEMPLATE_LIMIT", "Нужен UTF-8 файл до 1 000 000 байт.");
    if (!archived) {
      let value;
      try { value = JSON.parse(text); } catch (_) { fail("TEMPLATE_INVALID", "Некорректный JSON шаблона."); }
      if (!value.template || value.template.id !== id)
        fail("TEMPLATE_INVALID", "Код в JSON не совпадает с кодом библиотечного шаблона.");
      const inspection = await root.DataTemplatesTemplateInspection.inspect(text, signal || new AbortController().signal);
      if (!inspection || !inspection.template || inspection.template.id !== id)
        fail("TEMPLATE_INVALID", "Проверенный код шаблона не совпадает с файлом.");
    }
    const before = await scan(project, true);
    const current = before.entries.find(item => item.id === id);
    if ((current && current.head || null) !== expectedHead)
      fail("TEMPLATE_CHANGED", "Шаблон изменился. Обновите библиотеку; текущий черновик сохраните отдельно.");
    if (archived && (!current || current.archived)) fail("TEMPLATE_INVALID", "Активный шаблон не найден.");
    const timestamp = Math.max(Date.now(), current ? current.savedAt + 1 : 0);
    const name = "template-" + id + "-" + timestamp + "-" + root.crypto.randomUUID().replace(/-/g, "") +
      (archived ? ".removed" : ".json");
    try { await before.directory.getFileHandle(name); fail("target_exists", "Версия шаблона уже существует."); }
    catch (error) { if (!missing(error)) throw error; }
    try {
      const handle = await before.directory.getFileHandle(name, { create: true });
      const writer = await handle.createWritable({ keepExistingData: false });
      await writer.write(text); await writer.close();
      const stored = await handle.getFile();
      if (stored.size !== bytes.length || decoder.decode(await stored.arrayBuffer()) !== text)
        fail("effect_unknown", "Байты версии не прошли проверку чтением.");
      const after = await scan(project, false);
      const head = after.entries.find(item => item.id === id);
      if (!head || head.head !== name)
        fail("effect_unknown", "Конкурирующая версия изменила библиотеку.");
      return head;
    } catch (error) {
      fail("effect_unknown", "Проверьте точный файл templates/" + name + " перед повторением: " + error.message);
    }
  }
  async function save(project, id, text, expectedHead, signal) { return append(project, id, text, expectedHead, false, signal); }
  async function archive(project, entry) {
    return append(project, entry.id, JSON.stringify({ projectId: project.info.projectId, id: entry.id,
      previous: entry.head, removedAt: Date.now() }) + "\n", entry.head, true);
  }
  async function restore(project, entry) {
    const current = (await list(project)).find(item => item.id === entry.id);
    if (!current || current.head !== entry.head || !current.archived || !current.contentFile)
      fail("TEMPLATE_CHANGED", "Архивная версия изменилась. Обновите библиотеку.");
    return read(project, current.contentFile, current.id);
  }
  root.DataTemplatesBrowserTemplateLibrary = Object.freeze({ list, open, save, archive, restore });
}(window));
