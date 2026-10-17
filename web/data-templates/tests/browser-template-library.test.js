"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const notFound = () => Object.assign(new Error("not found"), { name: "NotFoundError" });
class FileHandle {
  constructor() { this.kind = "file"; this.blob = new Blob([]); }
  async getFile() { return this.blob; }
  async createWritable() {
    const parts = [];
    return { write: async part => { parts.push(part); }, close: async () => { this.blob = new Blob(parts); } };
  }
}
class Directory {
  constructor() { this.kind = "directory"; this.children = new Map(); }
  async getDirectoryHandle(name, options = {}) {
    let child = this.children.get(name);
    if (!child && options.create) { child = new Directory(); this.children.set(name, child); }
    if (!child || child.kind !== "directory") throw notFound(); return child;
  }
  async getFileHandle(name, options = {}) {
    let child = this.children.get(name);
    if (!child && options.create) { child = new FileHandle(); this.children.set(name, child); }
    if (!child || child.kind !== "file") throw notFound(); return child;
  }
  async *entries() { yield* this.children.entries(); }
}
(async () => {
  let next = 1, checks = 0;
  const scope = { Blob, TextEncoder, TextDecoder, AbortController, console,
    DataTemplatesTemplateInspection: {
      inspect: async text => ({ template: { id: JSON.parse(text).template.id } }),
      inspectDictionary: async text => ({ revision: JSON.parse(text).revision })
    }, crypto: { randomUUID: () => "00000000-0000-0000-0000-" + String(next++).padStart(12, "0") },
    DataTemplatesBrowserProjectFiles: { unchanged: async () => { checks++; } } };
  scope.window = scope; scope.globalThis = scope;
  for (const file of ["core/selectors.js", "core/json.js", "core/project-template.js",
    "ui/browser-project-dictionary.js", "ui/browser-template-library.js"])
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, "..", file), "utf8"), scope, { filename: file });
  const library = scope.DataTemplatesBrowserTemplateLibrary, dictionaries = scope.DataTemplatesBrowserProjectDictionary,
    projectTemplate = scope.DataTemplatesProjectTemplate;
  const project = { directory: new Directory(), info: { projectId: "tp_" + "a".repeat(32) } };
  const full = id => JSON.stringify({ version: 2, kind: "xlsx-extraction", template: { id },
    source: {}, dictionaries: { version: 1, revision: 1, units: [{ id: "t", name: "Тонны" }],
      parameters: [{ id: "p", name: "Параметр", valueType: "decimal", unitId: "t",
        dimensions: [], temporalMeaning: "flow" }], parameterMappings: [], unitMappings: [] }, dataset: {} });
  const shared = await dictionaries.save(project, JSON.parse(full("alpha")).dictionaries, null);
  const store = async (id, expectedHead) => {
    const source = full(id), detached = projectTemplate.detach(source, project.info.projectId);
    return library.save(project, id, detached, expectedHead,
      { materialized: projectTemplate.materialize(detached, shared.dictionary, project.info.projectId),
        dictionaryHead: shared.head, dictionaryText: shared.text });
  };
  const a = await store("alpha", null), b = await store("beta", null);
  assert.equal((await library.list(project)).length, 2);
  const raw = await library.open(project, a);
  assert.equal(JSON.parse(raw).kind, "data-templates-project-template");
  assert.equal(Object.hasOwn(JSON.parse(raw), "dictionaries"), false, "catalog files do not duplicate the dictionary");
  assert.equal(JSON.parse(projectTemplate.materialize(raw, shared.dictionary, project.info.projectId)).template.id, "alpha");
  const updated = await store("alpha", a.head);
  assert.equal(updated.versions, 2);
  await assert.rejects(store("alpha", a.head), error => error.code === "TEMPLATE_CHANGED");
  const archived = await library.archive(project, updated);
  assert.equal(archived.archived, true);
  const restoredRaw = await library.restore(project, archived);
  const restored = await library.save(project, "alpha", restoredRaw, archived.head,
    { materialized: projectTemplate.materialize(restoredRaw, shared.dictionary, project.info.projectId),
      dictionaryHead: shared.head, dictionaryText: shared.text });
  assert.equal(restored.versions, 3);
  const changed = { ...shared.dictionary, revision: 2, units: [...shared.dictionary.units, { id: "kg", name: "Килограммы" }] };
  const dictionaryFolder = await project.directory.getDirectoryHandle("dictionaries");
  const headFile = await dictionaryFolder.getFileHandle(shared.head), originalBytes = headFile.blob;
  headFile.blob = new Blob([JSON.stringify({ ...shared.dictionary, revision: 999 })]);
  await assert.rejects(dictionaries.save(project, changed, shared.head, null, shared.text),
    error => error.code === "DICTIONARY_CHANGED", "in-place edits to the observed head are detected");
  headFile.blob = originalBytes;
  const nextShared = await dictionaries.save(project, changed, shared.head, null, shared.text);
  assert.equal(JSON.parse(projectTemplate.materialize(raw, nextShared.dictionary, project.info.projectId)).dictionaries.units.length, 2,
    "all templates read the new project dictionary");
  await assert.rejects(store("beta", b.head), error => error.code === "DICTIONARY_CHANGED",
    "a stale dictionary cannot authorize a template version");
  assert.ok(checks >= 12, "project manifest is checked around catalog effects");
  console.log("PASS browser project dictionary and templates: shared versions, thin files, stale guards, archive and restore");
})().catch(error => { console.error(error); process.exitCode = 1; });
