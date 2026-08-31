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
    this.childNodes = []; this.parentNode = null; this.handlers = {}; this.attributes = {};
    this.disabled = false; this.value = ""; this._text = ""; this._html = "";
  }
  get firstElementChild() { return this.childNodes[0] || null; }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  replaceChildren(...children) { this.childNodes.forEach(child => { child.parentNode = null; }); this.childNodes = []; children.forEach(child => this.appendChild(child)); this._text = ""; this._html = ""; }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); }
  click() { if (!this.disabled) (this.handlers.click || []).forEach(handler => handler({})); }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  querySelectorAll(selector) {
    const matches = node => selector.startsWith(".") ? node.classList.contains(selector.slice(1)) : node.tagName === selector.toLowerCase();
    const result = [];
    const walk = node => node.childNodes.forEach(child => { if (matches(child)) result.push(child); walk(child); });
    walk(this); return result;
  }
  set textContent(value) { this._text = String(value); this.childNodes = []; this._html = ""; }
  get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(""); }
  set innerHTML(value) { this._html = String(value); this._text = ""; this.childNodes = []; }
  get innerHTML() { return this._html; }
}

const root = path.join(__dirname, "../..");
const copied = [];
const downloads = [];
const context = vm.createContext({
  document: { createElement: tag => new Element(tag) },
  state: {},
  Promise
});
context.window = context;
context.copyTextResult = text => { copied.push(String(text)); return Promise.resolve(); };
context.markdown = text => "<p>" + String(text).replace(/</g, "&lt;") + "</p>";
context.enhanceMarkdown = node => { node.attributes.enhanced = "true"; };
context.clearMarkdownEnhancements = () => {};
for (const file of ["app-viewer-registry.js", "app-text-viewer.js"]) {
  vm.runInContext(fs.readFileSync(path.join(root, "web/js", file), "utf8"), context, { filename: file });
}

function button(node, label) {
  return node.querySelectorAll("button").find(item => item.textContent === label);
}
function settle() { return new Promise(resolve => setImmediate(resolve)); }

