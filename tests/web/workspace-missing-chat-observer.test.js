"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), vm = require("node:vm");
const timers = new Map(), frames = new Map(), views = [];
let next = 0, readCount = 0, readMode = null;
const retainedDraft = Object.freeze({ text: "Keep this unsent workspace text", attachments: [] });
const context = {
  state: { activeChatId: "selected-live", bridgeUnavailable: false },
  setInterval(fn) { timers.set(++next, fn); return next; }, clearInterval(id) { timers.delete(id); },
  setTimeout(fn) { frames.set(++next, fn); return next; }, clearTimeout(id) { frames.delete(id); },
  requestAnimationFrame(fn) { frames.set(++next, fn); return next; }, cancelAnimationFrame(id) { frames.delete(id); },
  readWorkspaceDraft: () => retainedDraft,
  updateWorkspaceDraft: () => retainedDraft,
  workspaceComposerTarget: target => ({ source: "workspace", workspaceId: target.workspaceId, sessionId: target.sessionId }),
  settingsModel: () => "test-model",
  effectiveModelSupportsReasoning: () => true,
  RNAssistantRunViewState: { normalize: value => value || {}, runNotice: () => ({}) },
  RNAssistantProjectChats: {
    read() { readCount += 1; return readMode(); },
    refreshTree() {}, focus() {}
  },
  RNAssistantDockWorkspace: { setTitle() {}, mount() {} },
  RNAssistantConversationPanel: { selected: () => null, key: () => "workspace-chat", open() {} },
  RNAssistantConversationInput: { isPending: () => false },
  RNAssistantChatInspector: { render() {} },
  RNAssistantPlugins: { onChatSnapshot() {} },
  RNAssistantChatDrafts: {}, RNAssistantAgentCommunication: {}, RNAssistantIcons: {}
};
context.window = context;
vm.createContext(context);
for (const name of ["app-conversation-state", "app-conversation-observer", "app-conversation-view", "app-workspace-chat"])
  vm.runInContext(fs.readFileSync("web/js/" + name + ".js", "utf8"), context, { filename: name + ".js" });
const view = context.RNAssistantConversationView.get({ children: [], scrollTop: 0, scrollHeight: 100, clientHeight: 100 });
views.push(view);
const target = Object.freeze({ source: "workspace", workspaceId: "workspace", sessionId: "missing" });
view.begin(target);
const errors = [];
const component = { target, view, composer: { edit: () => null }, node: () => ({}), contextOwner: "project",
  loading: true, hasSnapshot: false, loadError: "", isCurrent: () => true,
  render() {}, controls() {}, stream() {}, notice() {},
  loadFailed(error) { errors.push(error); component.loading = false; component.loadError = error.detail || error.message; }
};
const adapter = context.RNAssistantWorkspaceChat.createPanelAdapter(component);
const missing = () => Object.assign(new Error("Проектный чат недоступен."), { code: "project_chat_missing" });
const transient = () => Object.assign(new Error("Temporary bridge failure."), { code: "bridge_request_failed" });
const snapshot = (revision, status = "running") => ({ sessionId: "missing", project: { workspaceId: "workspace", available: true },
  messages: [], state: { status, runId: "run-" + revision, revision, canSteer: false, title: "Workspace chat" } });
const tick = () => new Promise(resolve => setImmediate(resolve));
async function pollTimers() { [...timers.values()].forEach(fn => fn()); await tick(); }
(async () => {
  adapter.accept(snapshot(1));
  adapter.activate();
  assert.equal(timers.size, 1, "a cached running Workspace chat starts one exact run observer");
  readMode = () => Promise.reject(missing());
  await pollTimers();
  assert.equal(readCount, 1);
  assert.equal(errors[0].code, "project_chat_missing");
  assert.equal(timers.size, 0, "typed missing stops only this run observer");
  for (let i = 0; i < 4; i++) await pollTimers();
  assert.equal(readCount, 1, "10-second ticks cannot repeat a missing project chat read");
  adapter.activate();
  view.setVisible(false); view.setVisible(true, () => {}); await tick();
  assert.equal(timers.size, 0, "activation and hide/show cannot restart the missing view");
  assert.equal(context.state.activeChatId, "selected-live", "missing read does not substitute another chat");
  assert.equal(adapter.draft(), retainedDraft, "missing read preserves the workspace draft");

  readMode = () => Promise.resolve(snapshot(2));
  await adapter.refresh();
  assert.equal(component.loadError, "", "successful explicit retry clears the missing state");
  assert.equal(timers.size, 1, "successful retry restores the same exact running chat observer");
  readMode = () => Promise.reject(transient());
  await pollTimers();
  assert.equal(readCount, 3, "the recovered run observer reads normally");
  assert.equal(timers.size, 1, "transient failures do not become terminal missing state");

  readMode = () => Promise.resolve(snapshot(3, "completed"));
  await pollTimers();
  assert.equal(timers.size, 0, "completed run stops polling");
  view.dispose(); assert.equal(timers.size, 0);
  console.log("PASS workspace missing chat: terminal observer, exact retry, preserved draft and transient recovery");
})().catch(error => { console.error(error); process.exitCode = 1; });
