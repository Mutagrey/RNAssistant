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
  const pending = { PendingId: "pending", ToolCallId: "call", ToolName: "excel.range.write" };
  const waiting = runView.normalize(state("waiting", "awaiting_confirmation", "clean", pending));
  assert.equal(waiting.pendingConfirmation.pendingId, "pending");
  assert.ok(Object.isFrozen(waiting.pendingConfirmation));
  assert.equal(runView.normalize(Object.assign(state("bad-case"), { Lifecycle: "Completed" })), null);
  assert.equal(runView.normalize(Object.assign(state("bad-health-case"), { ExecutionHealth: "Clean" })), null);
  assert.equal(runView.normalize(Object.assign(state("bad-value"), { Lifecycle: "toString" })), null);
  assert.equal(runView.normalize(state("bad-pending-case", "awaiting_confirmation", "clean",
    { pendingId: "pending", toolCallId: "call", toolName: "excel.range.write" })), null);
  const partial = runView.normalize(Object.assign(state("partial"), { Reason: "model_finish", ModelOutcome: "partial",
    ModelResults: [{ Summary: "Chart remains unfinished", Assessment: "partially_satisfied",
      Evidence: ["call-1"] }] }));
  assert.equal(partial.modelResults[0].evidence[0], "call-1");
  assert.ok(Object.isFrozen(partial.modelResults) && Object.isFrozen(partial.modelResults[0]));
  assert.match(runView.outcomeLabel(partial), /частично по оценке модели/);
  assert.equal(partial.goalVerification, "not_assessed");
  assert.equal(partial.modelOutcome, "partial");
  assert.equal(runView.normalize(Object.assign(state("bad-outcome"), { Reason: "model_finish", ModelOutcome: "unknown" })), null);
  assert.equal(runView.normalize(Object.assign(state("bad-outcome-type"), { Reason: "model_finish", ModelOutcome: ["completed"] })), null);
  assert.equal(runView.normalize(Object.assign(state("early-outcome", "running"), { Reason: "model_finish", ModelOutcome: "completed" })), null);
  const unable = runView.normalize(Object.assign(state("unable"), { Reason: "model_finish", ModelOutcome: "unable_to_complete" }));
  assert.match(runView.outcomeLabel(unable), /не выполнена по оценке модели/);
  assert.equal(runView.normalize(Object.assign(state("forged"), { ModelResults: [{
    Summary: "No evidence", Assessment: "satisfied", Evidence: ["call-1", "call-1"] }] })), null);
  assert.equal(runView.normalize(Object.assign(state("running-claim", "running"), {
    ModelResults: [{ Summary: "Too early", Assessment: "unverified", Evidence: [] }]
  })), null);
  const patchPreview = {type:'rnassistant.patch',targets:[{target:'a.js',preview:[{before:'old',after:'new'}]}]};
  const approval = runView.normalize(Object.assign(state('patch', 'awaiting_confirmation'), {
    PendingConfirmation: {PendingId:'p',ToolCallId:'c',ToolName:'core.patch',PatchPreview:patchPreview}
  }));
  assert.equal(approval.pendingConfirmation.patchPreview, patchPreview, 'prepared package reaches approval without re-parsing model arguments');
  assert.equal(runView.fromMessage({ RunViewState: state("message") }).runId, "message");
  assert.equal(runView.fromChatSummary({ RunViewState: state("summary") }).runId, "summary");
  assert.equal(runView.fromMessage({ runViewState: state("wrong-case") }), null);
  assert.equal(runView.fromChatSummary({ runViewState: state("wrong-case") }), null);
  const failed = runView.normalize(Object.assign(state("failure", "failed"), { Reason: "provider" }));
  assert.equal(runView.runNotice(failed).text, runView.failureReasonLabel("provider"),
    "all chat surfaces use the same terminal failure text");
  assert.match(runView.failureReasonLabel("reflection_limit"), /промежуточных решений/);
  assert.match(runView.failureReasonLabel("ReflectionDisabled"), /Рефлексия отключена.*корректировка не запрашивалась/);
  assert.match(runView.failureReasonLabel("repeated_tool_no_progress"), /отклонённые действия/);
  assert.equal(runView.runNotice(runView.normalize(state("completed"))), null);
  assert.equal(runView.sessionRevision({ sessionRevision: 0 }), 0);
  assert.equal(runView.sessionRevision({ SessionRevision: 4 }), null);
  assert.equal(runView.chatRevision({ Revision: 0 }), 0);
  assert.equal(runView.chatRevision({ revision: 4 }), null);
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
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-utils.js"), "utf8"), ui,
    { filename: "app-utils.js" });
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-conversation-state.js"), "utf8"), ui,
    { filename: "app-conversation-state.js" });
  ui.state = {
    activeChatId: "chat-a", activeRunViewState: null, chatProjectionRevisions: {},
    chats: [], messages: [], artifacts: [], tools: [{ Id: "tool-new" }], skills: [], chatStateApplyVersion: 0,
    activeSends: {}, chatRuns: {}, chatInboxes: {}
  };
  ui.RNAssistantNavigationTree = { icon: () => "" };
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
  assert.equal(ui.RNAssistantConversationState.lifecycle("running", "run-new", ui.state.activeRunViewState), "completed",
    "kernel terminal state outranks the shell status for the same run");
  assert.equal(ui.RNAssistantConversationState.lifecycle("running", "run-next", ui.state.activeRunViewState), "running",
    "a previous run cannot change the next run's status");
  assert.equal(ui.state.chats[0].Revision, 7, "late catalog cannot regress the visible chat summary");
  assert.equal(ui.state.chats[1].Id, "chat-new", "late catalog cannot remove a chat created after its snapshot");
  assert.equal(ui.state.tools[0].Id, "tool-new", "unversioned global catalogs are not accepted from a stale response");
  const olderRunning = { Id: "chat-a", Title: "Chat A", Revision: 6,
    RunViewState: state("run-new", "running") };
  const selectedNode = ui.chatTreeNode(olderRunning);
  assert.equal(selectedNode.status, "", "selected terminal detail outranks an older running catalog entry");
  assert.equal(selectedNode.actions[1].disabled, false, "an older running catalog does not block chat actions");
  const differentRun = ui.chatTreeNode({ Id: "chat-a", Title: "Chat A", Revision: 8,
    RunViewState: state("run-next", "running") });
  assert.equal(differentRun.status, "unknown", "a newer run keeps its own status");
  assert.equal(differentRun.actions[1].disabled, true, "the next run still protects chat actions");
  const transcript = ui.state.messages;
  assert.equal(ui.applyChatPreferenceStateForChat({ chatId: "chat-a", sessionRevision: 8,
    model: "model-new", mode: "agent", reasoningEnabled: true }, "chat-a"), true);
  assert.strictEqual(ui.state.messages, transcript, "model preference keeps the transcript projection");
  assert.equal(ui.state.activeChatModel, "model-new");
  assert.equal(ui.applyChatPreferenceStateForChat({ chatId: "chat-a", sessionRevision: 7,
    model: "model-old" }, "chat-a"), false, "stale preference cannot undo a newer revision");
  assert.equal(ui.applyChatState({ activeChatId: "chat-b", sessionRevision: 2,
    detailDeferred: true, messageTotalCount: 90 }), true);
  assert.equal(ui.state.chatDetailPending, true);
  assert.equal(ui.state.messages.length, 0, "selection shell clears the previous transcript");
  assert.equal(ui.state.artifacts.length, 0, "selection shell clears the previous resource cards");
  assert.equal(ui.applyChatState({ activeChatId: "chat-b", sessionRevision: 2,
    messages: [{ Content: "detail" }] }), true);
  assert.equal(ui.state.chatDetailPending, false);
  assert.equal(ui.state.messages[0].Content, "detail");
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
  ["app-utils.js", "app-run-view-state.js", "app-agent-model.js", "app-agent-approval.js",
    "app-agent.js", "app-chat-session.js", "app-core.js", "app-chat-state.js", "app-messages.js",
    "app-chat.css", "app-agent.css"].forEach(asset => {
    assert.ok(index.includes(asset + "?v="), asset + " has a cache key");
  });
  assert.ok(index.indexOf("app-run-view-state.js") < index.indexOf("app-chat-state.js"));
  assert.ok(index.indexOf("app-run-view-state.js") < index.indexOf("app-agent-model.js"));
  console.log("PASS run view state: bridge/UI consumers use the typed projection without model-status inference");
}

