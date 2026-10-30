"use strict";
// Exercise the real reducer, grouping, unit builder and DOM reconciler together.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
class Element {
  constructor(tag) { this.tagName = tag; this.childNodes = []; this.className = ""; this.attributes = {}; this._text = ""; this.innerHTML = ""; this.open = false; }
  get children() { return this.childNodes; }
  get firstChild() { return this.childNodes[0] || null; }
  get nextSibling() { const peers = this.parentNode.childNodes; return peers[peers.indexOf(this) + 1] || null; }
  get classList() { return { contains: token => this.className.split(" ").includes(token) }; }
  appendChild(node) { return this.insertBefore(node, null); }
  insertBefore(node, cursor) { if (node.parentNode) node.parentNode.removeChild(node); this.childNodes.splice(cursor ? this.childNodes.indexOf(cursor) : this.childNodes.length, 0, node); node.parentNode = this; return node; }
  removeChild(node) { this.childNodes.splice(this.childNodes.indexOf(node), 1); node.parentNode = null; }
  replaceChildren(...nodes) { this.childNodes.slice().forEach(node => this.removeChild(node)); nodes.forEach(node => this.appendChild(node)); }
  replaceWith(node) { const parent = this.parentNode; parent.insertBefore(node, this); parent.removeChild(this); }
  setAttribute(key, value) { this.attributes[key] = String(value); }
  getAttribute(key) { return this.attributes[key]; }
  addEventListener(type, listener) { (this.listeners || (this.listeners = {}))[type] = listener; }
  querySelectorAll(selector) { return walk(this).slice(1).filter(n => selector.startsWith(".") ? n.classList.contains(selector.slice(1)) : n.tagName === selector); }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  set textContent(value) { this._text = String(value); this.childNodes.forEach(n => { n.parentNode = null; }); this.childNodes = []; }
  get textContent() { return this._text + this.innerHTML + this.childNodes.map(n => n.textContent).join(""); }
}
const walk = node => [node].concat(node.childNodes.flatMap(walk));
const visibleStepMessages = root => walk(root).filter(node => {
  if (!node.classList.contains("agent-step-message") && !node.classList.contains("agent-reflection-body")) return false;
  for (let parent = node.parentNode; parent; parent = parent.parentNode) {
    if (parent.tagName === "details" && !parent.open) return false;
  }
  return true;
}).map(node => node.textContent);
const box = new Element("main");
const ctx = vm.createContext({
  state: { activeChatId: "chat", messages: [], chatRuns: {}, editingMessageIndex: -1 },
  document: { createElement: tag => new Element(tag) },
  RNAssistantAgentApproval: { create: () => ({ pendingActivity: () => null }) },
  currentActiveSend: () => true, hasActiveMessageEdit: () => false,
  markdown: text => text, enhanceMarkdown: () => {},
  appendQuestionCards: () => {}, appendMessageArtifactCards: () => {},
  smallIconButton: () => new Element("button"), copyText: () => {}, log: () => {},
  canEditMessage: () => false,
  $: () => box, renderChatResourceNavigation: () => {}, isPanelActive: () => true
});
ctx.window = ctx;
for (const name of ["app-utils.js", "app-run-view-state.js", "app-conversation-state.js", "app-agent-model.js", "app-agent.js", "app-agent-activity.js", "app-messages.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", name), "utf8"), ctx);
}
ctx.enhanceActivity = () => {};
ctx.isChatNearBottom = () => false; ctx.syncChatScroll = () => {};
ctx.renderedMessagesChatId = "chat";
const action = (step, id, status, args) => ({ RunId: "r", Kind: "tool", ToolId: "core.read", ToolCallId: id,
  StepId: step, StepMessage: "Narration " + step, Status: status, ArgumentsJson: args,
  Subtitle: "Target " + id, ResultMessage: status === "completed" ? "Read complete" : null });
