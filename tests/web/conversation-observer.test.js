"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), vm = require("node:vm");
const timers = new Map(), frames = new Map(), window = { next: 0,
  setInterval(fn) { timers.set(++this.next, fn); return this.next; },
  clearInterval(id) { timers.delete(id); },
  requestAnimationFrame(fn) { frames.set(++this.next, fn); return this.next; },
  cancelAnimationFrame(id) { frames.delete(id); }
};
vm.runInNewContext(["app-conversation-observer", "app-conversation-view"]
  .map(name => fs.readFileSync("web/js/" + name + ".js", "utf8")).join("\n"), { window });
const tick = () => new Promise(resolve => setImmediate(resolve));
function setup(source, sessionId, shouldPoll) {
  const box = { children: [], scrollTop: 0, scrollHeight: 100, clientHeight: 100 };
  let target = { source, sessionId, generation: 1 };
  const view = window.RNAssistantConversationView.get(box), pending = [], applied = [], errors = [];
  view.begin({ source, sessionId });
  const observer = view.observe({ current: () => target, shouldPoll,
    read: address => new Promise((resolve, reject) => pending.push({ address, resolve, reject })),
    changed: (value, address) => applied.push([value, address.sessionId]),
    failed: error => errors.push(error.message) });
  return { view, observer, pending, applied, errors,
    select(value) { target = value; if (value) view.begin({ source: value.source, sessionId: value.sessionId }); } };
}
(async () => {
  const a = setup("office", "a"), b = setup("workspace", "a");
  a.observer.start(); a.observer.start(); b.observer.start();
  assert.equal(timers.size, 2, "each view owns one poll timer");
  const first = a.observer.readNow(); assert.equal(a.observer.readNow(), first);
  await tick(); assert.equal(a.pending.length, 1, "same binding shares one pending read");
  assert.equal(Object.isFrozen(a.pending[0].address), true);
  a.observer.stop(); a.pending[0].resolve("obsolete"); await first;
  assert.equal(a.applied.length, 0, "stopping rejects an already-dispatched reply");
  assert.equal(timers.size, 1, "stopping one view leaves another running");
  a.observer.start();
  const current = a.observer.readNow(); await tick();
  for (let i = 0; i < 100; i++) assert.equal(a.observer.refresh(), current);
  a.pending[1].resolve("accepted"); await current; await tick();
  assert.equal(a.pending.length, 3, "100 refreshes request only one catch-up read");
  a.pending[2].resolve("catch-up"); await tick();
  assert.deepEqual(a.applied.map(x => x[0]), ["accepted", "catch-up"]);

  const oldVisit = a.observer.readNow(); await tick();
  a.select({ source: "office", sessionId: "b", generation: 2 });
  a.select({ source: "office", sessionId: "a", generation: 3 });
  a.pending[3].resolve("first visit"); await oldVisit;
  assert.equal(a.applied.length, 2, "A→B→A does not revive an older binding");
  const externalNavigation = a.observer.readNow(); await tick();
  a.select({ source: "office", sessionId: "a", generation: 4 });
  a.pending[4].reject(new Error("old navigation")); await externalNavigation;
  assert.equal(a.errors.length, 0, "adapter navigation generations also guard errors");

  const beforeHide = a.pending.length;
  const queued = a.observer.readNow();
  a.view.setVisible(false); await queued;
  assert.equal(a.pending.length, beforeHide, "hiding before dispatch avoids the bridge read");
  assert.equal(timers.size, 1);
  await a.observer.refresh(); assert.equal(a.pending.length, beforeHide);
  const other = b.observer.readNow(); await tick(); b.pending[0].resolve("other view"); await other;
  assert.deepEqual(b.applied, [["other view", "a"]]);
  a.view.setVisible(true, () => {}); await tick();
  assert.equal(timers.size, 2); assert.equal(a.pending.length, beforeHide + 1, "resume catches up exactly once");
  a.pending.at(-1).resolve("resumed"); await tick();

  const beforeDispose = a.observer.readNow(); await tick();
  a.view.dispose(); a.pending.at(-1).reject(new Error("disposed read")); await beforeDispose;
  assert.equal(a.errors.length, 0); assert.equal(timers.size, 1);
  await a.observer.readNow(); a.observer.start();
  assert.equal(timers.size, 1, "disposed observers cannot restart");
  b.view.begin({ source: "office", sessionId: "a" });
  const count = b.pending.length; await b.observer.readNow();
  assert.equal(b.pending.length, count, "equal session ids across sources do not match a binding");
  const passive = setup("workspace", "awaiting-user");
  passive.view.setVisible(false);
  await passive.observer.refresh(); await passive.observer.refresh();
  passive.view.setVisible(true, () => {}); await tick();
  assert.equal(passive.pending.length, 1, "hidden push invalidation catches up even without a poll timer");
  passive.pending[0].resolve("answer arrived"); await tick();
  assert.deepEqual(passive.applied, [["answer arrived", "awaiting-user"]]);
  assert.equal(timers.size, 1, "passive reads do not create polling");
  passive.view.dispose();
  const idle = setup("office", "idle", () => false);
  idle.observer.start(); idle.view.setVisible(false);
  idle.view.setVisible(true, () => {}); await tick();
  assert.equal(idle.pending.length, 0, "layout-only hide/return does not read an idle conversation");
  idle.view.setVisible(false); await idle.observer.refresh();
  idle.view.setVisible(true, () => {}); await tick();
  assert.equal(idle.pending.length, 1, "an explicit hidden update still refreshes an idle conversation");
  idle.pending[0].resolve("updated"); await tick(); idle.view.dispose();
  b.view.dispose(); assert.equal(timers.size, 0);
  console.log("PASS conversation observation: isolated bindings, stop/hide/dispose, stale replies/errors and coalesced catch-up");
})().catch(error => { console.error(error); process.exitCode = 1; });
