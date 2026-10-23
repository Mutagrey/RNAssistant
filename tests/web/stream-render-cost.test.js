"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const read = name => fs.readFileSync(path.join(__dirname, "../../web/js", name), "utf8");
class Node {
  constructor(id) { this.id = id; this.children = []; this.parentNode = null; this.removals = 0; }
  get firstChild() { return this.children[0] || null; }
  get nextSibling() { const siblings = this.parentNode.children; return siblings[siblings.indexOf(this) + 1] || null; }
  insertBefore(node, cursor) {
    if (node.parentNode) node.parentNode.removeChild(node);
    const index = cursor === null ? this.children.length : this.children.indexOf(cursor);
    assert.ok(index >= 0); this.children.splice(index, 0, node); node.parentNode = this;
  }
  removeChild(node) { this.children.splice(this.children.indexOf(node), 1); node.parentNode = null; node.removals++; }
}
const box = new Node("box"), cleaned = [], frames = [];
const context = vm.createContext({ console, document: { activeElement: null }, state: { activeChatId: "a", messages: [] },
  $: () => box, clearMarkdownEnhancements: node => cleaned.push(node.id), isPanelActive: () => true });
context.window = context;
context.requestAnimationFrame = callback => frames.push(callback);
vm.runInContext(read("app-messages.js"), context);
const unit = (key, signature) => ({ key, signature, render: () => new Node(signature) });
const history = Array.from({ length: 1000 }, (_, i) => unit("message:" + i, "message" + i));
context.reconcileMessageUnits(box, history.concat(unit("live:stream", "first")));
const original = box.children.slice(0, 1000);
context.reconcileMessageUnits(box, history.concat(unit("live:stream", "second")));
assert.equal(box.children.length, 1001);
assert.ok(original.every((node, i) => node === box.children[i] && node.removals === 0));
assert.deepEqual(cleaned, ["first"]);
context.renderedMessagesChatId = "a";
context.buildMessageUnits = () => { throw new Error("stream must not serialize history"); };
context.renderChatResourceNavigation = () => { throw new Error("stream must not rebuild resources"); };
context.isChatNearBottom = () => false;
context.syncChatScroll = () => {};
context.buildLiveMessageUnits = () => [unit("live:reasoning", "reasoning"), unit("live:stream", "third")];
for (let i = 0; i < 100; i++) context.scheduleLiveStreamRender();
assert.equal(frames.length, 1); frames.shift()();
assert.equal(box.children.length, 1002);
assert.deepEqual(box.children.slice(-2).map(node => node.id), ["reasoning", "third"]);
assert.ok(original.every(node => node.removals === 0));
context.buildLiveMessageUnits = () => [];
context.renderStreamingMessages();
assert.equal(box.children.length, 1000);
context.reconcileMessageUnits(box, [history[2], history[0]]);
assert.deepEqual(box.children.map(node => node.id), ["message2", "message0"]);
let fullRenders = 0;
context.state.activeChatId = "b"; context.renderMessages = () => fullRenders++;
context.renderStreamingMessages(); assert.equal(fullRenders, 1, "chat change uses full current projection");
console.log("PASS stream: coalesced tail rendering retains 1000 attached history nodes, orders/replaces/removes live nodes and handles navigation");
const disclosure = (key, open) => ({ open, getAttribute: () => key });
function withDetails(signature, details) {
  const item = unit("live:agent", signature);
  item.render = () => { const node = new Node(signature); node.querySelectorAll = () => details; return node; };
  return item;
}
context.reconcileMessageUnits(box, [withDetails("run-first", [disclosure("step:a", true), disclosure("activity:call-a", false)])]);
const changedDetails = [disclosure("step:new", false), disclosure("step:a", false), disclosure("activity:call-a", true)];
context.reconcileMessageUnits(box, [withDetails("run-updated", changedDetails)]);
assert.deepEqual(changedDetails.map(item => item.open), [false, true, false], "open and closed disclosures survive a live update by identity, despite insertion");
console.log("PASS stream: live update preserves explicitly opened and closed action history");
let listener, sidebarRenders = 0;
const timers = [];
const bridge = vm.createContext({ console }); bridge.window = bridge;
bridge.document = { getElementById: () => null };
bridge.setTimeout = callback => timers.push(callback);
bridge.chrome = { webview: { addEventListener: (_, callback) => { listener = callback; } } };
bridge.renderChatSessions = () => sidebarRenders++;
vm.runInContext(read("app-core.js"), bridge);
vm.runInContext(read("app-chat-run.js"), bridge);
bridge.state.activeChatId = "foreground";

for (let i = 0; i < 100; i++) listener({ data: { type: "progress", id: "inbox:background", payload: { chatId: "background", contentDelta: "x" } } });
assert.equal(bridge.state.chatRuns.background.stream.length, 100, "every delta is retained immediately");
assert.equal(sidebarRenders, 0); assert.equal(timers.length, 1);
timers.shift()(); assert.equal(sidebarRenders, 1);
console.log("PASS background stream: 100 deltas produce one sidebar refresh without dropping tokens");
const foregroundFrames = [];
let foregroundRenders = 0;
bridge.requestAnimationFrame = callback => foregroundFrames.push(callback);
vm.runInContext(read("app-messages.js"), bridge);
bridge.renderMessages = () => foregroundRenders++;
bridge.normalizeProgressActivity = progress => ({ id: progress.message, kind: "notice", title: progress.message });
bridge.cloneActivity = activity => Object.assign({}, activity);
bridge.log = () => {};