(async function () {
  const hostile = "first\n<script>window.run()</script>";
  let nextCount = 0;
  const partialHost = new Element("div");
  context.RNAssistantViewerRegistry.mount("text", partialHost, {
    text: hostile,
    offset: 32000,
    startLine: 8,
    totalCharacters: 50000,
    sourceComplete: true,
    fullReadAllowed: true,
    hasNext: true,
    onNext() { nextCount += 1; },
    onLoadFull() {},
    onCopy: context.copyTextResult
  });
  assert.equal(partialHost.querySelector(".rn-text-viewer-content").textContent, hostile);
  assert.equal(partialHost.querySelector(".rn-text-viewer-lines").textContent, "8\n9");
  assert.match(partialHost.textContent, /Страница 32001/);
  assert.equal(button(partialHost, "Скачать"), undefined);
  button(partialHost, "Копировать страницу").click();
  button(partialHost, "→").click();
  await settle();
  assert.equal(copied.at(-1), hostile);
  assert.equal(nextCount, 1);
  const search = partialHost.querySelector(".rn-text-viewer-search");
  search.value = "script";
  button(partialHost, "Найти").click();
  assert.match(partialHost.textContent, /Совпадений: 2/);
  console.log("PASS artifact text viewer: bounded page is inert, numbered, searchable and page-copy only");

  const fullHost = new Element("div");
  context.RNAssistantViewerRegistry.mount("text", fullHost, {
    text: hostile,
    fullText: hostile,
    complete: true,
    sourceComplete: true,
    onCopy: context.copyTextResult,
    onDownload(text) { downloads.push(text); }
  });
  button(fullHost, "Скачать").click();
  await settle();
  assert.equal(downloads[0], hostile);
  assert.match(fullHost.textContent, /Полный exact source/);
  console.log("PASS artifact text viewer: full copy/download appears only for a complete exact read");

  const markdownHost = new Element("div");
  context.RNAssistantViewerRegistry.mount("markdown", markdownHost, {
    text: "# Safe\n<script>x</script>",
    fullText: "# Safe\n<script>x</script>",
    complete: true,
    sourceComplete: true,
    onCopy: context.copyTextResult
  });
  const rendered = markdownHost.querySelector(".rn-markdown-viewer-rendered");
  assert.ok(rendered);
  assert.match(rendered.innerHTML, /&lt;script>/);
  button(markdownHost, "Источник").click();
  assert.equal(markdownHost.querySelector(".rn-text-viewer-content").textContent, "# Safe\n<script>x</script>");
  console.log("PASS artifact Markdown viewer: sanitized rendered view and exact Source share one controller");

  const incompleteMarkdown = new Element("div");
  context.RNAssistantViewerRegistry.mount("markdown", incompleteMarkdown, {
    text: "# Partial",
    complete: false,
    sourceComplete: false,
    fullReadAllowed: false,
    onCopy: context.copyTextResult
  });
  assert.equal(incompleteMarkdown.querySelector(".rn-markdown-viewer-rendered"), null);
  assert.match(incompleteMarkdown.textContent, /preview отключён/);
  assert.equal(button(incompleteMarkdown, "Скачать"), undefined);
  console.log("PASS artifact Markdown viewer: truncated source never becomes rendered or full-download authority");

  const actionContext = vm.createContext({ Promise });
  actionContext.window = actionContext;
  actionContext.alert = () => {};
  for (const file of ["app-artifact-viewer-actions.js", "app-html-workspace-actions.js"]) {
    vm.runInContext(fs.readFileSync(path.join(root, "web/js", file), "utf8"), actionContext, { filename: file });
  }
  const uri = "rna://chat/chat-text/artifact/notes/revision/1";
  const exact = "x".repeat(32000) + "tail exact";
  const calls = [];
  const applied = [];
  const downloaded = [];
  const state = { activeChatId: "chat-text", bridgeUnavailable: false };
  const actions = actionContext.RNAssistantHtmlWorkspaceActions.create({
    state,
    send: async (method, payload) => {
      calls.push({ method, payload: JSON.parse(JSON.stringify(payload)) });
      const offset = payload.cursor ? 32000 : 0;
      const text = exact.slice(offset, offset + 32000);
      return {
        resourceUri: uri,
        viewerKind: "text",
        title: "notes.txt",
        mimeType: "text/plain",
        contentSha256: "c".repeat(64),
        text,
        offset,
        returnedCharacters: text.length,
        totalCharacters: exact.length,
        nextCursor: offset ? null : "32000",
        complete: offset > 0,
        truncated: offset === 0,
        sourceComplete: true,
        fullReadAllowed: true,
        viewerLimitReached: false,
        maximumDocumentCharacters: 512000
      };
    },
    applyArtifactViewerText: (resourceUri, hash, text) => applied.push({ resourceUri, hash, text }),
    downloadArtifactText: value => downloaded.push(value),
    log: () => {},
    render: () => {}
  });
  assert.equal(await actions.loadArtifactViewer({ resourceUri: uri }), true);
  assert.equal(actions.artifactViewerState(uri).pages.length, 1);
  assert.equal(await actions.loadArtifactViewerFull({ resourceUri: uri }), true);
  assert.equal(actions.artifactViewerState(uri).fullText, exact);
  assert.equal(calls[1].payload.cursor, "32000");
  assert.equal(applied.at(-1).text, exact);
  actions.downloadArtifactViewer({ resourceUri: uri });
  assert.equal(downloaded[0].resourceUri, uri);
  assert.equal(downloaded[0].text, exact);
  console.log("PASS artifact viewer owner: exact pinned pages assemble contiguously before full copy/download");

  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  assert.ok(index.includes("app-text-viewer.js?v=artifact-text-20260831-1"));
  assert.ok(index.includes("app-text-viewer.css?v=artifact-text-20260831-1"));
  assert.ok(index.includes("app-artifact-viewer-actions.js?v=artifact-text-20260831-1"));
  assert.ok(index.indexOf("app-viewer-registry.js") < index.indexOf("app-text-viewer.js"));
  assert.ok(index.indexOf("app-artifact-viewer-actions.js") < index.indexOf("app-html-workspace-actions.js"));
  const viewerSource = fs.readFileSync(path.join(root, "web/js/app-text-viewer.js"), "utf8");
  const actionSource = fs.readFileSync(path.join(root, "web/js/app-artifact-viewer-actions.js"), "utf8");
  assert.doesNotMatch(viewerSource, /send\(|chrome\.webview|fetch\(|XMLHttpRequest|createObjectURL/);
  assert.match(viewerSource, /window\.markdown\(String\(options\.fullText\)\)/);
  assert.match(actionSource, /readArtifactViewerPage/);
  console.log("PASS artifact viewers: allowlisted modules are UI-only and loaded after their registry");
  console.log("OK 6/6");
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
