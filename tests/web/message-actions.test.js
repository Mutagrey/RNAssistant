"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
class Element {
  constructor() { this.children = []; this.parentNode = null; this.className = ""; }
  get childNodes() { return this.children; }
  addEventListener(name, handler) { (this.listeners || (this.listeners = {}))[name] = handler; }
  get classList() { return {
    contains: name => this.className.split(" ").includes(name),
    add: name => { if (!this.className.split(" ").includes(name)) this.className += " " + name; }
  }; }
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
  state: { activeChatId: "chat", messages: [{ Id: "user", Role: "user", Content: "Question" }], editingMessageIndex: -1 },
  $: () => box,
  document: { createElement: () => new Element() },
  currentActiveSend: () => sending,
  pendingAgentApprovalActivity: () => approval,
  RNAssistantAgentApproval: { create: () => ({ pendingActivity: () => approval }) },
  messageId: m => m.Id, messageRole: m => m.Role, messageContent: m => m.Content,
  messageRunId: m => m.RunId || "",
  messageActivity: () => null, messageRunViewState: () => null, messageProtocolMessage: () => false,
  messageTotalTokens: m => m.TotalTokens ?? null,
  messagePromptTokens: m => m.PromptTokens ?? null,
  messageCompletionTokens: m => m.CompletionTokens ?? null,
  canCollectAgentRunAt: () => false,
  smallIconButton: (title, icon, click) => Object.assign(new Element(), { title, click }),
  forkChatAtMessage: (message, index) => { ctx.forked = [message.Id, index]; },
  copyText: () => {}, log: () => {}
});
ctx.window = ctx;
for (const name of ["app-chat-composer.js", "app-chat-edit.js", "app-conversation-view.js", "app-messages.js", "app-agent.js", "app-chat.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", name), "utf8"), ctx);
}
ctx.chatComposer().register("office", { target: () => ctx.officeComposerTarget(), canMessageAction: ctx.officeCanMessageAction, messageAction: ctx.officeMessageAction });
ctx.messageView().begin(ctx.officeConversationTarget());
const presentation = () => ctx.officeMessagePresentation(ctx.messageView(), ctx.chatComposer().capture(), ctx.state);
ctx.messageUsageText = () => "";
ctx.messageUnitSignature = m => JSON.stringify(m);
ctx.renderMessageArticle = (message, index, options) => {
  renders++;
  const node = new Element(); node.appendChild(new Element());
  ctx.appendMessageFooter(node, message, index, null, options); return node;
};
const refresh = () => ctx.reconcileMessageUnits(box, ctx.buildMessageUnits(presentation(), ctx.messageActionsSignature()));
const titles = node => {
  const footer = node.children.find(child => child.classList.contains("message-footer"));
  const actions = footer.children.find(child => child.classList.contains("message-actions"));
  return actions.children.map(button => button.title);
};
refresh();
const article = box.firstChild, body = article.firstChild;
assert.deepEqual(titles(article), ["Копировать сообщение"]);
assert.equal(article.children.at(-1).classList.contains("has-status"), false);
const failed = new Element();
ctx.appendMessageFooter(failed, { Role: "user", Content: "Retry", Failed: true }, 0, null);
assert.equal(failed.children[0].classList.contains("has-status"), true, "failed delivery stays visible");
const incompatible = new Element();
ctx.appendMessageFooter(incompatible, { Role: "assistant", Content: "Old answer" }, 0, null);
assert.equal(incompatible.children[0].classList.contains("has-status"), true, "missing runtime state stays visible");
sending = false; refresh();
assert.ok(titles(article).includes("Изменить сообщение"));
assert.ok(titles(article).includes("Ответвить чат отсюда"));
article.children.at(-1).children.at(-1).children.find(b => b.title === "Ответвить чат отсюда").click();
assert.deepEqual(ctx.forked, ["user", 0]);
ctx.chatComposer().startEdit(ctx.officeComposerTarget(), { messageId: "user", index: 0, text: "edit" }); refresh();
assert.deepEqual(titles(article), ["Копировать сообщение"]);
ctx.chatComposer().cancelEdit(ctx.officeComposerTarget(), { restore: false }); refresh();
assert.ok(titles(article).includes("Изменить сообщение"));
approval = true; refresh();
assert.ok(!titles(article).includes("Ответвить чат отсюда"));
approval = false; refresh();
ctx.state.bridgeUnavailable = true; refresh();
assert.ok(!titles(article).includes("Изменить сообщение"));
ctx.state.bridgeUnavailable = false; refresh();
assert.ok(titles(article).includes("Изменить сообщение"));
assert.equal(box.firstChild, article); assert.equal(article.firstChild, body); assert.equal(renders, 1);
// Builders use only their borrowed snapshot and their own view cache.
const originalMessages = ctx.state.messages, firstPresentation = presentation();
const secondView = ctx.RNAssistantConversationView.get(new Element());
secondView.begin({ source: "office", sessionId: "second" });
const secondPresentation = ctx.createMessagePresentation(secondView, {
  messages: [{ Id: "second-message", Role: "user", Content: "Second" }], start: 0, total: 1,
  live: { activities: [], runId: "", content: "", reasoning: "" }
});
ctx.state.messages = [{ Id: "unrelated-selection", Role: "user" }];
assert.equal(ctx.buildMessageUnits(firstPresentation)[0].key, "message:user");
assert.equal(ctx.buildMessageUnits(secondPresentation)[0].key, "message:second-message");
assert.equal(box.firstChild, article, "building another view does not touch the mounted transcript");
ctx.state.messages = originalMessages;
// The same invalidation restores the grouped agent-run footer.
const final = { message: { Id: "answer", Role: "assistant", Content: "Answer" }, index: 1 };
const run = { items: [{ message: final.message, index: 0 }], finalMessage: final, nextIndex: 1 };
ctx.canCollectAgentRunAt = () => true; ctx.collectAgentRun = () => run;
ctx.agentRunUnitKey = () => "run:test"; ctx.agentRunUnitSignature = () => "unchanged";
ctx.renderAgentRunArticle = (_, scope) => { const node = new Element(); ctx.appendAgentRunFooter(node, run.items, final, scope); return node; };
sending = true; refresh(); const grouped = box.firstChild;
assert.ok(!titles(grouped).includes("Ответвить чат отсюда"));
sending = false; refresh();
assert.equal(box.firstChild, grouped);
assert.ok(titles(grouped).includes("Ответвить чат отсюда"));
console.log("PASS message actions: send/edit/approval/reconnect transitions restore footers without remounting content");

