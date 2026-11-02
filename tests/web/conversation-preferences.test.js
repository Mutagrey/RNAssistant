"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), vm = require("node:vm");
const window = { cancelAnimationFrame() {} };
vm.runInNewContext(["app-conversation-input", "app-chat-composer"]
  .map(name => fs.readFileSync("web/js/" + name + ".js", "utf8")).join("\n"), { window });
const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
const tick = () => new Promise(resolve => setImmediate(resolve));
function fixture(source, workspaceId) {
  let target = { source, workspaceId, sessionId: "a" }, data = { selected: "old", mode: "agent", reasoning: false };
  const controller = window.RNAssistantChatComposer.create({}), writes = [], reads = [], applied = [], failures = [], unavailable = [];
  let refreshes = 0, visit = 0;
  controller.register("office", {
    target: () => target, preferences: () => data,
    preferenceService: scope => { const capturedVisit = visit; return {
      write: change => { const wait = deferred(); writes.push({ target: scope.target, change, ...wait }); return wait.promise; },
      read: () => { const wait = deferred(); reads.push({ target: scope.target, ...wait }); return wait.promise; },
      apply: (value, change) => { if (capturedVisit !== visit) return false; data = value; applied.push({ value, change }); return true; }
    }; },
    preferenceChanged: () => refreshes++, preferenceFailed: error => failures.push(error),
    preferenceUnavailable: error => unavailable.push(error)
  });
  return { controller, writes, reads, applied, failures, unavailable, refreshes: () => refreshes,
    select: sessionId => { ++visit; target = { source, workspaceId, sessionId }; data = { selected: "old", mode: "agent", reasoning: false }; controller.sync(); } };
}
(async () => {
  const a = fixture("office"), b = fixture("workspace", "w");
  const office = a.controller.setPreference("model", "new"), workspace = b.controller.setPreference("model", "other");
  assert.equal(a.controller.preferences().selected, "old", "no optimistic model success");
  assert.equal(a.controller.preferences().disabled, true);
  assert.equal(await a.controller.setPreference("mode", "plan"), false, "one preference write per exact target");
  assert.equal(a.writes.length, 1); assert.equal(b.writes.length, 1, "another source with the same session id is independent");
  assert.equal(Object.isFrozen(a.writes[0].target), true);
  assert.equal(Object.isFrozen(a.writes[0].change), true);
  a.select("b"); assert.equal(a.controller.preferences().disabled, false, "A's pending save does not lock B");
  a.writes[0].resolve({ selected: "new" }); b.writes[0].resolve({ selected: "other" });
  assert.equal(await office, false); assert.equal(await workspace, true);
  assert.equal(a.applied.length, 0); assert.equal(a.controller.preferences().selected, "old");

  a.select("a");
  const roundTrip = a.controller.setPreference("mode", "plan");
  assert.equal(a.controller.preferences().mode, "agent", "mode also waits for confirmation");
  a.select("b"); a.select("a");
  a.writes[1].resolve({ selected: "stale", mode: "plan" }); await tick();
  assert.equal(a.applied.length, 0, "A→B→A does not adopt the first visit's response");
  assert.equal(a.reads.length, 1); assert.equal(a.reads[0].target.sessionId, "a");
  assert.equal(a.controller.preferences().disabled, true, "read-back keeps Send/settings guarded");
  a.reads[0].resolve({ selected: "fresh", mode: "plan" }); await roundTrip;
  assert.equal(a.controller.preferences().selected, "fresh"); assert.equal(a.controller.preferences().disabled, false);

  const uncertain = a.controller.setPreference("model", "lost");
  a.writes[2].reject(Object.assign(new Error("lost acknowledgement"), { resultUncertain: true })); await tick();
  assert.equal(a.writes.length, 3, "uncertain write is not retried"); assert.equal(a.reads.length, 2);
  a.reads[1].reject(new Error("read failed")); assert.equal(await uncertain, false);
  assert.equal(a.unavailable.length, 1, "failed read-back is reported to the transport owner");
  assert.equal(a.failures.length, 1); assert.equal(a.controller.preferenceBusy(), false);

  const background = a.controller.setPreference("reasoning", true), refreshes = a.refreshes();
  a.controller.dispose(); a.writes[3].reject(new Error("late failure")); await background;
  assert.equal(a.failures.length, 1); assert.equal(a.refreshes(), refreshes, "disposed view has no late notices or renders");
  assert.equal(window.RNAssistantConversationInput.isPending("office", { chatId: "a" }), false, "completion releases its exact barrier");
  b.controller.dispose();
  console.log("PASS shared preferences: independent targets, confirmed state, A→B→A read-back, uncertainty and disposal");
})().catch(error => { console.error(error); process.exitCode = 1; });
