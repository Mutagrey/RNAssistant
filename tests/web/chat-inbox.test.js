"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
class Element {
  constructor() { this.children = []; this.value = ""; this.classList = { toggle() {}, remove() {} }; }
  appendChild(node) { this.children.push(node); return node; }
  replaceChildren() { this.children = []; }
  addEventListener() {}
  setAttribute() {}
  focus() {}
}
const elements = new Map();
const calls = [];
let transport;
const context = vm.createContext({
  state: { activeChatId: "a", activeSends: {}, settings: {}, draftAttachments: [{ Id: "file" }], pendingChatSubmits: {} },
  document: { createElement: () => new Element() },
  $: id => { if (!elements.has(id)) elements.set(id, new Element()); return elements.get(id); },
  attachmentId: a => a.Id,
  renderSendControls() {}, endChatRunTracking() {}, clearSendError() {}, log() {},
  setPendingChatSubmit(id, pending) { context.state.pendingChatSubmits[id] = pending; },
  setChatInputText(text) { context.$("chatInput").value = text; },
  clearDraftAttachments() { context.state.draftAttachments = []; },
  send(type, payload) { calls.push({type, payload}); return transport(type, payload); },
  applyChatStateForChat() {}, loadChatState: () => Promise.resolve({}),
  window: { requestAnimationFrame(fn) { fn(); } }
});
vm.runInContext(fs.readFileSync("web/js/app-chat-inbox.js", "utf8"), context);
(async () => {
  assert.equal(context.runningMessageDelivery(), "Steer");
  context.state.settings.RunningMessageDelivery = "Queue";
  assert.equal(context.runningMessageDelivery(), "Queue");
  context.$("chatInput").value = "keep me";
  transport = () => Promise.reject(new Error("lost acknowledgement"));
  await context.submitInboxMessage("keep me", context.state.draftAttachments, "a", "Steer");
  const id = calls[0].payload.operationId;
  assert.equal(context.$("chatInput").value, "keep me", "failed intake preserves text");
  assert.equal(context.state.draftAttachments.length, 1, "failed intake preserves files");
  transport = () => Promise.resolve({ chatId: "a", revision: 2, items: [], running: true });
  await context.submitInboxMessage("keep me", context.state.draftAttachments, "a", "Steer");
  assert.equal(calls[1].payload.operationId, id, "uncertain retry retains idempotency key");
  assert.equal(context.$("chatInput").value, "", "acknowledged input clears draft");
  context.applyChatInbox({ chatId: "a", revision: 1, items: [{ Id: "stale" }] });
  assert.equal(context.chatInbox().revision, 2, "older projections cannot resurrect queue entries");
  context.state.activeChatId = "b"; context.$("chatInput").value = "another chat draft";
  await context.submitInboxMessage("old chat", [], "a", "Queue");
  assert.equal(context.$("chatInput").value, "another chat draft", "late ack cannot clear another chat draft");
  context.applyChatInbox({ chatId: "b", revision: 4, paused: true, pauseReason: "Stop", items: [
    { Id: "queued", Revision: 1, Status: "Pending", Delivery: "Queue", Text: "<script>unsafe</script>", Attachments: [] }
  ] });
  assert.equal(context.$("chatInbox").hidden, false);
  const walk = node => [node, ...(node.children || []).flatMap(walk)];
  const nodes = walk(context.$("chatInbox"));
  for (const label of ["Продолжить очередь", "Отправить сейчас", "Редактировать", "Удалить"])
    assert.ok(nodes.some(n => n.textContent === label), label);
  assert.ok(nodes.some(n => n.textContent === "<script>unsafe</script>"), "message rendered as plain text");
  context.state.messages = [];
  context.applyChatInbox({ chatId: "b", revision: 5, running: true, phase: "executing", items: [
    { Id: "steer", Revision: 1, Status: "Pending", Delivery: "Steer", Text: "new requirement", Attachments: [] }
  ] });
  assert.equal(context.buildPendingInputUnits().length, 1, "accepted steer appears in history while awaiting application");
  context.state.messages = [{ Id: "steer" }];
  assert.equal(context.buildPendingInputUnits().length, 0, "persisted steer does not duplicate the pending projection");
  console.log("PASS chat inbox: settings, ack/retry, chat isolation, stale projections, queue controls");
})().catch(error => { console.error(error); process.exitCode = 1; });
