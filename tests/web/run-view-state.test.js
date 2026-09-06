"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const context = vm.createContext({});
context.window = context;
const source = fs.readFileSync(path.join(__dirname, "../../web/js/app-run-view-state.js"), "utf8");
vm.runInContext(source, context, { filename: "app-run-view-state.js" });
const runView = context.RNAssistantRunViewState;

function state(runId, lifecycle = "completed", health = "clean", pending = null) {
  return {
    RunId: runId, TurnId: "turn-" + runId, Narrative: "model narrative",
    Lifecycle: lifecycle, ExecutionHealth: health, SuccessfulReads: 1,
    VerifiedWrites: 0, NoChangeWrites: 0, UnverifiedWrites: 0,
    FailedCalls: 0, UnknownEffects: 0, PendingConfirmation: pending,
    Reason: null, CurrentAction: "", StartedUtc: "2026-08-30T10:00:00Z"
  };
}

{
  const normalized = runView.normalize(state("run-1"));
  assert.equal(normalized.lifecycle, "completed");
  assert.equal(normalized.narrative, "model narrative");
  assert.ok(Object.isFrozen(normalized));
  assert.equal(runView.normalize(Object.assign(state("bad"), { UnknownEffects: -1 })), null);
  assert.equal(runView.normalize(Object.assign(state("bad-health"), { UnknownEffects: 1 })), null);
  assert.equal(runView.normalize(Object.assign(state("bad-pending", "awaiting_confirmation"), { PendingConfirmation: null })), null);
  const pending = { PendingId: "pending", ToolCallId: "call", ToolName: "excel.write_range" };
  const waiting = runView.normalize(state("waiting", "awaiting_confirmation", "clean", pending));
  assert.equal(waiting.pendingConfirmation.pendingId, "pending");
  assert.ok(Object.isFrozen(waiting.pendingConfirmation));
  console.log("PASS run view state: strict immutable normalization keeps narrative separate");
}

{
  const revisions = {};
  assert.equal(runView.accept(revisions, "chat-a", 7), true);
  assert.equal(runView.accept(revisions, "chat-a", 6), false, "late concurrent response is stale");
  assert.equal(revisions["chat-a"], 7);
  assert.equal(runView.accept(revisions, "chat-a", 7), true, "same durable projection is idempotent");
  assert.equal(runView.accept(revisions, "chat-b", 2), true, "ordering is isolated per chat");
  assert.equal(runView.accept(revisions, "chat-a", null), false, "unversioned response cannot replace known state");
  console.log("PASS run view state: per-chat monotonic ordering rejects stale concurrent responses");
}

{
  const revisions = { "chat-a": 7 };
  const current = [
    { Id: "chat-a", Revision: 7, Title: "new", RunViewState: state("run-new") },
    { Id: "removed", Revision: 3, Title: "removed" }
  ];
  const incoming = [
    { Id: "chat-a", Revision: 6, Title: "stale", RunViewState: state("run-old") },
    { Id: "chat-b", Revision: 2, Title: "second", RunViewState: state("run-b", "running") }
  ];
  const merged = runView.mergeCatalog(current, incoming, revisions);
  assert.equal(merged.length, 2);
  assert.equal(merged[0].Title, "new", "older summary cannot replace newer known chat state");
  assert.equal(merged[1].Title, "second");
  assert.equal(merged.some(item => item.Id === "removed"), false, "authoritative list still removes absent chats");
  assert.equal(revisions["chat-a"], 7);
  assert.equal(revisions["chat-b"], 2);

  const guarded = runView.mergeCatalog(current, incoming, revisions, true);
  assert.deepEqual(Array.from(guarded, item => item.Id), ["chat-a", "removed", "chat-b"],
    "stale catalog keeps current membership and order while appending a newly observed chat");
  assert.equal(guarded[0].Title, "new");
  console.log("PASS run view state: catalog merge preserves order, deletions and newest per-chat revision");
}

