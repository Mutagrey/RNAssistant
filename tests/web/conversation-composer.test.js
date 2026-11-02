"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const frames = new Map(); let nextFrame = 0;
const window = { requestAnimationFrame(fn) { frames.set(++nextFrame, fn); return nextFrame; }, cancelAnimationFrame(id) { frames.delete(id); } };
class Node {
  constructor(value = "") { this.value = value; this.selectionStart = 2; this.selectionEnd = 5; this.scrollTop = 12; this.events = new Map(); this.focused = 0; }
  addEventListener(name, fn) { assert.equal(this.events.has(name), false, "no duplicate listeners"); this.events.set(name, fn); }
  removeEventListener(name, fn) { if (this.events.get(name) === fn) this.events.delete(name); }
  closest() { return null; }
  focus() { this.focused++; }
  fire(name, values = {}) { const event = { ...values, preventDefault() { this.defaultPrevented = true; } }; this.events.get(name)?.(event); return event; }
}
vm.runInNewContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-chat-composer.js"), "utf8"), { window });
function make(text) {
  const nodes = Object.fromEntries(["form", "input", "send", "stop", "clear", "cancelEdit", "approve", "reject"].map(n => [n, new Node(n === "input" ? text : "")]));
  const controller = window.RNAssistantChatComposer.create(nodes); controller.bind(); controller.bind();
  return { nodes, controller };
}
const a = make("Original A"), b = make("Original B"), sent = [];
const office = { source: "office", sessionId: "same-id" }, project = { source: "workspace", workspaceId: "project", sessionId: "same-id" };
let target = office, inputs = 0, escapes = 0;
a.controller.register("office", { target: () => target, input: () => inputs++, submit: (_, scope) => sent.push(scope.target), escape: () => escapes++ });
a.controller.register("project", { target: () => project, input: () => inputs++ });
b.controller.register("office", { target: () => office, input: () => {} });
const editA = a.controller.startEdit(office, { messageId: "m1", text: "Edit A" });
const editB = b.controller.startEdit(office, { messageId: "m1", text: "Edit B" });
a.nodes.input.value = "Changed A"; a.nodes.input.fire("input");
assert.equal(editA.text, "Changed A"); assert.equal(editA.draft, "Original A");
assert.equal(editB.text, "Edit B", "two views do not share edit state even for the same address");
assert.equal(a.controller.startEdit(office, { messageId: "m2" }), null, "a second edit cannot overwrite an unfinished one");
a.controller.sync(); assert.equal(a.controller.edit(office), editA, "a snapshot refresh does not reset editing");
a.nodes.form.fire("submit"); assert.equal(sent.length, 1); assert.equal(sent[0].sessionId, office.sessionId);
a.nodes.input.fire("keydown", { key: "Enter", isComposing: true });
a.nodes.input.fire("keydown", { key: "Enter", shiftKey: true });
assert.equal(sent.length, 1, "IME and multiline input do not submit");
a.nodes.input.fire("keydown", { key: "Enter" }); assert.equal(sent.length, 2);
a.nodes.send.disabled = true;
a.nodes.input.fire("keydown", { key: "Enter" }); a.nodes.form.fire("submit");
assert.equal(sent.length, 2, "keyboard/form cannot bypass unavailable Send"); a.nodes.send.disabled = false;
a.nodes.cancelEdit.disabled = true; a.nodes.input.fire("keydown", { key: "Escape" });
assert.equal(escapes, 0, "Escape cannot cancel an edit while its button is disabled");
a.nodes.cancelEdit.disabled = false;
a.nodes.input.fire("keydown", { key: "Escape", shiftKey: true }); assert.equal(escapes, 0);
a.nodes.input.fire("keydown", { key: "Escape" }); assert.equal(escapes, 1);
const staleCommand = a.controller.capture();
a.controller.defer(() => a.nodes.input.focus()); const staleFocus = [...frames.values()][0];
a.controller.activate("project"); a.nodes.input.value = "Project original";
const projectEdit = a.controller.startEdit(project, { messageId: "m1", revision: 7, text: "Project edit" });
assert.equal(a.controller.edit(office), editA); assert.notEqual(projectEdit, editA, "source and workspace are part of the target");
staleFocus(); staleCommand.invoke("submit"); assert.equal(sent.length, 2); assert.equal(a.nodes.input.focused, 0);
a.nodes.input.value = "Project edit";
a.controller.cancelEdit(office);
assert.equal(a.nodes.input.value, "Project edit", "background completion cannot restore another chat's input");
assert.equal(a.controller.edit(project), projectEdit); assert.equal(b.controller.edit(office), editB);
a.controller.cancelEdit(project, { focus: true }); assert.equal(a.nodes.input.value, "Project original");
const cancelFocus = [...frames.values()][0];
a.controller.activate("office"); cancelFocus(); assert.equal(a.nodes.input.focused, 0, "cancel focus is scoped to its target");
const roundTrip = a.controller.capture();
target = { source: "office", sessionId: "other" }; a.controller.sync(); target = office; a.controller.sync();
roundTrip.invoke("submit"); assert.equal(sent.length, 2, "A→B→A does not revive an old command");
b.controller.cancelEdit(office, { focus: true });
for (const [id, fn] of [...frames]) { frames.delete(id); fn(); }
assert.equal(b.nodes.input.value, "Original B"); assert.equal(b.nodes.input.selectionStart, 2); assert.equal(b.nodes.input.selectionEnd, 5);
assert.equal(b.nodes.input.focused, 1); assert.equal(a.nodes.input.focused, 0);
a.controller.dispose(); b.controller.dispose();
for (const node of Object.values(a.nodes)) assert.equal(node.events.size, 0);
assert.equal(frames.size, 0); assert.equal(a.controller.capture().isCurrent(), false);
console.log("conversation-composer: independent edits, draft restoration, scoped commands/focus, IME and disposal passed");
