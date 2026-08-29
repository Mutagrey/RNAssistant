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
  toggle(name, force) {
    const values = this.values();
    const enabled = force === undefined ? !values.has(name) : !!force;
    if (enabled) values.add(name); else values.delete(name);
    this.write(values); return enabled;
  }
  contains(name) { return this.values().has(name); }
}

class Element {
  constructor(tag) {
    this.tagName = String(tag).toLowerCase(); this.className = ""; this.classList = new ClassList(this);
    this.childNodes = []; this.parentNode = null; this.attributes = {}; this.handlers = {}; this._text = "";
    this.id = ""; this.type = ""; this.title = "";
  }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  replaceChildren(...children) { this.childNodes.forEach(child => { child.parentNode = null; }); this.childNodes = []; children.forEach(child => this.appendChild(child)); }
  setAttribute(name, value) { this.attributes[name] = String(value); if (name === "id") this.id = String(value); }
  getAttribute(name) { return Object.prototype.hasOwnProperty.call(this.attributes, name) ? this.attributes[name] : null; }
  removeAttribute(name) { delete this.attributes[name]; if (name === "id") this.id = ""; }
  addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); }
  dispatch(name) { (this.handlers[name] || []).forEach(handler => handler({ preventDefault() {}, stopPropagation() {} })); }
  click() { this.dispatch("click"); }
  remove() { if (this.parentNode) this.parentNode.childNodes.splice(this.parentNode.childNodes.indexOf(this), 1); this.parentNode = null; }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  querySelectorAll(selector) {
    const matches = node => {
      if (selector === '[role="treeitem"][aria-selected="true"]') {
        return node.getAttribute("role") === "treeitem" && node.getAttribute("aria-selected") === "true";
      }
      if (selector.startsWith(".")) return node.classList.contains(selector.slice(1));
      return node.tagName === selector.toLowerCase();
    };
    const result = [];
    const walk = node => node.childNodes.forEach(child => { if (matches(child)) result.push(child); walk(child); });
    walk(this); return result;
  }
  closest(selector) {
    let node = this;
    while (node) { if (selector.startsWith(".") && node.classList.contains(selector.slice(1))) return node; node = node.parentNode; }
    return null;
  }
  set textContent(value) { this._text = String(value); this.replaceChildren(); }
  get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(""); }
}

const roots = [];
const document = {
  createElement: tag => new Element(tag),
  getElementById(id) {
    let match = null;
    const walk = node => {
      if (node.id === id) match = node;
      if (!match) node.childNodes.forEach(walk);
    };
    roots.forEach(walk); return match;
  }
};

class FakeNode {
  constructor(tree, parent, source) {
    this.tree = tree; this.parent = parent; this.key = source.key; this.title = source.title;
    this.expanded = !!source.expanded; this.selected = !!source.selected; this.data = {};
    for (const [key, value] of Object.entries(source)) {
      if (!["key", "title", "tooltip", "expanded", "selected", "unselectable", "classes", "icon", "children"].includes(key)) this.data[key] = value;
    }
    this.children = (source.children || []).map(child => new FakeNode(tree, this, child));
  }
  getLevel() { let level = 0; for (let node = this.parent; node; node = node.parent) level += 1; return level; }
  setExpanded(flag, options) {
    this.expanded = !!flag;
    if (!(options && options.noEvents)) this.tree.options.expand({ tree: this.tree, node: this, flag: this.expanded });
    return Promise.resolve();
  }
  setActive(flag, options) {
    const previous = this.tree.activeNode;
    this.tree.activeNode = flag ? this : null;
    if (flag && !(options && options.noEvents)) this.tree.options.activate({ tree: this.tree, node: this, prevNode: previous });
    return Promise.resolve();
  }
}

class FakeWunderbaum {
  static iconMaps = { bootstrap: {} };
  constructor(options) {
    this.options = options; this.element = options.element; this.activeNode = null; this.destroyed = false;
    this.element.classList.add("wunderbaum");
    this.root = { children: options.source.map(source => new FakeNode(this, null, source)) };
    this.nodes = [];
    const collect = node => { this.nodes.push(node); node.children.forEach(collect); };
    this.root.children.forEach(collect);
    for (const node of this.nodes) {
      const nodeElem = new Element("span");
      const title = new Element("span"); title.className = "wb-title"; title.textContent = node.title; nodeElem.appendChild(title);
      options.render({ tree: this, node, nodeElem });
      this.element.appendChild(nodeElem);
    }
    FakeWunderbaum.last = this;
    this.ready = Promise.resolve();
  }
  findKey(key) { return this.nodes.find(node => node.key === key) || null; }
  getActiveNode() { return this.activeNode; }
  getFocusNode() { return this.activeNode; }
  destroy() { this.destroyed = true; this.element.replaceChildren(); }
}

