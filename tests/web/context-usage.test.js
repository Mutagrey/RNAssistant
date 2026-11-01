"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

function fixture() {
  const nodes = new Map();
  function element() {
    const classes = new Set();
    return { textContent: "", children: [], dataset: {}, open: false, attributes: {},
      style: { setProperty(name, value) { this[name] = value; } },
      setAttribute(name, value) { this.attributes[name] = value; },
      classList: { add: name => classes.add(name), remove: name => classes.delete(name),
        contains: name => classes.has(name),
        toggle: (name, force) => force ? classes.add(name) : classes.delete(name) },
      appendChild(child) { this.children.push(child); },
      replaceChildren() { this.children = []; },
      get childElementCount() { return this.children.length; }
    };
  }
  const node = id => { if (!nodes.has(id)) nodes.set(id, element()); return nodes.get(id); };
  const c = vm.createContext({ console, TextEncoder, $: node,
    document: { createElement: element },
    state: { activeChatId: "chat-a", messages: [], settings: {} },
    contextNotes: () => [], activeChatModel: () => "model", settingsModel: () => "model",
    activeChatSummary: () => ({ revision: 1 })
  });
  c.window = c;
  for (const file of ["app-utils.js", "app-chat-state.js", "app-context-meter.js", "app-agent-model.js", "app-messages.js"])
    vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), c);
  return { c, node };
}

const f = fixture(), c = f.c;
const usage = { lastPromptTokens: 1234, lastCompletionTokens: 56, lastTotalTokens: 1290 };
const snapshot = { ...usage, chatId: "chat-a", sessionRevision: 1,
  usedTokens: 9999, admissionTokens: 11111, inputLimitTokens: 30000, estimated: true,
  sections: [{ id: "history", included: true, tokens: 9999, items: [] }] };
c.state.contextUsage = { ...usage, usedTokens: 1234, actual: true };
c.state.messages = [{ Role: "assistant", PromptTokens: 321, CompletionTokens: 12, TotalTokens: 333 },
  { Role: "user", Content: "pending local draft", Local: true }];
c.renderContextMeter();
assert.ok(f.node("contextMeterSummary").textContent.includes(c.formatNumber(1234)));
console.log("PASS context usage: live counts override paged history, drafts and next-request estimates");

c.state.contextUsage = { actual: true, usedTokens: 777, lastPromptTokens: 777 };
assert.equal(c.lastModelUsage().prompt, 777, "exact request input precedes stale message usage");
c.state.contextUsage = { ...usage, actual: false, usedTokens: 9999 };
c.state.messages = [];
c.renderContextMeter();
assert.ok(f.node("contextMeterSummary").textContent.includes(c.formatNumber(1234)),
  "reloaded usage does not depend on the visible page");
console.log("PASS context usage: request and reload projections retain exact counts");
c.state.contextUsage = { ...usage, scope: "last_response", contextWindowTokens: 10000 };
c.renderContextMeter();
assert.match(f.node("contextMeterSummary").textContent, /Последний принятый ответ/);
assert.doesNotMatch(f.node("contextMeterSummary").textContent, /≈/);

for (const partial of [
  { lastPromptTokens: 0, lastCompletionTokens: 0, lastTotalTokens: 0 },
  { lastPromptTokens: null, lastCompletionTokens: 0, lastTotalTokens: 333 },
  { LastPromptTokens: 456, LastCompletionTokens: 0, LastTotalTokens: 456 },
  { lastPromptTokens: null, lastCompletionTokens: null, lastTotalTokens: null }
]) {
  c.state.contextUsage = { ...partial, usedTokens: 9999, actual: false };
  c.state.messages = [{ PromptTokens: 100, CompletionTokens: 10, TotalTokens: 110 }];
  c.renderContextMeter();
  assert.doesNotMatch(f.node("contextMeterSummary").textContent, /≈|undefined|NaN/);
  if (partial.lastTotalTokens === null) {
    assert.equal(f.node("contextMeterSummary").textContent, "API ещё не сообщил число входных токенов");
  } else if (partial.lastPromptTokens === null) {
    assert.equal(c.lastModelUsage().prompt, null, "total and estimates cannot become exact input");
  }
}
console.log("PASS context usage: zero, partial and absent usage are explicit");

c.state.contextUsage = { lastPromptTokens: 118000, lastCompletionTokens: 5000,
  contextWindowTokens: 258000, usedTokens: 200000, actual: false };
c.renderContextMeter();
assert.ok(Math.abs(Number(f.node("contextMeter").style["--context-meter-offset"]) - (100 - 118000 / 258000 * 100)) < .001);
assert.match(f.node("contextMeterSummary").textContent, /Последний ответ API:.*заполнено 46%/);
assert.ok(f.node("contextMeterSummary").textContent.includes(c.formatNumber(118000)));
assert.equal(f.node("contextMeter").dataset.level, "ok");
assert.match(f.node("contextMeter").attributes["aria-label"], /заполнено 46%/);

