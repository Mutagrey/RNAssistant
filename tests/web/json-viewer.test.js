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
  toggle(name, force) {
    const values = this.values();
    const enabled = force === undefined ? !values.has(name) : !!force;
    if (enabled) values.add(name); else values.delete(name);
    this.write(values);
    return enabled;
  }
  contains(name) { return this.values().has(name); }
}

class Element {
  constructor(tag) {
    this.tagName = String(tag).toLowerCase();
    this.className = "";
    this.classList = new ClassList(this);
    this.childNodes = [];
    this.parentNode = null;
    this.attributes = {};
    this.handlers = {};
    this.open = false;
    this.disabled = false;
    this._text = "";
  }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  removeChild(child) { this.childNodes.splice(this.childNodes.indexOf(child), 1); child.parentNode = null; return child; }
  replaceChildren(...children) { this.childNodes.forEach(child => { child.parentNode = null; }); this.childNodes = []; children.forEach(child => this.appendChild(child)); }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  getAttribute(name) { return this.attributes[name]; }
  addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); }
  dispatch(name) { (this.handlers[name] || []).forEach(handler => handler({ preventDefault() {}, stopPropagation() {} })); }
  click() { if (!this.disabled) this.dispatch("click"); }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  querySelectorAll(selector) {
    const matches = node => {
      if (selector === "details[open]") return node.tagName === "details" && node.open;
      if (selector.startsWith(".")) return node.classList.contains(selector.slice(1));
      return node.tagName === selector.toLowerCase();
    };
    const found = [];
    const walk = node => node.childNodes.forEach(child => { if (matches(child)) found.push(child); walk(child); });
    walk(this);
    return found;
  }
  set textContent(value) { this._text = String(value); this.childNodes = []; }
  get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(""); }
}

const sourceFile = path.join(__dirname, "../../web/js/app-json-viewer.js");
const shippedSource = fs.readFileSync(sourceFile, "utf8");
const context = vm.createContext({ document: { createElement: tag => new Element(tag) } });
context.window = context;
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-viewer-registry.js"), "utf8"), context, { filename: "app-viewer-registry.js" });
vm.runInContext(shippedSource, context, { filename: "app-json-viewer.js" });
const viewer = context.RNAssistantJsonViewer;

function raw(document, node) { return viewer.raw(document, node); }
function findByText(root, tag, text) {
  return root.querySelectorAll(tag).find(node => node.textContent === text);
}

