(function (root) {
  "use strict";
  const invalid = message => { throw Object.assign(new Error(message), { code: "project_invalid" }); };
  const fields = (value, names) => value && !Array.isArray(value) && typeof value === "object" &&
    Object.keys(value).length === names.length && names.every(name => Object.prototype.hasOwnProperty.call(value, name));
  const identity = (value, prefix) => typeof value === "string" &&
    new RegExp("^" + prefix + "[0-9a-f]{32}$").test(value);
  const control = value => /\p{Cc}/u.test(value);
  function parse(text) {
    if (typeof text !== "string" || new TextEncoder().encode(text).byteLength > 256 * 1024)
      invalid("Манифест проекта превышает 256 КиБ.");
    let value;
    try { value = root.RNTabularJson.parse(text, "/formatVersion"); }
    catch (error) { invalid("Некорректный JSON манифеста: " + error.message); }
    if (!fields(value, ["kind", "formatVersion", "projectId", "name", "sources"]) ||
        value.kind !== "data-templates" || value.formatVersion?.lexeme !== "2" ||
        !identity(value.projectId, "tp_") || typeof value.name !== "string" ||
        !value.name.trim() || value.name.length > 200 || control(value.name) ||
        !Array.isArray(value.sources) || value.sources.length > 500)
      invalid("Нужен поддерживаемый project.json формата 2.");
    const ids = new Set(), paths = new Set();
    for (const source of value.sources) {
      if (!fields(source, ["sourceId", "path"]) || !identity(source.sourceId, "src_") ||
          typeof source.path !== "string" || !source.path.trim() || source.path.length > 4096 ||
          control(source.path) || /[\\:]/.test(source.path) ||
          source.path.split("/").some(part => !part || part === "." || part === "..") ||
          ids.has(source.sourceId) || paths.has(source.path.toLowerCase()))
        invalid("Некорректные или повторяющиеся пути источников в project.json.");
      ids.add(source.sourceId); paths.add(source.path.toLowerCase());
    }
    return Object.freeze({ projectId: value.projectId, name: value.name, sources: value.sources.length });
  }
  function create(name) {
    const text = JSON.stringify({ kind: "data-templates", formatVersion: 2,
      projectId: "tp_" + root.crypto.randomUUID().replace(/-/g, ""), name: name, sources: [] }, null, 2) + "\n";
    parse(text);
    return text;
  }
  root.DataTemplatesProjectManifest = Object.freeze({ parse: parse, create: create });
}(globalThis));
