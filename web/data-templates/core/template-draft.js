(function (root) {
  "use strict";
  const fail = message => { throw Object.assign(new Error(message), { code: "DRAFT_INVALID" }); };
  const serialize = value => {
    const text = JSON.stringify(value, null, 2);
    if (text.length > 1000000) fail("JSON-черновик превысит 1 000 000 символов.");
    return text;
  };
  const segments = path => path.slice(1).split("/").map(part => part.replace(/~1/g, "/").replace(/~0/g, "~"));
  function replaceExpression(input, inspection, path, expression) {
    const known = [...inspection.contexts, ...inspection.commonRules,
      ...inspection.rows.flatMap(group => group.rules), ...inspection.columns.flatMap(group => group.rules)]
      .some(rule => rule.path === path);
    if (!known || typeof expression !== "string") fail("Правило отсутствует в проверенном шаблоне.");
    // The caller supplies the exact text that produced inspection. Invalid source
    // stays in the raw editor and never reaches this structured mutator.
    const document = JSON.parse(input), parts = segments(path);
    let parent = document;
    for (const part of parts.slice(0, -1)) {
      if (!parent || !Object.prototype.hasOwnProperty.call(parent, part)) fail("Путь правила изменился.");
      parent = parent[part];
    }
    const last = parts[parts.length - 1];
    if (!parent || !Object.prototype.hasOwnProperty.call(parent, last) || typeof parent[last] !== "string")
      fail("Путь правила не указывает на выражение.");
    parent[last] = expression;
    return serialize(document);
  }
  function upsertMapping(input, inspection, edit) {
    if (!edit || !["parameter", "unit"].includes(edit.kind)) fail("Неизвестный вид соответствия.");
    const known = edit.path && inspection.mappings.find(item => item.path === edit.path && item.kind === edit.kind);
    if (edit.path && !known) fail("Соответствие отсутствует в проверенном шаблоне.");
    const document = JSON.parse(input);
    const entries = document.dictionaries[edit.kind === "parameter" ? "parameterMappings" : "unitMappings"];
    const value = { scope: edit.scope, source: edit.source, targetId: edit.targetId };
    if (known) entries[Number(known.path.slice(known.path.lastIndexOf("/") + 1))] = value;
    else entries.push(value);
    return serialize(document);
  }
  function removeMapping(input, inspection, path) {
    const known = inspection.mappings.find(item => item.path === path);
    if (!known) fail("Соответствие отсутствует в проверенном шаблоне.");
    const document = JSON.parse(input);
    const entries = document.dictionaries[known.kind === "parameter" ? "parameterMappings" : "unitMappings"];
    entries.splice(Number(path.slice(path.lastIndexOf("/") + 1)), 1);
    return serialize(document);
  }
  function upsertParameter(input, inspection, edit) {
    const known = edit && edit.path && inspection.parameters.find(item => item.path === edit.path);
    if (!edit || edit.path && !known || known && edit.id !== known.id) fail("Код параметра нельзя менять при редактировании.");
    const document = JSON.parse(input);
    const value = { id: edit.id, name: edit.name, valueType: "decimal", unitId: edit.unitId,
      dimensions: edit.dimensions, temporalMeaning: edit.temporalMeaning };
    if (known) document.dictionaries.parameters[Number(known.path.slice(known.path.lastIndexOf("/") + 1))] = value;
    else document.dictionaries.parameters.push(value);
    return serialize(document);
  }
  function removeParameter(input, inspection, path) {
    const known = inspection.parameters.find(item => item.path === path);
    if (!known) fail("Параметр отсутствует в проверенном шаблоне.");
    const document = JSON.parse(input), dictionary = document.dictionaries;
    if (dictionary.parameters.length <= 1) fail("Последний параметр нельзя удалить из словаря.");
    if (dictionary.parameterMappings.some(item => item.targetId === known.id))
      fail("Сначала удалите соответствия, которые ссылаются на этот параметр.");
    dictionary.parameters.splice(Number(path.slice(path.lastIndexOf("/") + 1)), 1);
    return serialize(document);
  }
  function upsertUnit(input, inspection, edit) {
    const known = edit && edit.path && inspection.units.find(item => item.path === edit.path);
    if (!edit || edit.path && !known || known && edit.id !== known.id) fail("Код единицы нельзя менять при редактировании.");
    const document = JSON.parse(input), value = { id: edit.id, name: edit.name };
    if (known) document.dictionaries.units[Number(known.path.slice(known.path.lastIndexOf("/") + 1))] = value;
    else document.dictionaries.units.push(value);
    return serialize(document);
  }
  function removeUnit(input, inspection, path) {
    const known = inspection.units.find(item => item.path === path);
    if (!known) fail("Единица отсутствует в проверенном шаблоне.");
    const document = JSON.parse(input), dictionary = document.dictionaries;
    if (dictionary.units.length <= 1) fail("Последнюю единицу нельзя удалить из словаря.");
    if (dictionary.parameters.some(item => item.unitId === known.id) ||
        dictionary.unitMappings.some(item => item.targetId === known.id))
      fail("Сначала измените параметры и удалите соответствия, которые используют эту единицу.");
    dictionary.units.splice(Number(path.slice(path.lastIndexOf("/") + 1)), 1);
    return serialize(document);
  }
  root.RNTabularTemplateDraft = Object.freeze({ replaceExpression, upsertMapping, removeMapping,
    upsertParameter, removeParameter, upsertUnit, removeUnit });
}(globalThis));
