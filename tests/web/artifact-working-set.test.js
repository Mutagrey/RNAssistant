"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");
const { webcrypto } = require("node:crypto");
class Element {
  constructor(tag = "div") { this.tag = tag; this.children = []; this.handlers = {}; this.value = ""; this.hidden = false;
    this.classList = { add() {}, remove() {}, toggle() {} }; this.attributes = {}; }
  appendChild(node) { this.children.push(node); return node; }
  insertBefore(node, before) { this.children.splice(Math.max(0, this.children.indexOf(before)), 0, node); }
  get firstChild() { return this.children[0]; }
  replaceChildren(...nodes) { this.children = nodes; this._text = ""; }
  addEventListener(name, handler) { this.handlers[name] = handler; }
  setAttribute(name, value) { this.attributes[name] = value; }
  removeAttribute(name) { delete this.attributes[name]; }
  set textContent(value) { this.replaceChildren(); this._text = String(value); }
  get textContent() { return (this._text || "") + this.children.map(c => c.textContent).join(""); }
  click() { return this.handlers.click && this.handlers.click(); }
}
const ids = ["", "Scope", "Import", "File", "Kind", "Status", "PreviewStatus", "More", "Preview", "Actions", "PreviewTitle", "PreviewBody",
  "All", "Overview", "OverviewRows", "OverviewTable", "OverviewEmpty", "OverviewCreate"];
