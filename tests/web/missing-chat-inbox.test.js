"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), vm = require("node:vm");
const timers = new Map(), frames = new Map(), calls = [], inboxReplies = new Map(), historyReplies = new Map(), views = [];
let next = 0;
const context = {
  state: { activeChatId: "selected-live", activeSends: {}, chatRuns: {}, chatInboxes: {}, bridgeUnavailable: false,
    chatDrafts: { "office:missing": { text: "Keep this unsent text", attachments: [{ id: "retained-draft" }] } },
    inboxSubmissions: { missing: { OperationId: "unconfirmed-operation", Text: "Keep pending input" } } },
  setInterval(fn) { timers.set(++next, fn); return next; }, clearInterval(id) { timers.delete(id); },
  requestAnimationFrame(fn) { frames.set(++next, fn); return next; }, cancelAnimationFrame(id) { frames.delete(id); },
  createOfficeChatSnapshot: id => ({ activeChatId: id, sessionRevision: null, messages: [], artifacts: [] }),
  mergeOfficeChatSnapshot: (previous, incoming) => Object.assign({}, previous, incoming),
  chatInputEchoes: () => [], reconcileChatInputEchoes() {}, applyChatCatalogState() {},
  RNAssistantConversationInput: { isPending: () => false },
  readOfficeDraft: id => context.state.chatDrafts["office:" + id],
  send(type, payload) {
    calls.push({ type, id: payload.chatId });
    return inboxReplies.get(payload.chatId)();
  },
  loadChatState: id => context.officeChatSource(id).read(() => historyReplies.get(id)()),
  applyChatInbox: inbox => { context.state.chatInboxes[inbox.chatId] = inbox; },
  chatInbox: id => context.state.chatInboxes[id] || { running: false, items: [] }
};
context.window = context; vm.createContext(context);
for (const name of ["app-conversation-state", "app-conversation-observer", "app-conversation-view", "app-office-chat-view"])
  vm.runInContext(fs.readFileSync("web/js/" + name + ".js", "utf8"), context, { filename: name + ".js" });
context.officeChatSource = id => context.RNAssistantConversationState.createSnapshotSource(
  { source: "office", sessionId: id }, response => ({ source: "office", sessionId: response.activeChatId }));
