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
  toggle(name, force) { const values = this.values(); const enabled = force === undefined ? !values.has(name) : !!force; if (enabled) values.add(name); else values.delete(name); this.write(values); return enabled; }
  contains(name) { return this.values().has(name); }
}

class Element {
  constructor(tag) {
    this.tagName = String(tag).toLowerCase(); this.className = ""; this.classList = new ClassList(this);
    this.childNodes = []; this.parentNode = null; this.attributes = {}; this.handlers = {};
    this.dataset = {}; this.style = {}; this.open = false; this.disabled = false; this._text = ""; this._html = "";
  }
  get firstElementChild() { return this.childNodes[0] || null; }
  appendChild(child) { this.detach(child); child.parentNode = this; this.childNodes.push(child); return child; }
  insertBefore(child, reference) { this.detach(child); const index = this.childNodes.indexOf(reference); child.parentNode = this; this.childNodes.splice(index < 0 ? this.childNodes.length : index, 0, child); return child; }
  replaceChild(child, oldChild) { const index = this.childNodes.indexOf(oldChild); if (index < 0) throw new Error("old child missing"); this.detach(child); oldChild.parentNode = null; child.parentNode = this; this.childNodes[index] = child; return oldChild; }
  removeChild(child) { const index = this.childNodes.indexOf(child); if (index >= 0) this.childNodes.splice(index, 1); child.parentNode = null; return child; }
  detach(child) { if (child.parentNode) child.parentNode.removeChild(child); }
  replaceChildren(...children) { this.childNodes.forEach(child => { child.parentNode = null; }); this.childNodes = []; this._text = ""; this._html = ""; children.forEach(child => this.appendChild(child)); }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  getAttribute(name) { return this.attributes[name]; }
  addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); }
  dispatch(name) { (this.handlers[name] || []).forEach(handler => handler({ preventDefault() {}, stopPropagation() {} })); }
  click() { if (!this.disabled) this.dispatch("click"); }
  select() {}
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  querySelectorAll(selector) {
    const matches = node => {
      if (selector === "pre code") return node.tagName === "code" && node.parentNode && node.parentNode.tagName === "pre";
      if (selector === "details[open]") return node.tagName === "details" && node.open;
      if (selector.startsWith(".")) return node.classList.contains(selector.slice(1));
      return node.tagName === selector.toLowerCase();
    };
    const result = [];
    const walk = node => node.childNodes.forEach(child => { if (matches(child)) result.push(child); walk(child); });
    walk(this); return result;
  }
  set textContent(value) { this._text = String(value); this.childNodes.forEach(child => { child.parentNode = null; }); this.childNodes = []; this._html = ""; }
  get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(""); }
  set innerHTML(value) { this._html = String(value); this._text = ""; this.childNodes.forEach(child => { child.parentNode = null; }); this.childNodes = []; }
  get innerHTML() { return this._html; }
  get innerText() { return this.textContent; }
}

