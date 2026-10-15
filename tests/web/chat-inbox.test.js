"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
class Element {
  constructor() { this.children = []; this.value = ""; this.classList = { toggle() {}, remove() {} }; }
  appendChild(node) { this.children.push(node); return node; }
  get childNodes() { return this.children; }
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
  renderSendControls() {}, renderChatSessions() {}, endChatRunTracking() {}, clearSendError() {}, log() {},
  setPendingChatSubmit(id, pending) { context.state.pendingChatSubmits[id] = pending; },
  isPendingChatSubmit(id) { return !!context.state.pendingChatSubmits[id]; },
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
  transport = () => Promise.resolve({ chatId: "a", revision: 2, items: [], running: true,
    acceptedInput: { Id: "accepted", OperationId: id, Text: "keep me", Status: "Applied", Revision: 1 } });
  await context.submitInboxMessage("keep me", context.state.draftAttachments, "a", "Steer");
  assert.equal(calls[1].payload.operationId, id, "uncertain retry retains idempotency key");
  assert.equal(context.$("chatInput").value, "", "acknowledged input clears draft");
  assert.equal(context.pendingChatInputs().length, 1, "consumed-before-ack input keeps its echo until history arrives");
  assert.equal(context.pendingChatInputs()[0].Id, "accepted", "receipt supplies exact runtime identity");
  context.reconcileChatInputEchoes("a", []);
  assert.equal(context.pendingChatInputs().length, 1,
    "an empty queue with matching revision cannot remove a receipt before its exact history message arrives");
  context.state.messages = [{ Id: "accepted" }];
  assert.equal(context.pendingChatInputs().length, 0);
  context.reconcileChatInputEchoes("a", context.state.messages);
  context.applyChatInbox({ chatId: "a", revision: 1, items: [{ Id: "stale" }] });
  assert.equal(context.chatInbox().revision, 2, "older projections cannot resurrect queue entries");
  context.state.activeChatId = "b"; context.$("chatInput").value = "another chat draft";
  await context.submitInboxMessage("old chat", [], "a", "Queue");
  assert.equal(context.$("chatInput").value, "another chat draft", "late ack cannot clear another chat draft");
  context.applyChatInbox({ chatId: "b", revision: 4, paused: true, pauseReason: "Stop", items: [
    { Id: "queued", Revision: 1, Status: "Pending", Delivery: "Queue", Text: "<script>unsafe</script>", Attachments: [] }
  ] });
  const walk = node => [node, ...(node.children || []).flatMap(walk)];
  const nodes = context.buildPendingInputUnits().flatMap(unit => walk(unit.render()));
  for (const label of ["Продолжить очередь", "Отправить сейчас", "Редактировать", "Удалить"])
    assert.ok(nodes.some(n => n.textContent === label), label);
  assert.ok(nodes.some(n => n.textContent === "<script>unsafe</script>"), "message rendered as plain text");
  context.state.messages = [];
  context.reconcileChatInputEchoes("b", []);
  context.applyChatInbox({ chatId: "b", revision: 5, running: true, phase: "executing", items: [
    { Id: "steer", Revision: 1, Status: "Pending", Delivery: "Steer", Text: "new requirement", Attachments: [] }
  ] });
  assert.equal(context.buildPendingInputUnits().length, 2, "previous queue bubble remains until history catches up");
  context.reconcileChatInputEchoes("b", [{ Id: "queued" }]);
  assert.equal(context.buildPendingInputUnits().length, 1, "accepted steer appears in history while awaiting application");
  context.state.messages = [{ Id: "steer" }];
  assert.equal(context.buildPendingInputUnits().length, 0, "persisted steer does not duplicate the pending projection");

