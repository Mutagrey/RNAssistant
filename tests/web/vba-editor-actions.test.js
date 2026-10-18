"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const elements = Object.create(null);
for (const id of ["vbaActionsMenu", "saveVbaButton", "discardVbaChangesButton", "vbaStatus", "vbaCodeInput"]) {
  elements[id] = { open: true, hidden: false, disabled: false, textContent: "", value: "" };
}
const vbaModule = { name: "Sales", code: "Option Explicit\n" };
let editorCode = "Option Explicit\nPublic Sub Draft()\nEnd Sub\n";
let downloaded;
let revoked;
let canceled = 0;
let accepted = true;
const context = vm.createContext({
  state: { vba: { modules: [vbaModule] }, vbaEditorDirty: true, vbaEditorMode: "edit", activeChatId: "chat" },
  $: id => elements[id] || null,
  selectedVbaModule: () => vbaModule,
  hasVbaModuleCode: item => !!item && Object.hasOwn(item, "code"),
  vbaModuleCode: item => item.code,
  vbaModuleName: item => item.name,
  getCodeEditorValue: () => editorCode,
  setCodeEditorValue: (_id, text) => { editorCode = text; },
  updateVbaMacroSuggestion: () => {},
  send: () => {}, cancelBridgeRequest: () => {}, log: () => {}, logToolResult: () => {},
  loadSelectedVbaModule: () => {},
  Blob: class { constructor(parts, options) { this.parts = parts; this.type = options.type; } },
  URL: {
    createObjectURL: blob => { downloaded = blob; return "blob:module"; },
    revokeObjectURL: url => { revoked = url; }
  },
  document: {
    body: { appendChild: link => { downloaded.link = link; } },
    createElement: () => ({ click() { this.clicked = true; }, remove() { this.removed = true; } })
  }
});
context.window = context;
context.window.confirm = () => accepted;
context.window.setTimeout = callback => callback();
context.window.RNAssistantVbaActions = { create: () => ({ cancelWrite: () => { canceled += 1; } }) };
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-vba.js"), "utf8"), context);

context.downloadVbaModule();
assert.equal(downloaded.parts[0], editorCode, "download includes the current draft");
assert.equal(downloaded.link.download, "Sales.bas");
assert.equal(downloaded.link.clicked, true);
assert.equal(revoked, "blob:module");

accepted = false;
context.discardVbaChanges();
assert.match(editorCode, /Draft/, "declined discard preserves the draft");
accepted = true;
context.discardVbaChanges();
assert.equal(editorCode, vbaModule.code, "discard restores the loaded module code");
assert.equal(context.state.vbaEditorDirty, false);
assert.equal(elements.saveVbaButton.hidden, true);
assert.equal(elements.discardVbaChangesButton.disabled, true);
assert.equal(canceled, 1);
console.log("PASS VBA editor menu: download current draft and discard to loaded code");
