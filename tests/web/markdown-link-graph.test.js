'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

class ClassList {
  constructor() { this.values = new Set(); }
  add(name) { this.values.add(name); }
  remove(name) { this.values.delete(name); }
  toggle(name, value) { if (value === undefined ? !this.values.has(name) : value) this.values.add(name); else this.values.delete(name); }
  contains(name) { return this.values.has(name); }
}
class Element {
  constructor(tag) { this.tagName = tag; this.children = []; this.attributes = {}; this.classList = new ClassList(); this.listeners = {}; this.textContent = ''; this.type = ''; this.disabled = false; }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  appendChild(child) { this.children.push(child); return child; }
  append(...children) { children.forEach(x => this.appendChild(x)); }
  replaceChildren(...children) { this.children = []; this.append(...children); }
  addEventListener(name, callback) { (this.listeners[name] ||= []).push(callback); }
  click() { (this.listeners.click || []).forEach(fn => fn({ preventDefault() {} })); }
  findAll(pred) { return [this, ...this.children.flatMap(x => x.findAll(pred))].filter(pred); }
}

let constructed = [], destroyed = 0;
class Network {
  constructor(element, data, options) { this.element = element; this.data = data; this.options = options; this.listeners = {}; constructed.push(this); }
  on(name, callback) { this.listeners[name] = callback; }
  once(name, callback) { this.listeners[name] = callback; }
  fit() { this.fitted = true; }
  selectNodes() {}
  setOptions(next) { this.changedOptions = next; }
  destroy() { destroyed++; }
}
const context = vm.createContext({
  document: { createElement: name => new Element(name), documentElement: new Element('html') },
  getComputedStyle() { return { getPropertyValue() { return ''; } }; },
  Promise, Object, Set, String, Number, Array, Error, AbortController, console
});
context.window = context;
context.vis = { Network };
context.RNAssistantIcons = { render: name => "<svg data-icon='" + name + "'></svg>" };
const js = fs.readFileSync(path.resolve(__dirname, '../../web/js/app-markdown-graph.js'), 'utf8');
vm.runInContext(js, context, { filename: 'app-markdown-graph.js' });
const ui = context.RNAssistantMarkdownGraph;
const snapshot = { scope: 'document', focus: 'doc:a', scanned: 2, complete: true, notice: '',
  nodes: [
    { id: 'doc:a', title: 'Parent.md', kind: 'document', state: 'resolved' },
    { id: 'doc:b', title: 'Child.md', kind: 'document', state: 'resolved' },
    { id: 'ref:missing', title: 'Gone.md', kind: 'reference', state: 'missing' }
  ],
  edges: [
    { from: 'doc:a', to: 'doc:b', kind: 'reference', status: 'resolved', destination: 'Child.md', label: 'Child', line: 7 },
    { from: 'doc:a', to: 'doc:b', kind: 'reference', status: 'resolved', destination: 'Child.md', label: 'Child', line: 9 },
    { from: 'doc:b', to: 'doc:a', kind: 'reference', status: 'resolved', destination: 'Parent.md', label: 'Parent', line: 2 },
    { from: 'doc:a', to: 'ref:missing', kind: 'reference', status: 'missing', destination: 'Gone.md', label: 'Gone', line: 12 }
  ]
};
assert.equal(ui.validate(snapshot), snapshot);
assert.throws(() => ui.validate({ ...snapshot, edges: [{ from: 'doc:a', to: 'absent', destination: 'h' }] }), /неизвестной/);
assert.throws(() => ui.validate({ ...snapshot, complete: 'yes' }), /некорректный/);
const analysis = { links: [{ destination: 'Other.md', text: 'Other', line: 4, start: 2 }, { destination: 'Image.png', isImage: true }] };
assert.equal(ui.localSnapshot(analysis, 'a', 'A').edges.length, 1);
const host = new Element('root'), opened = [], revealed = [];
let requested = 0;
const callbacks = { focus: 'doc:a', title: 'Parent.md', presentation: {},
  onGraphData() { requested++; return Promise.resolve(snapshot); },
  onOpen(item, line) { opened.push([item.id, line]); }, onReveal(line) { revealed.push(line); } };
async function flush() { await new Promise(resolve => setImmediate(resolve)); await new Promise(resolve => setImmediate(resolve)); }
(async () => {
  const graph = ui.mount(host, callbacks);
  await flush();
  assert.equal(requested, 1, 'one graph query on mount');
  assert.equal(constructed.length, 1, 'vis.Network initialized lazily');
  assert.equal(constructed[0].data.nodes.length, 3);
  assert.equal(constructed[0].data.edges.length, 3, 'duplicate evidence represented by one edge');
  assert.equal(constructed[0].data.edges.find(e => e.from === 'doc:a' && e.to === 'doc:b').label, '2');
  assert.equal(constructed[0].data.edges.find(e => e.to === 'ref:missing').dashes, true);
  const choose = constructed[0].listeners.click;
  choose({ nodes: ['doc:b'] });
  assert.equal(host.findAll(x => x.textContent === 'Обратные ссылки · 2').length, 1,
    'backlinks are calculated from directed evidence');
  const hierarchy = host.findAll(x => x.attributes['aria-label'] === 'Иерархия')[0]; hierarchy.click();
  assert.equal(constructed.length, 2);
  assert.equal(destroyed, 1, 'changing layout disposes previous network');
  assert.equal(constructed[1].options.physics.enabled, false);
  const refresh = host.findAll(x => x.attributes['aria-label'] === 'Обновить')[0]; refresh.click();
  await flush();
  assert.equal(requested, 2);
  graph.destroy();
  assert.equal(destroyed, 3, 'closing viewer releases canvas and physics');
  assert.equal(host.children.length, 0);
  console.log('PASS Markdown graph: validation, backlinks, dedup, status, vis lifetime, layout, refresh');
})().catch(err => { console.error(err); process.exitCode = 1; });
