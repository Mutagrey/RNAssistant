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
  ui.state.liveStreamContent = "old live text";
  ui.applyChatState({ activeChatId: "", messages: [], activeChatMode: "agent" });
  assert.equal(ui.state.liveStreamContent, null, "empty selection also clears the old live presentation");
  assert.equal(ui.state.activeChatMode, "agent");
  workspaceView.dispose();
  console.log("PASS Office projection: one shared owner, bounded history, independent background delivery, released callbacks and empty selection");
})().catch(error => { console.error(error); process.exitCode = 1; });
