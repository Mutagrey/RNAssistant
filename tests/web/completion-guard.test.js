"use strict";

// No packages or browser required. Exercise the shipped typed projection/render
// functions with a minimal DOM; this is not WebView delivery/layout validation.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

class Element {
  constructor(tag) {
    this.tagName = tag; this.childNodes = []; this.attributes = {}; this.open = false;
    this._text = ""; this.innerHTML = ""; this.className = "";
  }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  addEventListener() {}
  querySelectorAll() { return []; }
  set textContent(value) { this._text = String(value); }
  get textContent() { return this._text + this.innerHTML + this.childNodes.map(child => child.textContent).join(""); }
}

const context = vm.createContext({
  state: { messages: [], activeRunViewState: null },
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
for (const file of ["app-utils.js", "app-run-view-state.js", "app-agent-model.js", "app-agent.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), context, { filename: file });
}

function walk(node) { return [node].concat(node.childNodes.flatMap(walk)); }
function view(health, options = {}) {
  return {
    RunId: "run", TurnId: "turn", Narrative: "Все изменения применены.",
    Lifecycle: options.lifecycle || "completed", ExecutionHealth: health,
    SuccessfulReads: options.reads || 0, VerifiedWrites: options.verified || 0,
    NoChangeWrites: options.noChange || 0, UnverifiedWrites: options.unverified || 0,
    FailedCalls: options.failed || 0, UnknownEffects: options.unknown || 0,
    PendingConfirmation: options.pending || null, Reason: options.reason || null,
    CurrentAction: options.action || "", StartedUtc: "2026-08-30T10:00:00Z"
  };
}
function finalMessage(runViewState) {
  return { Role: "assistant", Content: "Все изменения применены.", RunId: "run", Id: "final", RunViewState: runViewState };
}
function renderFinal(runViewState) {
  context.state.messages = [finalMessage(runViewState)];
  assert.equal(context.canCollectAgentRunAt(0), true, "final-only replies enter the typed run projection");
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
  const finalSection = note.parentNode.childNodes.find(item => item.className === "agent-final-step");
  if (finalSection) {
    assert.ok(note.parentNode.childNodes.indexOf(note) < note.parentNode.childNodes.indexOf(finalSection));
    assert.equal(finalSection.textContent, "Все изменения применены.", "narrative is preserved, not parsed or rewritten");
  }
  return note;
}

const tests = [
  ["failed calls override completed lifecycle", () => {
    const node = renderFinal(view("errors", { verified: 1, failed: 1 }));
    const note = assertVisibleEvidence(node, "errors");
    assert.equal(note.attributes.role, "alert");
    assert.match(note.textContent, /ошибки вызовов — 1/);
    assert.match(walk(node).find(item => item.className === "agent-run-history-title").textContent, /содержит ошибки/);
  }],
  ["legacy unverified write is explained once without duplicate unknown count", () => {
    const node = renderFinal(view("unknown", { unverified: 1, failed: 1, unknown: 1 }));
    const text = assertVisibleEvidence(node, "unknown").textContent;
    assert.match(text, /legacy-handler без read-back/);
    assert.match(text, /legacy без read-back — 1/);
    assert.match(text, /прочие неизвестные эффекты — 0/);
    assert.match(walk(node).find(item => item.className === "agent-run-history-title").textContent, /не определён/);
  }],
  ["no write is an ordinary response", () => {
    const node = renderFinal(view("clean", { reads: 1 }));
    assert.match(assertVisibleEvidence(node, "clean").textContent, /Подтверждённых изменений нет/);
    assert.equal(walk(node).find(item => item.className === "agent-run-history-title").textContent, "Ответ получен");
  }],
  ["verified and no-change writes remain distinct", () => {
    const note = assertVisibleEvidence(renderFinal(view("clean", { verified: 1, noChange: 2 })), "clean");
    assert.equal(note.attributes.role, "status");
    assert.match(note.textContent, /изменения — 1, без изменения — 2/);
  }],
  ["legacy flat or malformed projection is never promoted", () => {
    assert.equal(context.RNAssistantRunViewState.fromMessage({ ExecutionSummary: { ExecutionHealth: "clean", WriteOk: 9 } }), null);
    assert.equal(context.RNAssistantRunViewState.normalize(view("completed")), null);
    assert.equal(context.RNAssistantRunViewState.normalize(view("clean", { verified: -1 })), null);
  }],
  ["cancelled boundary retains unknown health", () => {
    const message = { Role: "assistant", RunId: "run", RunViewState: view("unknown", { lifecycle: "cancelled", unknown: 1 }),
      Activity: { Kind: "diagnostic", Status: "cancelled", Title: "Cancelled", ResultMessage: "Запрос отменён." } };
    const items = [{ message, index: 0, activity: message.Activity }];
    const runViewState = context.agentRunViewState(items, null);
    const stats = context.agentRunStats(items, false, runViewState);
    assert.equal(stats.lifecycleStatus, "cancelled");
    assert.equal(stats.status, "unknown");
    assertVisibleEvidence(context.renderAgentRunArticle({ items }), "unknown");
  }],
  ["camelCase bridge shape projects the same state", () => {
    const camel = Object.fromEntries(Object.entries(view("errors", { reads: 2, failed: 1 }))
      .map(([key, item]) => [key.charAt(0).toLowerCase() + key.slice(1), item]));
    const normalized = context.RNAssistantRunViewState.normalize(camel);
    assert.equal(normalized.executionHealth, "errors");
    assert.equal(normalized.successfulReads, 2);
    const stats = context.agentRunStats([], true, normalized);
    assert.equal(stats.lifecycleStatus, "completed");
    assert.equal(stats.status, "failed");
  }],
  ["recovery without typed state cannot inherit earlier clean state", () => {
    const items = [
      { message: { RunViewState: view("clean", { verified: 1 }) } },
      { message: { Activity: { Kind: "diagnostic", Status: "interrupted_unknown" } } }
    ];
    assert.equal(context.agentRunViewState(items, null), null);
  }]
];

for (const [name, test] of tests) {
  test();
  process.stdout.write("PASS run view UI: " + name + "\n");
}
process.stdout.write("OK " + tests.length + "/" + tests.length + "\n");
