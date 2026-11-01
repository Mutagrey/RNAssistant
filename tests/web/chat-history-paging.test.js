"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");

async function testPageBoundary() {
  const calls = [];
  const button = { disabled: false };
  const box = { scrollHeight: 100, scrollTop: 20 };
  let renderOptions;
  const context = vm.createContext({ console });
  context.window = context;
  context.state = {
    activeChatId: "chat-a", messageStartIndex: 80, messageTotalCount: 160,
    chatProjectionRevisions: { "chat-a": 7 },
    messages: Array.from({ length: 80 }, (_, index) => ({ Id: "m-" + (index + 80) }))
  };
  context.$ = () => box;
  context.send = async (type, payload) => {
    calls.push({ type, payload });
    return {
      chatId: "chat-a", sessionRevision: 7, startIndex: 0,
      totalCount: context.state.messageTotalCount,
      messages: Array.from({ length: 80 }, (_, index) => ({ Id: "m-" + index }))
    };
  };
  context.renderMessages = options => { renderOptions = options; };
  context.renderChatSessions = () => {};
  context.log = () => {};
  vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-conversation-state.js"), "utf8"), context);
  vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-chat-session.js"), "utf8"), context);

  await context.loadPreviousChatMessages({ currentTarget: button });
  assert.equal(calls.length, 1);
  assert.equal(calls[0].type, "getPreviousChatMessages");
  assert.equal(calls[0].payload.beforeIndex, 80);
  assert.equal(context.state.messageStartIndex, 0);
  assert.equal(context.state.messages.length, 160);
  assert.equal(context.state.messages[0].Id, "m-0");
  assert.equal(context.state.messages[159].Id, "m-159");
  assert.equal(renderOptions.preserveTop, true, "prepend requests anchored viewport");
  assert.equal(renderOptions.previousScrollHeight, 100);
  assert.equal(button.disabled, false);

  context.state.messageStartIndex = 80;
  context.state.messageTotalCount = 400;
  context.state.messages = Array.from({ length: 240 }, (_, index) => ({ Id: "m-" + (index + 80) }));
  await context.loadPreviousChatMessages({ currentTarget: button });
  assert.equal(context.state.messages.length, 240, "paging keeps the browser window bounded");
  assert.equal(context.state.messages[0].Id, "m-0");
  assert.equal(context.state.messages[239].Id, "m-239");

  context.state.messageStartIndex = 80;
  const previousSend = context.send;
  context.send = async (...args) => {
    const page = await previousSend(...args);
    context.state.chatProjectionRevisions["chat-a"] = 8;
    return page;
  };
  await context.loadPreviousChatMessages({ currentTarget: button });
  assert.equal(context.state.messageStartIndex, 80,
    "a page from the previous revision cannot be prepended to a newer transcript");
}

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

async function testStaleRefreshDoesNotSelectOldChat() {
  const context = vm.createContext({ console });
  context.window = context;
  context.state = {
    activeChatId: "chat-a", chatNavigationVersion: 1,
    messageStartIndex: 80, messageTotalCount: 160,
    chatProjectionRevisions: { "chat-a": 7 }, messages: []
  };
  context.send = async () => ({ chatId: "chat-a", sessionRevision: 8, startIndex: 0,
    totalCount: 160, messages: [] });
  context.log = () => {};
  let resolveLoad, signalLoad;
  const loadStarted = new Promise(resolve => { signalLoad = resolve; });
  let applied = 0;
  vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-chat-session.js"), "utf8"), context);
  context.loadChatState = () => {
    signalLoad();
    return new Promise(resolve => { resolveLoad = resolve; });
  };
  context.applyChatState = () => { applied++; };

  const paging = context.loadPreviousChatMessages();
  await loadStarted;
  context.state.activeChatId = "chat-b";
  context.state.chatNavigationVersion++;
  resolveLoad({ chatId: "chat-a" });
  await paging;
  assert.equal(applied, 0, "late page recovery cannot restore the previous chat");
  assert.equal(context.state.messagePageBusy, false);

  context.state.activeChatId = "chat-a";
  const latestStarted = new Promise(resolve => { signalLoad = resolve; });
  const latest = context.jumpToLatestChatMessages();
  await latestStarted;
  context.state.chatNavigationVersion++;
  resolveLoad({ chatId: "chat-a" });
  await latest;
  assert.equal(applied, 0, "late latest-page response cannot overwrite a newer navigation");
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
  await testPageBoundary();
  await testStaleRefreshDoesNotSelectOldChat();
  testSharedWindowBounds();
  testClosedRunIsLazy();
  console.log("OK chat history paging and lazy run details");
})().catch(error => { console.error(error); process.exitCode = 1; });