const exact = "{\r\n  \"dup\": 9007199254740993123456789,\r\n  \"dup\": -0.00010e+09,\r\n  \"escaped\": \"line\\n\\u0041\",\r\n  \"html\": \"</script><img src=x onerror=alert(1)>\",\r\n  \"items\": [true, false, null, {}]\r\n}";
const parsed = viewer.parse(exact);
assert.equal(parsed.ok, true);
assert.equal(parsed.root.entries.length, 5);
assert.equal(parsed.duplicateKeyCount, 2);
assert.equal(parsed.root.entries[0].value.path, '$["dup"]#1');
assert.equal(parsed.root.entries[1].value.path, '$["dup"]#2');
assert.equal(raw(parsed, parsed.root.entries[0].value), "9007199254740993123456789");
assert.equal(raw(parsed, parsed.root.entries[1].value), "-0.00010e+09");
assert.equal(parsed.root.entries[2].value.value, "line\nA");
assert.equal(raw(parsed, parsed.root), exact);
const pretty = viewer.format(parsed);
assert.equal(pretty.ok, true);
assert.equal((pretty.text.match(/\"dup\"/g) || []).length, 2);
assert.match(pretty.text, /9007199254740993123456789/);
assert.match(pretty.text, /-0\.00010e\+09/);
assert.equal(viewer.parse(pretty.text).ok, true);
console.log("PASS json viewer: lossless duplicate keys, numbers, strings and CRLF source");

for (const [input, code] of [
  ['{"a":1', "syntax.object-separator"],
  ['{"a":01}', "syntax.number-leading-zero"],
  ['{"a":"x\\q"}', "syntax.string-escape"],
  ['true false', "syntax.trailing"]
]) {
  const result = viewer.parse(input);
  assert.equal(result.ok, false, input);
  assert.equal(result.error.code, code, input);
  assert.equal(result.source, input);
  assert.equal(Number.isInteger(result.error.position), true);
}
console.log("PASS json viewer: invalid and truncated input keeps raw source and exact error kind");

assert.equal(viewer.parse("[1,2,3]", { maxNodes: 3 }).error.code, "limit.nodes");
assert.equal(viewer.parse("[[[0]]]", { maxDepth: 2 }).error.code, "limit.depth");
assert.equal(viewer.parse("12345", { maxChars: 4 }).error.code, "limit.chars");
let cancellationChecks = 0;
const cancelled = viewer.parse(" ".repeat(2500) + "null", {
  shouldCancel() { cancellationChecks += 1; return true; }
});
assert.equal(cancelled.error.code, "cancelled");
assert.equal(cancellationChecks, 1);
const clamped = viewer.normalizeLimits({ maxChars: Number.MAX_SAFE_INTEGER, maxDepth: 99999, childPageSize: 0 });
assert.equal(clamped.maxChars, 2000000);
assert.equal(clamped.maxDepth, 128);
assert.equal(clamped.childPageSize, 1);
console.log("PASS json viewer: parse, depth, node, cancellation and hard limits cannot be bypassed");

const copies = [];
const component = viewer.create({
  text: exact,
  completeness: "preview",
  limits: { childPageSize: 5, maxDomRows: 20, maxInlineStringChars: 24 },
  onCopy(text, metadata) { copies.push({ text, metadata }); }
});
assert.equal(component.element.getAttribute("data-completeness"), "preview");
assert.match(component.element.textContent, /Ограниченный preview/);
assert.match(component.element.textContent, /повтор 1\/2/);
assert.match(component.element.textContent, /<\/script><img src=x/);
const rootDetails = component.element.querySelector("details");
assert.ok(rootDetails.childNodes[1].classList.contains("rn-json-children"));
assert.ok(rootDetails.childNodes[rootDetails.childNodes.length - 1].classList.contains("rn-json-closing-row"));
assert.equal(component.element.querySelector(".rn-json-closing-row").getAttribute("aria-hidden"), "true");
assert.ok(component.element.querySelector(".rn-json-container-close"));
assert.match(fs.readFileSync(path.join(__dirname, "../../web/css/app-json-viewer.css"), "utf8"), /rn-json-has-children\[open\].*rn-json-container-close/s);
assert.equal(shippedSource.includes("innerHTML"), false, "renderer must use text nodes only");
const twoItemComponent = viewer.create({ text: "{\"a\":1,\"b\":2}", onCopy() {} });
assert.match(twoItemComponent.element.textContent, /2 элемента/);
assert.doesNotMatch(twoItemComponent.element.textContent, /2 элементов/);
const nodeCopyButtons = component.element.querySelectorAll("button").filter(node => node.textContent === "Узел");
const pathCopyButtons = component.element.querySelectorAll("button").filter(node => node.textContent === "Путь");
const valueCopyButton = findByText(component.element, "button", "Текст");
nodeCopyButtons[1].click();
assert.equal(copies.at(-1).text, "9007199254740993123456789");
pathCopyButtons[1].click();
assert.equal(copies.at(-1).text, '$["dup"]#1');
valueCopyButton.click();
assert.equal(copies.at(-1).text, "line\nA");
findByText(component.element, "button", "Копировать preview").click();
assert.equal(copies.at(-1).text, exact);
assert.equal(copies.at(-1).metadata.kind, "source");
findByText(component.element, "button", "Исходный").click();
const rawPre = component.element.querySelector("pre");
assert.equal(rawPre.textContent, exact);
findByText(component.element, "button", "Форматированный").click();
assert.match(component.element.querySelector("pre").textContent, /9007199254740993123456789/);
console.log("PASS json viewer: safe modes, completeness and owner-controlled exact copy");

const many = viewer.create({
  text: "[" + Array.from({ length: 40 }, (_, index) => index).join(",") + "]",
  limits: { childPageSize: 5, maxDomRows: 8 },
  onCopy() {}
});
assert.equal(many.element.querySelectorAll(".rn-json-scalar-row").length, 5);
let more = many.element.querySelector(".rn-json-more");
assert.ok(more);
more.click();
assert.equal(many.element.querySelectorAll(".rn-json-scalar-row").length, 8);
more = many.element.querySelector(".rn-json-more");
assert.ok(more && more.disabled);
findByText(many.element, "button", "Свернуть").click();
assert.equal(many.element.querySelectorAll("details[open]").length, 0);
console.log("PASS json viewer: lazy child pages and DOM budget remain bounded");

const invalid = viewer.create({ text: '{"html":"<b>safe text</b>"', onCopy() { throw new Error("clipboard denied"); } });
assert.match(invalid.element.textContent, /Позиция:/);
assert.match(invalid.element.querySelector("pre").textContent, /<b>safe text<\/b>/);
findByText(invalid.element, "button", "Копировать всё").click();
assert.match(invalid.element.textContent, /Не удалось скопировать/);
console.log("PASS json viewer: malformed markup stays text and clipboard failure is visible");

const host = new Element("div");
const registered = context.RNAssistantViewerRegistry.mount("json", host, { text: "{\"ready\":true}", onCopy() {} });
assert.equal(host.childNodes[0], registered.element);
assert.deepEqual(Array.from(context.RNAssistantViewerRegistry.kinds()), ["json"]);
assert.throws(() => context.RNAssistantViewerRegistry.mount("pdf", host, {}), /not registered/);
context.RNAssistantViewerRegistry.unmount(host);
assert.equal(host.childNodes.length, 0);
const page = fs.readFileSync(path.join(__dirname, "../../web/index.html"), "utf8");
assert.ok(page.indexOf("app-viewer-registry.js") < page.indexOf("app-json-viewer.js"));
assert.ok(page.indexOf("app-json-viewer.js") < page.indexOf("app-trajectory.js"));
console.log("PASS json viewer: registry is allowlisted, UI-only and replaces mounted controllers");

console.log("OK 7/7");
