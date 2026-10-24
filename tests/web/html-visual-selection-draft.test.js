"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const elements = new Map();
function element(id) {
  if (!elements.has(id)) elements.set(id, { disabled: false, textContent: "", listeners: {},
    addEventListener(type, callback) { this.listeners[type] = callback; }, focus() { this.focused = true; } });
  return elements.get(id);
}
const calls = [], navigation = [], drafts = new Map();
let selected = { generation: 4, selection: { tag: "button", selector: "button:nth-of-type(1)" } };
let stageResponse = () => Promise.resolve({ resource: { Id: "selection-1", FileName: "html-selection-button.json" } });
const state = { activeChatId: "office-chat", activeHtmlArtifactId: "html-r1",
  artifacts: [{ Id: "html-r1", Kind: "html_workspace", Revision: 1 }] };
const model = { prop(source, pascal, camel, fallback) {
    return source && source[camel] !== undefined ? source[camel] : source && source[pascal] !== undefined ? source[pascal] : fallback;
  }, artifactId: item => item.Id, artifactKind: item => item.Kind };
const context = vm.createContext({ state, $: element, ATTACHMENT_MAX_FILES: 10,
  log() {}, updateComposerInputState() {}, isPendingChatSubmit: () => false,
  hasActiveMessageEdit: () => false,
  setAttachmentStatus() {},
  attachmentId: item => item.Id,
  officeChatDraftText: () => drafts.get("office-chat").text,
  readOfficeDraft: id => drafts.get(id),
  saveOfficeDraft: (id, text, attachments) => drafts.set(id, { text, attachments }),
  restoreChatDraft: id => { assert.equal(id, "office-chat"); },
  switchTab: name => navigation.push(name),
  send: (type, payload) => { calls.push({ type, payload });
    return type === "stageHtmlVisualSelection" ? stageResponse() : Promise.resolve({}); },
  window: null });
context.window = context;
context.addEventListener = () => {};
context.RNAssistantHtmlWorkspaceModel = { create: () => model };
context.RNAssistantHtmlWorkspaceSource = { create: () => ({ release() {} }) };
context.RNAssistantHtmlWorkspaceEditor = { create: () => ({}) };
context.RNAssistantHtmlWorkspaceActions = { create: () => ({}) };
context.RNAssistantHtmlWorkspaceArtifacts = {};
context.RNAssistantHtmlWorkspaceTree = {};
context.RNAssistantHtmlWorkspacePreview = {};
context.RNAssistantProjectChats = { setScope: scope => navigation.push(scope) };
context.RNAssistantChatDrafts = { flushOffice: async () => {} };
context.RNAssistantVisualInspector = { createHost: options => {
  context.inspectorChanged = options.onSelectionChanged;
  return { selectionRecord: () => selected, generation: () => selected ? selected.generation : 4,
    deactivate: () => { selected = null; options.onSelectionChanged(false); } };
} };
drafts.set("office-chat", { text: "Change the color", attachments: [{ Id: "existing-file" }] });
vm.runInContext(fs.readFileSync("web/js/app-html-workspace.js", "utf8"), context);

(async () => {
  context.inspectorChanged(true);
  const button = element("attachHtmlWorkspaceSelectionButton");
  await button.listeners.click.call(button);
  assert.equal(calls[0].type, "stageHtmlVisualSelection");
  assert.equal(calls[0].payload.chatId, "office-chat");
  assert.equal(calls[0].payload.artifactId, "html-r1");
  assert.equal(calls[0].payload.artifactRevision, 1);
  assert.equal(calls[0].payload.previewGeneration, 4);
  assert.deepEqual(drafts.get("office-chat").attachments.map(item => item.Id), ["existing-file", "selection-1"]);
  assert.equal(drafts.get("office-chat").text, "Change the color", "comment remains in shared composer");
  assert.deepEqual(navigation, ["office", "chat"]);
  assert.equal(element("chatInput").focused, true);

  selected = { generation: 5, selection: { tag: "div", selector: "div:nth-of-type(1)" } };
  context.inspectorChanged(true);
  let release;
  stageResponse = () => new Promise(resolve => { release = resolve; });
  const pending = button.listeners.click.call(button);
  assert.equal(button.disabled, true);
  state.activeHtmlArtifactId = "html-r2";
  release({ resource: { Id: "stale-selection" } });
  await pending;
  assert.equal(calls.at(-1).type, "discardChatResourceDraft", "late staged result is discarded");
  assert.equal(drafts.get("office-chat").attachments.length, 2, "late result cannot enter draft");
  console.log("PASS HTML visual selection: existing Office draft, exact stage request and late-source discard");
})().catch(error => { console.error(error); process.exitCode = 1; });
