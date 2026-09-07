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
function manifest(code) {
  return JSON.parse(code.split("<RNAssistantTool>")[1].split("</RNAssistantTool>")[0]
    .split(/\r\n|\n|\r/).map(line => line.replace(/^\s*' ?/, "")).join("\n"));
}
assert.equal(get("toolCodeInput").value, created.Components[0].Code);
assert.equal(manifest(created.Code).id, created.Id);
assert.equal(manifest(created.Code).host, "Excel");
assert.equal(manifest(created.Code).entryPoint, "Run");
assert.match(created.Code, /Public Function Run\(\) As String/);
assert.match(created.Code, /Err.Raise/, "unfinished draft cannot claim successful execution");
const originalCode = created.Code;
get("cloneToolButton").handlers.click();
const cloned = context.state.tools[1];
assert.equal(manifest(cloned.Code).id, cloned.Id);
assert.equal(cloned.Components[0].Code, cloned.Code);
assert.equal(created.Code, originalCode, "clone preserves original source");
get("addToolButton").handlers.click();
assert.equal(context.state.tools[2].Id, "excel.new_tool_2");
context.state.selectedToolIndex = 0;
context.renderToolEditor();
assert.equal(context.inferredVbaComponentName({ Id: "x".repeat(100) }).length, 31);
const count = context.state.tools.length;
context.state.host = "Outlook";
get("addToolButton").handlers.click();
assert.equal(context.state.tools.length, count, "unsupported host cannot create invalid VBA draft");
context.state.host = "Excel";
pendingCode = "Option Explicit\n' edited source";
context.syncSelectedToolFromEditor();
assert.equal(context.toolSourceBody(created).components[0].code, "Option Explicit\n' edited source");
assert.equal(context.validateSelectedToolEditors(), true);
const beforeInvalidClone = context.state.tools.length;
get("cloneToolButton").handlers.click();
assert.equal(context.state.tools.length, beforeInvalidClone, "missing manifest cannot produce a broken clone");
created.Components[0].Code = "' <RNAssistantTool>\n' { broken }\n' </RNAssistantTool>";
context.renderToolEditor();
get("cloneToolButton").handlers.click();
assert.equal(context.state.tools.length, beforeInvalidClone, "invalid JSON cannot produce a broken clone");
context.state.tools = [{ Id: "excel.inspect", Host: "Excel", Executor: "builtin", BuiltIn: true,
  ArgumentSchemaJson: context.emptyToolSchema(), Enabled: true }];
context.state.selectedToolIndex = 0;
context.renderToolEditor();
assert.equal(get("cloneToolButton").disabled, true);
get("cloneToolButton").handlers.click();
assert.equal(context.state.tools.length, 1, "built-ins no longer clone into pipelines");
const unloaded = { Id: "excel.unloaded", Host: "Excel", Executor: "vba", Source: { sha256: "a".repeat(64), byteLength: 100 }, Enabled: true };
context.state.tools = [unloaded]; context.renderToolEditor();
assert.equal(get("toolCodeInput").disabled, true); assert.equal(get("toolIdInput").disabled, true);
assert.equal(get("cloneToolButton").disabled, true); assert.equal(get("installVbaToolButton").disabled, true);
get("toolCodeInput").value = "not source"; context.syncSelectedToolFromEditor();
assert.equal(unloaded.Code, undefined); assert.equal(unloaded.Components, undefined, "unloaded body has no fabricated component");
console.log("PASS tools editor: VBA draft, source sync and disabled built-in clone");