{
  const ui = vm.createContext({});
  ui.window = ui;
  vm.runInContext(source, ui, { filename: "app-run-view-state.js" });
  ui.state = {
    activeChatId: "chat-a", activeRunViewState: null, chatProjectionRevisions: {},
    chats: [], messages: [], artifacts: [], tools: [{ Id: "tool-new" }], skills: [], chatStateApplyVersion: 0
  };
  ui.$ = () => null;
  ["renderMessages", "renderContext", "renderContextMeter", "renderModelControls"].forEach(name => {
    ui[name] = () => {};
  });
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-chat-state.js"), "utf8"), ui,
    { filename: "app-chat-state.js" });
  const newest = {
    activeChatId: "chat-a", sessionRevision: 7, runViewState: state("run-new"),
    messages: [{ Content: "new" }], chats: [
      { Id: "chat-a", Revision: 7, RunViewState: state("run-new") },
      { Id: "chat-new", Revision: 1, RunViewState: state("run-created") }
    ]
  };
  const stale = {
    activeChatId: "chat-a", sessionRevision: 6, runViewState: state("run-old"),
    messages: [{ Content: "old" }], tools: [{ Id: "tool-old" }],
    chats: [{ Id: "chat-a", Revision: 6, RunViewState: state("run-old") }]
  };
  assert.equal(ui.applyChatState(newest), true);
  assert.equal(ui.applyChatState(stale), false);
  assert.equal(ui.state.messages[0].Content, "new", "late detail cannot replace newer transcript");
  assert.equal(ui.state.activeRunViewState.runId, "run-new", "late detail cannot replace newer outcome");
  assert.equal(ui.state.chats[0].Revision, 7, "late catalog cannot regress the visible chat summary");
  assert.equal(ui.state.chats[1].Id, "chat-new", "late catalog cannot remove a chat created after its snapshot");
  assert.equal(ui.state.tools[0].Id, "tool-new", "unversioned global catalogs are not accepted from a stale response");
  console.log("PASS run view state: integrated chat state rejects stale transcript and outcome");
}

{
  const agent = fs.readFileSync(path.join(__dirname, "../../web/js/app-agent-model.js"), "utf8");
  const messages = fs.readFileSync(path.join(__dirname, "../../web/js/app-messages.js"), "utf8");
  const approval = fs.readFileSync(path.join(__dirname, "../../web/js/app-agent-approval.js"), "utf8");
  const chatState = fs.readFileSync(path.join(__dirname, "../../web/js/app-chat-state.js"), "utf8");
  const index = fs.readFileSync(path.join(__dirname, "../../web/index.html"), "utf8");
  const agentCss = fs.readFileSync(path.join(__dirname, "../../web/css/app-agent.css"), "utf8");
  const chatCss = fs.readFileSync(path.join(__dirname, "../../web/css/app-chat.css"), "utf8");
  assert.equal(/ExecutionSummary|executionSummary|messageResponseStatus/.test(agent + messages), false);
  assert.equal(/activityPendingId|pendingConfirmation\(activity\)/.test(approval), false);
  assert.match(approval, /activeRunViewState/);
  assert.match(chatState, /RNAssistantRunViewState\.accept/);
  assert.match(chatState, /RNAssistantRunViewState\.mergeCatalog/);
  assert.equal(/fetch\(|XMLHttpRequest|WebSocket|EventSource/.test(source), false);
  assert.equal(/agent-run-history-state\.status-(?:blocked|refused|awaiting_user|planned)/.test(agentCss), false);
  assert.equal(/message-outcome\.status-(?:blocked|refused|awaiting_user|planned)/.test(chatCss), false);
  ["app-utils.js", "app-run-view-state.js", "app-agent-model.js", "app-agent-approval.js"].forEach(asset => {
    assert.ok(index.includes(asset + "?v=run-view-state-20260830-1"), asset + " uses the atomic cutover cache key");
  });
  assert.ok(index.includes("app-agent.js?v=runtime-diagnostics-20260831-1"),
    "agent outcome uses the diagnostics cache key");
  assert.ok(index.includes("app-chat-session.js?v=startup-secondary-lazy-20260907-1"), "chat session uses the current cache key");
  assert.ok(index.includes("app-core.js?v=chat-sync-20260903-1"), "core uses the chat sync cache key");
  assert.ok(index.includes("app-chat-state.js?v=context-usage-display-20260907-1"), "chat state uses the current cache key");
  assert.ok(index.includes("app-messages.js?v=transcript-incremental-20260907-1"), "messages uses the transcript incremental cache key");
  assert.equal(/function updateEstimatedContextUsage\(\)[\s\S]*?state\.messages\.forEach/.test(chatState), false,
    "context meter does not scan and encode the whole transcript");
  assert.match(chatState, /localDeltaTokens/, "context meter exposes presentation-only local delta");
  ["app-chat.css", "app-agent.css"].forEach(asset => {
    assert.ok(index.includes(asset + "?v=run-view-state-20260830-1"), asset + " uses the atomic cutover cache key");
  });
  assert.ok(index.indexOf("app-run-view-state.js") < index.indexOf("app-chat-state.js"));
  assert.ok(index.indexOf("app-run-view-state.js") < index.indexOf("app-agent-model.js"));
  console.log("PASS run view state: bridge/UI consumers use the typed projection without model-status inference");
}

console.log("OK 5/5");
