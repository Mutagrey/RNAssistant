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
  toggle(name, force) {
    const values = this.values();
    const enabled = force === undefined ? !values.has(name) : !!force;
    if (enabled) values.add(name); else values.delete(name);
    this.write(values);
    return enabled;
  }
  contains(name) { return this.values().has(name); }
}

class Element {
  constructor(tag, document) {
    this.tagName = String(tag).toLowerCase();
    this.ownerDocument = document;
    this.className = "";
    this.classList = new ClassList(this);
    this.childNodes = [];
    this.handlers = {};
    this.attributes = {};
    this.disabled = false;
    this.open = false;
    this._text = "";
  }
  appendChild(child) { this.childNodes.push(child); return child; }
  replaceChildren(...children) { this.childNodes = []; children.forEach(child => this.appendChild(child)); }
  addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); }
  click() { if (!this.disabled) (this.handlers.click || []).forEach(handler => handler({ target: this })); }
  change() { if (!this.disabled) (this.handlers.change || []).forEach(handler => handler({ target: this })); }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  getAttribute(name) { return this.attributes[name]; }
  focus() { this.ownerDocument.activeElement = this; }
  set textContent(value) { this._text = String(value); this.childNodes = []; }
  get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(""); }
}

class FakeDocument {
  constructor() { this.nodes = []; this.ids = {}; this.handlers = {}; this.activeElement = null; }
  createElement(tag) { const node = new Element(tag, this); this.nodes.push(node); return node; }
  register(id, tag = "div") { const node = this.createElement(tag); node.id = id; this.ids[id] = node; return node; }
  getElementById(id) { return this.ids[id] || null; }
  querySelectorAll(selector) {
    if (!selector.startsWith(".")) return [];
    return this.nodes.filter(node => node.classList.contains(selector.slice(1)));
  }
  addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); }
}

function runState(status, currentStepId) {
  return {
    runId: "run-shell",
    packId: "common.ui-shell",
    packRevision: "1",
    packSha256: "a".repeat(64),
    host: "Excel",
    productVersion: "16.1.0-dev",
    buildCommit: "unavailable",
    channel: "development",
    suite: "quick",
    status,
    currentStepId,
    canResume: status === "awaiting_user",
    hasDurableTerminal: status === "passed",
    reportTruncated: false,
    steps: [
      { stepId: "preflight", kind: "precondition", outcome: "passed", evidenceStrength: "none", actualJson: "{\"runner\":\"reachable\"}" },
      { stepId: "acknowledge", kind: "userAction", outcome: status === "awaiting_user" ? "awaiting_user" : "passed", evidenceStrength: status === "awaiting_user" ? "none" : "manual" },
      { stepId: "verify", kind: "assertion", outcome: status === "passed" ? "passed" : "not_run", evidenceStrength: status === "passed" ? "automatic" : "none", expectedJson: status === "passed" ? "{\"ok\":true}" : null, actualJson: status === "passed" ? "{\"ok\":true}" : null },
      { stepId: "cleanup", kind: "cleanup", outcome: status === "passed" ? "passed" : "not_run", evidenceStrength: "none" }
    ]
  };
}

const document = new FakeDocument();
[
  ["qualificationCenterOverlay"], ["qualificationCenterDialog"], ["closeQualificationCenterButton", "button"],
  ["qualificationCenterStatus"], ["qualificationPackList"], ["qualificationSuiteSelect", "select"], ["qualificationRunTitle"],
  ["qualificationRunDescription"], ["qualificationRunStatus"], ["qualificationProvenance"],
  ["qualificationUserInstruction"], ["qualificationSteps"], ["startQualificationButton", "button"],
  ["continueQualificationButton", "button"], ["cancelQualificationButton", "button"],
  ["repeatQualificationButton", "button"], ["openQualificationJournalButton", "button"],
  ["copyQualificationReportButton", "button"], ["qualificationReportStatus"],
  ["openQualificationCenterButton", "button"]
].forEach(([id, tag]) => document.register(id, tag));
document.ids.qualificationCenterOverlay.className = "qualification-overlay hidden";