for (let i = 0; i < 100; i++) listener({ data: { type: "progress", id: "inbox:foreground", payload: { chatId: "foreground", phase: "thinking", message: "step-" + i } } });
assert.equal(bridge.state.chatRuns.foreground.activities.length, 100, "all activity events are recorded before rendering");
assert.equal(foregroundRenders, 0);
assert.equal(foregroundFrames.length, 1);
bridge.scheduleLiveStreamRender();
assert.equal(foregroundFrames.length, 1, "content and activity share one pending frame");
foregroundFrames.shift()();
assert.equal(foregroundRenders, 1, "activity burst produces one full projection, not 100");
console.log("PASS foreground progress: 100 activities and content deltas share one frame without dropping events");

bridge.resetLiveReasoning = () => { bridge.state.liveReasoning = ""; bridge.state.liveReasoningComplete = false; };
let outputPaints = 0;
bridge.renderStreamingMessages = outputOnly => { assert.equal(outputOnly, true); outputPaints++; };
const progress = (payload, chatId = "foreground") => listener({ data: { type: "progress", id: "inbox:" + chatId,
  payload: { chatId, runId: "run", ...payload } } });
progress({ phase: "thinking", message: "New run" });
foregroundFrames.shift()();
assert.equal(foregroundRenders, 2, "the first event establishes the new run projection");
progress({ contentReset: true, reasoningReset: true });
assert.equal(bridge.state.chatRuns.foreground.streamRetrying, false, "the first attempt is not a retry");
for (let i = 0; i < 100; i++) progress({ contentDelta: "x" });
assert.equal(bridge.state.liveStreamContent.length, 100);
assert.equal(foregroundFrames.length, 1, "first and subsequent tokens coalesce into the next frame");
assert.equal(foregroundRenders, 2, "even the first token avoids a synchronous full history render");
foregroundFrames.shift()(); assert.equal(outputPaints, 1);
progress({ reasoningDelta: "Old reasoning", reasoningComplete: true });
progress({ contentReset: true, reasoningReset: true });
assert.equal(bridge.state.liveStreamContent, null, "reset immediately invalidates the visible draft");
assert.equal(bridge.state.liveReasoning, "");
assert.equal(bridge.state.chatRuns.foreground.streamRetrying, true);
progress({ reasoningDelta: "New reasoning" });
assert.equal(bridge.state.liveStreamContent, null, "new reasoning cannot restore the rejected text");
assert.equal(bridge.state.liveReasoning, "New reasoning");
progress({ contentDelta: "Corrected" });
assert.equal(bridge.state.liveStreamContent, "Corrected", "the replacement does not append to the invalid draft");
assert.equal(bridge.state.chatRuns.foreground.streamRetrying, false);
progress({ phase: "acting", message: "Accepted step" });
progress({ contentReset: true, reasoningReset: true });
assert.equal(bridge.state.chatRuns.foreground.streamRetrying, false, "the next accepted step starts a fresh attempt");
progress({ contentReset: true, reasoningReset: true });
assert.equal(bridge.state.chatRuns.foreground.streamRetrying, true, "a retry is visible even after an empty attempt");
progress({ contentDelta: "Background draft" }, "background");
progress({ contentReset: true, reasoningReset: true }, "background");
assert.equal(bridge.state.chatRuns.background.stream, "", "background reset clears the saved transient draft");
assert.equal(bridge.state.chatRuns.background.streamRetrying, true);
bridge.state.activeChatId = "background";
bridge.restoreActiveChatRun(false);
assert.equal(bridge.state.liveStreamContent, null, "switching chats cannot revive the invalid background draft");
bridge.endChatRunTracking("background");
assert.equal(bridge.state.chatRuns.background, undefined, "completion retires attempt and retry state");
console.log("PASS stream lifecycle: immediate reset, empty retries, background navigation and coalesced first-token paints");

bridge.state.activeChatId = "foreground";
progress({ reasoningDelta: "Accepted reflection reasoning" });
progress({ phase: "reflection", message: "Accepted reflection" });
const acceptedReflection = bridge.state.chatRuns.foreground.activities.slice(-1)[0];
assert.equal(acceptedReflection.__reasoningMessage.ReasoningContent, "Accepted reflection reasoning");
progress({ contentReset: true, reasoningReset: true });
assert.equal(bridge.state.chatRuns.foreground.streamRetrying, false, "a new step after reflection is not a format retry");
assert.equal(acceptedReflection.__reasoningMessage.ReasoningContent, "Accepted reflection reasoning", "new attempt cannot erase accepted reasoning");
console.log("PASS reflection: accepted reasoning handoff survives next attempt reset");
