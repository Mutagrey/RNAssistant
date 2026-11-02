"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), vm = require("node:vm");
const context = vm.createContext({ state: { chatDrafts: {} }, Promise,
  window: { cancelAnimationFrame() {}, requestAnimationFrame() { return 1; } } });
for (const name of ["app-chat-state", "app-chat-composer"])
  vm.runInContext(fs.readFileSync("web/js/" + name + ".js", "utf8"), context);
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
