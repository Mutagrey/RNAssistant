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
for (const file of ["app-run-view-state.js", "app-conversation-state.js", "app-chat-state.js"])
  vm.runInContext(read(file), context);
context.selectOfficeChatProjection("a");
vm.runInContext(read("app-conversation-view.js"), context);
vm.runInContext(read("app-messages.js"), context);
vm.runInContext(read("app-chat-composer.js"), context);
context.chatComposer().register("office", { target: () => context.officeComposerTarget() });
context.messageView().begin(context.officeConversationTarget()); cleaned.length = 0;
const unit = (key, signature) => ({ key, signature, transient: key.startsWith("live:"), render: () => new Node(signature) });
const history = Array.from({ length: 1000 }, (_, i) => unit("message:" + i, "message" + i));
context.reconcileMessageUnits(box, history.concat(unit("live:stream", "first")));
const original = box.children.slice(0, 1000);
context.reconcileMessageUnits(box, history.concat(unit("live:stream", "second")));
assert.equal(box.children.length, 1001);
assert.ok(original.every((node, i) => node === box.children[i] && node.removals === 0));
assert.deepEqual(cleaned, ["first"]);
context.buildMessageUnits = () => { throw new Error("stream must not serialize history"); };
context.renderChatResourceNavigation = () => { throw new Error("stream must not rebuild resources"); };
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
const actualStreamingRender = context.renderStreamingMessages, scheduledModes = [];
context.renderStreamingMessages = outputOnly => { scheduledModes.push(outputOnly); return true; };
context.state.chatRuns = context.state.chatRuns || {};
context.state.chatRuns.a = { runId: "run" }; context.messageView().cache.liveRunId = "run";
context.scheduleLiveStreamRender("activity");
context.scheduleLiveStreamRender();
assert.equal(frames.length, 1, "activity upgrades a queued output paint without adding a frame");
frames.shift()();
assert.deepEqual(scheduledModes, [false], "activity uses the live-only transcript path");
context.scheduleLiveStreamRender();
frames.shift()();
assert.deepEqual(scheduledModes, [false, true], "ordinary output keeps the leaf-only path");
context.renderStreamingMessages = actualStreamingRender;
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
vm.runInContext(read("app-core.js"), bridge);
vm.runInContext(read("app-run-view-state.js"), bridge);
vm.runInContext(read("app-conversation-state.js"), bridge);
vm.runInContext(read("app-chat-state.js"), bridge);
vm.runInContext(read("app-chat-run.js"), bridge);
bridge.renderChatSessions = () => sidebarRenders++;
bridge.state.activeChatId = "foreground";
bridge.selectOfficeChatProjection("foreground");

for (let i = 0; i < 100; i++) listener({ data: { type: "progress", id: "inbox:background", payload: { chatId: "background", runId: "initial-run", modelProgress: { RunId: "initial-run", AttemptId: "initial", Sequence: i+1, Revision: i+1, Message: "x".repeat(i+1) } } } });
assert.equal(bridge.state.chatRuns.background.stream.length, 100, "every snapshot replaces the preview immediately");
assert.equal(sidebarRenders, 0); assert.equal(timers.length, 1);
timers.shift()(); assert.equal(sidebarRenders, 1);
console.log("PASS background stream: 100 snapshots produce one sidebar refresh without dropping tokens");
const foregroundFrames = [];
let foregroundRenders = 0;
bridge.requestAnimationFrame = callback => foregroundFrames.push(callback);
const foregroundBox = new Node("foreground");
bridge.document.getElementById = id => id === "messages" ? foregroundBox : null;
vm.runInContext(read("app-conversation-view.js"), bridge);
vm.runInContext(read("app-messages.js"), bridge);
vm.runInContext(read("app-chat-composer.js"), bridge);
bridge.chatComposer().register("office", { target: () => bridge.officeComposerTarget() });
bridge.renderMessages = () => {
  foregroundRenders++; bridge.messageView().begin(bridge.officeConversationTarget());
  bridge.messageView().cache.liveRunId = bridge.state.chatRuns[bridge.state.activeChatId]?.runId || "";
};
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

let outputPaints = 0;
bridge.renderStreamingMessages = outputOnly => { assert.equal(outputOnly, true); outputPaints++; };
const progress = (payload, chatId = "foreground") => listener({ data: { type: "progress", id: "inbox:" + chatId,
  payload: { chatId, runId: "run", ...payload } } });
