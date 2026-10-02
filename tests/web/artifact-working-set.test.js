"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");
const { webcrypto } = require("node:crypto");
class Element {
  constructor(tag = "div") { this.tag = tag; this.children = []; this.handlers = {}; this.value = ""; this.hidden = false;
    this.classList = { add() {}, remove() {} }; this.attributes = {}; }
  appendChild(node) { this.children.push(node); return node; }
  replaceChildren(...nodes) { this.children = nodes; this._text = ""; }
  addEventListener(name, handler) { this.handlers[name] = handler; }
  setAttribute(name, value) { this.attributes[name] = value; }
  set textContent(value) { this.replaceChildren(); this._text = String(value); }
  get textContent() { return (this._text || "") + this.children.map(c => c.textContent).join(""); }
  click() { return this.handlers.click && this.handlers.click(); }
}
const ids = ["", "Scope", "Show", "Editor", "Import", "File", "Query", "Kind", "Document", "Refresh", "Status", "List", "More", "Preview", "Back", "PreviewTitle", "PreviewBody"];
const elements = Object.fromEntries(ids.map(id => ["artifactCatalog" + id, new Element()]));
elements.htmlWorkspaceLayout = new Element(); elements.artifactCatalogScope.value = "chat";
const requests = [], applied = [], previews = [];
const context = vm.createContext({ console, TextDecoder, TextEncoder, Blob, crypto: webcrypto, URL, setTimeout,
  Option: function (text, value) { const e = new Element("option"); e.textContent = text; e.value = value; return e; },
  document: { getElementById: id => elements[id], createElement: tag => new Element(tag) },
  $: id => elements[id], state: { activeChatId: "a", chatNavigationVersion: 1, htmlWorkspaceEditVersion: 0 },
  send: (type, payload) => new Promise((resolve, reject) => requests.push({ type, payload, resolve, reject })),
  applyChatStateForChat: (data, id) => applied.push({ data, id }), fetch() {},
  RNAssistantResourceDownload: { read: async () => new TextEncoder().encode(JSON.stringify({ Project: { Files: [{ Path: "index.html", Kind: "html", Content: "<h1>Saved</h1>" }], EntryPath: "index.html", Data: [], ExternalDependencies: [] }, Views: [] })) },
  RNAssistantHtmlWorkspacePreview: { ensureECharts: async () => {}, build: options => { previews.push(options); return "<h1>Saved</h1>"; } }
});
context.window = context;
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-artifact-catalog.js"), "utf8"), context);
const flush = () => new Promise(resolve => setImmediate(resolve));
const answer = (id = "a", revision = 5) => ({ chatId: id, documentId: "doc", sessionRevision: revision, documents: [{ id: "doc", title: "Book" }], items: [{ documentId: "doc", documentTitle: "Book", resourceUri: "rna://chat/doc/artifact/html_ws_a_r1/revision/1", title: "Dashboard", kind: "html_workspace", updatedUtc: "2026-10-02T12:00:00Z", canTransfer: true, linked: false }] });
const button = label => elements.artifactCatalogList.children.flatMap(r => r.children).flatMap(r => r.children).find(n => n.tag === "button" && n.textContent === label);
(async () => {
  context.RNAssistantArtifactCatalog.show("all");
  assert.equal(requests[0].payload.scope, "all"); requests.shift().resolve(answer()); await flush();
  assert.ok(elements.artifactCatalogList.textContent.includes("Book"));
  button("Подключить к чату").click(); button("Подключить к чату").click();
  assert.equal(requests.length, 1, "duplicate click submits only once");
  const mutation = requests.shift(); assert.equal(mutation.type, "changeArtifactLink"); assert.equal(mutation.payload.expectedSessionRevision, 5);
  context.state.activeChatId = "b"; context.state.chatNavigationVersion++;
  mutation.resolve({ activeChatId: "a" }); await flush(); assert.equal(applied.length, 0, "late mutation cannot apply another chat");
  context.RNAssistantArtifactCatalog.show("document"); const staleList = requests.shift();
  context.RNAssistantArtifactCatalog.show("chat"); const latestList = requests.shift();
  latestList.resolve(answer("b", 8)); await flush(); staleList.resolve({ ...answer("b"), items: [] }); await flush();
  assert.ok(button("Независимая копия"), "late query cannot replace current catalog");
  context.state.htmlWorkspaceDirty = true; button("Независимая копия").click();
  assert.equal(requests.length, 0, "dirty editor blocks replacement"); context.state.htmlWorkspaceDirty = false;
  button("Просмотр").click(); const read = requests.shift(); assert.equal(read.type, "exportArtifact"); assert.equal(read.payload.preview, true);
  read.resolve({ kind: "html_workspace", contentType: "application/json", data: { leaseId: "lease" } }); await flush();
  assert.equal(requests[0].type, "closeArtifactTransfer"); requests.shift().resolve({ closed: true }); await flush();
  const frame = elements.artifactCatalogPreviewBody.children.find(n => n.tag === "iframe");
  assert.equal(frame.attributes.sandbox, "allow-scripts", "preview has no same-origin permission");
  assert.equal(previews[0].hostBridge, false); assert.equal(applied.length, 0, "preview never attaches or selects the workspace");
  elements.artifactCatalogBack.click(); assert.equal(elements.artifactCatalogPreview.hidden, true);
  console.log("PASS artifact catalog: scope, duplicate clicks, dirty state, late responses and isolated preview");
})().catch(error => { console.error(error); process.exitCode = 1; });
