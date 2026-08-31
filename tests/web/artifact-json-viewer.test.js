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
  toggle(name, force) { const values = this.values(); const enabled = force === undefined ? !values.has(name) : !!force; if (enabled) values.add(name); else values.delete(name); this.write(values); return enabled; }
  contains(name) { return this.values().has(name); }
}

class Element {
  constructor(tag) {
    this.tagName = String(tag).toLowerCase(); this.className = ""; this.classList = new ClassList(this);
    this.childNodes = []; this.parentNode = null; this.attributes = {}; this.handlers = {};
    this.disabled = false; this._text = "";
  }
  get firstElementChild() { return this.childNodes[0] || null; }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  removeChild(child) { this.childNodes.splice(this.childNodes.indexOf(child), 1); child.parentNode = null; return child; }
  replaceChildren(...children) { this.childNodes.forEach(child => { child.parentNode = null; }); this.childNodes = []; children.forEach(child => this.appendChild(child)); }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  getAttribute(name) { return this.attributes[name]; }
  addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); }
  dispatch(name) { (this.handlers[name] || []).forEach(handler => handler({ preventDefault() {}, stopPropagation() {} })); }
  click() { if (!this.disabled) this.dispatch("click"); }
  select() {}
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  querySelectorAll(selector) {
    const matches = node => selector.startsWith(".") ? node.classList.contains(selector.slice(1)) : node.tagName === selector.toLowerCase();
    const result = [];
    const walk = node => node.childNodes.forEach(child => { if (matches(child)) result.push(child); walk(child); });
    walk(this); return result;
  }
  set textContent(value) { this._text = String(value); this.replaceChildren(); }
  get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(""); }
}