const body = new Element("body");
const copied = [];
const context = vm.createContext({
  document: { body, createElement: tag => new Element(tag), execCommand: () => true },
  navigator: { clipboard: { writeText(text) { copied.push(String(text)); return Promise.resolve(); } } },
  state: { highlightRetryAttempts: 0, highlightRetryScheduled: false, highlightLoadLogged: false },
  DOMPurify: { sanitize: text => text },
  marked: { parse: text => text },
  logOnce() {},
  setTimeout() { return 1; }
});
context.window = context;
for (const file of ["app-utils.js", "app-viewer-registry.js", "app-json-viewer.js", "app-markdown.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), context, { filename: file });
}

function codeRoot(language, text) {
  const root = new Element("div");
  const pre = new Element("pre");
  const code = new Element("code");
  code.className = "language-" + language;
  code.textContent = text;
  pre.appendChild(code); root.appendChild(pre);
  return root;
}
function button(root, text) { return root.querySelectorAll("button").find(node => node.textContent === text); }
function settle() { return new Promise(resolve => setImmediate(resolve)); }

(async function () {
  const exactBody = "{\r\n  \"dup\": 9007199254740993123456789,\r\n  \"dup\": \"</script><img onerror=1>\"\r\n}\r\n";
  const source = "before\r\n```json\r\n" + exactBody + "```\r\nafter";
  const root = codeRoot("json", exactBody.replace(/\r\n/g, "\n"));
  context.enhanceMarkdown(root, { enableJsonViewer: true, sourceText: source });
  const details = root.querySelector(".markdown-json-block");
  const host = root.querySelector(".markdown-json-viewer");
  assert.ok(details && host);
  assert.equal(details.open, false);
  assert.equal(host.childNodes.length, 0, "collapsed Markdown JSON stays lazy");
  details.open = true; details.dispatch("toggle");
  assert.match(host.textContent, /повтор 1\/2/);
  assert.match(host.textContent, /9007199254740993123456789/);
  assert.match(host.textContent, /<\/script><img onerror=1>/);
  assert.equal(host.querySelector("script"), null);
  assert.equal(host.querySelector("img"), null);
  button(host, "Копировать всё").click();
  await settle();
  assert.equal(copied.at(-1), exactBody, "copy keeps the exact fenced CRLF body");
  console.log("PASS Markdown JSON viewer: completed fence is lazy, lossless and text-safe");

  context.clearMarkdownEnhancements(root);
  assert.equal(host.childNodes.length, 0, "message replacement destroys mounted viewer state");
  console.log("PASS Markdown JSON viewer: explicit message cleanup unmounts viewer state");

  const streaming = codeRoot("json", "{\"partial\":");
  context.enhanceMarkdown(streaming, { enableJsonViewer: true, sourceText: "```json\n{\"partial\":", streaming: true });
  assert.equal(streaming.querySelector(".markdown-json-block"), null);
  assert.ok(streaming.querySelector(".code-wrap"));
  console.log("PASS Markdown JSON viewer: live stream never parses a partial fence");

  const unclosed = codeRoot("json", "{\"partial\":");
  context.enhanceMarkdown(unclosed, { enableJsonViewer: true, sourceText: "```json\n{\"partial\":" });
  assert.equal(unclosed.querySelector(".markdown-json-block"), null);
  assert.ok(unclosed.querySelector(".code-wrap"));
  console.log("PASS Markdown JSON viewer: unclosed stable fence remains ordinary code");

  const javascript = codeRoot("javascript", "{\"looks\":\"json\"}");
  context.enhanceMarkdown(javascript, { enableJsonViewer: true, sourceText: "```javascript\n{\"looks\":\"json\"}\n```\n" });
  assert.equal(javascript.querySelector(".markdown-json-block"), null);
  assert.ok(javascript.querySelector(".code-wrap"));
  console.log("PASS Markdown JSON viewer: language contract prevents content sniffing");

  const mixed = new Element("div");
  const injected = codeRoot("json", "{\"injected\":true}\n").firstElementChild;
  const fenced = codeRoot("json", "{\"actual\":true}\n").firstElementChild;
  mixed.appendChild(injected); mixed.appendChild(fenced);
  context.enhanceMarkdown(mixed, { enableJsonViewer: true, sourceText: "<pre><code class=\"language-json\">injected</code></pre>\n\n```json\n{\"actual\":true}\n```\n" });
  assert.ok(injected.parentNode.classList.contains("code-wrap"), "raw HTML block does not steal fenced source metadata");
  assert.ok(mixed.querySelector(".markdown-json-block"));
  console.log("PASS Markdown JSON viewer: DOM/source mismatch fails back to ordinary code");

  const indentedBody = "  {\"indented\":true}\n";
  const indented = codeRoot("json", "{\"indented\":true}\n");
  context.enhanceMarkdown(indented, { enableJsonViewer: true, sourceText: "  ```json\n" + indentedBody + "  ```\n" });
  const indentedDetails = indented.querySelector(".markdown-json-block");
  assert.ok(indentedDetails, "CommonMark fence indentation is matched to rendered text");
  indentedDetails.open = true; indentedDetails.dispatch("toggle");
  button(indented, "Копировать всё").click();
  await settle();
  assert.equal(copied.at(-1), indentedBody, "raw copy retains source indentation");
  console.log("PASS Markdown JSON viewer: indented fence matches DOM but preserves raw source");

  const markdownSource = fs.readFileSync(path.join(__dirname, "../../web/js/app-markdown.js"), "utf8");
  const messagesSource = fs.readFileSync(path.join(__dirname, "../../web/js/app-messages.js"), "utf8");
  assert.match(markdownSource, /DOMPurify\.sanitize\(marked\.parse/);
  assert.match(messagesSource, /sourceText:\s*state\.liveStreamContent,\s*streaming:\s*true/);
  assert.match(messagesSource, /clearMarkdownEnhancements\(box\)/);
  assert.equal(/innerHTML\s*=\s*fence\.text/.test(markdownSource), false);
  console.log("PASS Markdown JSON viewer: post-sanitize opt-in and stream lifecycle are wired");
  console.log("OK 8/8");
})().catch(error => { console.error(error); process.exitCode = 1; });
