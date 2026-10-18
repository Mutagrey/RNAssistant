"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const read = file => fs.readFileSync(path.join(__dirname, "../../web", file), "utf8");
const root = {
  id: "materials-tree", className: "html-workspace-tree", attributes: { "aria-label": "Материалы" },
  getAttribute(name) { return this.attributes[name] || null; },
  removeAttribute(name) { delete this.attributes[name]; },
  replaceChildren() { this.cleared = true; },
  appendChild(child) { this.child = child; }
};
let rendered = null, released = false;
const context = vm.createContext({ Promise, AbortController,
  document: { getElementById: id => id === root.id ? root : null, createElement: tag => ({ tagName: tag, setAttribute() {}, textContent: "" }) },
  RNAssistantNavigationTree: {
    icon: name => `<svg data-icon="${name}"></svg>`,
    render: (target, nodes, options) => { rendered = { target, nodes, options }; },
    unmount: () => { released = true; }
  }
});
context.window = context;
vm.runInContext(read("js/app-tree-adapter.js"), context, { filename: "app-tree-adapter.js" });
const adapter = context.RNAssistantTreeAdapter;

const malicious = '<img src=x onerror="alert(1)">';
const normalized = adapter.normalize([{ key: "g", title: malicious, groupKey: "g", expanded: true, children: [
  { key: "item::file::1", title: "main.html", itemType: "file", itemId: "1", deletable: true }
]}], { limits: { maxNodes: 999999, maxDepth: 999999 } });
assert.equal(normalized.count, 2);
assert.equal(normalized.nodes[0].title, malicious);
assert.equal(normalized.limits.maxNodes, 2500);
assert.equal(normalized.limits.maxDepth, 16);
assert.throws(() => adapter.normalize("/tree.json"), /local array/);
assert.throws(() => adapter.normalize([{ key: "a", children: [{ key: "a" }] }]), /Duplicate tree key/);
assert.throws(() => adapter.normalize([{ key: "a", children: "/lazy" }]), /children must be a local array/);
assert.throws(() => adapter.normalize([{ key: "a", itemType: "url", itemId: "1" }]), /Unsupported tree item type/);
console.log("PASS tree adapter: bounded local typed nodes are fail-closed");

const activated = [], deleted = [], toggled = [];
const controller = adapter.mount(root, {
  nodes: [{ key: "group::files", groupKey: "files", title: "Файлы", expanded: true, iconKind: "folder", children: [
    { key: "item::file::1", itemType: "file", itemId: "1", title: malicious, meta: "HTML", iconKind: "html", deletable: true },
    { key: "item::file::2", itemType: "file", itemId: "2", title: "next.html", iconKind: "html" }
  ] }],
  selectedKey: "item::file::1",
  onActivate(item) { activated.push(item); return item.id !== "2"; },
  onDelete(item) { deleted.push(item); },
  onToggle(key, expanded) { toggled.push([key, expanded]); }
});
assert.equal(controller.vendor, "navigation-tree");
assert.equal(rendered.target, root);
assert.equal(rendered.nodes[0].kind, "group");
assert.equal(rendered.nodes[0].children[0].selected, true);
assert.equal(rendered.nodes[0].children[0].title, malicious);
assert.match(rendered.nodes[0].children[0].tooltip, /HTML/);
assert.match(rendered.nodes[0].children[0].iconHtml, /html/);
rendered.options.onSelect(rendered.nodes[0].children[1]);
assert.equal(activated.at(-1).id, "2");
rendered.nodes[0].children[0].actions[0].onClick();
assert.equal(deleted.at(-1).id, "1");
rendered.options.onToggle(rendered.nodes[0], false);
assert.deepEqual(toggled.at(-1), ["files", false]);
controller.destroy();
assert.equal(released, true);
assert.equal(root.className, "html-workspace-tree");
console.log("PASS tree adapter: selection, delete, collapse and lifecycle use the shared renderer");

context.state = { collapsedResourceGroups: { "html-styles": true } };
let captured = null;
context.RNAssistantTreeAdapter = { mount(target, options) { captured = options; return { count: 9 }; } };
vm.runInContext(read("js/app-html-workspace-tree.js"), context, { filename: "app-html-workspace-tree.js" });
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
  dependencies: [{ id: "runtime/echarts.min.js", path: "runtime/echarts.min.js", title: "echarts.min.js",
    kind: "script", version: "5.6.0", loaded: true, readOnly: true, description: "Bundled runtime" }],
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
const dependencies = captured.nodes[0].children.find(node => node.groupKey === "html-dependencies");
assert.equal(dependencies.children[0].title, "echarts.min.js");
assert.match(dependencies.children[0].meta, /загружено · только чтение/);
assert.equal(dependencies.children[0].itemType, undefined);
function sourceDepth(nodes, depth = 1) { return nodes.reduce((max, node) => Math.max(max, node.children ? sourceDepth(node.children, depth + 1) : depth), depth); }
function sourceNode(nodes, key) { for (const node of nodes) { if (node.key === key) return node; const nested = node.children && sourceNode(node.children, key); if (nested) return nested; } return null; }
assert.equal(sourceDepth(captured.nodes), 12);
assert.match(sourceNode(captured.nodes, "item::file::deep").tooltip, /a\/b\/c\/d\/e\/f\/g\/h\/i\/j\/k\/deep\.html/);
assert.equal(captured.nodes.some(node => node.groupKey === "artifact-plans"), true);
const generated = captured.nodes.find(node => node.groupKey === "artifact-generated");
assert.equal(generated.itemType, "collection");
captured.onActivate({ type: "file", id: "h" });
assert.deepEqual(selected.at(-1), ["file", "h"]);
captured.onToggle("html-styles", true);
assert.equal(context.state.collapsedResourceGroups["html-styles"], false);
console.log("PASS tree consumer: domain grouping, stable selection and collapse ownership remain intact");

const index = read("index.html");
assert.ok(index.indexOf("app-navigation-tree.js") < index.indexOf("app-tree-adapter.js"));
assert.ok(index.indexOf("app-tree-adapter.js") < index.indexOf("app-html-workspace-tree.js"));
assert.doesNotMatch(index, /wunderbaum|app-resource-list\.js/);
assert.equal(/fetch\s*\(|XMLHttpRequest|WebSocket|EventSource|localStorage/.test(read("js/app-tree-adapter.js")), false);
console.log("PASS tree integration: one renderer, no network or retired vendor path");