const body = new Element("body");
const copied = [];
const context = vm.createContext({
  document: { body, createElement: tag => new Element(tag), execCommand: () => true },
  navigator: { clipboard: { writeText(text) { copied.push(String(text)); return Promise.resolve(); } } },
  state: { activePlanDocumentArtifactId: "" }
});
context.window = context;
for (const file of ["app-utils.js", "app-viewer-registry.js", "app-json-viewer.js", "app-html-workspace-artifacts.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), context, { filename: file });
}

function button(root, text) { return root.querySelectorAll("button").find(node => node.textContent === text); }
function settle() { return new Promise(resolve => setImmediate(resolve)); }
function render(item, actions) {
  const root = new Element("div");
  context.RNAssistantHtmlWorkspaceArtifacts.renderDetail(root, { type: "artifact", item }, "", actions);
  return root;
}

(async function () {
  const exact = '{"dup":9007199254740993123456789,"dup":"</script><img onerror=1>"}';
  const full = render({ Kind: "tool_result", MimeType: "application/json", InlineText: exact, InlineTruncated: false, Revision: 1 });
  const fullHost = full.querySelector(".artifact-json-viewer");
  assert.ok(fullHost && fullHost.firstElementChild.classList.contains("rn-json-viewer"));
  assert.equal(fullHost.firstElementChild.getAttribute("data-completeness"), "full");
  assert.match(fullHost.textContent, /повтор 1\/2/);
  assert.match(fullHost.textContent, /9007199254740993123456789/);
  button(fullHost, "Копировать всё").click();
  await settle();
  assert.equal(copied.at(-1), exact);
  console.log("PASS artifact JSON viewer: exact inline JSON keeps duplicate keys and numeric lexemes");

  const preview = '{"html":"<main>partial';
  const truncated = render({ Kind: "tool_result", MimeType: "application/json", InlineText: preview, InlineTruncated: true, Revision: 1 });
  const previewHost = truncated.querySelector(".artifact-json-viewer");
  assert.equal(previewHost.firstElementChild.getAttribute("data-completeness"), "preview");
  assert.match(previewHost.textContent, /Позиция:/);
  button(previewHost, "Копировать preview").click();
  await settle();
  assert.equal(copied.at(-1), preview);
  console.log("PASS artifact JSON viewer: bridge-truncated JSON stays an explicit exact preview");

  const text = "<img src=x onerror=alert(1)>\nplain tool output";
  const plain = render({ Kind: "tool_result", MimeType: "text/plain; charset=utf-8", InlineText: text, InlineTruncated: true, Revision: 1 });
  assert.equal(plain.querySelector(".artifact-json-viewer"), null);
  assert.equal(plain.querySelector("pre").textContent, text);
  assert.match(plain.textContent, /ограниченный preview/);
  console.log("PASS artifact JSON viewer: non-JSON content remains inert text with explicit preview state");

  const metadataText = '{"attachmentId":"a-1","size":42}';
  const metadata = render({ Kind: "attachment", MimeType: "image/png", InlineText: "", MetadataJson: metadataText, Revision: 1 });
  const metadataHost = metadata.querySelector(".artifact-json-viewer");
  assert.ok(metadataHost);
  assert.match(metadata.textContent, /Metadata JSON/);
  button(metadataHost, "Копировать всё").click();
  await settle();
  assert.equal(copied.at(-1), metadataText);
  console.log("PASS artifact JSON viewer: metadata fallback uses exact shared viewer");

  const htmlUri = "rna://chat/c/artifact/upload-html/revision/1";
  const hostileHtml = "<script>window.parent.postMessage('run')</script><img onerror=alert(1)>";
  const imported = [];
  context.RNAssistantArtifactVisuals = {
    libraryHead(artifact) { return artifact.libraryHead || null; },
    versionLabel() { return "Оригинал"; }
  };
  const uploadedHtml = render({
    id: "upload-html",
    Kind: "attachment",
    Title: "landing.html",
    MimeType: "text/html; charset=utf-8",
    ResourceUri: htmlUri,
    libraryHead: {
      ResourceClass: "immutable_original",
      History: [{ ArtifactId: "upload-html", ResourceUri: htmlUri }]
    }
  }, {
    uploadedHtmlPreview() {
      return { status: "ready", sourceResourceUri: htmlUri, text: hostileHtml, complete: true, truncated: false };
    },
    loadUploadedHtmlSource() {},
    importUploadedHtml(request) { imported.push(request); }
  });
  assert.equal(context.RNAssistantHtmlWorkspaceArtifacts.isUploadedHtmlArtifact({
    Kind: "attachment", Title: "landing.html", MimeType: "text/html", libraryHead: { ResourceClass: "immutable_original" }
  }), true);
  assert.equal(uploadedHtml.querySelector("pre").textContent, hostileHtml);
  assert.equal(uploadedHtml.querySelector("script"), null, "uploaded source never becomes DOM");
  assert.match(uploadedHtml.textContent, /инертен/);
  button(uploadedHtml, "Импортировать в HTML workspace").click();
  assert.equal(imported.length, 1);
  assert.equal(imported[0].sourceResourceUri, htmlUri);
  assert.equal(imported[0].targetPath, "landing.html");
  console.log("PASS artifact JSON viewer: uploaded HTML stays escaped and inert until explicit import");

  const oldHost = fullHost;
  context.RNAssistantHtmlWorkspaceArtifacts.renderDetail(full, {
    type: "artifact",
    item: { Kind: "tool_result", MimeType: "text/plain", InlineText: "next", Revision: 1 }
  }, "");
  assert.equal(oldHost.childNodes.length, 0, "re-render destroys the replaced viewer controller");
  const source = fs.readFileSync(path.join(__dirname, "../../web/js/app-html-workspace-artifacts.js"), "utf8");
  assert.equal(/JSON\.stringify\(JSON\.parse\(content\)/.test(source), false);
  assert.equal(/createElement\("pre"\)[\s\S]{0,120}JSON\.parse\(content\)/.test(source), false);
  console.log("PASS artifact JSON viewer: re-render unmounts viewer and old pretty/pre path is removed");
  console.log("OK 6/6");
})().catch(error => { console.error(error); process.exitCode = 1; });
