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
  constructor(tag) {
    this.tagName = String(tag).toLowerCase();
    this.className = "";
    this.classList = new ClassList(this);
    this.childNodes = [];
    this.parentNode = null;
    this.attributes = {};
    this.dataset = {};
    this.handlers = {};
    this.type = "";
    this.title = "";
    this.disabled = false;
    this._text = "";
  }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  replaceChildren(...children) { this.childNodes.forEach(child => { child.parentNode = null; }); this.childNodes = []; children.forEach(child => this.appendChild(child)); }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  getAttribute(name) { return Object.prototype.hasOwnProperty.call(this.attributes, name) ? this.attributes[name] : null; }
  addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  querySelectorAll(selector) {
    const matches = node => selector.startsWith(".") ? node.classList.contains(selector.slice(1)) : node.tagName === selector.toLowerCase();
    const result = [];
    const walk = node => node.childNodes.forEach(child => { if (matches(child)) result.push(child); walk(child); });
    walk(this);
    return result;
  }
  set textContent(value) { this._text = String(value); this.replaceChildren(); }
  get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(""); }
}

const root = path.join(__dirname, "../..");
const context = vm.createContext({
  document: {
    createElement: tag => new Element(tag),
    getElementById() { return null; }
  },
  state: {
    activeChatId: "chat-a",
    activeHtmlArtifactId: "html-r2",
    artifacts: [
      {
        id: "chart-r1",
        kind: "chart",
        title: "Динамика продаж",
        revision: 1,
        resourceUri: "rna://chat/chat-a/artifact/chart-r1/revision/1"
      },
      {
        id: "html-r2",
        kind: "html_workspace",
        title: "HTML bound data: sampleData",
        revision: 2,
        resourceUri: "rna://chat/chat-a/artifact/html-r2/revision/2"
      }
    ],
    artifactLibrary: { heads: [], removedResourceUris: [] },
    htmlWorkspace: { files: [], dataSources: [] }
  },
  switchTab() {}
});
context.window = context;
context.$ = () => null;
context.renderHtmlWorkspace = () => {};
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-artifacts.js"), "utf8"), context,
  { filename: "app-artifacts.js" });

function ref(id, revision) {
  return { uri: "rna://chat/chat-a/artifact/" + id + "/revision/" + revision };
}

{
  const parent = new Element("div");
  context.appendAgentRunResourceCards(parent, [
    { message: { resourceRefs: [ref("chart-r1", 1), ref("html-r2", 2)] } }
  ], null);
  assert.equal(parent.querySelectorAll(".chat-artifact-card").length, 1);
  assert.match(parent.textContent, /Динамика продаж/);
  assert.doesNotMatch(parent.textContent, /HTML bound data/);
  assert.equal(parent.querySelector(".chat-resource-bundle"), null);
  console.log("PASS chat resource cards: chart plus support HTML workspace renders as one visible chart resource");
}

{
  const parent = new Element("div");
  context.appendAgentRunResourceCards(parent, [
    { message: { resourceRefs: [ref("html-r2", 2)] } }
  ], null);
  assert.equal(parent.querySelectorAll(".chat-artifact-card").length, 1);
  assert.match(parent.textContent, /HTML bound data: sampleData/);
  console.log("PASS chat resource cards: HTML-only run still exposes the workspace artifact");
}

{
  const old = { id: "html-r1", kind: "html_workspace", title: "index.html", revision: 1 };
  const current = { id: "html-r3", kind: "html_workspace", title: "index.html", revision: 3 };
  const independent = { id: "other-html", kind: "html_workspace", title: "index.html", revision: 1 };
  context.state.artifacts.push(old, current, independent);
  context.state.artifactLibrary.heads = [{ artifactId: "html-r3", logicalId: "workspace-one",
    resourceClass: "versioned_aggregate", history: [
      { artifactId: "html-r1", revision: 1 }, { artifactId: "html-r3", revision: 3 }
    ] }];
  const parent = new Element("div");
  const messages = [current, old, independent].map(item => ({ message: { resourceRefs: [ref(item.id, item.revision)] } }));
  context.appendAgentRunResourceCards(parent, messages, null);
  assert.deepEqual(parent.querySelectorAll(".chat-artifact-card").map(node => node.dataset.artifactId),
    ["html-r3", "other-html"]);
  assert.equal(messages[1].message.resourceRefs[0].uri, ref(old.id, 1).uri);
  assert.match(parent.textContent, /Только этот чат/);
  current.documentScoped = true;
  assert.equal(context.RNAssistantArtifactVisuals.scopeLabel(current), "Общий для чатов документа");
  console.log("PASS chat resource cards: logical versions collapse, independent same-name files and pinned refs survive");
}

console.log("OK 3/3");
