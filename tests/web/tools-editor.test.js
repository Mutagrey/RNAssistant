"use strict";

// Real tool editor/actions against IDs from the shipped page; no WebView or COM.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const page = fs.readFileSync(path.join(__dirname, "../../web/index.html"), "utf8");
const ids = new Set(Array.from(page.matchAll(/\bid="([^"]+)"/g), match => match[1]));
class Element {
  constructor() {
    this.value = ""; this.checked = false; this.style = {}; this.handlers = {}; this.children = [];
    this.classList = { toggle() {}, add() {}, remove() {} };
  }
  addEventListener(name, handler) { this.handlers[name] = handler; }
  appendChild(child) { this.children.push(child); return child; }
  querySelector() { return new Element(); }
  querySelectorAll() { return []; }
}
const elements = new Map();
const get = id => {
  if (!ids.has(id)) return null;
  if (!elements.has(id)) elements.set(id, new Element());
  return elements.get(id);
};
let pendingCode = null;
const context = vm.createContext({
  state: { host: "Excel", tools: [], selectedToolIndex: -1 }, $: get,
  document: { createElement: () => new Element(), querySelectorAll: () => [] },
  createResourceEmptyState: () => new Element(),
  send() {}, setControlBusy() {}, log() {}, logToolResult() {}, addEventListener() {},
  renderInstructions() { context.renderToolEditor(); },
  syncCodeEditors(ids) {
    if (pendingCode !== null && ids.includes("toolCodeInput")) {
      get("toolCodeInput").value = pendingCode; pendingCode = null;
    }
  }
});
context.window = context;
for (const file of ["app-tools-structured.js", "app-tools-actions.js", "app-tools-documentation.js", "app-tools.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), context, { filename: file });
}
context.bindToolActions();
context.renderToolEditor();
get("addToolButton").handlers.click();
const created = context.state.tools[0];
assert.equal(created.Executor, "vba");
assert.equal(created.Components.length, 1);
assert.equal(get("toolCodeInput").value, "Option Explicit\n");
pendingCode = "Option Explicit\n' edited source";
context.syncSelectedToolFromEditor();
assert.equal(context.readTools()[0].Components[0].Code, "Option Explicit\n' edited source");
assert.equal(context.validateSelectedToolEditors(), true);
context.state.tools = [{ Id: "excel.inspect", Host: "Excel", Executor: "builtin", BuiltIn: true,
  ArgumentSchemaJson: context.emptyToolSchema(), Enabled: true }];
context.state.selectedToolIndex = 0;
context.renderToolEditor();
assert.equal(get("cloneToolButton").disabled, true);
get("cloneToolButton").handlers.click();
assert.equal(context.state.tools.length, 1, "built-ins no longer clone into pipelines");
console.log("PASS tools editor: VBA draft, source sync and disabled built-in clone");
