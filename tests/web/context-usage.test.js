"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

function fixture() {
  const nodes = new Map();
  function element() {
    const classes = new Set();
    return { textContent: "", children: [], dataset: {}, open: false,
      style: { setProperty() {} }, setAttribute() {},
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
  for (const file of ["app-utils.js", "app-chat-state.js", "app-context-inspector.js"])
    vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), c);
  c.renderPromptContextRaw = () => {};
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
c.renderPromptContextInspector(snapshot);
assert.equal(f.node("contextMeterDetail").textContent, "↑" + c.formatNumber(1234) + " · ↓56");
assert.equal(f.node("promptContextInspectorUsage").textContent, "Вход " + c.formatNumber(1234) + " токенов");
assert.doesNotMatch(f.node("promptContextInspectorUsage").textContent, /≈/);
assert.match(f.node("promptContextInspectorLastUsage").textContent, /выход 56/);
assert.ok(f.node("promptContextInspectorLastUsage").textContent.includes(c.formatNumber(1290)));
assert.ok(f.node("promptContextInspectorEstimateUsage").textContent.includes("≈ " + c.formatNumber(9999)));
console.log("PASS context usage: live counts override paged history, drafts and next-request estimates");

c.state.contextUsage = { actual: true, usedTokens: 777, lastPromptTokens: 777 };
assert.equal(c.lastModelUsage().prompt, 777, "exact request input precedes stale message usage");
c.state.contextUsage = { ...usage, actual: false, usedTokens: 9999 };
c.state.messages = [];
c.renderContextMeter();
assert.equal(f.node("contextMeterDetail").textContent, "↑" + c.formatNumber(1234) + " · ↓56",
  "reloaded usage does not depend on the visible page");
console.log("PASS context usage: request and reload projections retain exact counts");

c.promptContextInspectorSnapshot = snapshot;
c.state.contextUsage = { lastPromptTokens: 2000, lastCompletionTokens: 0, lastTotalTokens: 2000 };
c.activeChatSummary = () => ({ revision: 2 });
c.renderContextMeter();
c.syncPromptContextInspectorState();
assert.equal(f.node("promptContextInspectorUsage").textContent, "Вход " + c.formatNumber(2000) + " токенов");
assert.match(f.node("promptContextInspectorLastUsage").textContent, /выход 0/);
assert.ok(f.node("promptContextInspectorEstimateUsage").textContent.includes(c.formatNumber(9999)),
  "manual preview is not relabeled as the completed request");
console.log("PASS context usage: open inspector follows the newest completed call");

for (const partial of [
  { lastPromptTokens: 0, lastCompletionTokens: 0, lastTotalTokens: 0 },
  { lastPromptTokens: null, lastCompletionTokens: 0, lastTotalTokens: 333 },
  { LastPromptTokens: 456, LastCompletionTokens: 0, LastTotalTokens: 456 },
  { lastPromptTokens: null, lastCompletionTokens: null, lastTotalTokens: null }
]) {
  c.state.contextUsage = { ...partial, usedTokens: 9999, actual: false };
  c.state.messages = [{ PromptTokens: 100, CompletionTokens: 10, TotalTokens: 110 }];
  c.renderContextMeter();
  c.renderPromptContextInspector({ ...partial, usedTokens: 9999 });
  assert.doesNotMatch(f.node("contextMeterDetail").textContent, /≈|undefined|NaN/);
  assert.doesNotMatch(f.node("promptContextInspectorUsage").textContent, /≈|undefined|NaN/);
  if (partial.lastTotalTokens === null) {
    assert.equal(f.node("contextMeterDetail").textContent, "нет usage");
    assert.equal(f.node("promptContextInspectorUsage").textContent, "Нет данных usage");
  } else if (partial.lastPromptTokens === null) {
    assert.equal(c.lastModelUsage().prompt, null, "total and estimates cannot become exact input");
    assert.equal(f.node("promptContextInspectorUsage").textContent, "Вход: API не сообщил число токенов");
  } else {
    assert.equal(f.node("promptContextInspectorUsage").textContent,
      "Вход " + c.formatNumber(partial.lastPromptTokens ?? partial.LastPromptTokens) + " токенов");
  }
}
console.log("PASS context usage: zero, partial and absent usage are explicit");

const html = fs.readFileSync(path.join(__dirname, "../../web/index.html"), "utf8");
const estimateTag = html.match(/<details\b[^>]*id="promptContextInspectorEstimate"[^>]*>/)[0];
assert.doesNotMatch(estimateTag, /\bopen(?:\s|=|>)/, "estimates start collapsed");
assert.ok(html.indexOf('id="promptContextInspectorUsage"') < html.indexOf(estimateTag),
  "exact usage is outside the estimated preview");
assert.ok(html.indexOf('id="promptContextInspectorEstimateUsage"') > html.indexOf(estimateTag));
console.log("PASS context usage: exact summary is separate from the collapsed estimate");
