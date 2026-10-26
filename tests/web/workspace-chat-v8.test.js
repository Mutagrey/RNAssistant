"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

class Element {
  constructor(tag) {
    this.tagName = tag;
    this.childNodes = [];
    this.dataset = {};
    this.className = "";
    this.textContent = "";
    this.scrollHeight = 0;
    this.scrollTop = 0;
    this.clientHeight = 0;
  }
  get firstChild() { return this.childNodes[0] || null; }
  get nextSibling() { return this.parentNode?.childNodes[this.parentNode.childNodes.indexOf(this) + 1] || null; }
  appendChild(child) { if (child.parentNode) child.parentNode.removeChild(child); this.childNodes.push(child); child.parentNode = this; return child; }
  removeChild(child) { this.childNodes.splice(this.childNodes.indexOf(child), 1); child.parentNode = null; }

  append(...children) { children.forEach(child => this.appendChild(child)); }
  replaceChildren(...children) { this.childNodes.forEach(child => child.parentNode = null); this.childNodes = []; this.append(...children); }
  insertBefore(child, before) {
    if (child.parentNode) child.parentNode.removeChild(child);
    const index = this.childNodes.indexOf(before);
    this.childNodes.splice(index < 0 ? this.childNodes.length : index, 0, child); child.parentNode = this;
  }
  setAttribute(name, value) { this[name] = value; }
  querySelectorAll() { return []; }
  querySelector(selector) {
    if (selector === ".message-footer") return this.childNodes.find(item => item.className === "message-footer");
    return null;
  }
}

const context = vm.createContext({ document: { createElement: tag => new Element(tag), activeElement: null } });
context.window = context;
for (const script of ["app-utils.js", "app-agent-model.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/" + script), "utf8"), context,
    { filename: script });
}
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-messages.js"), "utf8"), context,
  { filename: "app-messages.js" });
const rendered = [];
context.renderMessageArticle = function (message) {
  rendered.push(message);
  const article = new Element("article");
  article.textContent = message.Content;
  const footer = new Element("div"); footer.className = "message-footer";
  article.appendChild(footer);
  return article;
};
context.renderChatEmptyState = () => new Element("empty");
context.agentActionCountText = count => String(count);
const source = fs.readFileSync(path.join(__dirname, "../../web/js/app-workspace-chat.js"), "utf8");
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-run-view-state.js"), "utf8"), context,
  { filename: "app-run-view-state.js" });
vm.runInContext(source, context, { filename: "app-workspace-chat.js" });

const failedRun = { RunId: "run-1", TurnId: "turn-1", Lifecycle: "failed", ExecutionHealth: "clean",
  SuccessfulReads: 0, VerifiedWrites: 0, NoChangeWrites: 0, UnverifiedWrites: 0,
  FailedCalls: 0, UnknownEffects: 0, Reason: "provider", StartedUtc: "2026-10-23T00:00:00Z" };
assert.equal(context.RNAssistantWorkspaceChat.runNotice({ state: { runViewState: failedRun } }).text,
  context.RNAssistantRunViewState.failureReasonLabel("provider"),
  "project and plugin state reuse the Office run notice");

const list = new Element("section");
context.RNAssistantWorkspaceChat.renderMessages(list, { sessionId: "chat-1", messages: [
  { Id: "reflection", Role: "assistant", Content: "", ReflectionDecision: "Inspect the exact file first." },
  { Id: "finish", Role: "assistant", Content: "Chart remains unfinished.", Results: [
    { Summary: "Chart absent", Assessment: "partially_satisfied", Evidence: ["E1"] }
  ] },
  { Id: "hidden", Role: "assistant", ProtocolMessage: true, Content: "private v8 JSON" }
] });

assert.equal(rendered.length, 2, "one shared renderer receives both visible v8 projections");
assert.equal(list.childNodes.length, 2);
assert.equal(rendered[0].ReflectionDecision, "Inspect the exact file first.");
assert.equal(rendered[1].Results[0].Assessment, "partially_satisfied");
assert.equal(rendered[1].Results[0].Evidence[0], "E1");
assert.ok(!rendered.some(message => message.Id === "hidden"), "raw protocol bodies stay out of transcript");
const firstNode = list.childNodes[0], answerNode = list.childNodes[1];
const sameMessages = JSON.parse(list._chatSignature)[0];
sameMessages.push({ Id: "new", Role: "assistant", Content: "New step" });
context.RNAssistantWorkspaceChat.renderMessages(list, { sessionId: "chat-1", messages: sameMessages });
assert.equal(list.childNodes[0], firstNode, "accepted reflection stays attached when new messages arrive");
assert.equal(list.childNodes[1], answerNode, "unchanged answer preserves focus and disclosure nodes");
assert.equal(context.RNAssistantWorkspaceChat.readOnly({ state: { readOnly: true } }), true);
assert.match(context.RNAssistantWorkspaceChat.runNotice({ state: { readOnly: true } }).text, /Старый протокол/);
const loaded = { project: { workspaceId: "project-1" }, sessionId: "chat-1",
  state: { revision: 5 }, messageStartIndex: 0, messageTotalCount: 3,
  messages: [{ Id: "old" }, { Id: "middle" }, { Id: "latest" }] };
const refreshed = context.RNAssistantWorkspaceChat.latestSnapshot(loaded, {
  project: { workspaceId: "project-1" }, sessionId: "chat-1", state: { revision: 6 },
  messageStartIndex: 2, messageTotalCount: 4, messages: [{ Id: "latest" }, { Id: "new" }] });
assert.deepEqual(Array.from(refreshed.messages, item => item.Id), ["old", "middle", "latest", "new"],
  "fresh snapshots retain loaded older pages without duplicating overlap");
assert.equal(refreshed.messageStartIndex, 0);
assert.throws(() => context.RNAssistantWorkspaceChat.prependMessages(refreshed, {
  chatId: "chat-1", sessionRevision: 5, startIndex: 0, totalCount: 4, messages: []
}), /История изменилась/, "stale history pages cannot overwrite a newer session");
console.log("PASS workspace: shared views, stable nodes, paging and read-only history");
