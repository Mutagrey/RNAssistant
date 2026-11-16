"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), vm = require("node:vm");
let clockNow = 0, nextTimer = 0;
const timers = new Map();
const context = vm.createContext({ state: { chatDrafts: {} }, Promise,
  Date: { now: () => clockNow },
  window: {
    cancelAnimationFrame() {}, requestAnimationFrame() { return 1; },
    setTimeout(fn, delay) { timers.set(++nextTimer, { fn: fn, at: clockNow + delay }); return nextTimer; },
    clearTimeout(id) { timers.delete(id); }
  } });
context.saveCalls = [];
context.saveOk = true;
context.log = () => {};
context.send = async (type, payload) => {
  if (type === "getChatDrafts") return { drafts: [] };
  if (type !== "saveChatDraft") throw new Error("Unexpected bridge request: " + type);
  context.saveCalls.push(payload);
  if (!context.saveOk) throw new Error("storage unavailable");
  return { saved: true };
};
for (const name of ["app-chat-state", "app-chat-composer"])
  vm.runInContext(fs.readFileSync("web/js/" + name + ".js", "utf8"), context);
async function advanceClock(ms) {
  const end = clockNow + ms;
  for (;;) {
    let due = null;
    for (const [id, timer] of timers) if (timer.at <= end && (!due || timer.at < due.timer.at)) due = { id: id, timer: timer };
    if (!due) break;
    timers.delete(due.id);
    clockNow = Math.max(clockNow, due.timer.at);
    due.timer.fn();
    await new Promise(resolve => setImmediate(resolve));
  }
  clockNow = end;
  await new Promise(resolve => setImmediate(resolve));
}
const a = { source: "office", sessionId: "a" }, b = { source: "workspace", workspaceId: "w", sessionId: "a" };
function fixture() {
  let target = a, value = "", writes = 0;
  const input = { get value() { return value; }, set value(text) { value = text; writes++; }, focus() {} };
  const controller = context.window.RNAssistantChatComposer.create({ form: {}, input });
  controller.register("office", { target: () => target, input: () => {} });
  const store = address => ({
    read: () => address.source === "office" ? context.readOfficeDraft(address.sessionId) : context.readWorkspaceDraft(address),
    write: text => address.source === "office" ? context.saveOfficeDraft(address.sessionId, text, [], true) :
      context.updateWorkspaceDraft(address, { text })
  });
  return { controller, input, store, writes: () => writes,
    select(address) { target = address; controller.restoreDraft(address, store(address).read()); },
    save() { return controller.captureDraft(target, store(target).write); } };
}
const view = fixture(), sibling = fixture();
view.select(a); view.input.value = "Sent text";
const submitted = view.save(), original = view.store(a).read();
view.controller.startEdit(a, { messageId: "old", text: "Unfinished edit" }); view.input.value = "Unfinished edit";
context.acceptOfficeDraft("a", "Sent text", submitted.version, []);
view.controller.acceptDraft(a, original, view.store(a).read());
assert.equal(view.input.value, "Unfinished edit", "Office ACK clears the normal draft without overwriting an active edit");
assert.equal(view.controller.edit(a).draft, "");
view.controller.cancelEdit(a); assert.equal(view.input.value, "", "cancel cannot resurrect the accepted text");

view.input.value = "Old send"; const old = view.save();
view.input.value = "New draft"; const newer = view.save();
context.acceptOfficeDraft("a", "Old send", old.version, []);
view.controller.acceptDraft(a, newer, view.store(a).read());
assert.equal(view.input.value, "New draft"); assert.equal(view.store(a).read().text, "New draft");
view.controller.startEdit(a, { messageId: "old", text: "Edit stays" }); view.input.value = "Edit stays";
view.select(b); view.input.value = "Other source"; view.save();
const beforeBackground = view.store(a).read(); context.acceptOfficeDraft("a", "New draft", beforeBackground.version, []);
view.controller.acceptDraft(a, beforeBackground, view.store(a).read());
assert.equal(view.input.value, "Other source", "same session id across sources stays isolated");
view.select(a); assert.equal(view.input.value, "Edit stays");
view.controller.cancelEdit(a); assert.equal(view.input.value, "");

