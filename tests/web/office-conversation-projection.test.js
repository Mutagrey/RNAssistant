"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), vm = require("node:vm");
const ui = vm.createContext({ console }); ui.window = ui;
for (const file of ["app-core", "app-conversation-state", "app-conversation-view", "app-run-view-state",
  "app-chat-composer", "app-workspace-chat", "app-chat-state", "app-chat-session", "app-models"])
  vm.runInContext(fs.readFileSync("web/js/" + file + ".js", "utf8"), ui, { filename: file });
for (const name of ["captureChatDraft", "restoreChatDraft", "renderChatSessions", "renderMessages", "renderContext",
  "renderContextMeter", "renderModelControls", "renderSendControls", "syncTokenEstimateCalibrationFromUsage"])
  ui[name] = () => {};
ui.$ = () => null; ui.log = () => { throw Error("A stale read must not report an error"); };
const rows = (start, count) => Array.from({ length: count }, (_, i) => ({ Id: "m-" + (start + i) }));
const data = (id, revision, start = 0, count = 80, total = 1000) => ({ activeChatId: id, sessionRevision: revision,
  messages: rows(start, count), messageStartIndex: start, messageTotalCount: total });
{
  // Two sources and their render inputs must work without consulting selected UI state.
  const isolated = vm.createContext({}); isolated.window = isolated;
  for (const file of ["app-conversation-state", "app-conversation-view", "app-run-view-state", "app-chat-state", "app-messages"])
    vm.runInContext(fs.readFileSync("web/js/" + file + ".js", "utf8"), isolated, { filename: file });
  isolated.state = new Proxy({}, { get() { throw Error("An addressed source read global state"); },
    set() { throw Error("An addressed source wrote global state"); } });
  const a = isolated.createOfficeChatProjection("a"), b = isolated.createOfficeChatProjection("b");
  const responseA = { ...data("a", 7, 0, 1000), activeChatModel: "model-a", activeChatMode: "plan",
    activeChatReasoning: true, context: { Items: ["context-a"] }, artifacts: [{ Id: "artifact-a" }],
    htmlWorkspace: { files: ["editor-owned"] }, tools: ["catalog-owned"] };
  const responseB = { ...data("b", 2), activeChatModel: "model-b", context: { Items: ["context-b"] } };
  a.accept(responseA); b.accept(responseB);
  assert.equal(a.snapshot().messages.length, 240); assert.equal(a.snapshot().messageStartIndex, 760);
  assert.equal(responseA.messages.length, 1000, "normalization does not mutate the bridge response");
  assert.equal(a.snapshot().context, responseA.context); assert.equal(a.snapshot().artifacts, responseA.artifacts);
  assert.equal(b.snapshot().messages, responseB.messages, "bounded input is borrowed without another history array");
  assert.equal(a.snapshot().htmlWorkspace, undefined); assert.equal(a.snapshot().tools, undefined);
  assert.throws(() => a.accept(responseB), /другого чата/);
  const before = a.snapshot();
  a.accept({ ...data("a", 6), activeChatModel: "stale" });
  assert.equal(a.snapshot(), before, "stale delivery does not replace any presentation field");
  a.accept({ activeChatId: "a", activeChatModel: "unversioned" });
  assert.equal(a.snapshot(), before, "a known revision cannot regress to unversioned data");
  a.accept(data("a", 7, 0)); a.accept(data("a", 7, 920));
  assert.equal(a.snapshot().messageStartIndex, 0, "same-revision refresh retains the reading window");
  a.accept(data("a", 7, 920), { latestMessages: true });
  assert.equal(a.snapshot().messageStartIndex, 920);
  assert.equal(a.snapshot().activeChatModel, "model-a", "partial snapshots retain only their own preferences");
  a.snapshot().liveRun = { runId: "run-a", activities: [], stream: "A streaming", reasoning: "A reasoning" };
  b.snapshot().liveRun = { runId: "run-b", activities: [], stream: "B streaming" };
  const viewA = isolated.RNAssistantConversationView.get({ children: [] }); viewA.begin(a.target);
  const viewB = isolated.RNAssistantConversationView.get({ children: [] }); viewB.begin(b.target);
  const presentationA = isolated.officeMessagePresentation(viewA, null, a.snapshot());
  const presentationB = isolated.officeMessagePresentation(viewB, null, b.snapshot());
  assert.equal(presentationA.data.messages, a.snapshot().messages);
  assert.equal(presentationB.data.messages, b.snapshot().messages);
  assert.equal(presentationA.data.live.content, "A streaming");
  assert.equal(presentationB.data.live.content, "B streaming");
  assert.throws(() => isolated.officeMessagePresentation(viewA, null, b.snapshot()), /не соответствует/);
  const retainedB = b.snapshot(), domainRun = a.snapshot().liveRun;
  a.accept({ activeChatId: "a", sessionRevision: 8, detailDeferred: true, messageTotalCount: 1000 });
  assert.equal(a.snapshot().messages.length, 0); assert.equal(a.snapshot().artifacts.length, 0);
  assert.equal(a.snapshot().liveRun, domainRun, "detail loading does not clear the transient run");
  assert.equal(b.snapshot(), retainedB);
  a.dispose(); assert.equal(a.snapshot(), null);
  assert.equal(a.accept(responseA), null, "late full delivery cannot recreate a disposed source");
  assert.equal(b.snapshot(), retainedB);
  viewA.dispose(); viewB.dispose(); b.dispose();
  console.log("PASS Office source factory: independent normalization/render inputs, revisions, bounds and release without selected state");
}
assert.equal(ui.state.messages.length, 0);
assert.equal(ui.officeChatProjection(), null);
ui.applyChatState({ activeChatId: "", messages: [], activeChatMode: "agent" });
assert.equal(ui.officeChatProjection(), null, "no document selection does not manufacture a chat");
ui.applyChatState({ ...data("a", 4, 0, 1000), activeChatModel: "model-a", activeChatMode: "plan", activeChatReasoning: true });
const first = ui.officeChatProjection();
assert.equal(ui.state.messages, first.snapshot().messages, "the legacy facade borrows the only retained history");
assert.equal(ui.state.messages.length, 240); assert.equal(ui.state.messageStartIndex, 760);
for (const key of ["messages", "messageStartIndex", "messageTotalCount", "activeChatModel", "activeChatMode", "activeChatReasoning"])
  assert.equal(typeof Object.getOwnPropertyDescriptor(ui.state, key).get, "function", key + " has no second stored value");
