"use strict";
// Exercise the real reducer, grouping, unit builder and DOM reconciler together.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
class Element {
  constructor(tag) { this.tagName = tag; this.childNodes = []; this.className = ""; this.attributes = {}; this._text = ""; this.innerHTML = ""; this.open = false; }
  get children() { return this.childNodes; }
  get firstChild() { return this.childNodes[0] || null; }
  get nextSibling() { const peers = this.parentNode.childNodes; return peers[peers.indexOf(this) + 1] || null; }
  get classList() { return { contains: token => this.className.split(" ").includes(token) }; }
  appendChild(node) { return this.insertBefore(node, null); }
  insertBefore(node, cursor) { if (node.parentNode) node.parentNode.removeChild(node); this.childNodes.splice(cursor ? this.childNodes.indexOf(cursor) : this.childNodes.length, 0, node); node.parentNode = this; return node; }
  removeChild(node) { this.childNodes.splice(this.childNodes.indexOf(node), 1); node.parentNode = null; }
  setAttribute(key, value) { this.attributes[key] = String(value); }
  getAttribute(key) { return this.attributes[key]; }
  addEventListener() {}
  querySelectorAll(selector) { return walk(this).slice(1).filter(n => selector === "details" ? n.tagName === "details" : n.classList.contains(selector.slice(1))); }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  set textContent(value) { this._text = String(value); this.childNodes.forEach(n => { n.parentNode = null; }); this.childNodes = []; }
  get textContent() { return this._text + this.innerHTML + this.childNodes.map(n => n.textContent).join(""); }
}
const walk = node => [node].concat(node.childNodes.flatMap(walk));
const box = new Element("main");
const ctx = vm.createContext({
  state: { activeChatId: "chat", messages: [], chatRuns: {}, editingMessageIndex: -1 },
  document: { createElement: tag => new Element(tag) },
  RNAssistantAgentApproval: { create: () => ({ pendingActivity: () => null }) },
  currentActiveSend: () => true, hasActiveMessageEdit: () => false,
  markdown: text => text, enhanceMarkdown: () => {},
  appendQuestionCards: () => {}, appendMessageArtifactCards: () => {},
  smallIconButton: () => new Element("button"), copyText: () => {}, log: () => {},
  canEditMessage: () => false,
  $: () => box, renderChatResourceNavigation: () => {}, isPanelActive: () => true
});
ctx.window = ctx;
for (const name of ["app-utils.js", "app-run-view-state.js", "app-agent-model.js", "app-agent.js", "app-agent-activity.js", "app-messages.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", name), "utf8"), ctx);
}
ctx.enhanceActivity = () => {};
ctx.isChatNearBottom = () => false; ctx.syncChatScroll = () => {};
ctx.renderedMessagesChatId = "chat";
const action = (step, id, status, args) => ({ RunId: "r", Kind: "tool", ToolId: "common.resources_read", ToolCallId: id,
  StepId: step, StepMessage: "Narration " + step, Status: status, ArgumentsJson: args,
  Subtitle: "Target " + id, ResultMessage: status === "completed" ? "Read complete" : null });