// A persisted mid-run snapshot and the live replay must produce one run unit.
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-run-view-state.js"), "utf8"), ctx);
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-agent-model.js"), "utf8"), ctx);
const activity = (id, status) => ({ RunId: "r", StepId: "s", StepMessage: "One step",
  Kind: "tool", ToolId: "core.read", ToolCallId: id, Status: status });
const persisted = { message: { RunId: "r", Id: "p" }, index: 0, activity: activity("a", "completed") };
let snapshot = { items: [persisted], finalMessage: null, nextIndex: 1 };
ctx.canCollectAgentRunAt = () => true; ctx.collectAgentRun = () => snapshot;
ctx.state.liveRun = { activities: [activity("a", "running"), activity("b", "running")] };
ctx.renderAgentRunArticle = value => { ctx.projected = value; return new Element(); };
let units = ctx.buildMessageUnits(presentation(), ctx.messageActionsSignature());
assert.equal(units.length, 1); assert.equal(units[0].key, "live:agent-run");
units[0].render();
assert.equal(ctx.projected.items.length, 2);
assert.equal(ctx.projected.items[0].activity.Status, "completed", "persisted result wins over stale live progress");
assert.equal(ctx.projected.items[0].message, persisted.message, "resource provenance is retained");
ctx.agentRunUnitSignature = () => { throw new Error("stream must not serialize persisted history"); };
ctx.buildLiveMessageUnits(presentation());
ctx.agentRunUnitSignature = () => "snapshot";
snapshot = { items: [persisted], finalMessage: { message: { RunId: "r", Id: "final" }, index: 1 }, nextIndex: 1 };
units = ctx.buildMessageUnits(presentation(), ctx.messageActionsSignature());
assert.equal(units.length, 1); assert.notEqual(units[0].key, "live:agent-run", "terminal snapshot removes live replay");
ctx.state.liveRun = { activities: [Object.assign(activity("a", "running"), { RunId: "other" })] };
units = ctx.buildMessageUnits(presentation(), ctx.messageActionsSignature());
assert.equal(units.length, 2, "distinct runtime runs are not merged by matching prose or call names");
console.log("PASS run overlap: persisted/live replay merges once by runtime identity and yields to the final snapshot");

