"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
class Element {
  constructor() { this.children = []; this.parentNode = null; this.className = ""; }
  get childNodes() { return this.children; }
  get classList() { return { contains: name => this.className.split(" ").includes(name) }; }
  get firstChild() { return this.children[0] || null; }
  get nextSibling() { return this.parentNode.children[this.parentNode.children.indexOf(this) + 1] || null; }
  appendChild(node) { return this.insertBefore(node, null); }
  insertBefore(node, cursor) {
    if (node.parentNode) node.parentNode.removeChild(node);
    this.children.splice(cursor ? this.children.indexOf(cursor) : this.children.length, 0, node);
    node.parentNode = this; return node;
  }
  removeChild(node) { this.children.splice(this.children.indexOf(node), 1); node.parentNode = null; }
}
let sending = true, approval = false, renders = 0;
const box = new Element();
const ctx = vm.createContext({
  state: { messages: [{ Id: "user", Role: "user", Content: "Question" }], editingMessageIndex: -1 },
  document: { createElement: () => new Element() },
  currentActiveSend: () => sending,
  pendingAgentApprovalActivity: () => approval,
  RNAssistantAgentApproval: { create: () => ({ pendingActivity: () => approval }) },
  messageId: m => m.Id, messageRole: m => m.Role, messageContent: m => m.Content,
  messageActivity: () => null, messageRunViewState: () => null, messageProtocolMessage: () => false,
  canCollectAgentRunAt: () => false,
  smallIconButton: (title, icon, click) => Object.assign(new Element(), { title, click }),
  forkChatAtMessage: (message, index) => { ctx.forked = [message.Id, index]; },
  copyText: () => {}, log: () => {}
});
ctx.window = ctx;
for (const name of ["app-chat-edit.js", "app-messages.js", "app-agent.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", name), "utf8"), ctx);
}
ctx.messageUsageText = () => "";
ctx.messageUnitSignature = m => JSON.stringify(m);
ctx.renderMessageArticle = (message, index) => {
  renders++;
  const node = new Element(); node.appendChild(new Element());
  ctx.appendMessageFooter(node, message, index, null); return node;
};
const refresh = () => ctx.reconcileMessageUnits(box, ctx.buildMessageUnits());
const titles = node => node.children.at(-1).children.at(-1).children.map(button => button.title);
refresh();
const article = box.firstChild, body = article.firstChild;
assert.deepEqual(titles(article), ["Копировать сообщение"]);
sending = false; refresh();
assert.ok(titles(article).includes("Изменить сообщение"));
assert.ok(titles(article).includes("Ответвить чат отсюда"));
article.children.at(-1).children.at(-1).children.find(b => b.title === "Ответвить чат отсюда").click();
assert.deepEqual(ctx.forked, ["user", 0]);
ctx.state.editingMessageId = "user"; refresh();
assert.deepEqual(titles(article), ["Копировать сообщение"]);
ctx.state.editingMessageId = ""; refresh();
assert.ok(titles(article).includes("Изменить сообщение"));
approval = true; refresh();
assert.ok(!titles(article).includes("Ответвить чат отсюда"));
approval = false; refresh();
ctx.state.bridgeUnavailable = true; refresh();
assert.ok(!titles(article).includes("Изменить сообщение"));
ctx.state.bridgeUnavailable = false; refresh();
assert.ok(titles(article).includes("Изменить сообщение"));
assert.equal(box.firstChild, article); assert.equal(article.firstChild, body); assert.equal(renders, 1);
// The same invalidation restores the grouped agent-run footer.
const final = { message: { Id: "answer", Role: "assistant", Content: "Answer" }, index: 1 };
const run = { items: [{ message: final.message, index: 0 }], finalMessage: final, nextIndex: 1 };
ctx.canCollectAgentRunAt = () => true; ctx.collectAgentRun = () => run;
ctx.agentRunUnitKey = () => "run:test"; ctx.agentRunUnitSignature = () => "unchanged";
ctx.renderAgentRunArticle = () => { const node = new Element(); ctx.appendAgentRunFooter(node, run.items, final); return node; };
sending = true; refresh(); const grouped = box.firstChild;
assert.ok(!titles(grouped).includes("Ответвить чат отсюда"));
sending = false; refresh();
assert.equal(box.firstChild, grouped);
assert.ok(titles(grouped).includes("Ответвить чат отсюда"));
console.log("PASS message actions: send/edit/approval/reconnect transitions restore footers without remounting content");

// A persisted mid-run snapshot and the live replay must produce one run unit.
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-agent-model.js"), "utf8"), ctx);
const activity = (id, status) => ({ RunId: "r", StepId: "s", StepMessage: "One step",
  Kind: "tool", ToolId: "common.resources_read", ToolCallId: id, Status: status });
const persisted = { message: { RunId: "r", Id: "p" }, index: 0, activity: activity("a", "completed") };
let snapshot = { items: [persisted], finalMessage: null, nextIndex: 1 };
ctx.canCollectAgentRunAt = () => true; ctx.collectAgentRun = () => snapshot;
ctx.state.liveAgentRun = [activity("a", "running"), activity("b", "running")];
ctx.renderAgentRunArticle = value => { ctx.projected = value; return new Element(); };
let units = ctx.buildMessageUnits();
assert.equal(units.length, 1); assert.equal(units[0].key, "live:agent-run");
units[0].render();
assert.equal(ctx.projected.items.length, 2);
assert.equal(ctx.projected.items[0].activity.Status, "completed", "persisted result wins over stale live progress");
assert.equal(ctx.projected.items[0].message, persisted.message, "resource provenance is retained");
ctx.agentRunUnitSignature = () => { throw new Error("stream must not serialize persisted history"); };
ctx.buildLiveMessageUnits();
ctx.agentRunUnitSignature = () => "snapshot";
snapshot = { items: [persisted], finalMessage: { message: { RunId: "r", Id: "final" }, index: 1 }, nextIndex: 1 };
units = ctx.buildMessageUnits();
assert.equal(units.length, 1); assert.notEqual(units[0].key, "live:agent-run", "terminal snapshot removes live replay");
ctx.state.liveAgentRun = [Object.assign(activity("a", "running"), { RunId: "other" })];
units = ctx.buildMessageUnits();
assert.equal(units.length, 2, "distinct runtime runs are not merged by matching prose or call names");
console.log("PASS run overlap: persisted/live replay merges once by runtime identity and yields to the final snapshot");