const marker = step => ({ RunId: "r", Kind: "step", Status: "running", StepId: step, StepMessage: "Narration " + step });
const message = (a, index) => ({ Id: "m" + index, Role: "assistant", RunId: "r", Activity: a, Content: "Fallback " + index });
const live = [marker("s1"), action("s1", "a", "completed", '{"target":"A"}'), marker("s2"), action("s2", "b", "completed", '{"target":"B"}')];
const saved = [action("s1", "a", "completed", null), action("s2", "b", "completed", '{\n  "target": "B"\n}')];
saved[0].ArgumentsPayload = { Hash: "blob", ContentType: "application/json" };
ctx.state.messages = saved.map(message);
ctx.state.liveAgentRun = live;
function render() { ctx.reconcileMessageUnits(box, ctx.buildMessageUnits()); }
function assertFeed(steps, calls) {
  const nodes = walk(box);
  assert.equal(box.childNodes.length, 1, "one visible run article");
  assert.equal(nodes.filter(n => n.classList.contains("agent-step-message")).length, steps, "each step narration appears once");
  assert.equal(nodes.filter(n => n.classList.contains("kind-step")).length, 0, "step marker does not repeat the narration");
  assert.equal(nodes.filter(n => n.classList.contains("kind-tool")).length, calls, "each actual call appears once");
}
render();
assertFeed(2, 2);
assert.equal(walk(box).filter(n => n.classList.contains("agent-step-message")).map(n => n.textContent).join("|"), "Narration s1|Narration s2");
// A replay after a push may use inline JSON; a later call in the same step is new.
ctx.state.liveAgentRun = live.concat(action("s2", "c", "running", '{"target":"C"}'));
render(); assertFeed(2, 3);
ctx.state.liveAgentRun = live.concat(action("s2", "c", "completed", null));
ctx.state.messages.push(message(action("s2", "c", "completed", null), 2));
render(); assertFeed(2, 3);
// Repeated completion/materialization is a row update, not another call.
const reduced = [];
ctx.recordActivityTimeline(reduced, action("s1", "a", "running", '{}'));
ctx.recordActivityTimeline(reduced, action("s1", "a", "completed", null));
ctx.recordActivityTimeline(reduced, Object.assign(action("s1", "a", "completed", '{}'), { ResultMessage: "Materialized" }));
assert.equal(reduced.length, 1); assert.equal(reduced[0].ResultMessage, "Materialized");
ctx.recordActivityTimeline(reduced, action("s1", "a", "running", '{}'));
assert.equal(reduced.length, 1); assert.equal(reduced[0].Status, "completed");
// IDs remain scoped to steps and runs, even when text and tool name are identical.
assert.notEqual(ctx.activityTimelineKey(action("s1", "a", "completed", '{}')), ctx.activityTimelineKey(action("s2", "a", "completed", '{}')));
assert.notEqual(ctx.activityTimelineKey(action("s1", "a", "completed", '{}')), ctx.activityTimelineKey(Object.assign(action("s1", "a", "completed", '{}'), { RunId: "other" })));
console.log("PASS live projection: real rendering merges persisted CAS/inline results, replayed step markers and repeated call updates");
// Return to an active chat with an already loaded persisted snapshot.
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-chat-run.js"), "utf8"), ctx);
ctx.state.activeSends = { chat: {} };
ctx.renderAgentPlanDock = () => {}; ctx.renderAgentApprovalDock = () => {}; ctx.renderSendControls = () => {};
ctx.state.chatRuns.chat = { activities: ctx.state.liveAgentRun, stream: "", reasoning: "" };
ctx.state.liveAgentRun = null;
ctx.resetRenderedMessageUnits(box);
ctx.restoreActiveChatRun(); assertFeed(2, 3);
const view = { RunId: "r", TurnId: "turn", Lifecycle: "completed", ExecutionHealth: "clean", SuccessfulReads: 3,
  VerifiedWrites: 0, NoChangeWrites: 0, UnverifiedWrites: 0, FailedCalls: 0, UnknownEffects: 0 };
ctx.state.messages.push({ Id: "final", RunId: "r", Role: "assistant", Content: "Final answer", RunViewState: view });
ctx.state.liveStreamContent = "Final answer";
render();
assert.equal(box.childNodes.length, 1, "late streaming replay cannot duplicate a persisted final answer");
assert.equal(walk(box).filter(n => n.classList.contains("agent-final-step")).length, 1);
const completedOverview = walk(box).find(n => n.classList.contains("agent-run-overview"));
assert.ok(completedOverview, "completed run keeps its expandable action history");
assert.equal(completedOverview.open, false);
assert.deepEqual(visibleStepMessages(box), [],
  "completing a run folds progress comments with their actions");