const context = vm.createContext({ document, mar10: { Wunderbaum: FakeWunderbaum }, Promise });
context.window = context;
const adapterPath = path.join(__dirname, "../../web/js/app-tree-adapter.js");
const adapterSource = fs.readFileSync(adapterPath, "utf8");
vm.runInContext(adapterSource, context, { filename: "app-tree-adapter.js" });
const adapter = context.RNAssistantTreeAdapter;

const malicious = '<img src=x onerror="alert(1)">';
const normalized = adapter.normalize([{ key: "g", title: malicious, groupKey: "g", expanded: true, children: [
  { key: "item::file::1", title: "main.html", itemType: "file", itemId: "1", deletable: true }
]}], { selectedKey: "item::file::1", limits: { maxNodes: 999999, maxDepth: 999999 } });
assert.equal(normalized.count, 2);
assert.equal(normalized.nodes[0].title, malicious);
assert.equal(normalized.nodes[0].children[0].selected, true);
assert.equal(normalized.limits.maxNodes, 2500);
assert.equal(normalized.limits.maxDepth, 16);
assert.notEqual(adapter.normalize([{ key: "other", title: "Other" }]).nodes[0].rnDomId, normalized.nodes[0].rnDomId);
assert.throws(() => adapter.normalize("/tree.json"), /local array/);
assert.throws(() => adapter.normalize([{ key: "a", children: [{ key: "a" }] }]), /Duplicate tree key/);
assert.throws(() => adapter.normalize([{ key: "a", children: "/lazy" }]), /children must be a local array/);
assert.throws(() => adapter.normalize([{ key: "a", itemType: "url", itemId: "1" }]), /Unsupported tree item type/);
console.log("PASS tree adapter: local typed input and hard node/depth/key bounds are fail-closed");

const root = new Element("div"); root.id = "tree"; root.className = "tool-list html-workspace-tree";
root.setAttribute("role", "tree"); root.setAttribute("aria-label", "Ресурсы чата"); roots.push(root);
const activated = []; const deleted = []; const toggled = [];
const controller = adapter.mount(root, {
  nodes: [{ key: "group::files", groupKey: "files", title: "Файлы", expanded: true, iconKind: "folder", children: [
    { key: "item::file::1", itemType: "file", itemId: "1", title: malicious, meta: "html", iconKind: "html", deletable: true },
    { key: "item::file::2", itemType: "file", itemId: "2", title: "next.html", iconKind: "html" }
  ] }],
  selectedKey: "item::file::1",
  onActivate(item) { activated.push(item); return item.id !== "2"; },
  onDelete(item) { deleted.push(item); },
  onToggle(key, expanded) { toggled.push([key, expanded]); }
});