  let resolveSend;
  transport = () => new Promise(resolve => { resolveSend = resolve; });
  context.$("chatInput").value = "submitted";
  context.state.draftAttachments = [{ Id: "first" }];
  const submitting = context.submitInboxMessage("submitted", context.state.draftAttachments, "b", "Queue");
  const operationId = calls.at(-1).payload.operationId;
  assert.equal(context.pendingChatInputs().at(-1).localStatus, "submitting", "bubble appears before acknowledgement");
  context.$("chatInput").value = "new draft typed while sending";
  context.state.draftAttachments.push({ Id: "second" });
  resolveSend({ chatId: "b", revision: 7, items: [], acceptedInput: {
    Id: "consumed", OperationId: operationId, Text: "submitted", Status: "Applied", Revision: 1 } });
  await submitting;
  assert.equal(context.$("chatInput").value, "new draft typed while sending", "late ack preserves new text in the same chat");
  assert.equal(context.state.draftAttachments.length, 1);
  assert.equal(context.state.draftAttachments[0].Id, "second", "late ack preserves new attachments");

  // A push can acknowledge delivery even when the original intake response is lost.
  context.$("chatInput").value = "uncertain";
  transport = async (type, payload) => {
    context.applyChatInbox({ chatId: "b", revision: 8, items: [{ Id: "pushed", OperationId: payload.operationId,
      Text: "uncertain", Status: "Pending", Delivery: "Steer", Revision: 1 }] });
    throw new Error("response lost after push");
  };
  await context.submitInboxMessage("uncertain", [], "b", "Steer");
  const uncertainId = calls.at(-1).payload.operationId;
  context.reconcileChatInputEchoes("b", [{ Id: "pushed" }]);
  transport = async (type, payload) => ({ chatId: "b", revision: 9, items: [], acceptedInput: {
    Id: "pushed", OperationId: payload.operationId, Text: "uncertain", Status: "Applied", Revision: 2 } });
  await context.submitInboxMessage("uncertain", [], "b", "Steer");
  assert.equal(calls.at(-1).payload.operationId, uncertainId, "retry after push/history retains the original operation identity");
  transport = () => new Promise(resolve => { resolveSend = resolve; });
  context.state.inboxEditing = "edit";
  context.state.inboxEditDraft = { id: "edit", text: "first edit", revision: 1 };
  const saving = context.changeInboxItem("editChatInput", { Id: "edit", Revision: 1 }, "first edit");
  const reopened = { id: "edit", text: "reopened edit while save is pending", revision: 1 };
  context.state.inboxEditDraft = reopened;
  resolveSend({ chatId: "b", revision: 10, items: [{ Id: "edit", Text: "first edit", Revision: 2, Status: "Pending" }] });
  await saving;
  assert.equal(context.state.inboxEditDraft, reopened, "late save cannot discard a subsequently reopened edit");
  transport = async () => ({ chatId: "b", revision: 11, items: [] });
  await context.changeInboxItem("removeChatInput", { Id: "edit", Revision: 2 });
  assert.equal(context.pendingChatInputs().some(item => item.Id === "edit"), false,
    "an explicit successful removal still removes the pending bubble");
  const answer = { runId: "run", questionSetId: "set", answers: [{ questionId: "q", optionIds: ["a"], freeText: "" }] };
  context.$("chatInput").value = "Unsent draft";
  transport = () => Promise.reject(new Error("lost answer acknowledgement"));
  assert.equal(await context.submitInboxMessage(null, [], "b", "Steer", answer), false);
  const answerId = calls.at(-1).payload.operationId;
  transport = async () => ({ chatId: "b", revision: 12, items: [], acceptedInput: {
    Id: "answer", OperationId: answerId, QuestionSetId: "set", Text: "Accepted answers", Status: "Applied", Revision: 1 } });
  assert.equal(await context.submitInboxMessage(null, [], "b", "Steer", answer), true);
  assert.equal(calls.at(-1).payload.operationId, answerId, "typed answer retry keeps its receipt identity");
  assert.equal(calls.at(-1).payload.answers, answer);
  assert.equal(calls.at(-1).payload.text, null);
  assert.equal(context.$("chatInput").value, "Unsent draft", "question answers preserve composer text");
  assert.equal(context.state.draftAttachments[0].Id, "second", "question answers preserve composer attachments");
  console.log("PASS chat inbox: settings, ack/retry, chat isolation, stale projections, queue controls");
})().catch(error => { console.error(error); process.exitCode = 1; });
