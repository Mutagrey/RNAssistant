(function (root) {
  "use strict";
  const encoder = new TextEncoder(), decoder = new TextDecoder("utf-8", { fatal: true });
  const maximumDraftBytes = 1000000, maximumManifestBytes = 256 * 1024;
  const pattern = /^draft-(\d{13})-([0-9a-f]{32})-([\p{L}\p{N}._-]{1,48})\.draft$/u;
  const fail = (code, message) => { throw Object.assign(new Error(message), { code: code }); };
  const missing = error => error && error.name === "NotFoundError";
  async function read(handle, maximum) {
    const file = await handle.getFile();
    if (file.size > maximum) fail("file_too_large", "Файл проекта превышает допустимый размер.");
    try { return decoder.decode(await file.arrayBuffer()); }
    catch (_) { fail("invalid_utf8", "Файл проекта должен быть в UTF-8."); }
  }
  async function manifest(directory) {
    const handle = await directory.getFileHandle("project.json");
    const text = await read(handle, maximumManifestBytes);
    return Object.freeze({ directory: directory, text: text, info: root.DataTemplatesProjectManifest.parse(text) });
  }
  async function create(parent, name) {
    const text = root.DataTemplatesProjectManifest.create(name);
    const slug = name.replace(/[^\p{L}\p{N}._-]+/gu, "_").slice(0, 48) || "project";
    const folder = "data-templates-" + slug + "-" + root.crypto.randomUUID().replace(/-/g, "");
    const exists = await (async () => {
      try { await parent.getDirectoryHandle(folder); return true; }
      catch (error) { if (missing(error)) return false; throw error; }
    })();
    if (exists) fail("target_exists", "Папка проекта уже существует. Выберите другую родительскую папку.");
    try {
      const directory = await parent.getDirectoryHandle(folder, { create: true });
      const handle = await directory.getFileHandle("project.json", { create: true });
      const writer = await handle.createWritable({ keepExistingData: false });
      await writer.write(text); await writer.close();
      if (await read(handle, maximumManifestBytes) !== text) fail("effect_unknown", "Создание проекта требует проверки.");
      return manifest(directory);
    } catch (_) { fail("effect_unknown", "Неясно, создан ли проект. Проверьте новую подпапку перед повторением."); }
  }
  async function unchanged(project) {
    const current = await manifest(project.directory);
    if (current.text !== project.text)
      fail("project_changed", "project.json изменён. Откройте папку проекта заново перед сохранением.");
  }
  async function draftsDirectory(project, create) {
    try { return await project.directory.getDirectoryHandle("drafts", { create: create }); }
    catch (error) { if (!create && missing(error)) return null; throw error; }
  }
  async function saveDraft(project, draft) {
    if (!draft || typeof draft.input !== "string" || !draft.input)
      fail("draft_limit", "Черновик должен содержать от 1 до 1 000 000 байт UTF-8.");
    const bytes = encoder.encode(draft.input);
    if (bytes.byteLength > maximumDraftBytes)
      fail("draft_limit", "Черновик должен содержать от 1 до 1 000 000 байт UTF-8.");
    if (decoder.decode(bytes) !== draft.input)
      fail("draft_encoding", "Черновик содержит некорректные символы Unicode.");
    await unchanged(project);
    const directory = await draftsDirectory(project, true), name = root.DataTemplatesDraftSession.newFileName(draft.name);
    try { await directory.getFileHandle(name); fail("target_exists", "Снимок уже существует."); }
    catch (error) { if (!missing(error)) throw error; }
    try {
      const handle = await directory.getFileHandle(name, { create: true });
      const writer = await handle.createWritable({ keepExistingData: false });
      await writer.write(draft.input); await writer.close();
      if (await read(handle, maximumDraftBytes) !== draft.input)
        fail("effect_unknown", "Сохранённые байты не совпали с черновиком.");
    } catch (_) { fail("effect_unknown", "Неясно, сохранён ли черновик. Обновите список и проверьте файл перед повторением."); }
    return name;
  }
  async function listDrafts(project) {
    await unchanged(project);
    const directory = await draftsDirectory(project, false);
    if (!directory) return [];
    const names = []; let scanned = 0;
    for await (const [name, handle] of directory.entries()) {
      if (++scanned > 1000) fail("draft_list_limit", "В папке слишком много файлов черновиков.");
      if (handle.kind === "file" && pattern.test(name)) names.push(name);
    }
    names.sort().reverse();
    return names.slice(0, 50).map(name => Object.freeze({ file: name, savedAt: Number(pattern.exec(name)[1]),
      label: pattern.exec(name)[3].replace(/_/g, " ") }));
  }
  async function openDraft(project, name) {
    if (typeof name !== "string" || !pattern.test(name)) fail("draft_invalid", "Неизвестный файл черновика.");
    await unchanged(project);
    const directory = await draftsDirectory(project, false);
    if (!directory) fail("draft_missing", "Папка черновиков отсутствует.");
    const input = await read(await directory.getFileHandle(name), maximumDraftBytes);
    if (!input) fail("draft_invalid", "Пустой файл не является черновиком шаблона.");
    return Object.freeze({ name: pattern.exec(name)[3] + ".json", input: input });
  }
  root.DataTemplatesBrowserProjectFiles = Object.freeze({ open: manifest, create: create,
    unchanged: unchanged, saveDraft: saveDraft, listDrafts: listDrafts, openDraft: openDraft });
}(window));