(async function () {
  assert.equal(controller.vendor, "wunderbaum@0.14.1");
  assert.equal(FakeWunderbaum.last.options.dnd, null);
  assert.equal(FakeWunderbaum.last.options.edit, null);
  assert.equal(FakeWunderbaum.last.options.filter, null);
  assert.equal(Array.isArray(FakeWunderbaum.last.options.source), true);
  await controller.ready;
  assert.equal(root.getAttribute("aria-busy"), null);
  assert.equal(FakeWunderbaum.last.getActiveNode().key, "item::file::1");
  assert.ok(root.getAttribute("aria-activedescendant"));
  assert.equal(root.querySelectorAll("img").length, 0);

  const group = FakeWunderbaum.last.findKey("group::files");
  FakeWunderbaum.last.options.click({
    tree: FakeWunderbaum.last, node: group, info: { region: "title" },
    event: { target: { closest() { return null; } } }
  });
  assert.deepEqual(toggled.at(-1), ["files", false]);

  await FakeWunderbaum.last.findKey("item::file::2").setActive(true);
  assert.equal(activated.at(-1).id, "2");
  assert.equal(FakeWunderbaum.last.getActiveNode().key, "item::file::1", "rejected activation restores the prior visible item");
  root.querySelector(".rn-tree-action").click();
  assert.equal(deleted.at(-1).id, "1");

  const tree = FakeWunderbaum.last;
  controller.destroy();
  assert.equal(tree.destroyed, true);
  assert.equal(root.className, "tool-list html-workspace-tree");
  assert.equal(root.getAttribute("aria-label"), "Ресурсы чата");
  console.log("PASS tree adapter: vendor lifecycle, ARIA, selection rejection, grouping and delete stay adapter-owned");

  context.state = { collapsedResourceGroups: { "html-styles": true } };
  let captured = null;
  context.RNAssistantTreeAdapter = { mount(target, options) { captured = options; return { count: 9 }; } };
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-html-workspace-tree.js"), "utf8"), context, { filename: "app-html-workspace-tree.js" });
  const selected = [];
  const count = context.RNAssistantHtmlWorkspaceTree.render({
    root,
    files: [
      { id: "h", path: "pages/main.html", kind: "html", content: "<main>" },
      { id: "deep", path: "a/b/c/d/e/f/g/h/i/j/k/deep.html", kind: "html", content: "deep" },
      { id: "c", path: "styles/site.css", kind: "css", content: "body{}" },
      { id: "j", path: "scripts/app.js", kind: "js", content: "run();" }
    ],
    dataSources: [{ id: "d", name: "metrics", json: "{\"ok\":true}" }],
    plans: [{ id: "p", title: "Plan", kind: "plan_document", meta: "plan", text: "steps" }],
    artifacts: [{ id: "a", title: "Chart", kind: "chart", meta: "chart", text: "chart" }],
    selected: { type: "file", id: "h" },
    onSelect(type, id) { selected.push([type, id]); return true; }
  });
  assert.equal(count, 9);
  assert.equal(captured.selectedKey, "item::file::h");
  assert.equal(captured.nodes[0].groupKey, "artifacts:html");
  const styles = captured.nodes[0].children.find(node => node.groupKey === "html-styles");
  assert.equal(styles.expanded, false);
  function sourceDepth(nodes, depth = 1) { return nodes.reduce((max, node) => Math.max(max, node.children ? sourceDepth(node.children, depth + 1) : depth), depth); }
  function sourceNode(nodes, key) { for (const node of nodes) { if (node.key === key) return node; const nested = node.children && sourceNode(node.children, key); if (nested) return nested; } return null; }
  assert.equal(sourceDepth(captured.nodes), 12);
  assert.match(sourceNode(captured.nodes, "item::file::deep").tooltip, /a\/b\/c\/d\/e\/f\/g\/h\/i\/j\/k\/deep\.html/);
  assert.equal(captured.nodes.some(node => node.groupKey === "artifact-plans"), true);
  captured.onActivate({ type: "file", id: "h" });
  assert.deepEqual(selected.at(-1), ["file", "h"]);
  captured.onToggle("html-styles", true);
  assert.equal(context.state.collapsedResourceGroups["html-styles"], false);
  console.log("PASS tree consumer: domain grouping, search state, stable selection and collapse ownership are preserved");

  const index = fs.readFileSync(path.join(__dirname, "../../web/index.html"), "utf8");
  assert.ok(index.indexOf("wunderbaum.umd.min.js") < index.indexOf("app-tree-adapter.js"));
  assert.ok(index.indexOf("app-tree-adapter.js") < index.indexOf("app-html-workspace-tree.js"));
  assert.equal(/fetch\s*\(|XMLHttpRequest|WebSocket|EventSource|localStorage/.test(adapterSource), false);
  assert.equal(/createResourceGroup|createResourceListItem|innerHTML/.test(fs.readFileSync(path.join(__dirname, "../../web/js/app-html-workspace-tree.js"), "utf8")), false);
  for (const file of fs.readdirSync(path.join(__dirname, "../../web/js")).filter(name => /^app.*\.js$/.test(name) && name !== "app-tree-adapter.js")) {
    assert.equal(/mar10\.Wunderbaum|new\s+Wunderbaum\s*\(/.test(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8")), false, file + " bypasses TreeAdapter");
  }
  console.log("PASS tree integration: local vendor loads before adapter/consumer and legacy renderer/network APIs are absent");
  console.log("OK 4/4");
})().catch(error => { console.error(error); process.exitCode = 1; });
