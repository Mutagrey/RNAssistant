"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

class Element {
  constructor() { this.children = []; this.className = ""; this.attributes = {}; }
  get classList() { return {
    add: name => { this.className += " " + name; },
    contains: name => this.className.split(" ").includes(name)
  }; }
  appendChild(node) { this.children.push(node); return node; }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener() {}
  set innerHTML(value) {
    this.children = Array.from(value.matchAll(/<button[^>]*class="([^"]+)"/g), match => {
      const button = new Element(); button.className = match[1]; return button;
    });
  }
  querySelector(selector) {
    for (const child of this.children) {
      if (child.classList.contains(selector.slice(1))) return child;
      const found = child.querySelector(selector); if (found) return found;
    }
    return null;
  }
}

function summary(id, lifecycle, revision = 1) {
  return { Id: id, Title: id, Revision: revision, RunViewState: {
    RunId: "run-" + id, TurnId: "turn-" + id, Lifecycle: lifecycle, ExecutionHealth: "clean",
    SuccessfulReads: 0, VerifiedWrites: 0, NoChangeWrites: 0, UnverifiedWrites: 0,
    FailedCalls: 0, UnknownEffects: 0
  } };
}
function inbox(id, running, revision = 1) {
  return { chatId: id, epoch: "epoch", revision, running, items: [] };
}
function ui() {
  let receive;
  const ctx = vm.createContext({ console }); ctx.window = ctx;
  ctx.document = { createElement: () => new Element(), getElementById: () => null };
  ctx.chrome = { webview: { addEventListener: (_, listener) => { receive = listener; } } };
  ctx.setTimeout = () => {};
  for (const file of ["app-core.js", "app-utils.js", "app-run-view-state.js", "app-chat-run.js", "app-chat-inbox.js", "app-chat-state.js"]) {
    vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), ctx, { filename: file });
  }
  ctx.state.activeChatId = "a";
  ctx.state.chats = [summary("a", "running"), summary("b", "completed")];
  ctx.rows = {}; ctx.sidebarRenders = 0;
  ctx.renderChatSessions = () => {
    ctx.sidebarRenders++;
    ctx.state.chats.forEach(chat => { ctx.rows[chat.Id] = ctx.renderChatTreeRow(chat); });
  };
  for (const name of ["renderMessages", "renderSendControls", "renderContext", "renderContextMeter", "renderModelControls"]) ctx[name] = () => {};
  ctx.resetLiveReasoning = () => { ctx.state.liveReasoning = ""; ctx.state.liveReasoningComplete = false; };
  ctx.progress = id => receive({ data: { type: "progress", id: "inbox:" + id,
    payload: { chatId: id, runId: "run-" + id, contentDelta: "Answer" } } });
  ctx.finish = (id, revision = 2) => receive({ data: { type: "chatState", scope: "full", payload: {
    activeChatId: id, sessionRevision: revision, runViewState: summary(id, "completed").RunViewState,
    chats: ctx.state.chats.map(chat => chat.Id === id ? summary(id, "completed", revision) : chat),
    messages: [{ Id: "final-" + id, Role: "assistant", Content: "Answer" }], inbox: inbox(id, false, revision)
  } } });
  return ctx;
}
function assertIdle(ctx, id) {
  assert.equal(ctx.state.chatRuns[id], undefined, "finished chat releases transient progress");
  assert.equal(ctx.rows[id].querySelector(".chat-run-spinner"), null, "finished chat has no spinner");
  assert.equal(ctx.rows[id].classList.contains("has-active-run"), false);
  assert.notEqual(ctx.rows[id].querySelector(".chat-delete").disabled, true, "finished chat can be deleted again");
}

{
  const ctx = ui();
  ctx.progress("a"); ctx.renderChatSessions();
  assert.ok(ctx.rows.a.querySelector(".chat-run-spinner"));
  ctx.finish("a");
  assertIdle(ctx, "a");
  assert.equal(ctx.state.liveStreamContent, null);
  assert.equal(ctx.state.messages[0].Content, "Answer");
  console.log("PASS completion: fast final response clears progress without an earlier running inbox snapshot");
}
{
  const ctx = ui();
  ctx.applyChatInbox(inbox("a", true), true);
  ctx.progress("a");
  ctx.state.activeChatId = "b";
  ctx.progress("b");
  const selectedRun = ctx.state.chatRuns.b;
  const selectedMessages = ctx.state.messages;
  ctx.finish("a");
  assertIdle(ctx, "a");
  assert.equal(ctx.chatInbox("a").running, false, "background full push applies its inbox with the catalog");
  assert.equal(ctx.state.chatRuns.b, selectedRun);
  assert.equal(ctx.state.messages, selectedMessages, "background completion keeps the selected transcript");
  assert.equal(ctx.state.liveStreamContent, "Answer", "background completion keeps selected live output");
  console.log("PASS completion: background push clears only its own progress and spinner");
}
{
  const ctx = ui();
  ctx.applyChatInbox(inbox("a", true), true);
  ctx.progress("a");
  ctx.applyChatState({ activeChatId: "a", sessionRevision: 2,
    chats: [summary("a", "completed", 2)], inbox: inbox("a", true, 2) });
  assert.ok(ctx.rows.a.querySelector(".chat-run-spinner"), "worker still owns active work until it stops");
  const rendered = ctx.sidebarRenders;
  ctx.applyChatInbox(inbox("a", false, 3));
  assert.ok(ctx.sidebarRenders > rendered, "idle inbox refresh repaints the chat list immediately");
  assertIdle(ctx, "a");
  console.log("PASS completion: idle polling clears the already-rendered sidebar spinner");
}
{
  const ctx = ui();
  ctx.applyChatInbox(inbox("a", true, 3), true);
  ctx.progress("a");
  const run = ctx.state.chatRuns.a;
  ctx.applyChatInbox(inbox("a", false, 2));
  assert.equal(ctx.state.chatRuns.a, run, "stale idle response cannot stop current progress");
  ctx.applyChatInbox(inbox("a", true, 4));
  assert.equal(ctx.state.chatRuns.a, run, "continued queue work retains progress");
  ctx.state.activeSends.a = { requestId: "direct-send" };
  ctx.applyChatInbox(inbox("a", false, 5));
  assert.equal(ctx.state.chatRuns.a, run, "a pending direct request retains its own tracking");
  console.log("PASS completion: stale inbox, ongoing queue and direct request retain active progress");
}