const elements = Object.fromEntries(ids.map(id => ["artifactCatalog" + id, new Element()]));
elements.htmlWorkspaceLayout = new Element(); elements.htmlWorkspaceEditor = new Element(); elements.htmlWorkspaceSearchInput = new Element();
elements.artifactCatalogScope.value = "chat"; elements.artifactCatalogPreview.hidden = true;
const requests = [], applied = [], previews = [];
const context = vm.createContext({ console, TextDecoder, TextEncoder, Blob, crypto: webcrypto, URL, setTimeout, clearTimeout, confirm: () => true,
  Option: function (text, value) { const e = new Element("option"); e.textContent = text; e.value = value; return e; },
  document: { getElementById: id => elements[id], createElement: tag => new Element(tag) },
  $: id => elements[id], state: { activeChatId: "a", chatNavigationVersion: 1, htmlWorkspaceEditVersion: 0 },
  send: (type, payload) => new Promise((resolve, reject) => requests.push({ type, payload, resolve, reject })),
  applyChatStateForChat: (data, id) => applied.push({ data, id }), fetch() {},
  RNAssistantResourceDownload: { read: async () => new TextEncoder().encode(JSON.stringify({ Project: { Files: [{ Path: "index.html", Kind: "html", Content: "<h1>Saved</h1>" }], EntryPath: "index.html", Data: [], ExternalDependencies: [] }, Views: [] })) },
  RNAssistantHtmlWorkspacePreview: { ensureVendors: async () => {}, build: options => { previews.push(options); return "<h1>Saved</h1>"; } }
});
context.RNAssistantNavigationTree = { icon: () => "" };
context.window = context;
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-artifact-catalog.js"), "utf8"), context);
const flush = () => new Promise(resolve => setImmediate(resolve));
const answer = (id = "a", revision = 5) => ({ chatId: id, documentId: "doc", sessionRevision: revision, documents: [{ id: "doc", title: "Book" }], items: [{ documentId: "doc", documentTitle: "Book", resourceUri: "rna://chat/doc/artifact/html_ws_a_r1/revision/1", title: "Dashboard", kind: "html_workspace", updatedUtc: "2026-10-02T12:00:00Z", canTransfer: true, canDelete: true, linked: false }] });
function descendants(root) { return root.children.flatMap(child => [child, ...descendants(child)]); }
const button = label => descendants(elements.artifactCatalogActions).find(n => n.tag === "button" && n.textContent === label);
const tree = () => context.RNAssistantArtifactCatalog.tree([], [], {}).nodes;
const itemUri = answer().items[0].resourceUri;
async function finishPreview() {
  const read = requests.shift(); assert.equal(read.type, "exportArtifact"); assert.equal(read.payload.preview, true);
  read.resolve({ kind: "html_workspace", contentType: "application/json", data: { leaseId: "lease" } }); await flush();
  assert.equal(requests[0].type, "closeArtifactTransfer"); requests.shift().resolve({ closed: true }); await flush();
}
(async () => {
  context.RNAssistantArtifactCatalog.show("all");
  assert.equal(requests[0].payload.scope, "all"); requests.shift().resolve(answer()); await flush();
  assert.equal(tree()[0].title, "В документе", "current document keeps its ownership group");
  await context.RNAssistantArtifactCatalog.select(itemUri); await finishPreview();
  assert.equal(elements.htmlWorkspaceEditor.hidden, true, "external preview uses the same right pane");
  button("Подключить к чату").click(); button("Подключить к чату").click();
  assert.equal(requests.length, 1, "duplicate click submits only once");
  const mutation = requests.shift(); assert.equal(mutation.type, "changeArtifactLink"); assert.equal(mutation.payload.expectedSessionRevision, 5);
  context.state.activeChatId = "b"; context.state.chatNavigationVersion++;
  mutation.resolve({ activeChatId: "a" }); await flush(); assert.equal(applied.length, 0, "late mutation cannot apply another chat");
  context.RNAssistantArtifactCatalog.show("document"); const staleList = requests.shift();
  context.RNAssistantArtifactCatalog.show("chat"); const latestList = requests.shift();
  latestList.resolve(answer("b", 8)); await flush(); staleList.resolve({ ...answer("b"), items: [] }); await flush();
  assert.equal(tree()[0].children[0].title, "Dashboard", "late query cannot replace current tree");
  await context.RNAssistantArtifactCatalog.select(itemUri); await finishPreview();
  context.state.htmlWorkspaceDirty = true; button("Независимая копия").click();
  assert.equal(await context.RNAssistantArtifactCatalog.select(itemUri), false);
  assert.equal(requests.length, 0, "dirty editor blocks mutation and selection"); context.state.htmlWorkspaceDirty = false;
  const frame = elements.artifactCatalogPreviewBody.children.find(n => n.tag === "iframe");
  assert.equal(frame.attributes.sandbox, "allow-scripts", "preview has no same-origin permission");
  assert.equal(previews[0].hostBridge, false); assert.equal(applied.length, 0, "preview never attaches or selects the workspace");
  assert.equal(elements.artifactCatalogPreviewBody.children.find(n => n.tag === "pre").hidden, true, "source is not shown below preview");
  context.confirm = () => false; button("Удалить из документа…").click(); assert.equal(requests.length, 0, "cancel does not delete");
  context.confirm = () => true; button("Удалить из документа…").click(); button("Удалить из документа…").click();
  assert.equal(requests.length, 1, "document deletion submits once");
  const deletion = requests.shift(); assert.equal(deletion.type, "deleteDocumentArtifact");
  assert.equal(deletion.payload.resourceUri, itemUri); assert.equal(deletion.payload.expectedSessionRevision, 8); assert.equal(deletion.payload.confirmed, true);
  deletion.resolve({ activeChatId: "b" }); await flush();
  requests.shift().resolve({ ...answer("b", 9), items: [] }); await flush();
  assert.equal(tree().length, 0); assert.equal(elements.artifactCatalogActions.hidden, true);
  assert.equal(elements.artifactCatalogPreviewTitle.textContent, "Выберите артефакт", "removed selection never keeps stale preview");
  context.RNAssistantArtifactCatalog.show(); requests.shift().resolve({ ...answer("b", 9), nextCursor: "page2" }); await flush();
  elements.artifactCatalogMore.click(); assert.equal(requests[0].payload.cursor, "page2");
  const second = { ...answer("b", 9).items[0], title: "Second", resourceUri: itemUri.replace("_a_", "_b_") };
  requests.shift().resolve({ ...answer("b", 9), items: [second] }); await flush();
  assert.equal(tree()[0].children.length, 2, "pagination appends without replacing the first page");
  // A preview begun before navigation cannot overwrite the new selection.
  await context.RNAssistantArtifactCatalog.select(itemUri); const oldRead = requests.shift();
  context.RNAssistantArtifactCatalog.editor();
  oldRead.resolve({ contentType: "application/json", data: { leaseId: "old" } }); await flush();
  requests.shift().resolve({ closed: true }); await flush();
  assert.equal(elements.artifactCatalogPreview.hidden, true, "late preview cannot replace editor");
  console.log("PASS artifact tree: scopes, pagination, guarded selection, duplicate/unconfirmed deletion, dirty editor, late responses and isolated preview");
})().catch(error => { console.error(error); process.exitCode = 1; });