ui.applyChatState(data("a", 3)); assert.equal(ui.state.messageStartIndex, 760, "older revision remains rejected");
ui.applyChatState(data("a", 4, 0));
ui.applyChatState(data("a", 4, 920)); assert.equal(ui.state.messageStartIndex, 0, "refresh retains the earlier reading window");
ui.applyChatState(data("a", 4, 920), false, { latestMessages: true }); assert.equal(ui.state.messageStartIndex, 920);
assert.equal(ui.officeChatProjection(), first);
ui.applyChatState(data("a", 4, 0));
ui.applyChatPreferenceStateForChat({ chatId: "a", sessionRevision: 5, model: "updated" }, "a");
ui.applyChatState(data("a", 5, 920));
assert.equal(ui.state.messageStartIndex, 0, "a revision learned from preference ACK retains the reading window on refresh");

// A hidden Office source is independent of the currently mounted Workspace view.
const workspaceView = ui.RNAssistantConversationView.get({ children: [] });
const workspace = workspaceView.bindProjection(ui.RNAssistantWorkspaceChat.snapshotSource({ workspaceId: "w", sessionId: "a" }),
  ui.RNAssistantWorkspaceChat.latestSnapshot);
const workspaceData = { project: { workspaceId: "w" }, sessionId: "a", state: { revision: 1 }, messages: rows(0, 1) };
workspace.accept(workspaceData);
assert.equal(ui.applyPushedChatState({ scope: "full", payload: data("a", 5) }), true);
assert.equal(workspace.snapshot(), workspaceData);
assert.equal(ui.officeChatProjection(), first);

(async () => {
  let rejectRead;
  ui.state.chatNavigationVersion = 7;
  ui.state.messageStartIndex = 80;
  ui.send = () => new Promise((resolve, reject) => { rejectRead = reject; });
  const paging = ui.loadPreviousChatMessages();
  const service = ui.officePreferenceService({ target: { sessionId: "a" } });
  ui.clearOfficeChatProjection(); // Reconnect may select the same ID with the same navigation version.
  ui.applyChatState(data("a", 5));
  assert.notEqual(ui.officeChatProjection(), first);
  assert.equal(first.snapshot(), null); assert.equal(first.isCurrent(), false);
  assert.equal(service.apply({ chatId: "a", sessionRevision: 6, model: "late" }, { name: "model", value: "late" }), false);
  rejectRead(Error("late page from old binding")); await paging;
  assert.equal(ui.state.activeChatModel, "", "a replacement source does not retain old preferences");
  const returned = ui.officeChatProjection();
  ui.applyChatState(data("b", 1));
  assert.equal(returned.snapshot(), null);
  ui.applyChatState({ activeChatId: "a", sessionRevision: 5, detailDeferred: true, messageTotalCount: 1000 });
  assert.equal(ui.state.messages.length, 0);
  ui.clearOfficeChatProjection();
  assert.equal(ui.applyPushedChatState({ scope: "full", payload: data("a", 6) }), false,
    "background delivery cannot recreate a released Office source");
  assert.equal(ui.officeChatProjection(), null); assert.equal(ui.state.messages.length, 0);
  assert.equal(workspace.snapshot(), workspaceData);
  ui.selectOfficeChatProjection("a");
  const running = ui.state.chatRuns.a = { activities: [], stream: "old live text" };
  ui.publishOfficeChatRun("a", running);
  ui.applyChatState({ activeChatId: "", messages: [], activeChatMode: "agent" });
  assert.equal(ui.state.liveStreamContent, null, "empty selection also clears the old live presentation");
  assert.equal(ui.state.chatRuns.a, running, "releasing presentation does not cancel a domain run");
  assert.equal(ui.state.activeChatMode, "agent");
  workspaceView.dispose();
  console.log("PASS Office projection: one shared owner, bounded history, independent background delivery, released callbacks and empty selection");
})().catch(error => { console.error(error); process.exitCode = 1; });
