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
  for (const file of ["app-core.js", "app-utils.js", "app-run-view-state.js", "app-conversation-state.js", "app-chat-run.js", "app-chat-inbox.js", "app-chat-state.js"]) {
    vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), ctx, { filename: file });
  }
  ctx.RNAssistantNavigationTree = { icon: () => "" };
  ctx.state.activeChatId = "a";
  ctx.selectOfficeChatProjection("a");
  ctx.state.chats = [summary("a", "running"), summary("b", "completed")];
  ctx.rows = {}; ctx.sidebarRenders = 0;
  ctx.renderChatSessions = () => {
    ctx.sidebarRenders++;
    ctx.state.chats.forEach(chat => { ctx.rows[chat.Id] = ctx.chatTreeNode(chat); });
  };
  for (const name of ["renderMessages", "renderSendControls", "renderContext", "renderContextMeter", "renderModelControls"]) ctx[name] = () => {};
  ctx.progress = id => receive({ data: { type: "progress", id: "inbox:" + id,
    payload: { chatId: id, runId: "run-" + id, contentDelta: "Answer" } } });
  ctx.newProgress = (id, runId) => receive({ data: { type: "progress", id: "inbox:" + id,
    payload: { chatId: id, runId, contentDelta: "Next" } } });
  ctx.finish = (id, revision = 2) => receive({ data: { type: "chatState", scope: "full", payload: {
    activeChatId: id, sessionRevision: revision, runViewState: summary(id, "completed").RunViewState,
    chats: ctx.state.chats.map(chat => chat.Id === id ? summary(id, "completed", revision) : chat),
    messages: [{ Id: "final-" + id, Role: "assistant", Content: "Answer" }], inbox: inbox(id, false, revision)
  } } });
  return ctx;
}
function assertIdle(ctx, id) {
  assert.equal(ctx.state.chatRuns[id], undefined, "finished chat releases transient progress");
  assert.equal(ctx.rows[id].status, "", "finished chat has no running badge");
  assert.equal(ctx.rows[id].actions.find(action => action.danger).disabled, false, "finished chat can be deleted again");
}
function assertStoppedIndicator(ctx, id) {
  assert.equal(ctx.state.chatRuns[id], undefined, "saved terminal state releases transient progress");
  assert.equal(ctx.rows[id].status, "", "saved terminal state has no running badge");
}

{
  const ctx = ui();
  const accepted = { Id: "accepted", Role: "assistant", Content: "Сохранённый ответ" };
  ctx.applyChatState({ activeChatId: "a", sessionRevision: 5,
    messages: [accepted], messageStartIndex: 0, messageTotalCount: 1 });
  ctx.applyChatState({ activeChatId: "a", sessionRevision: 5,
    messages: [], messageStartIndex: 0, messageTotalCount: 1 });
  assert.equal(ctx.state.messages[0].Id, "accepted", "same-revision sparse snapshot cannot erase an accepted answer");
  ctx.applyChatState({ activeChatId: "a", sessionRevision: 5,
    messages: [{ Id: "stale", Role: "assistant", Content: "Старый ответ" }],
    messageStartIndex: 0, messageTotalCount: 1 });
  assert.equal(ctx.state.messages[0].Id, "accepted", "same-revision mismatched snapshot cannot replace an accepted answer");
  ctx.applyChatState({ activeChatId: "a", sessionRevision: 5,
    messages: [{ Id: "older", Role: "user", Content: "Раннее сообщение" }, accepted],
    messageStartIndex: 0, messageTotalCount: 2 });
  ctx.applyChatState({ activeChatId: "a", sessionRevision: 5,
    messages: [accepted], messageStartIndex: 1, messageTotalCount: 2 });
  assert.deepEqual(ctx.state.messages.map(message => message.Id), ["older", "accepted"],
    "a same-revision refresh retains the reader's earlier page");
  ctx.applyChatState({ activeChatId: "a", sessionRevision: 5,
    messages: [accepted], messageStartIndex: 1, messageTotalCount: 2 }, false, { latestMessages: true });
  assert.equal(ctx.state.messageStartIndex, 1, "explicit jump to latest bypasses older-window retention");
  assert.equal(ctx.state.messages.length, 1);
  ctx.applyChatState({ activeChatId: "a", sessionRevision: 6,
    messages: [{ Id: "replacement", Role: "assistant", Content: "Новая версия" }],
    messageStartIndex: 0, messageTotalCount: 1 });
  assert.equal(ctx.state.messages[0].Id, "replacement", "a newer revision may replace the history window");
  console.log("PASS completion: accepted Office messages survive delayed same-revision windows");
}