const marker = step => ({ RunId: "r", Kind: "step", Status: "running", StepId: step, StepMessage: "Narration " + step });
const message = (a, index) => ({ Id: "m" + index, Role: "assistant", RunId: "r", Activity: a, Content: "Fallback " + index });
const live = [marker("s1"), action("s1", "a", "completed", '{"target":"A"}'), marker("s2"), action("s2", "b", "completed", '{"target":"B"}')];
const saved = [action("s1", "a", "completed", null), action("s2", "b", "completed", '{\n  "target": "B"\n}')];
saved[0].ArgumentsPayload = { Hash: "blob", ContentType: "application/json" };
ctx.state.messages = saved.map(message);
ctx.state.liveAgentRun = live;
function render() { ctx.reconcileMessageUnits(box, ctx.buildMessageUnits()); }
function assertFeed(steps, calls) {
  const nodes = walk(box);
  assert.equal(box.childNodes.length, 1, "one visible run article");
  assert.equal(nodes.filter(n => n.classList.contains("agent-step-message")).length, steps, "each step narration appears once");
  assert.equal(nodes.filter(n => n.classList.contains("kind-tool")).length, calls, "each actual call appears once");
}
render();
assertFeed(2, 2);
assert.equal(walk(box).filter(n => n.classList.contains("agent-step-message")).map(n => n.textContent).join("|"), "Narration s1|Narration s2");
// A replay after a push may use inline JSON; a later call in the same step is new.
ctx.state.liveAgentRun = live.concat(action("s2", "c", "running", '{"target":"C"}'));
render(); assertFeed(2, 3);
ctx.state.liveAgentRun = live.concat(action("s2", "c", "completed", null));
ctx.state.messages.push(message(action("s2", "c", "completed", null), 2));
render(); assertFeed(2, 3);
// Repeated completion/materialization is a row update, not another call.
const reduced = [];
ctx.recordActivityTimeline(reduced, action("s1", "a", "running", '{}'));
ctx.recordActivityTimeline(reduced, action("s1", "a", "completed", null));
ctx.recordActivityTimeline(reduced, Object.assign(action("s1", "a", "completed", '{}'), { ResultMessage: "Materialized" }));
assert.equal(reduced.length, 1); assert.equal(reduced[0].ResultMessage, "Materialized");
ctx.recordActivityTimeline(reduced, action("s1", "a", "running", '{}'));
assert.equal(reduced.length, 1); assert.equal(reduced[0].Status, "completed");
// IDs remain scoped to steps and runs, even when text and tool name are identical.
assert.notEqual(ctx.activityTimelineKey(action("s1", "a", "completed", '{}')), ctx.activityTimelineKey(action("s2", "a", "completed", '{}')));
assert.notEqual(ctx.activityTimelineKey(action("s1", "a", "completed", '{}')), ctx.activityTimelineKey(Object.assign(action("s1", "a", "completed", '{}'), { RunId: "other" })));
console.log("PASS live projection: real rendering merges persisted CAS/inline results, replayed step markers and repeated call updates");
// Return to an active chat with an already loaded persisted snapshot.
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-chat-run.js"), "utf8"), ctx);
ctx.state.activeSends = { chat: {} };
ctx.renderAgentPlanDock = () => {}; ctx.renderAgentApprovalDock = () => {}; ctx.renderSendControls = () => {};
ctx.state.chatRuns.chat = { activities: ctx.state.liveAgentRun, stream: "", reasoning: "" };
ctx.state.liveAgentRun = null;
ctx.resetRenderedMessageUnits(box);
ctx.restoreActiveChatRun(); assertFeed(2, 3);
const view = { RunId: "r", TurnId: "turn", Lifecycle: "completed", ExecutionHealth: "clean", SuccessfulReads: 3,
  VerifiedWrites: 0, NoChangeWrites: 0, UnverifiedWrites: 0, FailedCalls: 0, UnknownEffects: 0 };
ctx.state.messages.push({ Id: "final", RunId: "r", Role: "assistant", Content: "Final answer", RunViewState: view });
ctx.state.liveStreamContent = "Final answer";
render();
assert.equal(box.childNodes.length, 1, "late streaming replay cannot duplicate a persisted final answer");
assert.equal(walk(box).filter(n => n.classList.contains("agent-final-step")).length, 1);
// Distinct transcript segments of one logical run must not share a DOM cache key.
ctx.state.liveAgentRun = null; ctx.state.liveActivity = null; ctx.state.liveStreamContent = null; ctx.state.chatRuns = {};
ctx.state.messages = [message(action("s1", "a", "completed", null), 0),
  { Id: "reply", Role: "user", Content: "Continue" }, message(action("s2", "b", "completed", null), 1)];
const segments = ctx.buildMessageUnits();
assert.equal(new Set(segments.map(u => u.key)).size, segments.length, "separated run segments have unique identities");
render();
assert.equal(box.childNodes.length, 3);
console.log("PASS live projection: restoring active chats, final-stream replay and separate transcript segments");