progress({ phase: "thinking", message: "New run" });
foregroundFrames.shift()();
assert.equal(foregroundRenders, 2, "the first event establishes the new run projection");
let sequence = 0;
const snapshots = {};
function snapshot(update = {}, chatId = "foreground") {
  const next = snapshots[chatId] = { RunId: "run", StepId: "step", AttemptId: "first", Message: "", Reasoning: "",
    ...snapshots[chatId], ...update, Sequence: ++sequence, Revision: sequence };
  progress({ modelProgress: { ...next } }, chatId);
}
snapshot();
assert.equal(bridge.state.chatRuns.foreground.streamRetrying, false, "the first attempt is not a retry");
for (let i = 0; i < 100; i++) snapshot({ Message: "x".repeat(i+1) });
assert.equal(bridge.state.liveStreamContent.length, 100);
assert.equal(foregroundFrames.length, 1, "all snapshots coalesce into the next frame");
assert.equal(foregroundRenders, 2, "the first fragment avoids a synchronous full history render");
foregroundFrames.shift()(); assert.equal(outputPaints, 1);
snapshot({ Reasoning: "Old reasoning", TransportEnded: true });
snapshot({ Message: "late body", Reasoning: "late thoughts" });
assert.equal(bridge.state.liveStreamContent, "x".repeat(100), "transport-ended freezes every visible field");
snapshot({ AttemptId: "repair", Message: "", Reasoning: "", TransportEnded: false, Retrying: true });
assert.equal(bridge.state.liveStreamContent, null, "new attempt immediately invalidates the visible draft");
assert.equal(bridge.state.liveReasoning, "");
assert.equal(bridge.state.chatRuns.foreground.streamRetrying, true);
snapshot({ Reasoning: "New reasoning" });
assert.equal(bridge.state.liveStreamContent, null, "new reasoning cannot restore the rejected text");
assert.equal(bridge.state.liveReasoning, "New reasoning");
snapshot({ Message: "Corrected" });
assert.equal(bridge.state.liveStreamContent, "Corrected", "replacement does not append to rejected text");
assert.equal(bridge.state.chatRuns.foreground.streamRetrying, false);
progress({ phase: "acting", message: "Accepted step" });
snapshot({ AttemptId: "step-2-attempt", StepId: "step-2", Message: "", Reasoning: "", Retrying: false });
assert.equal(bridge.state.chatRuns.foreground.streamRetrying, false, "next accepted step starts fresh");
snapshot({ AttemptId: "step-2-retry", Retrying: true });
assert.equal(bridge.state.chatRuns.foreground.streamRetrying, true, "retry visible after an empty attempt");
progress({ phase: "preparing_context", message: "Next kernel step" });
snapshot({ AttemptId: "step-3-attempt", StepId: "step-3", Retrying: false });
assert.equal(bridge.state.chatRuns.foreground.streamRetrying, false, "new kernel step is not a retry");
snapshot({ Message: "Background draft" }, "background");
snapshot({ AttemptId: "background-retry", Message: "", Retrying: true }, "background");
assert.equal(bridge.state.chatRuns.background.stream, "", "background replacement clears the transient draft");
assert.equal(bridge.state.chatRuns.background.streamRetrying, true);
bridge.state.activeChatId = "background";
bridge.selectOfficeChatProjection("background");
bridge.restoreActiveChatRun(false);
assert.equal(bridge.state.liveStreamContent, null, "switching chats cannot revive the invalid background draft");
bridge.endChatRunTracking("background");
assert.equal(bridge.state.chatRuns.background, undefined, "completion retires attempt and retry state");
console.log("PASS stream lifecycle: immediate reset, empty retries, background navigation and coalesced first-token paints");

bridge.state.activeChatId = "foreground";
bridge.selectOfficeChatProjection("foreground");
snapshot({ Reasoning: "Accepted reflection reasoning" });
progress({ phase: "reflection", message: "Accepted reflection" });
const acceptedReflection = bridge.state.chatRuns.foreground.activities.slice(-1)[0];
assert.equal(acceptedReflection.__reasoningMessage.ReasoningContent, "Accepted reflection reasoning");
snapshot({ AttemptId: "reflection-next", Message: "", Reasoning: "", Retrying: false });
assert.equal(bridge.state.chatRuns.foreground.streamRetrying, false, "a new step after reflection is not a format retry");
assert.equal(acceptedReflection.__reasoningMessage.ReasoningContent, "Accepted reflection reasoning", "new attempt cannot erase accepted reasoning");
console.log("PASS reflection: accepted reasoning handoff survives next attempt reset");

snapshot({ Message: "Unaccepted draft" });
bridge.setChatRunCancelling("foreground", true);
assert.equal(bridge.state.liveStreamContent, "Unaccepted draft", "stop freezes the received text pending retention");
snapshot({ Message: "late chunk", Reasoning: "late reasoning" });
assert.equal(bridge.state.liveStreamContent, "Unaccepted draft", "late model data cannot change the frozen partial");
assert.equal(bridge.chatModelStageText(bridge.state.chatRuns.foreground), "Останавливаю…");
bridge.state.chats = [];
bridge.state.activeRunViewState = { runId: "run", lifecycle: "cancelled" };
bridge.reconcileTerminalChatRuns();
assert.equal(bridge.state.chatRuns.foreground, undefined,
  "a terminal active-chat snapshot retires the draft without a catalog refresh");
assert.equal(bridge.state.liveStreamContent, null);
for (const [phase, expected] of [["preparing_context", /контекст/], ["sending", /Ожидаю/],
  ["first_chunk", /Получаю/], ["persisting", /Сохраняю/], ["completed", /Проверяю/]]) {
  assert.match(bridge.chatModelStageText({ requestContext: { phase } }), expected);
}
console.log("PASS stream: staged status and cancellation retire unaccepted output without a catalog round trip");
