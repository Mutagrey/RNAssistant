"use strict";

// No packages or browser required. Exercise the real projection/render functions
// with a minimal DOM; this does not validate WebView layout or controller delivery.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

class Element {
  constructor(tag) {
    this.tagName = tag;
    this.childNodes = [];
    this.attributes = {};
    this.open = false;
    this._text = "";
    this.innerHTML = "";
  }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  addEventListener() {}
  querySelectorAll() { return []; }
  set textContent(value) { this._text = String(value); }
  get textContent() { return this._text + this.innerHTML + this.childNodes.map(child => child.textContent).join(""); }
}

const context = vm.createContext({
  state: { messages: [] },
  document: { createElement: tag => new Element(tag) },
  RNAssistantAgentApproval: { create: () => ({ pendingActivity: () => null }) },
  currentActiveSend: () => null,
  hasActiveMessageEdit: () => false,
  markdown: text => text,
  enhanceMarkdown: () => {},
  appendAgentRunArtifacts: () => {},
  appendAgentDiagnosticMessage: () => {},
  agentDiagnosticText: item => item.message.Content || "",
  renderActivityNode: () => new Element("div"),
  activityPrimaryText: activity => activity.Title || "Tool"
});
context.window = context;
for (const file of ["app-utils.js", "app-agent-model.js", "app-agent.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), context, { filename: file });
}

function walk(node) { return [node].concat(node.childNodes.flatMap(walk)); }
function summary(health, ok = 0, error = 0, unknown = 0) {
  return { ExecutionHealth: health, ReadOk: 1, ReadError: 0, WriteOk: ok, WriteError: error, WriteUnknown: unknown };
}
function finalMessage(evidence) {
  return {
    Role: "assistant", Content: "Все изменения применены.", RunId: "run", Id: "final",
    ResponseProtocolVersion: 2, ResponseStatus: "completed", ExecutionSummary: evidence
  };
}
function renderFinal(evidence) {
  context.state.messages = [finalMessage(evidence)];
  assert.equal(context.canCollectAgentRunAt(0), true, "final-only replies enter the evidence projection");
  const run = context.collectAgentRun(0);
  assert.equal(run.items.length, 0);
  assert.equal(run.nextIndex, 1);
  return context.renderAgentRunArticle(run);
}
function assertVisibleEvidence(node, health) {
  const note = walk(node).find(item => item.attributes["data-runtime-health"]);
  assert.ok(note, "runtime evidence is rendered");
  assert.equal(note.attributes["data-runtime-health"], health);
  for (let parent = note.parentNode; parent; parent = parent.parentNode) {
    assert.notEqual(parent.tagName, "details", "runtime warning is outside collapsed trace");
  }
  const overview = note.parentNode.childNodes.find(item => item.tagName === "details");
  assert.ok(overview && !overview.open, "warning stays visible with collapsed overview");
  const finalSection = note.parentNode.childNodes.find(item => item.className === "agent-final-step");
  if (finalSection) {
    assert.ok(note.parentNode.childNodes.indexOf(note) < note.parentNode.childNodes.indexOf(finalSection));
    assert.equal(finalSection.textContent, "Все изменения применены.", "model text is preserved, not parsed or rewritten");
  }
  return note;
}

const tests = [
  ["write error overrides completed", () => {
    const node = renderFinal(summary("errors", 1, 1));
    const note = assertVisibleEvidence(node, "errors");
    assert.equal(note.attributes.role, "alert");
    assert.match(note.textContent, /Нельзя считать все изменения применёнными/);
    assert.match(note.textContent, /успешно — 1, ошибка — 1/);
    assert.match(walk(node).find(item => item.className === "agent-run-history-title").textContent, /содержит ошибки/);
  }],
  ["unknown overrides error and later success", () => {
    const node = renderFinal(summary("unknown", 1, 1, 1));
    assert.match(assertVisibleEvidence(node, "unknown").textContent, /Требуется проверка/);
    assert.match(walk(node).find(item => item.className === "agent-run-history-title").textContent, /не определён/);
  }],
  ["no write is an ordinary response", () => {
    const node = renderFinal(summary("clean"));
    assert.match(assertVisibleEvidence(node, "clean").textContent, /Подтверждённых изменений нет/);
    assert.equal(walk(node).find(item => item.className === "agent-run-history-title").textContent, "Ответ получен");
  }],
  ["successful write keeps its count", () => {
    const note = assertVisibleEvidence(renderFinal(summary("clean", 1)), "clean");
    assert.equal(note.attributes.role, "status");
    assert.match(note.textContent, /успешно — 1/);
  }],
  ["legacy or malformed evidence never becomes clean", () => {
    for (const evidence of [null, { ExecutionHealth: "completed" }, summary("clean", -1)]) {
      assert.match(assertVisibleEvidence(renderFinal(evidence), "unknown").textContent, /нет runtime summary/);
    }
  }],
  ["cancelled activity retains unknown without a final answer", () => {
    const message = { Role: "assistant", RunId: "run", ExecutionSummary: summary("unknown", 0, 0, 1),
      Activity: { Kind: "diagnostic", Status: "cancelled", Title: "Cancelled", ResultMessage: "Запрос отменён." } };
    const items = [{ message, index: 0, activity: message.Activity }];
    const evidence = context.agentRunExecutionSummary(items, null);
    const stats = context.agentRunStats(items, false, "", evidence);
    assert.equal(stats.lifecycleStatus, "cancelled");
    assert.equal(stats.status, "unknown");
    assertVisibleEvidence(context.renderAgentRunArticle({ items }), "unknown");
  }],
  ["camelCase bridge shape projects the same health", () => {
    const value = context.messageExecutionSummary({ executionSummary: {
      executionHealth: "errors", readOk: 2, readError: 1, writeOk: 0, writeError: 0, writeUnknown: 0
    } });
    assert.equal(value.executionHealth, "errors");
    assert.equal(value.readError, 1);
    const stats = context.agentRunStats([], true, "completed", value);
    assert.equal(stats.lifecycleStatus, "completed");
    assert.equal(stats.status, "failed");
  }],
  ["recovery without a summary cannot inherit earlier clean evidence", () => {
    const items = [
      { message: { ExecutionSummary: summary("clean", 1) } },
      { message: { Activity: { Kind: "diagnostic", Status: "interrupted_unknown" } } }
    ];
    assert.equal(context.agentRunExecutionSummary(items, null), null);
  }]
];

for (const [name, test] of tests) {
  test();
  process.stdout.write("PASS completion guard UI: " + name + "\n");
}
process.stdout.write("OK " + tests.length + "/" + tests.length + "\n");