(async () => {
  sending = false; approval = false;
  ctx.state.activeChatId = "source";
  ctx.messageView().begin(ctx.officeConversationTarget());
  const removed = [];
  let finishDelete, historyReads = 0;
  ctx.deleteMessage = message => {
    removed.push([ctx.state.activeChatId, message.Id]);
    return new Promise(resolve => { finishDelete = resolve; });
  };
  ctx.confirm = () => true;
  ctx.chatComposer().register("office", { target: () => ctx.officeComposerTarget(),
    canMessageAction: ctx.officeCanMessageAction, messageAction: ctx.officeMessageAction,
    loadPrevious: () => historyReads++ });
  const source = presentation();
  const deleting = ctx.deleteAgentRun([{ message: { Id: "first" }, index: 0 }],
    { message: { Id: "last" }, index: 1 }, source);
  assert.deepEqual(removed, [["source", "last"]]);
  const history = ctx.createChatHistoryButton("previous", () => source.invoke("loadPrevious"));
  history.listeners.click({});
  ctx.state.activeChatId = "other"; ctx.chatComposer().sync();
  ctx.messageView().begin(ctx.officeConversationTarget());
  finishDelete(); await deleting; await Promise.resolve();
  assert.deepEqual(removed, [["source", "last"]], "an awaited run deletion never redirects remaining commands to the new chat");
  assert.equal(historyReads, 0, "deferred history read rejects a changed target before requesting data");
  ctx.state.activeChatId = "source"; ctx.chatComposer().sync();
  ctx.messageView().begin(ctx.officeConversationTarget());
  source.invoke("loadPrevious");
  assert.equal(historyReads, 0, "A→B→A cannot revive old message handlers");
  console.log("PASS addressed actions: async deletion, delayed history and A→B→A stay bound to their original view");
  // Exercise the actual delete delivery, including its error path.
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-chat-session.js"), "utf8"), ctx);
  const notices = [], applied = [], catalogs = [], requests = [];
  ctx.showSendError = error => notices.push(error);
  ctx.applyChatStateForChat = (value, id) => applied.push([value, id]);
  ctx.applyChatCatalogState = value => catalogs.push(value);
  ctx.send = (type, payload) => new Promise((resolve, reject) => requests.push({ type, payload, resolve, reject }));
  const delayedError = ctx.deleteMessage({ Id: "last" }, 1, ctx.chatComposer().capture());
  ctx.state.activeChatId = "other"; ctx.chatComposer().sync();
  ctx.state.activeChatId = "source"; ctx.chatComposer().sync();
  requests[0].reject(new Error("previous visit")); await delayedError;
  assert.equal(notices.length, 0, "late error cannot replace the current visit's notice after A→B→A");
  const delayedSuccess = ctx.deleteMessage({ Id: "last" }, 1, ctx.chatComposer().capture());
  ctx.state.activeChatId = "other"; ctx.chatComposer().sync();
  requests[1].resolve("background result"); await delayedSuccess;
  assert.equal(applied.length, 0); assert.deepEqual(catalogs, ["background result"]);
  const currentError = ctx.deleteMessage({ Id: "current" }, 0, ctx.chatComposer().capture());
  requests[2].reject(new Error("current failure")); await currentError;
  assert.deepEqual(notices, ["current failure"]);
  assert.deepEqual(requests.map(x => x.payload.chatId), ["source", "source", "other"]);
  console.log("PASS delete delivery: late success/error stay out of another chat and current failures remain visible");
})().catch(error => { console.error(error); process.exitCode = 1; });
