"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");

class Element {
  constructor() {
    this.children = []; this.handlers = {}; this.className = ""; this.value = "";
    this.open = false; this.disabled = false; this.parentElement = this;
    this.classList = { toggle() {}, contains() { return false; } };
  }
  appendChild(node) { this.children.push(node); return node; }
  replaceChildren() { this.children = []; this._text = ""; }
  setAttribute() {}
  addEventListener(event, handler) { this.handlers[event] = handler; }
  set textContent(text) { this.replaceChildren(); this._text = String(text); }
  get textContent() { return (this._text || "") + this.children.map(node => node.textContent).join(""); }
  focus() {}
}
const ids = ["toggleChatResourcesButton", "chatResourceMenu", "chatResourceDock", "chatResourceCount", "chatResourcesList",
  "chatResourcesSearchInput", "chatResourcesPopover", "documentArtifactsPicker", "documentArtifactsQuery", "documentArtifactsSearch", "documentArtifactsList"];
const elements = Object.fromEntries(ids.map(id => [id, new Element()]));
const requests = [];
const applied = [];
const context = vm.createContext({
  state: { activeChatId: "a", chatNavigationVersion: 1, artifacts: [], artifactLibrary: { sessionRevision: 5, heads: [] } },
  document: { createElement: () => new Element(), addEventListener() {} },
  $: id => elements[id] || null,
  send: (type, payload) => new Promise((resolve, reject) => requests.push({ type, payload, resolve, reject })),
  applyChatStateForChat: (response, chatId) => applied.push({ response, chatId })
});
context.window = context;
context.alert = () => {};
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-artifacts.js"), "utf8"), context);
context.bindChatResourceNavigation();
const flush = () => new Promise(resolve => setImmediate(resolve));
const rowButton = title => elements.documentArtifactsList.children.flatMap(row => row.children).find(node => node.textContent === title);
const answer = (chatId, revision, title = "Shared Plan") => ({ chatId, sessionRevision: revision,
  items: [{ resourceUri: "rna://chat/doc/artifact/plan_doc_p_r1/revision/1", title, kind: "plan_document", revision: 1, linked: false, selected: false }] });
(async () => {
  assert.equal(elements.toggleChatResourcesButton.disabled, false, "empty chat can open document resource picker");
  elements.documentArtifactsPicker.open = true;
  elements.documentArtifactsPicker.handlers.toggle();
  const old = requests.shift();
  elements.documentArtifactsQuery.value = "Plan";
  elements.documentArtifactsSearch.handlers.click();
  const latest = requests.shift();
  latest.resolve(answer("a", 5)); await flush();
  old.resolve(answer("a", 3, "Stale")); await flush();
  assert.ok(!elements.documentArtifactsList.textContent.includes("Stale"), "late search cannot replace a newer catalog");
  const select = rowButton("Выбрать Plan");
  select.handlers.click(); select.handlers.click();
  assert.equal(requests.length, 1, "duplicate clicks do not dispatch another change");
  const change = requests.shift();
  assert.equal(change.type, "changeArtifactLink");
  assert.equal(change.payload.chatId, "a");
  assert.equal(change.payload.expectedSessionRevision, 5);
  assert.equal(change.payload.detached, false);
  context.state.activeChatId = "b";
  context.state.chatNavigationVersion++;
  context.renderChatResourceNavigation();
  change.resolve({ activeChatId: "a", sessionRevision: 6 }); await flush();
  assert.equal(applied.length, 0, "late mutation cannot apply state or navigate another chat");
  assert.equal(elements.documentArtifactsPicker.open, false, "chat switch closes and clears picker");

  elements.documentArtifactsPicker.open = true;
  elements.documentArtifactsPicker.handlers.toggle();
  const second = requests.shift();
  const data = answer("b", 8); data.items[0].linked = true; data.items[0].selected = true;
  data.items[0].availabilityIssue = "metadata_unavailable";
  second.resolve(data); await flush();
  assert.ok(elements.documentArtifactsList.textContent.includes("метаданные недоступны"));
  assert.equal(rowButton("Выбрать Plan"), undefined, "unavailable resource exposes unlink but no selection action");
  rowButton("Убрать").handlers.click();
  const detach = requests.shift();
  assert.equal(detach.payload.chatId, "b");
  assert.equal(detach.payload.expectedSessionRevision, 8);
  assert.equal(detach.payload.detached, true);
  // Switching away and back still invalidates a response from the earlier visit.
  context.state.chatNavigationVersion += 2;
  detach.resolve({ activeChatId: "b", sessionRevision: 9 }); await flush();
  assert.equal(applied.length, 0);
  elements.documentArtifactsPicker.open = true;
  elements.documentArtifactsPicker.handlers.toggle();
  const htmlList = requests.shift();
  const htmlAnswer = answer("b", 10, "Shared HTML");
  htmlAnswer.items[0].kind = "html_workspace";
  htmlList.resolve(htmlAnswer); await flush();
  assert.ok(elements.documentArtifactsList.textContent.includes("Shared HTML · v"));
  context.state.htmlWorkspaceDirty = true;
  context.confirm = () => false;
  rowButton("Выбрать HTML").handlers.click(); await flush();
  assert.equal(requests.length, 0, "dirty editor selection can be cancelled before dispatch");
  context.confirm = () => true;
  rowButton("Выбрать HTML").handlers.click();
  const htmlSelect = requests.shift();
  assert.equal(htmlSelect.payload.expectedSessionRevision, 10);
  context.state.htmlWorkspaceEditVersion = 1;
  htmlSelect.resolve({ activeChatId: "b", sessionRevision: 11 }); await flush();
  assert.equal(applied.length, 0, "late HTML selection never overwrites new local edits");
  assert.equal(context.state.htmlWorkspaceDirty, true);
  context.state.htmlWorkspaceDirty = false;
  elements.documentArtifactsPicker.handlers.toggle();
  const markdownList = requests.shift();
  const mdAnswer = answer("b", 12, "Architecture.md");
  mdAnswer.items[0].kind = "markdown"; mdAnswer.items[0].linked = true; mdAnswer.items[0].selected = false;
  markdownList.resolve(mdAnswer); await flush();
  assert.ok(elements.documentArtifactsList.textContent.includes("Architecture.md · v"));
  rowButton("Подключить MD").handlers.click();
  const mdAttach = requests.shift();
  assert.equal(mdAttach.payload.expectedSessionRevision, 12);
  assert.equal(mdAttach.payload.detached, false);
  context.state.chatNavigationVersion++;
  mdAttach.resolve({ activeChatId: "b", sessionRevision: 13 }); await flush();
  assert.equal(applied.length, 0, "late Markdown attachment cannot overwrite a newer navigation");
  console.log("PASS artifact working set: Markdown refresh reuses exact link and navigation guards");
  console.log("PASS artifact working set: shared HTML selector preserves new edits and requires explicit dirty discard");
  console.log("PASS artifact working set: empty chat, query ordering, exact mutation, double click and navigation races");
})().catch(error => { console.error(error); process.exitCode = 1; });