for (const [prompt, limit, level, offset] of [
  [0, 100, "ok", 100], [80, 100, "warn", 20], [100, 100, "danger", 0],
  [120, 100, "danger", 0], [null, 100, "unknown", 100], [12, 0, "unknown", 100]
]) {
  c.state.contextUsage = { LastPromptTokens: prompt, LastCompletionTokens: 10,
    LastTotalTokens: 130, ContextWindowTokens: limit, usedTokens: 77 };
  c.renderContextMeter();
  assert.equal(f.node("contextMeter").dataset.level, level);
  assert.equal(f.node("contextMeterWrap").hidden, false, "unknown usage remains accessible");
  assert.equal(Number(f.node("contextMeter").style["--context-meter-offset"]), offset);
  assert.doesNotMatch(f.node("contextMeterSummary").textContent, /NaN|undefined|Осталось -/);
  if (prompt === null) assert.match(f.node("contextMeterSummary").textContent, /API ещё не сообщил/);
  if (!limit) assert.match(f.node("contextMeterSummary").textContent, /размер окна неизвестен/);
}
console.log("PASS context usage: ring uses exact input, clamps capacity and keeps unknown usage explicit");

c.state.chatRuns = { "chat-a": { runId: "run-a" }, "chat-b": { runId: "run-b" } };
const dispatch = { chatId: "chat-a", runId: "run-a", purpose: "agent", requestId: "req-1",
  phase: "sending", estimatedPromptTokens: 80000, contextWindowTokens: 100000, outputLimitTokens: 10000 };
c.applyModelContextDiagnostics({ ...dispatch, phase: "preparing", estimatedPromptTokens: null });
assert.match(f.node("contextMeterSummary").textContent, /Готовлю контекст/);
c.applyModelContextDiagnostics(dispatch);
assert.match(f.node("contextMeterSummary").textContent, /Текущий запрос: вход ≈.*заполнено ≈ 80%/);
assert.match(f.node("contextMeterSummary").textContent, /Лимит ответа:.*не заполнение/);
assert.equal(c.lastModelUsage(), null, "a running estimate is not old API usage");
c.applyModelContextDiagnostics({ ...dispatch, phase: "completed", promptTokens: 81000, completionTokens: 1000, totalTokens: 82000 });
assert.equal(c.lastModelUsage().prompt, 81000);
assert.match(f.node("contextMeterSummary").textContent, /заполнено 81%/);
assert.doesNotMatch(f.node("contextMeterSummary").textContent, /≈/);
const completedSummary = f.node("contextMeterSummary").textContent;
for (const foreign of [{ chatId: "chat-b", runId: "run-b" }, { runId: "stale-run" }, { purpose: "compaction" }]) {
  c.applyModelContextDiagnostics({ ...dispatch, ...foreign, estimatedPromptTokens: 99999 });
  assert.equal(f.node("contextMeterSummary").textContent, completedSummary);
}
c.applyModelContextDiagnostics({ ...dispatch, requestId: "req-2", phase: "preparing", estimatedPromptTokens: null });
c.applyModelContextDiagnostics({ ...dispatch, phase: "completed", promptTokens: 99999 });
assert.match(f.node("contextMeterSummary").textContent, /Готовлю контекст/, "late completion cannot overwrite a new attempt");
c.applyModelContextDiagnostics({ ...dispatch, requestId: "req-2", phase: "completed", completionTokens: 0, totalTokens: 123 });
assert.equal(c.lastModelUsage().prompt, null, "partial usage never becomes exact input");
assert.match(f.node("contextMeterSummary").textContent, /вход ≈/);
c.state.chatRuns = {};
console.log("PASS context usage: request estimates, actual usage, repair and scoped diagnostics");

const accepted = { Role: "assistant", RunId: "r", ProtocolMessage: true,
  PromptTokens: 100, CompletionTokens: 10, TotalTokens: 110 };
let runUsage = c.chatRunTokenUsage([accepted, { ...accepted, RuntimeProjection: true, ProtocolMessage: false }], 0, 2);
assert.equal(runUsage.TotalTokens, 110, "terminal projection does not double-count the provider response");
assert.equal(runUsage.UsageIncomplete, false);
runUsage = c.chatRunTokenUsage([accepted, { Role: "assistant", RunId: "r", ProtocolMessage: true,
  AcceptedCallOrigin: { StepId: "step", ModelAttemptId: "attempt", CallIndex: 1 } }], 0, 2);
assert.equal(runUsage.TotalTokens, 110);
assert.equal(runUsage.UsageIncomplete, false, "silent extra calls in the same response do not imply missing usage");
runUsage = c.chatRunTokenUsage([accepted, { Role: "assistant", RunId: "r", ProtocolMessage: true }], 0, 2);
assert.equal(runUsage.TotalTokens, 110);
assert.equal(runUsage.UsageIncomplete, true);
assert.match(c.messageUsageText(runUsage), /неполные данные API/);
runUsage = c.chatRunTokenUsage([{ Role: "assistant", RunId: "r", ProtocolMessage: true }], 0, 1);
assert.match(c.messageUsageText(runUsage), /API не сообщил/);
console.log("PASS context usage: run totals expose missing usage without double-counting projections");
