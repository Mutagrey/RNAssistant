"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), vm = require("node:vm");
const timers = new Map(), frames = new Map();
let sequence = 0;
const context = {
  setInterval(fn) { timers.set(++sequence, fn); return sequence; }, clearInterval(id) { timers.delete(id); },
  requestAnimationFrame(fn) { frames.set(++sequence, fn); return sequence; }, cancelAnimationFrame(id) { frames.delete(id); }
};
context.window = context; vm.createContext(context);
for (const name of ["app-run-view-state", "app-conversation-state", "app-conversation-observer", "app-conversation-view", "app-workspace-chat"])
  vm.runInContext(fs.readFileSync("web/js/" + name + ".js", "utf8"), context, { filename: name + ".js" });
const tick = () => new Promise(resolve => setImmediate(resolve));
function snapshot(sessionId, runId, status, revision) {
  return { project: { workspaceId: "workspace" }, sessionId, messages: [], messageStartIndex: 0, messageTotalCount: 0,
    state: { runId, status, revision, cursor: "cursor-" + revision } };
}
function address(value) { return { source: "workspace", workspaceId: value.project.workspaceId, sessionId: value.sessionId }; }
function notice(sessionId, runId, cursor) { return { workspaceId: "workspace", sessionId, runId, phase: "session_changed", cursor }; }
function setup(initial) {
  const view = context.RNAssistantConversationView.get({ children: [], scrollTop: 0, scrollHeight: 100, clientHeight: 100 });
  const pending = [], accepted = [], errors = [], paints = [];
  let owner, observer;
  function select(value) {
    const target = address(value); view.begin(target);
    owner = view.bindProjection(context.RNAssistantConversationState.createSnapshotSource(target, address),
      context.RNAssistantWorkspaceChat.latestSnapshot);
    owner.accept(value);
    if (observer) observer.select(owner.snapshot(), false);
  }
  select(initial);
  observer = context.RNAssistantWorkspaceChat.createRunObserver(view,
    target => new Promise((resolve, reject) => pending.push({ target, resolve, reject })),
    value => { owner.accept(value); accepted.push(value); observer.select(owner.snapshot(), false); },
    error => errors.push(error), value => paints.push(value));
  observer.select(owner.snapshot(), false);
  return { view, observer, select, pending, accepted, errors, paints, snapshot: () => owner.snapshot() };
}
(async () => {
  const current = setup(snapshot("chat", "finished-run", "completed", 1));
  assert.equal(timers.size, 0, "a completed chat does not poll");
  current.observer.acceptProgress(notice("chat", "queued-run", "cursor-2")); await tick();
  assert.equal(current.pending.length, 1, "a new run's session notice refreshes the exact completed chat");
  assert.equal(current.pending[0].target.sessionId, "chat");
  assert.equal(timers.size, 0, "the notice alone cannot declare the next run active");
  current.pending[0].resolve(snapshot("chat", "queued-run", "running", 2)); await tick();
  assert.equal(current.snapshot().state.runId, "queued-run");
  assert.equal(timers.size, 1, "the canonical running snapshot enables existing fallback observation");

  const progress = { workspaceId: "workspace", sessionId: "chat", runId: "queued-run", phase: "model_progress",
    modelProgress: { runId: "queued-run", attemptId: "attempt", sequence: 1, revision: 1, message: "Current answer" } };
  current.observer.acceptProgress(progress);
  assert.equal(current.paints.length, 1);
  current.observer.acceptProgress(Object.assign({}, progress, { runId: "finished-run", modelProgress: {
    runId: "finished-run", attemptId: "old-attempt", sequence: 99, revision: 99, message: "Obsolete answer" } }));
  current.observer.acceptProgress(Object.assign(notice("chat", "foreign-run", "foreign-cursor"), { workspaceId: "other-workspace" }));
  current.observer.acceptProgress(notice("other-chat", "foreign-run", "foreign-cursor"));
  current.observer.acceptProgress(notice("chat", "queued-run", "cursor-2")); await tick();
  assert.equal(current.pending.length, 1, "foreign addresses and unchanged cursors cause no reads");
  assert.equal(current.paints.length, 1, "a previous run's model progress cannot repaint the current run");
  assert.equal(current.snapshot().state.modelProgress.message, "Current answer");
  current.observer.acceptProgress(notice("chat", "queued-run", "cursor-3")); await tick();
  current.pending[1].resolve(snapshot("chat", "queued-run", "completed", 3)); await tick();
  assert.equal(timers.size, 0, "terminal refresh stops polling without an idle timer");
  current.view.dispose();

  const hidden = setup(snapshot("hidden-chat", "finished-run", "completed", 1));
  hidden.view.setVisible(false);
  hidden.observer.acceptProgress(notice("hidden-chat", "queued-run", "cursor-2"));
  hidden.observer.acceptProgress(notice("hidden-chat", "queued-run", "cursor-3")); await tick();
  assert.equal(hidden.pending.length, 0, "hidden views defer cross-run invalidation");
  hidden.view.setVisible(true, () => {}); await tick();
  assert.equal(hidden.pending.length, 1, "returning to the idle view performs one catch-up read");
  hidden.pending[0].resolve(snapshot("hidden-chat", "queued-run", "completed", 3)); await tick();
  assert.equal(hidden.snapshot().state.runId, "queued-run");
  assert.equal(timers.size, 0, "catch-up of an already completed run does not introduce polling");
  hidden.view.dispose();

  const rebound = setup(snapshot("original-chat", "finished-run", "completed", 1));
  rebound.observer.acceptProgress(notice("original-chat", "queued-run", "cursor-2")); await tick();
  rebound.select(snapshot("different-chat", "other-run", "completed", 1));
  rebound.select(snapshot("original-chat", "newer-run", "completed", 3));
  rebound.pending[0].resolve(snapshot("original-chat", "queued-run", "running", 2)); await tick();
  assert.equal(rebound.accepted.length, 0, "A-to-B-to-A cannot accept the previous binding's refresh");
  assert.equal(rebound.snapshot().state.runId, "newer-run");
  rebound.observer.acceptProgress(notice("original-chat", "last-run", "cursor-4")); await tick();
  rebound.view.dispose();
  rebound.pending[1].resolve(snapshot("original-chat", "last-run", "running", 4)); await tick();
  rebound.observer.acceptProgress(notice("original-chat", "last-run", "cursor-5")); await tick();
  assert.equal(rebound.accepted.length, 0, "disposal rejects late refreshes and cannot revive the observer");
  assert.equal(rebound.pending.length, 2);
  assert.equal(rebound.errors.length, 0);
  assert.equal(timers.size, 0); assert.equal(frames.size, 0);
  console.log("PASS workspace run observation: cross-run notices, exact address, stale model progress, hidden catch-up and binding disposal");
})().catch(error => { console.error(error); process.exitCode = 1; });
