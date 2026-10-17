(function (root) {
  "use strict";
  const encoder = new TextEncoder(), decoder = new TextDecoder("utf-8", { fatal: true });
  const maximumBytes = 1000000;
  const describe = error => (error.code || "TEMPLATE_INVALID") + (error.path ? " [" + error.path + "]" : "") + ": " + error.message;
  function validate(input) {
    if (typeof input !== "string" || !input) throw Object.assign(new Error("Нужен непустой JSON-черновик до 1 000 000 байт."), { code: "DRAFT_LIMIT" });
    const bytes = encoder.encode(input);
    if (bytes.length > maximumBytes) throw Object.assign(new Error("JSON-черновик превысил 1 000 000 байт."), { code: "DRAFT_LIMIT" });
    if (decoder.decode(bytes) !== input) throw Object.assign(new Error("Некорректные символы Unicode."), { code: "DRAFT_ENCODING" });
    return bytes.length;
  }
  function newFileName(name) {
    const slug = String(name || "template").replace(/\.json$/i, "").replace(/[^\p{L}\p{N}._-]+/gu, "_")
      .replace(/^[_\-.]+|[_\-.]+$/g, "");
    const bounded = Array.from(slug).slice(0, 48).join("") || "template";
    return "draft-" + Date.now() + "-" + root.crypto.randomUUID().replace(/-/g, "") + "-" + bounded + ".draft";
  }
  function create(options = {}) {
    let template = null, draft = null, dirty = false, generation = 0, rulePending = false,
      mappingPending = false, dictionaryPending = false, loading = false, ticket = 0, abort = null, timer = null,
      notice = "", withinLimit = false, disposed = false;
    const changed = () => { if (options.onChange) options.onChange(); };
    const busy = () => disposed || !!(options.busy && options.busy());
    const inspect = options.inspect;
    function snapshot() {
      return Object.freeze({ templateName: template && template.name, templateInspection: template && template.inspection,
        draftName: draft && draft.name, draftText: draft && draft.input, draftDirty: dirty,
        draftWithinLimit: withinLimit,
        draftGeneration: generation, rulePending, mappingPending, dictionaryPending, templateLoading: loading,
        templateNotice: notice, templateUnverified: !!draft && !inspect });
    }
    function stopInspection() { ticket++; clearTimeout(timer); if (abort) abort.abort(); abort = null; }
    async function replace(name, read) {
      if (busy()) return false;
      if ((dirty || rulePending || mappingPending || dictionaryPending) && !root.confirm("Текущий черновик изменён. Заменить его выбранным файлом?")) return false;
      stopInspection(); const current = ticket;
      generation++; template = null; draft = null; dirty = false; withinLimit = false;
      rulePending = false; mappingPending = false; dictionaryPending = false;
      loading = !!read; notice = ""; changed();
      if (!read) return true;
      abort = new AbortController();
      try {
        const input = await read();
        validate(input);
        if (current !== ticket || disposed) return false;
        draft = Object.freeze({ name, input }); withinLimit = true; changed();
        if (inspect) {
          const inspection = await inspect(input, abort.signal);
          if (current !== ticket || disposed) return false;
          template = Object.freeze({ name, input, inspection });
          notice = "Структура шаблона проверена. Значения источника проверяются при обработке файла.";
        } else notice = "Черновик открыт без проверки правил. Сохранение не публикует шаблон.";
      } catch (error) {
        if (current === ticket && !disposed) notice = "Черновик «" + name + "» не принят: " + describe(error);
      } finally { if (current === ticket && !disposed) { loading = false; changed(); } }
      return true;
    }
    function chooseFile(file) {
      if (!file) return replace(null, null);
      return replace(file.name, async () => {
        if (!file.size || file.size > maximumBytes)
          throw Object.assign(new Error("Нужен непустой JSON-шаблон до 1 000 000 байт."), { code: "DRAFT_LIMIT" });
        return decoder.decode(await file.arrayBuffer());
      });
    }
    function load(name, input) { return replace(name, async () => input); }
    function current() {
      return draft && withinLimit && !busy() && !rulePending && !mappingPending && !dictionaryPending && !loading
        ? Object.freeze({ name: draft.name, input: draft.input }) : null;
    }
    function validTemplate() { return !busy() && !loading && !rulePending && !mappingPending && !dictionaryPending ? template : null; }
    function changeDraft(input) {
      if (!draft || busy() || mappingPending || dictionaryPending) return;
      draft = Object.freeze({ name: draft.name, input }); dirty = true; template = null;
      stopInspection(); const current = ticket;
      loading = false; withinLimit = false;
      notice = inspect ? "Проверяем изменённый JSON…" : "Черновик изменён. Проверка правил в этом интерфейсе пока недоступна.";
      try { validate(input); withinLimit = true; }
      catch (error) { notice = describe(error); changed(); return; }
      if (!inspect) { changed(); return; }
      abort = new AbortController(); loading = true; changed();
      const signal = abort.signal;
      timer = setTimeout(async () => {
        try {
          const inspection = await inspect(input, signal);
          if (current !== ticket || disposed) return;
          template = Object.freeze({ name: draft.name, input, inspection });
          notice = "Черновик проверен. Значения источника будут проверены при обработке.";
        } catch (error) {
          if (current === ticket && !disposed) notice = "Черновик требует исправления: " + describe(error);
        } finally { if (current === ticket && !disposed) { loading = false; changed(); } }
      }, 300);
    }
    function ruleDirty(value) { if (rulePending !== value) { rulePending = value; changed(); } }
    function mappingDirty(value) { if (mappingPending !== value) { mappingPending = value; changed(); } }
    function dictionaryDirty(value) { if (dictionaryPending !== value) { dictionaryPending = value; changed(); } }
    async function commitCandidate(candidate, success, kind) {
      try { validate(candidate); }
      catch (error) { return { ok: false, message: describe(error) }; }
      stopInspection(); const current = ticket;
      abort = new AbortController(); loading = true; notice = "Проверяем изменённый шаблон…"; changed();
      try {
        const inspection = await inspect(candidate, abort.signal);
        if (current !== ticket || disposed) return { ok: false, message: "Проверка прервана." };
        draft = Object.freeze({ name: draft.name, input: candidate }); dirty = true; withinLimit = true;
        if (kind === "rule") rulePending = false;
        else if (kind === "mapping") mappingPending = false;
        else dictionaryPending = false;
        template = Object.freeze({ name: draft.name, input: candidate, inspection }); notice = success;
        return { ok: true };
      } catch (error) {
        if (current === ticket && !disposed) notice = "Правка не принята: " + describe(error);
        return { ok: false, message: "Правка не принята: " + describe(error) };
      } finally { if (current === ticket && !disposed) { loading = false; changed(); } }
    }
    function applyRule(path, expression) {
      if (!inspect || !draft || !template || busy() || mappingPending || dictionaryPending)
        return Promise.resolve({ ok: false, message: "Сначала закончите текущую правку JSON или маппинга." });
      let candidate;
      try { candidate = root.RNTabularTemplateDraft.replaceExpression(draft.input, template.inspection, path, expression); }
      catch (error) { return Promise.resolve({ ok: false, message: describe(error) }); }
      return commitCandidate(candidate, "Правило применено; полный черновик проверен.", "rule");
    }
    function applyMapping(edit) {
      if (!inspect || !draft || !template || busy() || rulePending || dictionaryPending)
        return Promise.resolve({ ok: false, message: "Сначала закончите текущую правку JSON или правила." });
      let candidate;
      try { candidate = root.RNTabularTemplateDraft.upsertMapping(draft.input, template.inspection, edit); }
      catch (error) { return Promise.resolve({ ok: false, message: describe(error) }); }
      return commitCandidate(candidate, "Соответствие применено; полный черновик проверен.", "mapping");
    }
    function removeMapping(path) {
      if (!inspect || !draft || !template || busy() || rulePending || dictionaryPending)
        return Promise.resolve({ ok: false, message: "Завершите текущую правку перед удалением соответствия." });
      let candidate;
      try { candidate = root.RNTabularTemplateDraft.removeMapping(draft.input, template.inspection, path); }
      catch (error) { return Promise.resolve({ ok: false, message: describe(error) }); }
      return commitCandidate(candidate, "Соответствие удалено; полный черновик проверен.", "mapping");
    }
    function changeDictionary(operation, value) {
      if (!inspect || !draft || !template || busy() || rulePending || mappingPending)
        return Promise.resolve({ ok: false, message: "Завершите текущую правку правила или маппинга." });
      let candidate;
      try { candidate = root.RNTabularTemplateDraft[operation](draft.input, template.inspection, value); }
      catch (error) { return Promise.resolve({ ok: false, message: describe(error) }); }
      return commitCandidate(candidate, "Справочник изменён; полный черновик проверен.", "dictionary");
    }
    function download() {
      if (!current()) return;
      const file = new Blob([draft.input], { type: "application/json;charset=utf-8" });
      const url = URL.createObjectURL(file), link = document.createElement("a");
      link.href = url;
      link.download = (draft.name.replace(/\.json$/i, "").replace(/[^\p{L}\p{N}._-]+/gu, "_").slice(0, 80) || "template") + ".draft.json";
      link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
    function setNotice(value) { notice = value; changed(); }
    function markSaved(input) { if (draft && draft.input === input) { dirty = false; changed(); } }
    function invalidate(message) {
      stopInspection(); template = null; loading = false;
      notice = message; changed();
    }
    function adoptValidated(input, inspection, message) {
      validate(input);
      if (!draft || disposed) throw Object.assign(new Error("Черновик был заменён во время правки."), { code: "DRAFT_CHANGED" });
      stopInspection(); draft = Object.freeze({ name: draft.name, input });
      template = Object.freeze({ name: draft.name, input, inspection });
      withinLimit = true; loading = false; notice = message; changed();
    }
    async function rebaseDictionary(dictionary) {
      if (!draft || disposed) return;
      let candidate;
      try { candidate = root.DataTemplatesProjectTemplate.replaceDictionary(draft.input, dictionary); validate(candidate); }
      catch (error) { invalidate("Справочник обновлён, но текущий JSON требует ручной проверки: " + describe(error)); return; }
      stopInspection(); const current = ticket;
      abort = new AbortController(); loading = true; template = null;
      notice = "Проверяем шаблон с новой версией общего справочника…"; changed();
      try {
        const inspection = await inspect(candidate, abort.signal);
        if (current !== ticket || disposed) return;
        draft = Object.freeze({ name: draft.name, input: candidate });
        template = Object.freeze({ name: draft.name, input: candidate, inspection });
        withinLimit = true; notice = "Общий справочник обновлён; шаблон проверен заново.";
      } catch (error) {
        if (current === ticket && !disposed) notice = "Справочник сохранён, но шаблон требует исправления: " + describe(error);
      } finally { if (current === ticket && !disposed) { loading = false; changed(); } }
    }
    function dispose() { disposed = true; stopInspection(); draft = null; template = null; }
    return Object.freeze({ snapshot, chooseFile, load, current, validTemplate, changeDraft, ruleDirty,
      mappingDirty, dictionaryDirty, applyRule, applyMapping, removeMapping,
      upsertParameter: edit => changeDictionary("upsertParameter", edit),
      removeParameter: path => changeDictionary("removeParameter", path),
      upsertUnit: edit => changeDictionary("upsertUnit", edit),
      removeUnit: path => changeDictionary("removeUnit", path),
      download, setNotice, markSaved, invalidate, adoptValidated, rebaseDictionary, dispose });
  }
  root.DataTemplatesDraftSession = Object.freeze({ create, newFileName });
}(window));
