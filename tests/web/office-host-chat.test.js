"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

class Element {
  constructor() { this.children = []; this.attributes = {}; this.labels = {}; }
  appendChild(child) { this.children.push(child); return child; }
  setAttribute(name, value) { this.attributes[name] = value; }
  querySelector(name) { return this.labels[name] || (this.labels[name] = new Element()); }
  addEventListener() {}
}

function fixture() {
  const calls = [], applied = [];
  const context = vm.createContext({ console, Date,
    state: { officeHostChatAvailable: true, host: "Excel", activeChatId: "excel-chat" },
    document: { createElement: () => new Element() },
    RNAssistantIcons: { render: () => "" },
    currentActiveSend: () => null, hasActiveMessageEdit: () => false,
    confirmDiscardHtmlWorkspaceChanges: () => true, log() {}
  });
  context.window = context;
  for (const file of ["app-chat-state.js", "app-chat-session.js", "app-chat.js"])
    vm.runInContext(fs.readFileSync("web/js/" + file, "utf8"), context, { filename: file });
  context.renderChatSessions = () => {
    context.list = new Element();
    context.renderOfficeHostLaunchRows(context.list, "");
  };
  context.applyChatState = response => { context.state.activeChatId = response.activeChatId; };
  context.applyInitState = init => { applied.push(init); Object.assign(context.state, init); };
  context.send = async (type, payload) => {
    calls.push({ type, payload });
    return { host: payload.host, chatId: "target-chat", init: {
      host: payload.host, activeChatId: "target-chat", tools: [payload.host + "-tool"], skills: [payload.host + "-skill"]
    } };
  };
  return { context, calls, applied };
}

const status = context => context.list.children.find(item => /chat-office-launch-status/.test(item.className));
const buttons = context => context.list.children[0].children
  .find(item => /chat-office-launch-menu/.test(item.className)).children
  .filter(item => /chat-office-launch-row/.test(item.className));

(async () => {
  for (const host of ["Excel", "Word", "PowerPoint", "Outlook"]) {
    const { context, calls, applied } = fixture();
    let release;
    context.state.chatNavigationPending = 1;
    context.state.chatNavigationPromise = new Promise(resolve => { release = () => {
      context.state.chatNavigationPending--;
      resolve();
    }; });
    context.renderChatSessions();
    assert.equal(buttons(context).length, 4);
    assert.ok(buttons(context).every(button => !button.disabled), "navigation allows queued creation");
    const pending = context.createOfficeHostChat(host);
    assert.equal(context.state.officeHostChatPending, true, "accepted click shows pending state");
    assert.match(status(context).textContent, new RegExp(host));
    assert.equal(status(context).attributes.role, "status");
    assert.equal(calls.length, 0, "host switch waits for navigation");
    await context.createOfficeHostChat(host);
    release();
    await pending;
    assert.equal(calls.length, 1, "double click cannot create two chats");
    assert.equal(calls[0].type, "createOfficeHostChat");
    assert.equal(applied[0].host, host);
    assert.equal(context.state.tools[0], host + "-tool");
    assert.equal(context.state.skills[0], host + "-skill");
    assert.equal(context.state.officeHostChatPending, false);
    assert.equal(context.state.chatNavigationPending, 0);
    assert.equal(status(context), undefined);
  }
  console.log("PASS Office chat: all four hosts queue clicks, show progress and apply target catalogs");

  {
    const { context, calls } = fixture();
    const succeed = context.send;
    context.send = async () => { throw new Error("PowerPoint unavailable <script>"); };
    await context.createOfficeHostChat("PowerPoint");
    assert.equal(context.state.host, "Excel", "failure preserves current host");
    assert.equal(context.state.activeChatId, "excel-chat");
    assert.equal(context.state.officeHostChatPending, false);
    assert.equal(status(context).attributes.role, "alert");
    assert.match(status(context).textContent, /PowerPoint unavailable <script>/, "error is visible inert text");
    assert.equal(status(context).innerHTML, undefined);
    context.send = succeed;
    await context.createOfficeHostChat("PowerPoint");
    assert.equal(calls.length, 1, "retry requires another user action");
    assert.equal(status(context), undefined, "successful explicit retry clears error");
  }
  console.log("PASS Office chat: failure is visible, preserves binding and allows explicit retry");

  {
    const { context } = fixture();
    context.send = async () => {
      const error = new Error("Создание чата PowerPoint (этап discovery): презентация недоступна.");
      error.detail = "Request failed. See the local runtime log for details.";
      error.code = "bridge_request_failed";
      throw error;
    };
    await context.createOfficeHostChat("PowerPoint");
    assert.match(status(context).textContent, /этап discovery.*презентация недоступна/,
      "Office creation shows the diagnostic exception already sent by the bridge");
    assert.doesNotMatch(status(context).textContent, /local runtime log/);
  }
  console.log("PASS Office chat: bridge failure shows its actionable creation stage");

  for (const setup of [
    context => { context.state.officeHostChatAvailable = false; },
    context => { context.state.bridgeUnavailable = true; },
    context => { context.currentActiveSend = () => ({}); },
    context => { context.hasActiveMessageEdit = () => true; }
  ]) {
    const { context, calls } = fixture();
    setup(context);
    context.renderChatSessions();
    assert.ok(status(context).textContent, "disabled actions explain the reason without hover");
    assert.ok(buttons(context).every(button => button.disabled && button.title));
    await context.createOfficeHostChat("PowerPoint");
    assert.equal(calls.length, 0);
    assert.ok(status(context).textContent);
  }
  console.log("PASS Office chat: unavailable bridge, host callback, active run and edit explain refusal");
})().catch(error => { console.error(error); process.exitCode = 1; });