completedOverview.open = true; completedOverview.listeners.toggle();
assert.deepEqual(visibleStepMessages(box), ["Narration s1", "Narration s2"],
  "expanding actions restores each original model comment once");
assert.equal(walk(completedOverview).filter(n => n.classList.contains("agent-model-step-history")).length, 2);
assert.equal(walk(completedOverview).filter(n => n.classList.contains("kind-tool")).length, 3);
completedOverview.open = false; completedOverview.listeners.toggle();
assert.equal(completedOverview.childNodes.length, 1, "collapse releases DOM while retaining disclosure state");
completedOverview.open = true; completedOverview.listeners.toggle();
assert.equal(walk(completedOverview).filter(n => n.classList.contains("agent-model-step-history")).length, 2,
  "every saved step can be reopened after completion");
ctx.resetRenderedMessageUnits(box); render();
assert.deepEqual(visibleStepMessages(box), [],
  "restoring the saved transcript folds completed progress without prior DOM state");
const restoredOverview = walk(box).find(n => n.classList.contains("agent-run-overview"));
restoredOverview.open = true; restoredOverview.listeners.toggle();
assert.deepEqual(visibleStepMessages(box), ["Narration s1", "Narration s2"],
  "reopening restored history retains every original comment");
// Distinct transcript segments of one logical run must not share a DOM cache key.
ctx.state.liveAgentRun = null; ctx.state.liveActivity = null; ctx.state.liveStreamContent = null; ctx.state.chatRuns = {};
ctx.state.messages = [message(action("s1", "a", "completed", null), 0),
  { Id: "reply", Role: "user", Content: "Continue" }, message(action("s2", "b", "completed", null), 1)];
const segments = ctx.buildMessageUnits();
assert.equal(new Set(segments.map(u => u.key)).size, segments.length, "separated run segments have unique identities");
render();
assert.equal(box.childNodes.length, 3);
console.log("PASS live projection: restoring active chats, final-stream replay and separate transcript segments");

// Applying a steer splits the same run; the prefix stays above the user's input.
ctx.state.liveAgentRun = live;
ctx.resetRenderedMessageUnits(box); render();
assert.equal(box.childNodes.length, 3);
assert.match(box.childNodes[0].messageUnitKey, /^run:r:segment:m0$/);
assert.equal(box.childNodes[1].messageUnitKey, "message:reply");
assert.equal(box.childNodes[2].messageUnitKey, "live:agent-run");
assert.equal(ctx.liveAgentTailActivities().map(a => a.StepId).join("|"), "s2|s2",
  "live replay cannot move earlier narration or actions below the applied steer");
const prefix = box.childNodes[0], reply = box.childNodes[1];
assert.deepEqual(visibleStepMessages(prefix), ["Narration s1"],
  "steering preserves progress above the user input without an explicit final");
assert.deepEqual(visibleStepMessages(prefix), ["Narration s1"],
  "the earlier comment remains inspectable above the user input");
ctx.state.liveAgentRun.push(action("s2", "c", "running", "{}"));
ctx.reconcileMessageUnits(box, ctx.buildLiveMessageUnits(), true);
assert.equal(box.childNodes[0], prefix); assert.equal(box.childNodes[1], reply);
assert.equal(box.childNodes[2].messageUnitKey, "live:agent-run");
ctx.state.messages.pop(); // New input persisted before the first following action.
render();
assert.equal(box.childNodes[0].messageUnitKey, "run:r:segment:m0");
assert.equal(box.childNodes[1].messageUnitKey, "message:reply");
ctx.state.liveAgentRun = null;
console.log("PASS live projection: applying steer preserves history order during full and stream rendering");

// A failed no-tool run remains one transcript, including every saved progress step.
const failed = { RunId: "r", TurnId: "r", Lifecycle: "failed", ExecutionHealth: "clean",
  SuccessfulReads: 0, VerifiedWrites: 0, NoChangeWrites: 0, UnverifiedWrites: 0,
  FailedCalls: 0, UnknownEffects: 0, Reason: "no_tool_progress" };
const projected = message(Object.assign(marker("state"), { Status: "completed" }), 10);
projected.RunViewState = Object.assign({}, failed, { Lifecycle: "running", Reason: "" });
ctx.state.messages = [projected];
ctx.resetRenderedMessageUnits(box); render();
assert.ok(walk(box).some(n => n.classList.contains("status-running")));
projected.RunViewState = failed;
render();
assert.ok(walk(box).some(n => n.classList.contains("agent-run") && n.classList.contains("status-failed")),
  "a lifecycle change on the same saved message updates the reconciled run");

