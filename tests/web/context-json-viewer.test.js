"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

class ClassList {
  constructor(owner) { this.owner = owner; }
  values() { return new Set(String(this.owner.className || "").split(/\s+/).filter(Boolean)); }
  write(values) { this.owner.className = Array.from(values).join(" "); }
  add(...names) { const values = this.values(); names.forEach(name => values.add(name)); this.write(values); }
  remove(...names) { const values = this.values(); names.forEach(name => values.delete(name)); this.write(values); }
  toggle(name, force) { const values = this.values(); const enabled = force === undefined ? !values.has(name) : !!force; if (enabled) values.add(name); else values.delete(name); this.write(values); return enabled; }
  contains(name) { return this.values().has(name); }
}

class Element {
  constructor(tag) {
    this.tagName = String(tag).toLowerCase(); this.className = ""; this.classList = new ClassList(this);
    this.childNodes = []; this.parentNode = null; this.attributes = {}; this.handlers = {};
    this.open = false; this.disabled = false; this.value = ""; this._text = "";
  }
  get firstElementChild() { return this.childNodes[0] || null; }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  removeChild(child) { this.childNodes.splice(this.childNodes.indexOf(child), 1); child.parentNode = null; return child; }
  replaceChildren(...children) { this.childNodes.forEach(child => { child.parentNode = null; }); this.childNodes = []; children.forEach(child => this.appendChild(child)); }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  getAttribute(name) { return this.attributes[name]; }
  addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); }
  dispatch(name) { (this.handlers[name] || []).forEach(handler => handler({ preventDefault() {}, stopPropagation() {} })); }
  click() { if (!this.disabled) this.dispatch("click"); }
  select() {}
  querySelectorAll(selector) {
    const matches = node => selector.startsWith(".") ? node.classList.contains(selector.slice(1)) : node.tagName === selector.toLowerCase();
    const result = [];
    const walk = node => node.childNodes.forEach(child => { if (matches(child)) result.push(child); walk(child); });
    walk(this); return result;
  }
  set textContent(value) { this._text = String(value); this.replaceChildren(); }
  get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(""); }
}

const elements = {};
function add(id, tag, parent) {
  const node = new Element(tag);
  elements[id] = node;
  if (parent) parent.appendChild(node);
  return node;
}

const body = new Element("body");
const contextManager = add("contextManager", "div", body);
contextManager.classList.add("hidden");
const contextDetails = add("contextJsonDetails", "details", contextManager);
const contextHost = add("contextBox", "div", contextDetails);
add("openContextTabButton", "button", body);
const rawDetails = add("promptContextInspectorRaw", "details", body);
rawDetails.classList.add("hidden");
const rawHost = add("promptContextInspectorRawText", "div", rawDetails);
const rawButton = add("loadPromptContextRawButton", "button", body);
const toolHost = add("toolRunOutput", "div", body);
toolHost.classList.add("tool-run-output", "is-text");
const vbaHost = add("vbaMetaBox", "div", body);

