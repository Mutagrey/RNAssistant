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
  const scope = { Blob, TextEncoder, TextDecoder, AbortController,
    DataTemplatesTemplateInspection: { inspect: async text => ({ template: { id: JSON.parse(text).template.id } }) }, crypto: {
    randomUUID: () => "00000000-0000-0000-0000-" + String(next++).padStart(12, "0")
  }, DataTemplatesBrowserProjectFiles: { unchanged: async () => { checks++; } } };
  scope.window = scope;
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, "../ui/browser-template-library.js"), "utf8"), scope);
  const library = scope.DataTemplatesBrowserTemplateLibrary;
  const project = { directory: new Directory(), info: { projectId: "tp_" + "a".repeat(32) } };
  const first = '{"template":{"id":"alpha"},"edition":1}';
  const second = '{"template":{"id":"beta"},"edition":1}';
  const a = await library.save(project, "alpha", first, null);
  const b = await library.save(project, "beta", second, null);
  assert.equal((await library.list(project)).length, 2);
  assert.equal(await library.open(project, a), first);
  assert.equal(await library.open(project, b), second);
  const updated = await library.save(project, "alpha", '{"template":{"id":"alpha"},"edition":2}', a.head);
  assert.equal(updated.versions, 2);
  await assert.rejects(library.save(project, "alpha", first, a.head), error => error.code === "TEMPLATE_CHANGED",
    "stale editors cannot silently replace a newer version");
  const archived = await library.archive(project, updated);
  assert.equal(archived.archived, true);
  assert.equal(await library.restore(project, archived), '{"template":{"id":"alpha"},"edition":2}');
  const restored = await library.save(project, "alpha", await library.restore(project, archived), archived.head);
  assert.equal(restored.archived, false);
  assert.equal(restored.versions, 3);
  await assert.rejects(library.save(project, "beta", '{"template":{"id":"wrong"}}', b.head),
    error => error.code === "TEMPLATE_INVALID");
  assert.ok(checks >= 12, "project manifest is checked around catalog effects");
  console.log("PASS browser template library: multiple IDs, immutable versions, stale guard, archive and restore");
})().catch(error => { console.error(error); process.exitCode = 1; });