ctx.state.messages = [{ Id: "user", Role: "user", RunId: "r", Content: "Build an app" },
  message(Object.assign(marker("s1"), { Status: "completed" }), 1),
  { Id: "protocol", Role: "assistant", RunId: "r", ProtocolMessage: true, Content: "continue" },
  message(Object.assign(marker("s2"), { Status: "completed" }), 2),
  { Id: "terminal", Role: "assistant", RunId: "r", Content: "", RunViewState: failed,
    Activity: { RunId: "r", Kind: "diagnostic", Status: "failed", ResultMessage: "No tool progress" } }];
ctx.resetRenderedMessageUnits(box);
assert.equal(ctx.buildMessageUnits().length, 2, "saved progress and terminal diagnostic belong to one run");
render();
assert.equal(walk(box).filter(n => n.classList.contains("agent-run")).length, 1);
assert.equal(walk(box).filter(n => n.classList.contains("agent-result-card")).length, 0);
assert.equal(walk(box).filter(n => n.attributes["data-runtime-health"]).length, 1);
assert.doesNotMatch(box.textContent, /Последний шаг|Подтверждено изменений/);
assert.match(box.textContent, /Модель повторила описание прогресса/);
assert.equal(walk(box).filter(n => n.classList.contains("status-running")).length, 0,
  "terminal failure does not leave earlier progress cards running");
assert.equal(walk(box).some(n => n.classList.contains("agent-run-overview")), false,
  "failure without a final answer does not fold the run");
assert.deepEqual(visibleStepMessages(box), ["Narration s1", "Narration s2"]);
assert.equal(walk(box).filter(n => n.classList.contains("kind-step")).length, 0);

ctx.state.messages = [{ Id: "cancelled", Role: "assistant", RunId: "r", Content: "Текст модели сохранён.",
  RunViewState: Object.assign({}, failed, { Lifecycle: "cancelled" }) }];
render();
assert.equal(box.textContent.split("Выполнение отменено.").length - 1, 1);
const footer = walk(box).find(n => n.classList.contains("message-footer"));
assert.equal(walk(footer).filter(n => n.classList.contains("message-outcome")).length, 0,
  "final-only run footer does not repeat the outcome from the body");

// Background inbox work can start another run without an idle state between them.
ctx.resetLiveReasoning = () => {};
ctx.state.chatRuns.chat = { runId: "old", activities: [marker("old-step")], stream: "old text", reasoning: "old reasoning" };
ctx.state.liveAgentRun = ctx.state.chatRuns.chat.activities;
ctx.state.liveStreamContent = "old text";
ctx.trackChatProgressRun("chat", "old");
assert.equal(ctx.state.chatRuns.chat.activities.length, 1, "same-run progress retains its activities");
ctx.trackChatProgressRun("chat", "next");
assert.equal(ctx.state.chatRuns.chat.runId, "next");
assert.equal(ctx.state.chatRuns.chat.activities.length, 0, "new run drops previous live activities");
assert.equal(ctx.state.liveStreamContent, "", "new run drops previous stream");
assert.equal(ctx.state.liveReasoning, "", "new run drops previous reasoning");
console.log("PASS live projection: terminal no-tool run and consecutive inbox runs stay isolated");

// A same-chat background snapshot must not erase the in-flight output for one paint.
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-chat-state.js"), "utf8"), ctx);
ctx.applyLibraryCatalogState = () => false;
ctx.renderChatSessions = () => {}; ctx.renderContext = () => {}; ctx.renderContextMeter = () => {}; ctx.renderModelControls = () => {};
ctx.state.chatProjectionRevisions = {};
ctx.state.chatRuns.chat = { runId: "r", activities: [marker("fresh")], stream: "Still streaming", reasoning: "Still reasoning" };
const renderedStreams = [], renderCurrent = ctx.renderMessages;
ctx.renderMessages = (...args) => { renderedStreams.push(ctx.state.liveStreamContent); renderCurrent(...args); };
ctx.applyChatState({ activeChatId: "chat", sessionRevision: 1,
  messages: [{ Id: "user", Role: "user", Content: "Continue working", RunId: "r" }] });