{
  class Node {
    constructor() { this.children = []; this.textContent = ""; this.attributes = {}; }
    appendChild(child) { this.children.push(child); return child; }
    setAttribute(name, value) { this.attributes[name] = value; }
    addEventListener() {}
  }
  const ui = vm.createContext({});
  ui.window = ui;
  ui.state = { activeChatId: "chat" };
  ui.RNAssistantRunViewState = runView;
  ui.RNAssistantAgentApproval = { create: () => ({}) };
  ui.document = { createElement: () => new Node() };
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-agent.js"), "utf8"), ui,
    { filename: "app-agent.js" });
  const partial = runView.normalize(Object.assign(state("partial"), { Reason: "model_finish", ModelOutcome: "partial",
    ModelResults: [{ Summary: "Chart unfinished", Assessment: "partially_satisfied",
      Evidence: ["call-1"] }] }));
  const parent = new Node();
  ui.appendAgentRunViewState(parent, partial, "run-partial");
  const flatten = node => [node.textContent, ...node.children.flatMap(flatten)].join(" ");
  assert.match(flatten(parent), /Chart unfinished/);
  assert.match(flatten(parent), /call-1/);
  assert.match(flatten(parent), /достижение цели отдельно не проверено/);
  const unable = new Node();
  ui.appendAgentRunViewState(unable, runView.normalize(Object.assign(state("unable"), {
    Reason: "model_finish", ModelOutcome: "unable_to_complete" })), "unable");
  assert.match(flatten(unable), /не выполнена по оценке модели/,
    "an incomplete direct answer remains visible even without result claims");
  const ordinary = new Node();
  ui.appendAgentRunViewState(ordinary, runView.normalize(state("ordinary")), "run-ordinary");
  assert.equal(ordinary.children.length, 0, "ordinary completed answers have no service card");
  console.log("PASS run view state: partial model claim is visible without promoting task success");
}


