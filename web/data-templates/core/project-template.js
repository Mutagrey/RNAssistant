(function (root) {
  "use strict";
  const fail = (code, message) => { throw Object.assign(new Error(message), { code }); };
  const own = (value, names) => value && typeof value === "object" && !Array.isArray(value) &&
    Object.keys(value).length === names.length && names.every(name => Object.prototype.hasOwnProperty.call(value, name));
  const canonical = value => JSON.stringify(value, (key, item) => item && typeof item === "object" &&
    !Array.isArray(item) ? Object.fromEntries(Object.keys(item).sort().map(name => [name, item[name]])) : item);
  function standalone(text) {
    let value;
    try { value = root.RNTabularJson.parse(text); } catch (_) { fail("TEMPLATE_INVALID", "Нужен корректный JSON-шаблон."); }
    if (!own(value, ["version", "kind", "template", "source", "dictionaries", "dataset"]) ||
        value.version !== 2 || value.kind !== "xlsx-extraction")
      fail("TEMPLATE_FORMAT", "Нужен проверенный xlsx-extraction версии 2.");
    return value;
  }
  function projectTemplate(text, projectId) {
    let value;
    try { value = root.RNTabularJson.parse(text); } catch (_) { fail("TEMPLATE_INVALID", "Некорректный JSON проекта."); }
    if (!own(value, ["kind", "formatVersion", "projectId", "template", "source", "dataset"]) ||
        value.kind !== "data-templates-project-template" || value.formatVersion !== 1 ||
        value.projectId !== projectId)
      fail("TEMPLATE_FORMAT", "Шаблон не принадлежит выбранному проекту или имеет неподдерживаемый формат.");
    return value;
  }
  function detach(text, projectId) {
    const value = standalone(text);
    return JSON.stringify({ kind: "data-templates-project-template", formatVersion: 1, projectId,
      template: value.template, source: value.source, dataset: value.dataset }, null, 2) + "\n";
  }
  function materialize(text, dictionary, projectId) {
    const value = projectTemplate(text, projectId);
    if (!dictionary) fail("DICTIONARY_MISSING", "В проекте нет общего справочника.");
    const combined = JSON.stringify({ version: 2, kind: "xlsx-extraction", template: value.template,
      source: value.source, dictionaries: dictionary, dataset: value.dataset }, null, 2) + "\n";
    if (combined.length > 1000000 || new TextEncoder().encode(combined).length > 1000000)
      fail("TEMPLATE_LIMIT", "Шаблон вместе с общим справочником превышает 1 000 000 байт.");
    return combined;
  }
  function replaceDictionary(text, dictionary) {
    const value = standalone(text);
    value.dictionaries = dictionary;
    return JSON.stringify(value, null, 2) + "\n";
  }
  function merge(current, incoming) {
    if (!current) return { ...incoming, revision: 1 };
    if (!incoming || current.version !== 1 || incoming.version !== 1)
      fail("DICTIONARY_FORMAT", "Версия общего справочника не поддерживается.");
    const next = JSON.parse(JSON.stringify(current)); let changed = false;
    for (const [name, key] of [["units", item => item.id], ["parameters", item => item.id],
      ["parameterMappings", item => JSON.stringify([item.scope, item.source])],
      ["unitMappings", item => JSON.stringify([item.scope, item.source])]]) {
      const known = new Map(next[name].map(item => [key(item), item]));
      for (const item of incoming[name]) {
        const existing = known.get(key(item));
        if (existing && canonical(existing) !== canonical(item))
          fail("DICTIONARY_CONFLICT", "Конфликт общего справочника: " + name + " / " + key(item) + ".");
        if (!existing) { next[name].push(item); known.set(key(item), item); changed = true; }
      }
    }
    if (changed) next.revision = current.revision + 1;
    return next;
  }
  const equal = (left, right) => canonical(left) === canonical(right);
  root.DataTemplatesProjectTemplate = Object.freeze({ standalone, projectTemplate, detach, materialize,
    replaceDictionary, merge, equal });
}(globalThis));