assert.deepEqual(renderedStreams, ["Still streaming"], "snapshot and restored live output paint together");
assert.equal(ctx.state.liveReasoning, "Still reasoning");
console.log("PASS live projection: same-chat refresh retains transient output without an intermediate empty paint");

// A meaningful update can cover several preparation calls, including after reload.
const preparation = [0, 1, 2].map(index => {
  const activity = action("prepare-" + index, "load-" + index, "completed", '{}');
  activity.ToolId = "core.tools.read";
  activity.StepMessage = index === 0 ? "Inspect the styles before choosing a change." : "";
  return { activity, message: { Id: "prepare-message-" + index, Role: "assistant", RunId: "r", Activity: activity, Content: "" }, index };
});
for (const active of [true, false]) {
  const article = ctx.renderAgentRunArticle({ live: active, items: preparation });
  assert.deepEqual(visibleStepMessages(article), ["Inspect the styles before choosing a change."],
    "a transcript without a final answer keeps its progress visible");
  const nodes = walk(article);
  assert.equal(nodes.filter(n => n.classList.contains("agent-step-message")).length, 1, "one update covers three tool steps");
  assert.equal(nodes.filter(n => n.classList.contains("kind-tool")).length, 3, "silent preparation keeps every tool activity");
  if (active) {
    assert.equal(nodes.filter(n => n.classList.contains("agent-model-step")).length, 1, "silent steps belong to the last comment");
    const completed = article.querySelector(".agent-completed-actions");
    assert.equal(completed.open, false, "completed silent work starts collapsed");
    assert.equal(completed.querySelectorAll(".kind-tool").length, 3);
    completed.open = true;
    const added = Object.assign(action("added", "added", "completed", '{}'), { StepMessage: "" });
    const next = ctx.renderAgentRunArticle({ live: true, items: preparation.concat({ activity: added }) });
    ctx.restoreMessageDisclosures(next, ctx.messageDisclosureSnapshot(article));
    assert.equal(next.querySelector(".agent-completed-actions").open, true, "explicit expansion survives new calls");
    assert.equal(next.querySelector(".agent-completed-actions").querySelectorAll(".kind-tool").length, 4);
  }
}
const originalPreparation = JSON.stringify(preparation);
for (const extra of [
  { Status: "running" }, { Status: "waiting" }, { Status: "failed" },
  { Status: "completed", ExecutionEvidence: { Effect: "Unknown" } },
  { Status: "completed", ResultSummary: { Kind: "resource-read", Complete: false, Partial: true } }
]) {
  const activity = Object.assign(action("next", "next", "running", '{}'), extra, { StepMessage: "" });
  const article = ctx.renderAgentRunArticle({ live: true, items: preparation.concat({ activity, message: message(activity, 3), index: 3 }) });
  const completed = article.querySelector(".agent-action-stage");
  assert.equal(completed.querySelectorAll(".kind-tool").length, extra.Status === "waiting" ? 3 : 4, "adjacent actions share a stage; confirmation remains outside");
  assert.equal(article.querySelectorAll(".kind-tool").length, 4, "attention and current activity remain outside the disclosure");
  assert.equal(article.querySelectorAll(".agent-model-step").length, 1);
}
const nextFinding = action("new-finding", "inspect", "running", '{}');
nextFinding.StepMessage = "The stylesheet is loaded; inspect the affected selector.";
const nextArticle = ctx.renderAgentRunArticle({ live: true, items: preparation.concat({ activity: nextFinding }) });
assert.equal(nextArticle.querySelectorAll(".agent-model-step").length, 2, "a new comment starts a new visible group");
nextFinding.StepMessage = preparation[0].activity.StepMessage;
for (const lifecycle of ["completed", "cancelled"]) {
  const article = ctx.renderAgentRunArticle({ items: preparation.concat({ activity: nextFinding }),
    finalMessage: { message: { Id: "terminal", Role: "assistant", RunId: "r", Content: "Final answer",
      RunViewState: Object.assign({}, view, { Lifecycle: lifecycle }) }, index: 4 } });
  assert.deepEqual(visibleStepMessages(article), [], "progress is folded after " + lifecycle);
  const history = walk(article).find(n => n.classList.contains("agent-run-overview"));
  history.open = true; history.listeners.toggle();
  assert.deepEqual(visibleStepMessages(article), preparation.map(item => item.activity.StepMessage)
    .filter(Boolean).concat(nextFinding.StepMessage),
    "expanded history keeps the original comments after " + lifecycle);
}
assert.equal(JSON.stringify(preparation), originalPreparation, "presentation never changes the stored step identities or comments");
const reflection = { Kind: "step", RunId: "r", StepId: "reflection-1", StepMessage: "Проверю результат ещё раз.",
  Title: "Промежуточное решение", Status: "completed" };
