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
    this.tagName = String(tag).toLowerCase();
    this.className = ""; this.classList = new ClassList(this);
    this.childNodes = []; this.parentNode = null; this.attributes = {}; this.handlers = {};
    this.dataset = {}; this.style = { setProperty() {} }; this.value = ""; this.checked = false;
    this.disabled = false; this.open = false; this.scrollTop = 0; this._text = "";
  }
  get children() { return this.childNodes; }
  get firstElementChild() { return this.childNodes[0] || null; }
  get childElementCount() { return this.childNodes.length; }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  removeChild(child) { this.childNodes.splice(this.childNodes.indexOf(child), 1); child.parentNode = null; return child; }
  replaceChildren(...children) { this.childNodes.forEach(child => { child.parentNode = null; }); this.childNodes = []; children.forEach(child => this.appendChild(child)); }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  getAttribute(name) { return this.attributes[name]; }
  addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); }
  dispatch(name, event = {}) { (this.handlers[name] || []).forEach(handler => handler(Object.assign({ preventDefault() {}, stopPropagation() {}, key: "" }, event))); }
  click() { if (!this.disabled) this.dispatch("click"); }
  select() {}
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  querySelectorAll(selector) {
    const matches = node => {
      if (selector === "details[open]") return node.tagName === "details" && node.open;
      if (selector.startsWith(".")) return node.classList.contains(selector.slice(1));
      return node.tagName === selector.toLowerCase();
    };
    const found = [];
    const walk = node => node.childNodes.forEach(child => { if (matches(child)) found.push(child); walk(child); });
    walk(this);
    return found;
  }
  set textContent(value) { this._text = String(value); this.replaceChildren(); }
  get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(""); }
}