view.select(b); view.input.value = "Next message"; const workspaceDraft = view.save();
view.controller.startEdit(b, { messageId: "old", text: "Edited message" }); view.input.value = "Edited message";
view.controller.appendDraft(b, view.store(b), "File: report.txt");
assert.equal(view.input.value, "Edited message", "late file link belongs to the normal draft, not the edit");
assert.equal(view.store(b).read().text, "Next message\nFile: report.txt");
view.controller.cancelEdit(b); assert.equal(view.input.value, "Next message\nFile: report.txt");
const beforeStaleAck = view.store(b).read(); context.acceptWorkspaceDraft(b, "Next message", workspaceDraft.version);
view.controller.acceptDraft(b, beforeStaleAck, view.store(b).read());
assert.equal(view.input.value, "Next message\nFile: report.txt", "Workspace version guard also preserves newer text");
const writes = view.writes();
for (let i = 0; i < 100; i++) view.controller.restoreDraft(b, view.store(b).read());
assert.equal(view.writes(), writes, "unchanged snapshots never reassign textarea or reset its caret");
sibling.select(a); sibling.input.value = "Independent input";
view.select({ source: "workspace", workspaceId: "w", sessionId: null });
assert.equal(view.input.value, "", "no selected conversation has an empty composer");
view.controller.dispose();
view.controller.restoreDraft(b, { text: "Late snapshot" });
assert.equal(view.input.value, ""); assert.equal(sibling.input.value, "Independent input");
sibling.controller.dispose();
console.log("PASS shared draft binding: versioned ACK, edit preservation, background file link, source isolation and stable input");

(async () => {
  await context.hydrateChatDrafts();
  context.saveCalls.length = 0;
  const office = { source: "office", sessionId: "debounce" };
  for (const [offset, text] of [[0, "1"], [100, "2"], [200, "3"], [300, "4"], [400, "5"],
    [500, "6"], [600, "7"], [700, "8"], [800, "9"]]) {
    await advanceClock(offset - clockNow);
    context.saveOfficeDraft(office.sessionId, text, [], true);
  }
  assert.equal(context.saveCalls.length, 0, "continuous typing does not issue a request per character");
  await advanceClock(199);
  assert.equal(context.saveCalls.length, 0, "debounce still waits for a quiet period");
  await advanceClock(1);
  assert.equal(context.saveCalls.length, 1, "max wait bounds the first continuous draft save");
  assert.equal(context.saveCalls[0].Text, "9", "coalesced save carries the latest dirty revision");

  context.saveOfficeDraft(office.sessionId, "10", [], true);
  await advanceClock(249);
  assert.equal(context.saveCalls.length, 1, "a quiet edit waits for the debounce window");
  await advanceClock(1);
  assert.equal(context.saveCalls.length, 2, "debounce flushes the last dirty revision");
  assert.equal(context.saveCalls[1].Text, "10");

  context.saveOfficeDraft(office.sessionId, "11", [], true);
  await context.window.RNAssistantChatDrafts.flushOffice(office.sessionId);
  assert.equal(context.saveCalls.length, 3, "submit/close flush is immediate");
  assert.equal(context.saveCalls[2].Text, "11");

  const workspace = { source: "workspace", workspaceId: "workspace", sessionId: "debounce" };
  context.saveWorkspaceDraft(workspace, { text: "workspace latest" });
  await context.window.RNAssistantChatDrafts.flushWorkspace(workspace);
  assert.equal(context.saveCalls.at(-1).Owner, "workspace");
  assert.equal(context.saveCalls.at(-1).Text, "workspace latest", "both shells share one flush owner");

  context.saveOk = false;
  context.saveOfficeDraft(office.sessionId, "12", [], true);
  await assert.rejects(context.window.RNAssistantChatDrafts.flushOffice(office.sessionId), /storage unavailable/,
    "failed save cannot produce a successful submit/close ACK");
  context.saveOk = true;
  context.saveOfficeDraft(office.sessionId, "13", [], true);
  await context.window.RNAssistantChatDrafts.flushAll();
  assert.equal(context.saveCalls.at(-1).Text, "13", "shutdown flush retries the still-dirty revision");
  console.log("PASS draft persistence: 9 continuous edits → 1 bounded save, 250 ms debounce, 1 s max wait, immediate submit/close flush and failed-save barrier");
})().catch(error => { console.error(error); process.exitCode = 1; });