const reflectedArticle = ctx.renderAgentRunArticle({ items: [{ activity: reflection, message: message(reflection, 0), index: 0 }],
  finalMessage: { message: { Id: "reflected-final", Role: "assistant", RunId: "r", Content: "Ответ готов." }, index: 1 } });
assert.equal(reflectedArticle.querySelector(".agent-run-overview").open, true,
  "accepted reflection is expanded after the final answer");
assert.deepEqual(visibleStepMessages(reflectedArticle), ["Проверю результат ещё раз."],
  "accepted reflection remains visible in the transcript");
const reflections = ["Проверю исходные данные.", "Проверю исходные данные."].map(function (text, index) {
  const activity = { Kind: "step", RunId: "r", StepMessage: text,
    Title: "Промежуточное решение", Status: "completed" };
  return { activity, message: { Id: "reflection-" + index, Role: "assistant", RunId: "r", Activity: activity }, index };
});
const repeatedReflectionArticle = ctx.renderAgentRunArticle({ items: reflections,
  finalMessage: { message: { Id: "reflection-final", Role: "assistant", RunId: "r", Content: "Готово." }, index: 2 } });
assert.deepEqual(visibleStepMessages(repeatedReflectionArticle), ["Проверю исходные данные.", "Проверю исходные данные."],
  "distinct accepted reflections do not collapse when their text repeats");
console.log("PASS live projection: silent steps fold below commentary while active work and attention stay visible");

// Streamed text belongs to the current run, with cheap leaf-only token paints.
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-reasoning.js"), "utf8"), ctx);
ctx.state.messages = [message(action("s1", "a", "completed", '{}'), 0)];
ctx.state.liveAgentRun = [marker("s1"), action("s1", "a", "completed", '{}'),
  ctx.normalizeProgressActivity({ runId: "r", phase: "thinking" })];
ctx.state.chatRuns.chat = { runId: "r", activities: ctx.state.liveAgentRun, streamRetrying: false };
ctx.state.liveStreamContent = "Создаю документ.";
ctx.state.liveReasoning = "Проверяю структуру.";
ctx.state.liveReasoningComplete = true;
ctx.resetRenderedMessageUnits(box); render();
assert.equal(box.children.length, 1, "stream and reasoning stay inside one run article");
const streamRun = box.firstChild;
assert.equal(streamRun.classList.contains("pending"), false, "the run and draft are not faded");
assert.equal(streamRun.querySelector(".agent-run-actions").classList.contains("has-actions"), false,
  "an unfinished draft has no final-answer divider");
assert.equal(streamRun.querySelector(".agent-stream-status").textContent, "Получаю ответ модели…");
assert.ok(!streamRun.textContent.includes("Думаю…"), "streaming replaces the ambient thinking label");
const retainedAction = streamRun.querySelector(".kind-tool");
const draftBody = streamRun.querySelector(".agent-stream-message");
const reasoningDetails = streamRun.querySelector(".reasoning-block");
const reasoningBody = reasoningDetails.querySelector("pre");
reasoningDetails.open = true;
const buildLive = ctx.buildLiveMessageUnits, buildFull = ctx.buildMessageUnits;
ctx.buildLiveMessageUnits = ctx.buildMessageUnits = () => { throw new Error("token paint must not rebuild transcript units"); };
ctx.state.liveStreamContent += " Добавляю график.";
ctx.renderStreamingMessages(true);
assert.equal(box.firstChild, streamRun);
assert.equal(streamRun.querySelector(".kind-tool"), retainedAction, "prior actions remain attached");
assert.equal(streamRun.querySelector(".agent-stream-message"), draftBody, "tokens update the same paragraph container");
assert.equal(streamRun.querySelector(".reasoning-block"), reasoningDetails, "text deltas do not rebuild reasoning");
ctx.state.liveReasoning += " Проверено.";
ctx.renderStreamingMessages(true);
assert.equal(streamRun.querySelector(".reasoning-block"), reasoningDetails, "reasoning deltas retain the disclosure node");
assert.equal(reasoningDetails.querySelector("pre"), reasoningBody, "reasoning deltas update the same text container");
assert.equal(reasoningBody.textContent, ctx.state.liveReasoning);
assert.equal(streamRun.querySelector(".reasoning-block").open, true, "reasoning disclosure survives its own deltas");
ctx.buildLiveMessageUnits = buildLive; ctx.buildMessageUnits = buildFull;

