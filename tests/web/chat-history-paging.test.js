"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");

function testSharedWindowBounds() {
  const context = vm.createContext({ console }); context.window = context;
  for (const file of ["app-run-view-state.js", "app-conversation-state.js", "app-workspace-chat.js"])
    vm.runInContext(fs.readFileSync(path.join(root, "web/js", file), "utf8"), context);
  const rows = (start, count) => Array.from({ length: count }, (_, index) => ({ Id: "m-" + (start + index) }));
  let snapshot = { project: { workspaceId: "project" }, sessionId: "chat", state: { revision: 7 },
    messageStartIndex: 920, messageTotalCount: 1000, messages: rows(920, 80) };
  while (snapshot.messageStartIndex) {
    const start = Math.max(0, snapshot.messageStartIndex - 80);
    context.RNAssistantWorkspaceChat.prependMessages(snapshot, { chatId: "chat", sessionRevision: 7,
      startIndex: start, totalCount: 1000, messages: rows(start, snapshot.messageStartIndex - start) });
    assert.ok(snapshot.messages.length <= 240, "Workspace uses the same bounded history as Office");
    assert.equal(snapshot.messages[0].Id, "m-" + start);
    assert.equal(snapshot.messages.at(-1).Id, "m-" + (start + snapshot.messages.length - 1));
    snapshot = context.RNAssistantWorkspaceChat.latestSnapshot(snapshot, {
      project: { workspaceId: "project" }, sessionId: "chat", state: { revision: 7 },
      messageStartIndex: 920, messageTotalCount: 1000, messages: rows(920, 80) });
    assert.equal(snapshot.messageStartIndex, start, "same-revision refresh keeps the reading window, even without overlap");
    assert.ok(snapshot.messages.length <= 240, "refresh cannot grow the bounded window again");
  }
  assert.equal(snapshot.messages.length, 240);
  const latest = context.RNAssistantConversationState.messageWindow(null, {
    messageStartIndex: 0, messageTotalCount: 1000, messages: rows(0, 1000) }, false);
  assert.equal(latest.messageStartIndex, 760, "oversized initial snapshot retains the most recent messages");
  assert.equal(latest.messages.length, 240);
  const previous = snapshot.messages;
  assert.throws(() => context.RNAssistantWorkspaceChat.prependMessages(snapshot, {
    chatId: "chat", sessionRevision: 6, startIndex: 0, totalCount: 1000, messages: [] }), /История изменилась/);
  assert.equal(snapshot.messages, previous, "stale page cannot mutate history");
}

function testClosedRunIsLazy() {
  let builtSteps = 0;
  const context = vm.createContext({ console });
  context.window = { RNAssistantAgentApproval: { create: () => ({}) } };
  context.state = {};
  context.document = {
    createElement: tag => ({
      tag, childNodes: [], open: false, attributes: {}, listeners: {},
      appendChild(child) { this.childNodes.push(child); return child; },
      removeChild(child) { this.childNodes.splice(this.childNodes.indexOf(child), 1); },
      setAttribute(name, value) { this.attributes[name] = value; },
      addEventListener(name, listener) { this.listeners[name] = listener; }
    })
  };
  vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-agent.js"), "utf8"), context);
  context.agentToolCallCount = () => 1;
  context.activityKind = activity => activity.Kind || "";
  context.agentRunStats = () => ({});
  context.appendAgentStepMessage = () => {};
  context.activityPresentationState = () => "completed";
  context.appendLiveAgentStep = () => { builtSteps++; };
  context.enhanceActivity = () => {};
  const parent = context.document.createElement("div");
  const details = context.appendAgentRunOverview(
    parent, [{ message: "step", items: [{ activity: {} }] }], [{}], { status: "completed" });
  assert.equal(builtSteps, 0, "closed run does not build its transcript");
  assert.equal(details.childNodes.length, 1, "closed run contains only its summary");
  details.open = true;
  details.listeners.toggle();
  assert.equal(builtSteps, 1, "opening builds the transcript");
  details.open = false;
  details.listeners.toggle();
  assert.equal(details.childNodes.length, 1, "closing releases transcript nodes");
  details.open = true;
  details.listeners.toggle();
  assert.equal(builtSteps, 2, "saved steps rebuild when the run is reopened");
}

(async () => {
  // Mounted-view paging and async navigation live in chat-history-navigation.test.js.
  testSharedWindowBounds();
  testClosedRunIsLazy();
  console.log("OK chat history paging and lazy run details");
})().catch(error => { console.error(error); process.exitCode = 1; });