{
  const ui = vm.createContext({});
  ui.window = ui;
  ui.chatId = chat => chat.Id || chat.id;
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-chat-state.js"), "utf8"), ui);
  const oldChat = { Id: "older", LastActivityUtc: "2026-09-01T10:00:00Z", UpdatedUtc: "2026-09-08T10:00:00Z" };
  const newChat = { id: "newer", lastActivityUtc: "2026-09-02T10:00:00Z" };
  const ids = chats => Array.from(ui.sortedChatSessions(chats), chat => ui.chatId(chat));
  assert.deepEqual(ids([oldChat, newChat]), ["newer", "older"], "save time does not determine chat order");
  assert.deepEqual(ids([newChat, oldChat]), ["newer", "older"], "navigation catalog order is irrelevant");
  const next = Object.assign({}, oldChat, { Revision: 2, LastActivityUtc: "2026-09-03T10:00:00Z" });
  const merged = runView.mergeCatalog([newChat, oldChat], [next], {}, true);
  assert.deepEqual(ids(merged), ["older", "newer"], "background activity sorts before navigation/full refresh");
  assert.deepEqual(Array.from(merged, chat => ui.chatId(chat)), ["newer", "older"], "render sorting does not mutate catalog");
  const tied = Object.assign({}, next, { Id: "another" });
  assert.deepEqual(ids([next, tied]), ids([tied, next]), "equal activity uses deterministic identity order");
  console.log("PASS chat ordering: activity ignores navigation/save time and includes background updates");
}

console.log("OK 7/7");