const page = fs.readFileSync(path.join(__dirname, "../../web/index.html"), "utf8");
const ids = new Set(Array.from(page.matchAll(/\bid="([^"]+)"/g), match => match[1]));
const elements = new Map();
const get = id => {
  assert.ok(ids.has(id), "missing shipped element #" + id);
  if (!elements.has(id)) elements.set(id, new Element("div"));
  return elements.get(id);
};
const panel = new Element("section"); panel.className = "trajectory-panel";
const body = new Element("body");
const clipboard = [];
let payloadResponse = null;
const rawResponse = {
  View: "raw", TotalEvents: 1, TotalMatches: 1, HasMore: false,
  Events: [{
    Sequence: 32, EventId: "evt-32", CreatedUtc: "2026-08-29T10:00:00Z", Type: "agent.response.rejected",
    RunId: "run-1", TurnId: "turn-1", StepId: "step-1", Hash: "hash-32", PreviousHash: "hash-31",
    HashAlgorithm: "sha256", DataJson: '{"dup":9007199254740993123456789,"dup":"</script><img onerror=1>"}', DataTruncated: false,
    PayloadByteLength: 5000, PayloadSha256: "blob-1", PayloadContentType: "application/json",
    SourceEventSeqs: [31, 32], SourceEventIds: ["evt-31", "evt-32"], ToolCallIds: [], ArtifactIds: [],
    ResourceRefs: [{ Uri: "rna://chat/blob", Revision: "sha256:blob-1" }], Statuses: ["rejected"]
  }]
};
let trajectoryResponse = rawResponse;

const context = vm.createContext({
  state: { activeChatId: "chat-1" },
  $: get,
  document: {
    body,
    createElement: tag => new Element(tag),
    querySelector: selector => selector === ".trajectory-panel" ? panel : null,
    querySelectorAll: () => [],
    execCommand: () => true
  },
  navigator: { clipboard: { writeText(text) { clipboard.push(String(text)); return Promise.resolve(); } } },
  send(action) {
    if (action === "getChatTrajectory") return Promise.resolve(trajectoryResponse);
    if (action === "getChatEventPayload") return Promise.resolve(payloadResponse);
    throw new Error("Unexpected bridge action: " + action);
  },
  setDiagnosticsTab() {},
  log() {},
  URL: { createObjectURL() { return "blob:test"; }, revokeObjectURL() {} },
  Blob: function () {},
  confirm: () => false,
  alert() {},
  RNAssistantVbaDiff: { render() {}, format() { return []; } }
});
context.window = context;
get("trajectoryViewInput").value = "raw";
get("trajectoryExportRedactionInput").value = "metadata";

for (const file of ["app-utils.js", "app-viewer-registry.js", "app-json-viewer.js", "app-trajectory.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), context, { filename: file });
}

function settle() { return new Promise(resolve => setImmediate(() => setImmediate(resolve))); }
function button(root, text) { return root.querySelectorAll("button").find(node => node.textContent === text); }

(async function () {
  context.bindTrajectoryActions();
  get("refreshTrajectoryButton").click();
  await settle();

  const dataHost = get("trajectoryEventData");
  assert.ok(dataHost.firstElementChild.classList.contains("rn-json-viewer"));
  assert.match(dataHost.textContent, /повтор 1\/2/);
  assert.match(dataHost.textContent, /9007199254740993123456789/);
  assert.match(dataHost.textContent, /<\/script><img onerror=1>/);
  assert.equal(get("trajectoryEvidenceDetails").classList.contains("hidden"), false);
  button(get("trajectoryEvidenceData"), "Форматированный").click();
  assert.match(get("trajectoryEvidenceData").textContent, /evt-31/);
  assert.match(get("trajectoryEvidenceData").textContent, /blob-1/);
  button(dataHost, "Копировать всё").click();
  await settle();
  assert.equal(clipboard.at(-1), rawResponse.Events[0].DataJson);
  console.log("PASS trajectory JSON viewer: raw event keeps exact tokens and separate source evidence");

  payloadResponse = {
    EventId: "evt-32", ContentType: "application/json", Text: '{"html":"<div>unfinished', TextTruncated: true
  };
  get("loadTrajectoryPayloadButton").click();
  await settle();
  const payloadHost = get("trajectoryEventPayload");
  assert.ok(payloadHost.firstElementChild.classList.contains("rn-json-viewer"));
  assert.equal(payloadHost.firstElementChild.getAttribute("data-completeness"), "preview");
  assert.match(payloadHost.textContent, /ограниченный preview/i);
  assert.match(payloadHost.textContent, /<div>unfinished/);
  console.log("PASS trajectory JSON viewer: truncated CAS JSON remains an explicit raw preview");

  payloadResponse = {
    EventId: "evt-32", ContentType: "text/html; charset=utf-8", Text: "<main onclick=evil()>safe</main>", TextTruncated: false
  };
  get("loadTrajectoryPayloadButton").click();
  await settle();
  assert.ok(payloadHost.firstElementChild.classList.contains("trajectory-text-viewer"));
  assert.equal(payloadHost.querySelector("pre").textContent, payloadResponse.Text);
  button(payloadHost, "Копировать всё").click();
  await settle();
  assert.equal(clipboard.at(-1), payloadResponse.Text);
  console.log("PASS trajectory JSON viewer: non-JSON payload stays inert text with exact copy");

  const trajectorySource = fs.readFileSync(path.join(__dirname, "../../web/js/app-trajectory.js"), "utf8");
  assert.equal(/function\s+prettyJson\b/.test(trajectorySource), false);
  assert.equal(/id="trajectoryEventData"[^>]*<\/pre>/.test(page), false);
  console.log("PASS trajectory JSON viewer: replaced diagnostics pretty/pre path is removed");

  trajectoryResponse = { View: "raw", TotalEvents: 0, TotalMatches: 0, HasMore: false, Events: [] };
  get("refreshTrajectoryButton").click();
  await settle();
  assert.equal(dataHost.childNodes.length, 0);
  assert.equal(get("trajectoryEvidenceDetails").classList.contains("hidden"), true);
  assert.equal(payloadHost.classList.contains("hidden"), true);
  console.log("PASS trajectory JSON viewer: refresh cleanup destroys stale detail viewers");
  console.log("OK 5/5");
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
