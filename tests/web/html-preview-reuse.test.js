"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
let builds = 0, closes = 0, loads = 0, resets = 0, ready = true;
const frame = { classList: { toggle() {}, add() {} }, removeAttribute() {},
  set srcdoc(value) { loads++; } };
const detail = { classList: { toggle() {}, remove() {} }, replaceChildren() {} };
const state = { activeChatId: "chat1", activeHtmlArtifactId: "workspace1", htmlWorkspaceMode: "preview", artifacts: [] };
const files = [{ id: "file1", path: "index.html", content: "<main>Hello</main>", sourceReadKey: "revision1" }];
const bindings = [{ name: "table", resourceUri: "rna://revision1" }];
const context = vm.createContext({ console, state, $: id => id === "htmlWorkspacePreviewFrame" ? frame : detail });
context.window = context;
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-html-workspace-editor.js"), "utf8"), context);
let watches = 0;
const editor = context.RNAssistantHtmlWorkspaceEditor.create({ state,
  previewSurface: { watch: () => { watches++; }, clear() {}, waiting() {}, fail() {} },
  source: { current: () => ready, ready: () => ready, message: () => "Loading" },
  preview: { missingVendors: () => [], build: options => {
    assert.equal(options.inspectGeneration, resets + 1); builds++; return "same markup";
  } },
  inspector: { reset: () => { resets++; }, generation: () => resets },
  closeResources: () => { closes++; },
  model: { selectedItem: () => ({ type: "file", item: files[0] }), workspace: () => ({ activeFileId: "file1" }),
    artifactId: item => item.id, filePath: file => file.path,
    files: () => files, dataSources: () => bindings } });
editor.renderPreview();
for (let i = 0; i < 100; i++) editor.renderPreview();
assert.deepEqual([builds, closes, loads], [1, 1, 1], "projection updates and tab revisits retain the preview and leases");
files[0].content = "changed"; editor.renderPreview();
bindings[0].resourceUri = "rna://revision2"; editor.renderPreview();
state.activeChatId = "chat2"; editor.renderPreview();
state.activeHtmlArtifactId = "workspace2"; editor.renderPreview();
assert.deepEqual([builds, closes, loads], [5, 5, 5], "source, exact binding and owner changes replace even identical markup");
ready = false; editor.renderPreview(); ready = true; editor.renderPreview();
assert.deepEqual([builds, closes, loads], [6, 7, 7], "source invalidation cannot reuse an old preview");
state.htmlWorkspaceMode = "edit"; editor.renderPreview();
for (let i = 0; i < 200; i++) editor.renderPreview();
assert.deepEqual([builds, closes, loads], [6, 8, 8], "edit updates do not repeatedly navigate an empty iframe");
state.htmlWorkspaceMode = "preview"; editor.renderPreview();
assert.deepEqual([builds, closes, loads], [7, 9, 9], "return from cleared edit preview rebuilds");
editor.reloadPreview();
assert.deepEqual([builds, closes, loads, watches], [8, 10, 10, 8], "explicit reload rebuilds once through the same preview owner");
assert.equal(resets, closes, "each invalidation revokes the old inspector generation once");
console.log("PASS HTML preview: unchanged updates reuse iframe; source/binding/owner/mode invalidation replaces it");
