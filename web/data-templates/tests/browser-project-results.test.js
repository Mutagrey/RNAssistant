"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { webcrypto } = require("node:crypto");

const notFound = () => Object.assign(new Error("Not found"), { name: "NotFoundError" });
class FileHandle {
  constructor() { this.kind = "file"; this.blob = new Blob([]); }
  async getFile() { return this.blob; }
  async createWritable() {
    const parts = [];
    return { write: async part => { parts.push(part); }, close: async () => { this.blob = new Blob(parts); }, abort: async () => {} };
  }
}
class Directory {
  constructor(name) { this.kind = "directory"; this.name = name; this.children = new Map(); this.refuseRemoval = false; }
  async getDirectoryHandle(name, options = {}) {
    let child = this.children.get(name);
    if (!child && options.create) { child = new Directory(name); this.children.set(name, child); }
    if (!child || child.kind !== "directory") throw notFound();
    return child;
  }
  async getFileHandle(name, options = {}) {
    let child = this.children.get(name);
    if (!child && options.create) { child = new FileHandle(); this.children.set(name, child); }
    if (!child || child.kind !== "file") throw notFound();
    return child;
  }
  async removeEntry(name) { if (this.refuseRemoval) throw new Error("removal blocked"); this.children.delete(name); }
  async *entries() { yield* this.children.entries(); }
}

(async () => {
  let nextId = 1, checks = 0, drift = false;
  const scope = { Blob, TextEncoder, TextDecoder, crypto: {
    subtle: webcrypto.subtle,
    randomUUID: () => "00000000-0000-0000-0000-" + String(nextId++).padStart(12, "0")
  }, DataTemplatesBrowserProjectFiles: { unchanged: async () => {
    checks++; if (drift) throw Object.assign(new Error("project changed"), { code: "project_changed" });
  } } };
  scope.window = scope;
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, "../ui/browser-project-results.js"), "utf8"), scope);
  const store = scope.DataTemplatesBrowserProjectResults;
  const project = { directory: new Directory("project"), info: { projectId: "tp_" + "a".repeat(32) } };
  const blob = new Blob(["x".repeat(11 * 1048576 + 17)]);
  let largestSlice = 0, lastProgress = null;
  const result = { file: { size: blob.size, slice: (start, end) => {
    largestSlice = Math.max(largestSlice, end - start); return blob.slice(start, end);
  } }, sourcePath: "folder/данные.xlsx", templateFile: "rules.json", recordCount: 42 };
  const receipt = await store.save(project, result, null, value => { lastProgress = value; });
  assert.equal(receipt.recordCount, 42);
  assert.equal(receipt.chunkHashes.length, 12);
  assert.equal(largestSlice, 1048576, "no full-file read while saving");
  assert.equal(lastProgress.phase, "verify");
  assert.equal(lastProgress.done, blob.size);
  assert.equal((await store.list(project)).entries.length, 1);
  const opened = await store.open(project, receipt.folder);
  assert.equal(opened.file.size, blob.size);
  assert.ok(checks >= 4, "manifest drift checked around write and read");

  const parent = await project.directory.getDirectoryHandle("results");
  const folder = await parent.getDirectoryHandle(receipt.folder);
  (await folder.getFileHandle("data.json")).blob = new Blob(["y".repeat(blob.size)]);
  await assert.rejects(store.open(project, receipt.folder), error => error.code === "RESULT_VERIFY_FAILED",
    "later download must recheck bytes");
  const controller = new AbortController();
  await assert.rejects(store.save(project, result, controller.signal, state => {
    if (state.phase === "copy" && state.done >= 1048576) controller.abort();
  }), error => error.code === "RESULT_CANCELLED");
  assert.equal((await store.list(project)).entries.length, 1, "cancelled folder removed");
  drift = true;
  await assert.rejects(store.save(project, result), error => error.code === "project_changed");
  drift = false;

  const incomplete = await parent.getDirectoryHandle("result-9999999999999-" + "b".repeat(32) + "-partial", { create: true });
  await incomplete.getFileHandle("data.json", { create: true });
  assert.equal((await store.list(project)).incomplete, 1, "interrupted folders are visible");
  parent.refuseRemoval = true;
  const cancelled = new AbortController();
  await assert.rejects(store.save(project, result, cancelled.signal, state => {
    if (state.phase === "copy" && state.done >= 1048576) cancelled.abort();
  }), error => error.code === "effect_unknown" && /results\/result-/.test(error.message),
  "failed cleanup names an exact recovery target");
  console.log("PASS browser project results: bounded copy, read-back, listing, cancellation and recovery");
})().catch(error => { console.error(error); process.exitCode = 1; });
