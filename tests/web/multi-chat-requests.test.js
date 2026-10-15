"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const requests = [];
const refreshedChats = [];
const context = vm.createContext({ Promise, console });
context.window = context;
context.state = {
  activeChatId: "chat-b",
  activeSends: {},
  chatRuns: {},
  messages: [],
  failedSend: null
};
context.$ = () => ({ value: "" });
context.attachmentId = item => item && (item.Id || item.id) || item;
context.messageContent = message => message && (message.Content || message.content) || "";
context.send = (type, payload) => {
  assert.equal(type, "submitChatInput");
  let resolve;
  let reject;
  const promise = new Promise((accept, decline) => { resolve = accept; reject = decline; });
  promise.requestId = "request-" + (requests.length + 1);
  requests.push({ payload, resolve, reject });
  return promise;
};
context.setChatInputText = () => {};
context.clearSendError = () => {};
context.logToolResults = () => {};
context.renderSendControls = () => {};
context.renderChatSessions = () => {};
context.renderMessages = () => {};
context.renderModelControls = () => {};
context.resetLiveReasoning = () => {};
context.log = () => {};

vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-chat-run.js"), "utf8"), context,
  { filename: "app-chat-run.js" });

vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-chat-inbox.js"), "utf8"), context);
context.refreshInboxChat = chatId => refreshedChats.push(chatId);

(async function () {
  const first = context.submitInboxMessage("Первый", [], "chat-a", "Steer");
  const second = context.submitInboxMessage("Второй", [], "chat-b", "Steer");

  assert.deepEqual(requests.map(item => item.payload.chatId), ["chat-a", "chat-b"],
    "each intake keeps its captured chat id");
  assert.notEqual(requests[0].payload.operationId, requests[1].payload.operationId);
  assert.deepEqual(Object.keys(context.state.pendingChatSubmits).sort(), ["chat-a", "chat-b"],
    "both chats own independent intake requests");

  requests[0].resolve({ chatId: "chat-a", epoch: "a", revision: 1, items: [],
    acceptedInput: { Id: "input-a", OperationId: requests[0].payload.operationId, Text: "Первый" } });
  await first;
  assert.deepEqual(refreshedChats, ["chat-a"], "background receipt refreshes only its own chat");
  assert.equal(context.state.activeChatId, "chat-b");
  assert.ok(context.state.pendingChatSubmits["chat-b"], "the second intake remains pending");

  requests[1].resolve({ chatId: "chat-b", epoch: "b", revision: 1, items: [],
    acceptedInput: { Id: "input-b", OperationId: requests[1].payload.operationId, Text: "Второй" } });
  await second;
  assert.deepEqual(refreshedChats, ["chat-a", "chat-b"]);
  assert.deepEqual(Object.keys(context.state.pendingChatSubmits), []);
  console.log("PASS multi-chat: input requests and receipts stay addressed per chat");

  context.setPendingChatSubmit("chat-a", true);
  assert.equal(context.isPendingChatSubmit("chat-a"), true);
  assert.equal(context.isPendingChatSubmit("chat-b"), false);
  console.log("PASS multi-chat: attachment submit barriers are per chat");

  const sendButton = {};
  context.$ = id => id === "sendButton" ? sendButton : null;
  context.hasActiveMessageEdit = () => false;
  vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-chat-composer.js"), "utf8"), context);
  context.state.chatNavigationPending = 1;
  context.updateSendButtonAvailability(true);
  assert.equal(sendButton.disabled, true, "composer cannot submit against an uncommitted binding");
  await context.submitChatInput();
  context.state.chatNavigationPending = 0;
  context.state.initializePromise = Promise.resolve();
  context.updateSendButtonAvailability(true);
  assert.equal(sendButton.disabled, true, "composer waits for initialization");
  await context.submitChatInput();
  assert.equal(requests.length, 2, "navigation and init submit attempts never reach the bridge");
  context.state.initializePromise = null;
  context.updateSendButtonAvailability(true);
  assert.equal(sendButton.disabled, false, "composer becomes available after navigation drains");
  console.log("PASS multi-chat: composer pauses and resumes around navigation");

  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  assert.ok(index.includes("app-chat-run.js?v=response-render-timing-20260928-1"));
  console.log("OK 4/4");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
