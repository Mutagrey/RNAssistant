"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const requests = [];
const fullStates = [];
const catalogStates = [];
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
  assert.equal(type, "sendChat");
  let resolve;
  let reject;
  const promise = new Promise((accept, decline) => { resolve = accept; reject = decline; });
  promise.requestId = "request-" + (requests.length + 1);
  requests.push({ payload, resolve, reject });
  return promise;
};
context.applyChatState = response => fullStates.push(response.activeChatId);
context.applyChatCatalogState = response => catalogStates.push(response.activeChatId);
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

(async function () {
  const first = context.sendChat("Первый", [], "chat-a");
  const second = context.sendChat("Второй", [], "chat-b");

  assert.deepEqual(requests.map(item => item.payload.chatId), ["chat-a", "chat-b"],
    "each bridge request keeps its captured chat id");
  assert.deepEqual(Object.keys(context.state.activeSends).sort(), ["chat-a", "chat-b"],
    "both chats own independent active sends");

  requests[0].resolve({ activeChatId: "chat-a", toolResults: [] });
  await first;
  assert.deepEqual(catalogStates, ["chat-a"], "background response only refreshes the catalog");
  assert.deepEqual(fullStates, []);
  assert.ok(context.state.activeSends["chat-b"], "the second request remains active");

  requests[1].resolve({ activeChatId: "chat-b", toolResults: [] });
  await second;
  assert.deepEqual(fullStates, ["chat-b"], "selected chat receives its own full response");
  assert.deepEqual(Object.keys(context.state.activeSends), []);
  console.log("PASS multi-chat: requests, responses and lifecycles stay addressed per chat");

  context.setPendingChatSubmit("chat-a", true);
  assert.equal(context.isPendingChatSubmit("chat-a"), true);
  assert.equal(context.isPendingChatSubmit("chat-b"), false);
  console.log("PASS multi-chat: attachment submit barriers are per chat");

  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  assert.ok(index.includes("app-chat-run.js?v=multi-chat-20260902-1"));
  console.log("OK 3/3");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
