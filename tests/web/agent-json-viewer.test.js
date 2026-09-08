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
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  querySelectorAll(selector) {
    const matches = node => {
      if (selector === "details[open]") return node.tagName === "details" && node.open;
      if (selector.startsWith(".")) return node.classList.contains(selector.slice(1));
      return node.tagName === selector.toLowerCase();
    };
    const result = [];
    const walk = node => node.childNodes.forEach(child => { if (matches(child)) result.push(child); walk(child); });
    walk(this); return result;
  }
  set textContent(value) { this._text = String(value); this.replaceChildren(); }
  get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(""); }
}

const body = new Element("body");
const copied = [];
const context = vm.createContext({
  document: {
    body,
    createElement: tag => new Element(tag),
    createTextNode: text => { const node = new Element("text"); node.textContent = text; return node; },
    execCommand: () => true
  },
  navigator: { clipboard: { writeText(text) { copied.push(String(text)); return Promise.resolve(); } } }
});
context.window = context;
for (const file of ["app-utils.js", "app-viewer-registry.js", "app-json-viewer.js", "app-agent-data.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), context, { filename: file });
}
context.activityDataJson = activity => activity.data;
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-chart-artifacts.js"), "utf8"), context, { filename: "app-chart-artifacts.js" });

function button(root, text) { return root.querySelectorAll("button").find(node => node.textContent === text); }
function settle() { return new Promise(resolve => setImmediate(resolve)); }

(async function () {
  const exact = '{"dup":9007199254740993123456789,"dup":"</script><img onerror=1>","arguments":{"html":"<main>full</main>"}}';
  const parent = new Element("div");
  context.appendArgumentsData(parent, exact);
  const details = parent.firstElementChild;
  const host = details.childNodes[1];
  assert.equal(details.open, false);
  assert.equal(host.childNodes.length, 0, "collapsed details do not create viewer DOM");
  details.open = true; details.dispatch("toggle");
  assert.ok(host.firstElementChild.classList.contains("rn-json-viewer"));
  assert.match(host.textContent, /повтор 1\/2/, "tree is the default");
  button(host, "Дерево").click();
  assert.match(host.textContent, /повтор 1\/2/);
  assert.match(host.textContent, /9007199254740993123456789/);
  assert.match(host.textContent, /<\/script><img onerror=1>/);
  button(host, "Копировать всё").click();
  await settle();
  assert.equal(copied.at(-1), exact);
  console.log("PASS agent JSON viewer: arguments use exact bounded shared viewer");

  details.open = false; details.dispatch("toggle");
  assert.equal(host.childNodes.length, 0, "collapse destroys mounted tree");
  details.open = true; details.dispatch("toggle");
  button(host, "Дерево").click();
  assert.match(host.textContent, /повтор 2\/2/);
  console.log("PASS agent JSON viewer: collapsed activity data releases and remounts lazy DOM");

  const invalidParent = new Element("div");
  context.appendActivityData(invalidParent, "Данные результата", '{"html":"unfinished');
  const invalidDetails = invalidParent.firstElementChild;
  invalidDetails.open = true; invalidDetails.dispatch("toggle");
  assert.match(invalidDetails.textContent, /Позиция:/);
  assert.match(invalidDetails.textContent, /unfinished/);
  console.log("PASS agent JSON viewer: invalid result remains raw without repair");

  context.navigator.clipboard.writeText = () => Promise.reject(new Error("denied"));
  await assert.rejects(Promise.resolve(context.copyTextResult("secret")), /denied/);
  context.navigator.clipboard = null;
  context.document.execCommand = () => false;
  await assert.rejects(Promise.resolve(context.copyTextResult("fallback")), /rejected/);
  const agentSource = fs.readFileSync(path.join(__dirname, "../../web/js/app-agent-data.js"), "utf8");
  const chartSource = fs.readFileSync(path.join(__dirname, "../../web/js/app-chart-artifacts.js"), "utf8");
  assert.equal(/JSON\.(parse|stringify)/.test(agentSource), false);
  assert.equal(/renderJson(Table|Object|Array|Value)|prettyJsonText|agent-data-table/.test(agentSource), false);
  assert.match(chartSource, /function tryParseChartJson/);
  assert.equal(/\btryParseJson\b/.test(chartSource), false);
  assert.equal(context.tryRenderChartArtifact({ data: "not-json" }), null);
  assert.equal(context.tryRenderChartArtifact({ data: '{"type":"not-a-chart"}' }), null);
  console.log("PASS agent JSON viewer: clipboard failures propagate and old renderer/cross-owner parser are removed");

  const chartPayload = JSON.stringify({
    type: "rnassistant.chart", title: "Sales",
    source: { workbook: "Book.xlsx", sheet: "Data", address: "A1:B2" },
    columns: [{ name: "Month", kind: "category" }, { name: "Sales", kind: "number" }],
    rows: [{ Month: "Jan", Sales: 10 }],
    config: { chartType: "line", x: "Month", series: ["Sales"] }
  });
  const refreshedPayload = JSON.stringify({
    type: "rnassistant.chart", title: "Sales",
    source: { workbook: "Book.xlsx", sheet: "Data", address: "A1:B3" },
    columns: [{ name: "Month", kind: "category" }, { name: "Sales", kind: "number" }],
    rows: [{ Month: "Feb", Sales: 20 }],
    config: { chartType: "line", x: "Month", series: ["Sales"] }
  });
  const bridgeCalls = [];
  const delayedSaves = [];
  let releaseRefresh;
  let renders = 0;
  context.state = {
    activeChatId: "chat-a",
    messages: [{ id: "message-a", activity: { dataJson: chartPayload } }]
  };
  context.echarts = {
    getInstanceByDom() { return null; },
    init() { return { clear() {}, setOption() {}, off() {}, on() {}, resize() {} }; }
  };
  context.setTimeout = (callback, delay) => {
    if (delay === 350) delayedSaves.push(callback); else callback();
    return delayedSaves.length;
  };
  context.clearTimeout = () => {};
  context.renderMessages = () => { renders += 1; };
  context.log = () => {};
  context.send = (method, payload) => {
    bridgeCalls.push({ method, payload: JSON.parse(JSON.stringify(payload)) });
    if (method === "runTool") {
      return new Promise(resolve => { releaseRefresh = resolve; });
    }
    return Promise.resolve({});
  };
  const chart = context.tryRenderChartArtifact(
    { data: chartPayload }, { messageId: "message-a" });
  button(chart, "По колонкам").click();
  context.state.activeChatId = "chat-b";
  context.state.messages = [];
  delayedSaves.shift()();
  await settle();
  assert.equal(bridgeCalls.find(call => call.method === "updateMessageActivityData").payload.chatId, "chat-a",
    "delayed chart save stays addressed to its source chat");

  context.state.activeChatId = "chat-a";
  button(chart, "Обновить").click();
  assert.equal(bridgeCalls.find(call => call.method === "runTool").payload.chatId, "chat-a",
    "chart refresh tool call stays addressed to its source chat");
  context.state.activeChatId = "chat-b";
  releaseRefresh({ Success: true, DataJson: refreshedPayload });
  await settle(); await settle();
  const updates = bridgeCalls.filter(call => call.method === "updateMessageActivityData");
  assert.equal(updates.at(-1).payload.chatId, "chat-a",
    "late chart refresh persists to its source chat");
  assert.equal(renders, 0, "late chart refresh cannot rewrite the newly active chat projection");
  console.log("PASS chart artifact: delayed save and refresh retain source chat identity");
  console.log("OK 5/5");
})().catch(error => { console.error(error); process.exitCode = 1; });