const copied = [];
const context = vm.createContext({
  AbortController,
  document: { body, createElement: tag => new Element(tag), execCommand: () => true },
  navigator: { clipboard: { writeText(text) { copied.push(String(text)); return Promise.resolve(); } } },
  state: { context: {} },
  $: id => elements[id] || null
});
context.window = context;
for (const file of ["app-utils.js", "app-viewer-registry.js", "app-json-viewer.js", "app-context.js", "app-context-inspector.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), context, { filename: file });
}
context.setControlBusy = () => {};
context.send = () => Promise.resolve({});
context.log = () => {};
context.logToolResult = () => {};
context.RNAssistantToolStructuredEditor = {
  create() { return { readRunArguments() { return {}; }, setMode() {}, syncSchemaDraft() {} }; }
};
for (const file of ["app-tools-actions.js", "app-tools-documentation.js", "app-tools.js", "app-vba-project.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), context, { filename: file });
}

function button(root, text) { return root.querySelectorAll("button").find(node => node.textContent === text); }
function settle() { return new Promise(resolve => setImmediate(resolve)); }

(async function () {
  const contextRequests = [];
  context.state.activeChatId = "context-chat";
  context.send = (type, payload) => { contextRequests.push({ type, payload }); return Promise.resolve({ Notes: [] }); };
  const applyContextResponse = context.applyContextResponse;
  context.applyContextResponse = () => true;
  context.syncActiveChatState = async () => {};
  await context.addTextContext("SuppliedData", "skill_definition", "Draft", "skill:draft", "Data", {});
  await context.addTextContext("UserInstruction", "note", "Preferences", "preferences", "Instruction", {});
  assert.equal(contextRequests[0].type, "addTextContext");
  assert.equal(contextRequests[0].payload.role, "SuppliedData");
  assert.equal(contextRequests[1].payload.role, "UserInstruction");
  context.state.tools = [{ Id: "demo" }];
  context.state.selectedToolIndex = 0;
  context.syncSelectedToolFromEditor = () => {};
  context.selectedToolContext = () => "Draft tool data";
  await context.addSelectedToolContextToContext();
  assert.equal(contextRequests[2].payload.role, "SuppliedData", "attaching a draft definition cannot activate instructions");
  assert.equal(contextRequests[2].payload.kind, "tool_definition");
  context.applyContextResponse = applyContextResponse;
  console.log("PASS context bridge: explicit roles distinguish data from user instructions");

  const stateProjection = { note: "</script><img onerror=1>", count: 9007199254740992 };
  const expectedProjection = JSON.stringify(stateProjection, null, 2);
  context.renderContextJson(stateProjection);
  assert.equal(contextHost.childNodes.length, 0, "hidden context manager stays lazy");
  contextDetails.open = true;
  context.setContextManagerOpen(true);
  assert.ok(contextHost.firstElementChild.classList.contains("rn-json-viewer"));
  assert.match(contextHost.textContent, /<\/script><img onerror=1>/);
  button(contextHost, "Копировать всё").click();
  await settle();
  assert.equal(copied.at(-1), expectedProjection);
  context.setContextManagerOpen(false);
  assert.equal(contextHost.childNodes.length, 0, "closing manager releases viewer DOM");
  console.log("PASS context JSON viewer: state projection is explicit, safe and lazy");

  const raw = '{"request":9007199254740993123456789,"tail":';
  const snapshot = { rawTruncated: true };
  context.promptContextInspectorRawText = raw;
  context.promptContextInspectorSnapshot = snapshot;
  context.renderPromptContextRaw(snapshot);
  assert.ok(rawHost.firstElementChild.classList.contains("rn-json-viewer"));
  assert.equal(rawHost.firstElementChild.getAttribute("data-completeness"), "preview");
  assert.match(rawHost.textContent, /Позиция:/);
  assert.equal(rawButton.textContent, "JSON сокращён · скрыть");
  button(rawHost, "Копировать preview").click();
  await settle();
  assert.equal(copied.at(-1), raw, "materialized request preview copy stays exact");
  context.togglePromptContextRaw();
  assert.equal(rawHost.childNodes.length, 0);
  assert.equal(rawButton.textContent, "Показать сокращённый JSON");
  context.togglePromptContextRaw();
  assert.ok(rawHost.firstElementChild.classList.contains("rn-json-viewer"));
  context.promptContextInspectorRawText = "";
  context.renderPromptContextRaw({});
  assert.equal(rawHost.childNodes.length, 0);
  assert.ok(rawDetails.classList.contains("hidden"));
  console.log("PASS context JSON viewer: raw request preserves preview completeness and lifecycle");

  const toolResult = { status: "ok", data: { html: "<main>exact</main>" } };
  const expectedToolResult = JSON.stringify(toolResult, null, 2);
  context.renderToolRunJson(toolResult);
  assert.ok(toolHost.firstElementChild.classList.contains("rn-json-viewer"));
  assert.equal(toolHost.classList.contains("is-text"), false);
  button(toolHost, "Копировать всё").click();
  await settle();
  assert.equal(copied.at(-1), expectedToolResult);
  context.renderToolRunText("<b>tool failed</b>");
  assert.equal(toolHost.childNodes.length, 0);
  assert.equal(toolHost.textContent, "<b>tool failed</b>");
  assert.ok(toolHost.classList.contains("is-text"));
  console.log("PASS read-only JSON surfaces: tool result uses viewer and status/error stays inert text");

  const vbaModule = { name: "Module1", type: "StdModule", lineCount: 42 };
  context.renderVbaMetadata(vbaModule);
  assert.ok(vbaHost.firstElementChild.classList.contains("rn-json-viewer"));
  assert.match(vbaHost.textContent, /Module1/);
  context.renderVbaMetadata(null);
  assert.equal(vbaHost.childNodes.length, 0);
  console.log("PASS read-only JSON surfaces: VBA metadata uses shared viewer and clears on deselection");

  const actionOrder = [];
  const actionState = { tools: [{ Id: "demo" }], selectedToolIndex: 0, selectedToolComponentIndex: 0, activeChatId: "chat" };
  const actions = context.RNAssistantToolActions.create({
    state: actionState,
    syncSelected() {},
    setBusy() {},
    updateWriteState() {},
    reconcile: tools => tools,
    send() { return Promise.resolve({ result: { contractVersion: 1, status: "ok", effect: "verified_change" },
      tools: { type: "rnassistant.toolLibrary", contractVersion: 1, items: [{ Id: "demo" }] } }); },
    parseLibrary(value) { assert.equal(value.type, "rnassistant.toolLibrary"); return value.items; },
    renderTools() { actionOrder.push("renderTools"); },
    renderEditor() { actionOrder.push("renderEditor"); },
    setJsonOutput(value) { actionOrder.push("json:" + value.status); },
    setTextOutput(value) { actionOrder.push("text:" + value); },
    log() {}
  });
  await actions.uninstallVba();
  assert.deepEqual(actionOrder.slice(-2), ["renderEditor", "json:ok"], "editor refresh must not erase package result");
  console.log("PASS read-only JSON surfaces: package result renders after editor refresh");

  const indexSource = fs.readFileSync(path.join(__dirname, "../../web/index.html"), "utf8");
  const contextSource = fs.readFileSync(path.join(__dirname, "../../web/js/app-context.js"), "utf8");
  const inspectorSource = fs.readFileSync(path.join(__dirname, "../../web/js/app-context-inspector.js"), "utf8");
  assert.equal(/<pre[^>]+(?:contextBox|promptContextInspectorRawText|toolRunOutput|vbaMetaBox)/.test(indexSource), false);
  assert.equal(/contextBox"\)\.textContent/.test(contextSource), false);
  assert.equal(/promptContextInspectorRawText"\)\.textContent/.test(inspectorSource), false);
  console.log("PASS context JSON viewer: replaced plain-pre paths are absent");
  console.log("OK 7/7");
})().catch(error => { console.error(error); process.exitCode = 1; });