const pack = {
  id: "common.ui-shell", revision: "1", sha256: "a".repeat(64), title: "Qualification Center shell",
  description: "Shell only", suite: "quick", workspacePolicy: "read-only", available: true,
  steps: [
    { id: "preflight", title: "Preflight", kind: "precondition" },
    { id: "acknowledge", title: "Manual", kind: "userAction", instructionKey: "qualification.shell.acknowledge" },
    { id: "verify", title: "Verify", kind: "assertion" },
    { id: "cleanup", title: "Cleanup", kind: "cleanup" }
  ]
};
const requests = [];
const copied = [];
const mounts = [];
let journal = null;
const state = { activeChatId: "source-chat" };
const context = vm.createContext({
  window: null,
  document,
  state,
  $: id => document.getElementById(id),
  send(type, payload) {
    requests.push({ type, payload });
    if (type === "getQualificationCatalog") return Promise.resolve({ schemaVersion: 1, host: "Excel", suite: payload.suite, packs: payload.suite === "quick" ? [pack] : [], missingCoverage: [] });
    if (type === "getQualificationRun") return Promise.resolve({ schemaVersion: 1, chat: { activeChatId: state.activeChatId }, run: null });
    if (type === "startQualification") return Promise.resolve({ schemaVersion: 1, chat: { activeChatId: "qualification-chat" }, run: runState("awaiting_user", "acknowledge") });
    if (type === "advanceQualification") return Promise.resolve({ schemaVersion: 1, chat: { activeChatId: "qualification-chat" }, run: runState("passed", null) });
    return Promise.reject(new Error("unexpected request"));
  },
  applyChatState(chat) { state.activeChatId = chat.activeChatId; },
  Promise,
  JSON,
  Array,
  String,
  Error
});
context.window = context;
context.window.copyTextResult = text => { copied.push(text); return Promise.resolve(); };
context.window.openRunJournal = options => { journal = options; };
context.window.RNAssistantViewerRegistry = {
  has(kind) { return kind === "json"; },
  mount(kind, host, options) { mounts.push({ kind, host, options }); host.textContent = options.text; return {}; },
  unmount(host) { host.replaceChildren(); }
};

const sourcePath = path.join(__dirname, "../../web/js/app-qualification.js");
const source = fs.readFileSync(sourcePath, "utf8");
vm.runInContext(source, context, { filename: "app-qualification.js" });

async function flush() { await Promise.resolve(); await Promise.resolve(); await new Promise(resolve => setImmediate(resolve)); }

(async function run() {
  context.bindQualificationActions();
  await context.openQualificationCenter();
  assert.deepEqual(requests.slice(0, 2).map(item => item.type), ["getQualificationRun", "getQualificationCatalog"]);
  assert.equal(document.ids.qualificationCenterOverlay.classList.contains("hidden"), false);
  assert.match(document.ids.qualificationRunDescription.textContent, /Shell only/);
  document.ids.qualificationSuiteSelect.value = "release";
  document.ids.qualificationSuiteSelect.change();
  await flush();
  assert.equal(requests.at(-1).payload.suite, "release");
  document.ids.qualificationSuiteSelect.value = "quick";
  document.ids.qualificationSuiteSelect.change();
  await flush();
  console.log("PASS qualification center: empty/diagnostics entry opens catalog and restores active chat run");

  document.ids.startQualificationButton.click();
  await flush();
  assert.equal(requests.at(-1).type, "startQualification");
  assert.equal(requests.at(-1).payload.packId, "common.ui-shell");
  assert.equal(state.activeChatId, "qualification-chat");
  assert.equal(document.ids.continueQualificationButton.classList.contains("hidden"), false);
  assert.match(document.ids.qualificationUserInstruction.textContent, /Подтвердить и продолжить/);
  console.log("PASS qualification center: start creates dedicated chat and renders explicit manual checkpoint");

  document.ids.continueQualificationButton.click();
  await flush();
  assert.equal(requests.at(-1).type, "advanceQualification");
  assert.equal(requests.at(-1).payload.stepId, "acknowledge");
  assert.equal(requests.at(-1).payload.acknowledged, true);
  assert.equal(document.ids.qualificationRunStatus.textContent, "Пройден");
  assert.ok(mounts.some(item => item.options.text === "{\"ok\":true}"));
  console.log("PASS qualification center: server-owned terminal status and shared JSON evidence viewer are rendered");

  document.ids.copyQualificationReportButton.click();
  await flush();
  assert.equal(JSON.parse(copied.at(-1)).run.status, "passed");
  document.ids.openQualificationJournalButton.click();
  assert.equal(journal.chatId, "qualification-chat");
  assert.equal(journal.runId, "run-shell");
  assert.equal(document.ids.qualificationCenterOverlay.classList.contains("hidden"), true);
  console.log("PASS qualification center: bounded report copy and exact run journal navigation preserve source IDs");

  context.RNAssistantQualificationCenter.state.run = { runId: "large", packId: "common.ui-shell", status: "failed", steps: [{ actualJson: "x".repeat(800000) }] };
  assert.throws(() => context.RNAssistantQualificationCenter.reportJson(), /exceeds the UI limit/);
  const messages = fs.readFileSync(path.join(__dirname, "../../web/js/app-messages.js"), "utf8");
  const composer = fs.readFileSync(path.join(__dirname, "../../web/js/app-chat-composer.js"), "utf8");
  const page = fs.readFileSync(path.join(__dirname, "../../web/index.html"), "utf8");
  assert.match(messages, /chat-empty-qualification/);
  assert.match(messages, /openQualificationCenter/);
  assert.ok(page.indexOf("app-json-viewer.js") < page.indexOf("app-qualification.js"));
  assert.match(composer, /activeQualificationRun/);
  assert.match(composer, /Продолжите проверку через Qualification Center/);
  assert.equal(source.includes("innerHTML"), false);
  console.log("PASS qualification center: distinct empty-chat action, bounded safe rendering and script order are enforced");
  console.log("OK 5/5");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
