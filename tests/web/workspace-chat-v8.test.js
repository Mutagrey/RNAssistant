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
  appendChild(child) { this.childNodes.push(child); return child; }
  append(...children) { children.forEach(child => this.appendChild(child)); }
  replaceChildren(...children) { this.childNodes = children; }
  insertBefore(child, before) {
    const index = this.childNodes.indexOf(before);
    this.childNodes.splice(index < 0 ? this.childNodes.length : index, 0, child);
  }
  setAttribute(name, value) { this[name] = value; }
  querySelectorAll() { return []; }
  querySelector(selector) {
    if (selector === ".message-footer") return this.childNodes.find(item => item.className === "message-footer");
    return null;
  }
}

const context = vm.createContext({ document: { createElement: tag => new Element(tag) } });
context.window = context;
let bubbles = 0;
context.renderMessageArticle = function (message) {
  bubbles += 1;
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
  { role: "assistant", text: "", reflectionDecision: "Inspect the exact file first." },
  { role: "assistant", text: "Chart remains unfinished.", results: [
    { Summary: "Chart absent", Assessment: "partially_satisfied", Evidence: ["call-1"] }
  ] }
] });

assert.equal(bubbles, 1, "empty reflection has a saved step but no assistant bubble");
assert.equal(list.childNodes.length, 2);
assert.equal(list.childNodes[0].className, "workspace-reflection-step");
assert.equal(list.childNodes[0].childNodes[1].textContent, "Inspect the exact file first.");
const article = list.childNodes[1];
const outcome = article.childNodes.find(item => item.className.includes("agent-run-notice"));
assert.ok(outcome && outcome.className.includes("status-warning"), "partial finish stays visible");
const details = outcome.childNodes[0].childNodes[1];
assert.equal(details.open, true);
assert.match(details.childNodes[1].childNodes[0].textContent, /Chart absent · частично/);
assert.match(details.childNodes[1].childNodes[0].childNodes[0].textContent, /call-1/);
console.log("PASS workspace v8: reflection step and incomplete finish evidence survive chat rendering");