const missing = () => Object.assign(new Error("Чат недоступен в текущем хранилище."), { code: "chat_session_not_found" });
const failure = code => Object.assign(new Error("Temporary read failure"), { code });
const tick = () => new Promise(resolve => setImmediate(resolve));
async function pollTimers() { [...timers.values()].forEach(fn => fn()); await tick(); }
function setup(id) {
  const target = { source: "office", sessionId: id };
  const view = context.RNAssistantConversationView.get({ children: [], scrollTop: 0, scrollHeight: 100, clientHeight: 100 });
  view.begin(target); views.push(view);
  const errors = [], component = { target, view, node() {}, composer: { edit: () => null },
    render() {}, isCurrent: () => !view.isDisposed(), loading: true, hasSnapshot: false, loadError: "",
    loadFailed(error) { errors.push(error); component.loading = false; component.loadError = error.detail || error.message; }
  };
  const adapter = context.RNAssistantOfficeChatView.create(component);
  return { target, view, component, adapter, errors };
}
function count(id) { return calls.filter(call => call.id === id).length; }
function deferred() { let resolve, reject; const promise = new Promise((done, fail) => { resolve = done; reject = fail; }); return { promise, resolve, reject }; }
(async () => {
  const draftBefore = JSON.stringify(context.state.chatDrafts), pendingBefore = JSON.stringify(context.state.inboxSubmissions);
  const absent = setup("missing"), other = setup("other");
  inboxReplies.set("missing", () => Promise.reject(missing()));
  inboxReplies.set("other", () => Promise.resolve({ chatId: "other", running: true, items: [] }));
  absent.adapter.activate(); other.adapter.activate();
  await absent.adapter.pollInbox();
  assert.equal(count("missing"), 1);
  assert.equal(absent.errors[0].code, "chat_session_not_found");
  assert.equal(timers.size, 1, "typed missing stops only the affected view's polling");
  for (let i = 0; i < 4; i++) await pollTimers();
  assert.equal(count("missing"), 1, "1200ms ticks cannot repeat the terminal missing-session request");
  assert.ok(count("other") >= 4, "another exact chat continues observing normally");
  absent.adapter.activate(); await absent.adapter.pollInbox();
  absent.view.setVisible(false); absent.view.setVisible(true, () => {}); await tick();
  assert.equal(count("missing"), 1, "activation, draft receipt polling and hide/show cannot restart a missing view");
  assert.equal(absent.adapter.commands.preferences().disabled, true, "missing view retains its explicit unavailable state");
  assert.equal(context.state.activeChatId, "selected-live", "missing read does not select or substitute another chat");
  assert.equal(JSON.stringify(context.state.chatDrafts), draftBefore);
  assert.equal(JSON.stringify(context.state.inboxSubmissions), pendingBefore, "unconfirmed input is preserved for user recovery");

  historyReplies.set("missing", () => Promise.resolve({ activeChatId: "other", sessionRevision: 1, messages: [] }));
  await absent.adapter.refresh();
  assert.equal(timers.size, 1, "a mismatched manual-retry response cannot resume the missing view");
  assert.equal(absent.adapter.snapshot().activeChatId, "missing");
  historyReplies.set("missing", () => Promise.resolve({ activeChatId: "missing", sessionRevision: 1,
    messages: [{ Id: "recovered", Content: "Exact chat became available" }], inbox: { chatId: "missing", running: false, items: [] } }));
  inboxReplies.set("missing", () => Promise.resolve({ chatId: "missing", running: false, items: [] }));
  await absent.adapter.refresh();
  assert.equal(absent.component.loadError, "");
  assert.equal(absent.adapter.snapshot().messages[0].Id, "recovered");
  assert.equal(timers.size, 2, "successful explicit reload restores the normal observer lifecycle");
  await absent.adapter.pollInbox(); assert.equal(count("missing"), 2);
  assert.equal(context.state.activeChatId, "selected-live", "recovery still does not change active selection");

  const busy = setup("busy");
  inboxReplies.set("busy", () => Promise.reject(failure("chat_storage_busy")));
  busy.adapter.activate(); await busy.adapter.pollInbox(); await pollTimers();
  assert.equal(count("busy"), 2, "transient storage contention retains normal polling");
  inboxReplies.set("busy", () => Promise.reject(failure("bridge_request_failed")));
  await pollTimers();
  assert.equal(count("busy"), 3, "an unclassified failure is never treated as confirmed session absence");
  assert.equal(busy.errors.length, 0);

  const missingHistory = setup("history-missing"), oldInbox = deferred();
  inboxReplies.set("history-missing", () => oldInbox.promise);
  historyReplies.set("history-missing", () => Promise.reject(missing()));
  missingHistory.adapter.activate();
  const inboxRead = missingHistory.adapter.pollInbox(); await tick();
  await missingHistory.adapter.refresh();
  oldInbox.resolve({ chatId: "history-missing", running: true, items: [] }); await inboxRead;
  assert.equal(context.state.chatInboxes["history-missing"], undefined,
    "a reply dispatched before the missing history result cannot revive its inbox");
  const missingHistoryCalls = count("history-missing"); await pollTimers();
  assert.equal(count("history-missing"), missingHistoryCalls, "history failure also stops exact inbox polling");

  const late = setup("late-history"), oldHistory = deferred();
  historyReplies.set("late-history", () => oldHistory.promise);
  inboxReplies.set("late-history", () => Promise.reject(missing()));
  late.adapter.activate();
  const historyRead = late.adapter.refresh({ latestMessages: true }); await tick();
  await late.adapter.pollInbox();
  oldHistory.resolve({ activeChatId: "late-history", sessionRevision: 1, messages: [{ Id: "stale" }] }); await historyRead;
  assert.equal(late.adapter.snapshot().messages.length, 0, "late latest-page completion cannot clear the missing state");
  assert.equal(late.errors[0].code, "chat_session_not_found");
  assert.ok(late.component.loadError);

  const recovered = setup("recovered"), rejectedOldInbox = deferred();
  inboxReplies.set("recovered", () => rejectedOldInbox.promise);
  historyReplies.set("recovered", () => Promise.reject(missing()));
  recovered.adapter.activate();
  const oldRead = recovered.adapter.pollInbox(); await tick();
  await recovered.adapter.refresh();
  historyReplies.set("recovered", () => Promise.resolve({ activeChatId: "recovered", sessionRevision: 1, messages: [] }));
  await recovered.adapter.refresh();
  rejectedOldInbox.reject(missing()); await oldRead;
  assert.equal(recovered.component.loadError, "", "obsolete inbox rejection cannot override a newer successful manual reload");
  assert.equal(recovered.errors.length, 1, "only the current missing history request reaches the view");

  views.forEach(view => view.dispose()); assert.equal(timers.size, 0);
  console.log("PASS missing chat inbox: exact stop, independent views, preserved drafts, transient retry, manual recovery and stale replies");
})().catch(error => { console.error(error); process.exitCode = 1; });