{
  const ctx = ui();
  ctx.progress("a"); ctx.renderChatSessions();
  assert.equal(ctx.rows.a.status, "unknown", "a saved running run without a live inbox is uncertain");
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
  ctx.selectOfficeChatProjection("b");
  ctx.state.chats[1] = summary("b", "running", 2);
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
  ctx.progress("a");
  ctx.state.activeChatId = "b";
  ctx.selectOfficeChatProjection("b");
  ctx.applyChatCatalogState({ chats: [summary("a", "failed", 2), summary("b", "completed", 1)] });
  assert.equal(ctx.state.chatRuns.a, undefined, "background terminal catalog retires the exact run");
  ctx.progress("a");
  assert.equal(ctx.state.chatRuns.a, undefined, "late background progress stays retired");
  assertIdle(ctx, "a");
  console.log("PASS completion: background terminal catalog and late progress stay consistent");
}
{
  const ctx = ui();
  ctx.applyChatInbox(inbox("a", true), true);
  ctx.progress("a");
  ctx.applyChatState({ activeChatId: "a", sessionRevision: 2,
    chats: [summary("a", "completed", 2)], inbox: inbox("a", true, 2) });
  assertStoppedIndicator(ctx, "a");
  assert.equal(ctx.rows.a.actions.find(action => action.danger).disabled, true, "delete remains blocked while the inbox worker owns the chat");
  ctx.progress("a");
  ctx.renderChatSessions();
  assertStoppedIndicator(ctx, "a");
  const rendered = ctx.sidebarRenders;
  ctx.applyChatInbox(inbox("a", false, 3));
  assert.ok(ctx.sidebarRenders > rendered, "idle inbox refresh repaints the chat list immediately");
  assertIdle(ctx, "a");
  console.log("PASS completion: saved terminal state clears progress before the worker poll and ignores delayed frames");
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
{
  const ctx = ui();
  ctx.applyChatState({ activeChatId: "a", sessionRevision: 2,
    chats: [summary("a", "completed", 2)], inbox: inbox("a", true, 2) });
  const next = inbox("a", true, 3);
  next.items = [{ Id: "next-input", Status: "Pending" }];
  ctx.applyChatInbox(next);
  ctx.renderChatSessions();
  assert.equal(ctx.rows.a.status, "running", "queued next input remains visible");
  assert.equal(ctx.rows.a.statusLabel, "Запускается следующая задача");
  ctx.progress("a");
  assert.equal(ctx.state.chatRuns.a, undefined, "old run progress cannot return while the next input waits");
  ctx.newProgress("a", "run-next");
  assert.equal(ctx.state.chatRuns.a.runId, "run-next", "a distinct new run can start normally");
  console.log("PASS completion: queued work and a new run remain distinct from late old-run progress");
}

{
  const ctx = ui();
  ctx.progress("a");
  const first = ctx.officeChatProjection(), run = ctx.state.chatRuns.a;
  assert.equal(first.snapshot().liveRun, run, "the source borrows the tracked run, without another stream buffer");
  assert.equal(ctx.state.liveAgentRun, run.activities);
  for (const key of ["liveActivity", "liveAgentRun", "liveStreamContent", "liveReasoning", "liveReasoningComplete"]) {
    const descriptor = Object.getOwnPropertyDescriptor(ctx.state, key);
    assert.equal(typeof descriptor.get, "function");
    assert.equal(descriptor.set, undefined, key + " has no selected copy writer");
  }
  ctx.applyOfficeChatProgress({ chatId: "a", runId: "run-a", reasoningDelta: "r".repeat(25000) }, null, "inbox:a");
  assert.equal(ctx.state.liveReasoning.length, 24000, "reasoning remains bounded");
  ctx.state.activeRunViewState = ctx.RNAssistantRunViewState.normalize(summary("a", "running").RunViewState);
  assert.equal(first.snapshot().activeRunViewState, ctx.state.activeRunViewState);

  ctx.clearOfficeChatProjection();
  ctx.progress("a");
  assert.equal(ctx.state.chatRuns.a, run, "view release does not stop or replace the run");
  assert.equal(run.stream, "AnswerAnswer", "background progress continues without a mounted source");
  assert.equal(first.snapshot(), null);
  assert.equal(ctx.officeChatProjection(), null, "delivery cannot recreate a disposed source");
  assert.equal(ctx.state.liveStreamContent, null);
  assert.equal(ctx.state.activeRunViewState, null);

  ctx.state.activeChatId = "b"; ctx.selectOfficeChatProjection("b");
  ctx.newProgress("b", "new-b");
  ctx.progress("a");
  assert.equal(ctx.state.liveStreamContent, "Next", "another source never receives the background text");
  ctx.state.activeChatId = "a"; const returned = ctx.selectOfficeChatProjection("a");
  ctx.restoreActiveChatRun(false);
  assert.notEqual(returned, first);
  assert.equal(returned.snapshot().liveRun, run, "returning borrows the current run without replay or copy");
  assert.equal(ctx.state.liveStreamContent, "AnswerAnswerAnswer");
  assert.equal(first.isCurrent(), false);
  ctx.newProgress("a", "new-a");
  assert.notEqual(ctx.state.chatRuns.a, run);
  assert.equal(ctx.state.liveStreamContent, "Next", "a new run cannot inherit the previous draft");
  const runCount = Object.keys(ctx.state.chatRuns).length;
  ctx.applyOfficeChatProgress({ contentDelta: "unaddressed" }, null, "inbox:missing");
  assert.equal(Object.keys(ctx.state.chatRuns).length, runCount);
  assert.equal(ctx.state.liveStreamContent, "Next");
  console.log("PASS live ownership: addressed borrowed runs, bounded reasoning, release, return and run replacement");
}