// An invalidated attempt is removed before the retry emits any replacement text.
ctx.state.liveStreamContent = null; ctx.state.liveReasoning = "";
ctx.state.chatRuns.chat.streamRetrying = true;
ctx.renderStreamingMessages(true);
assert.equal(box.firstChild, streamRun, "retry updates the same current step");
assert.equal(streamRun.querySelector(".agent-stream-message"), null);
assert.equal(streamRun.querySelector(".reasoning-block"), null);
assert.equal(streamRun.querySelector(".agent-stream-status").textContent, "Повторная попытка…");
ctx.state.chatRuns.chat.streamRetrying = false;
ctx.state.liveStreamContent = "Исправленный комментарий.";
ctx.renderStreamingMessages(true);
assert.equal(streamRun.querySelector(".agent-stream-message").textContent, "Исправленный комментарий.");

const accepted = Object.assign(action("s2", "b", "running", '{}'), { StepMessage: ctx.state.liveStreamContent });
ctx.state.liveStreamContent = null;
ctx.state.liveAgentRun.push(accepted);
render();
assert.equal(box.querySelectorAll(".agent-stream-step").length, 0, "accepted text retires its draft");
assert.equal(box.textContent.split("Исправленный комментарий.").length - 1, 1, "the accepted comment appears once");
ctx.state.messages.push(message(Object.assign({}, accepted, { Status: "completed" }), 1),
  { Id: "stream-final", Role: "assistant", RunId: "r", Content: "Готово.", RunViewState: view });
ctx.state.liveStreamContent = "Готово.";
render();
assert.equal(box.querySelectorAll(".agent-stream-step").length, 0, "a saved final answer suppresses late stream replay");
assert.equal(box.querySelector(".agent-run-actions").classList.contains("has-actions"), true,
  "the divider appears for the accepted final answer");
console.log("PASS live stream: inline drafts, retry invalidation, stable leaf updates and accepted/final handoff");

for (const lifecycle of ["failed", "cancelled"]) {
  ctx.state.messages = [message(action("s1", "a", "completed", '{}'), 0),
    { Id: "stopped", Role: "assistant", RunId: "r", Content: "", RunViewState: { ...failed, Lifecycle: lifecycle },
      Activity: { RunId: "r", Kind: "diagnostic", Status: lifecycle, ResultMessage: "Stopped" } }];
  ctx.state.liveStreamContent = "Unaccepted draft";
  render();
  assert.equal(box.querySelectorAll(".agent-stream-step").length, 0, lifecycle + " clears the draft before the idle inbox arrives");
  assert.equal(box.querySelectorAll(".live").length, 0, "a saved terminal state cannot keep the run active");
  assert.equal(box.querySelectorAll(".message-outcome").length, 1, "the terminal warning stays visible");
  ctx.state.liveStreamContent += " late delta";
  ctx.renderStreamingMessages(true);
  assert.ok(!box.textContent.includes("Unaccepted draft"), "late output cannot revive a stopped draft");
}
const nextFrames = [];
ctx.requestAnimationFrame = callback => nextFrames.push(callback);
ctx.trackChatProgressRun("chat", "next-stream-run");
ctx.state.messages.push({ Id: "next-user", Role: "user", Content: "New request" });
ctx.state.liveStreamContent = "New draft";
ctx.scheduleLiveStreamRender(); nextFrames.shift()();
assert.equal(box.querySelector(".agent-stream-message").textContent, "New draft",
  "the next run can start with streaming after the preceding run suppressed its replay");
assert.equal(box.querySelector(".live").querySelectorAll(".kind-tool").length, 0,
  "a new run does not inherit the old run's live actions");
console.log("PASS live stream: terminal failure/cancel suppress late drafts and the next run starts independently");
