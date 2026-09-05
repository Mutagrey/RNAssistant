"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

class ClassList {
  constructor(owner) { this.owner = owner; }
  values() { return new Set(String(this.owner.className || "").split(/\s+/).filter(Boolean)); }
  write(values) { this.owner.className = Array.from(values).join(" "); }
  add(...names) { const values = this.values(); names.forEach(name => values.add(name)); this.write(values); }
  remove(...names) { const values = this.values(); names.forEach(name => values.delete(name)); this.write(values); }
  contains(name) { return this.values().has(name); }
}

class Element {
  constructor(tag) {
    this.tagName = String(tag).toLowerCase(); this.className = ""; this.classList = new ClassList(this);
    this.childNodes = []; this.parentNode = null; this.dataset = {}; this.attributes = {}; this.handlers = {};
    this.disabled = false; this.title = ""; this._text = ""; this.src = "";
  }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  replaceChildren(...children) { this.childNodes = []; children.forEach(child => this.appendChild(child)); this._text = ""; }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); }
  click() { if (!this.disabled) (this.handlers.click || []).forEach(handler => handler({})); }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  querySelectorAll(selector) {
    const matches = node => selector.startsWith(".")
      ? node.classList.contains(selector.slice(1))
      : node.tagName === selector.toLowerCase();
    const result = [];
    const walk = node => node.childNodes.forEach(child => { if (matches(child)) result.push(child); walk(child); });
    walk(this); return result;
  }
  set textContent(value) { this._text = String(value); this.childNodes = []; }
  get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(""); }
}

const root = path.join(__dirname, "../..");
const domRoots = [];
const document = {
  createElement: tag => new Element(tag),
  getElementById() { return null; },
  querySelectorAll(selector) { return domRoots.flatMap(node => node.querySelectorAll(selector)); }
};
const loads = [];
let switched = "";
const context = vm.createContext({ document, Promise });
context.window = context;
context.$ = () => null;
context.switchTab = tab => { switched = tab; };
context.renderHtmlWorkspace = () => {};
context.state = {
  activeChatId: "chat-media",
  artifacts: [],
  artifactLibrary: { heads: [], removedResourceUris: [] },
  htmlWorkspaceSelection: null,
  artifactViewerThumbnails: { items: {}, order: [], queue: [], pending: 0 }
};
context.RNAssistantArtifactThumbnailRuntime = {
  state(uri) { return context.state.artifactViewerThumbnails.items[uri] || null; },
  load(request) { loads.push(request.resourceUri); return Promise.resolve(true); }
};

for (let index = 1; index <= 5; index += 1) {
  const uri = `rna://chat/chat-media/artifact/image-${index}/revision/1`;
  context.state.artifacts.push({
    id: `image-${index}`, kind: "image", title: `Image ${index}.png`, revision: 1,
    mimeType: "image/png", resourceUri: uri, metadataJson: `{"attachmentId":"attachment-${index}"}`
  });
  context.state.artifactLibrary.heads.push({
    artifactId: `image-${index}`, resourceClass: "immutable_original", group: "files_media", displayKind: "image",
    history: [{ artifactId: `image-${index}`, revision: 1, resourceUri: uri }]
  });
}

vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-artifacts.js"), "utf8"), context,
  { filename: "app-artifacts.js" });

const parent = new Element("article");
domRoots.push(parent);
const message = {
  resourceRefs: context.state.artifacts.map(artifact => ({ uri: artifact.resourceUri }))
};
context.appendMessageMediaGallery(parent, message);
assert.equal(parent.querySelectorAll(".chat-media-item").length, 4, "chat mosaic renders at most four cells");
assert.equal(parent.querySelector(".chat-media-more").textContent, "+1");
assert.deepEqual(loads, context.state.artifacts.slice(0, 4).map(artifact => artifact.resourceUri));

const firstUri = context.state.artifacts[0].resourceUri;
const ready = {
  status: "ready", resourceUri: firstUri, viewerKind: "image", contentSha256: "a".repeat(64),
  width: 160, height: 120, imageMimeType: "image/jpeg", imageContentSha256: "b".repeat(64),
  imageByteLength: 4, data: { url: "https://rnassistant.local-resource/v1/" + "a".repeat(64) }
};
context.state.artifactViewerThumbnails.items[firstUri] = ready;
context.updateArtifactThumbnailViews(firstUri, ready);
assert.equal(parent.querySelector("img").src, ready.data.url);

parent.querySelectorAll(".chat-media-item")[2].click();
assert.equal(switched, "artifacts");
assert.equal(context.state.htmlWorkspaceSelection.id, "image-3");
assert.equal(context.state.artifactImageGalleryContext.items.length, 5,
  "opening one mosaic cell preserves every exact image in the message context");
assert.equal(context.state.artifactImageGalleryContext.source, "chat");
assert.equal(context.selectArtifactImageGalleryItem(4), true);
assert.equal(context.state.htmlWorkspaceSelection.id, "image-5");
const attachmentIds = context.messageImageAttachmentIds(message);
assert.equal(Object.keys(attachmentIds).length, 5, "generic attachment tiles can be suppressed only for exact image artifacts");
context.openArtifactResource(context.state.artifacts[0], context.state.artifacts, "library:artifact-files");
assert.equal(context.state.artifactImageGalleryContext.source, "library:artifact-files");
console.log("PASS artifact media gallery: chat mosaic, bounded thumbnails and exact navigation context stay separate");

const css = fs.readFileSync(path.join(root, "web/css/app-artifacts.css"), "utf8");
assert.match(css, /\.artifact-collection-grid\s*\{/);
assert.match(css, /\.chat-media-gallery\s*\{/);
assert.match(css, /content-visibility:\s*auto/);
console.log("PASS artifact media gallery: collection grid and lazy media surfaces are wired without a second store");
console.log("OK 2/2");
