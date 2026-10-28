'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
class Element {
  constructor(tag) { this.tagName = tag; this.children = []; this.handlers = {}; this.attributes = {}; this.textContent = ''; }
  appendChild(value) { this.children.push(value); return value; }
  addEventListener(name, fn) { this.handlers[name] = fn; }
  setAttribute(name, value) { this.attributes[name] = value; }
  set innerHTML(value) { throw new Error('Source text must never be interpreted as HTML'); }
}
const document = { createElement: tag => new Element(tag), createTextNode: text => ({ textContent: text }) };
const context = vm.createContext({ window: {}, document, Map, setTimeout });
for (const file of ['app-text-diff.js', 'app-run-changes.js', 'app-library-groups.js'])
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../../web/js', file), 'utf8'), context);
const diff = context.window.RNAssistantTextDiff;
const target = new Element('div');
const before = Array.from({ length: 65 }, (_, i) => 'before' + i).join('\n');
const after = Array.from({ length: 65 }, (_, i) => '<script>' + i).join('\n');
diff.renderInline(target, diff.format(before, after));
const body = target.children.find(node => node.className === 'inline-text-diff');
const more = target.children.find(node => node.tagName === 'button');
assert.equal(body.children.length, 40);
more.handlers.click();
assert.equal(body.children.length, 80);
assert(body.children.some(row => row.children[1].textContent.startsWith('<script>')));
assert.equal(body.children[0].children.length, 2, 'inline diff has only marker and code');
const root = new Element('div');
context.window.RNAssistantRunChanges.renderInline(root, { complete: true, items: [{ title: 'a.js', availability: 'unverified', before: 'old', intendedAfter: 'new' }] });
assert(root.children[0].children.some(node => node.textContent.includes('результат не подтверждён')));
assert(!root.children.some(node => node.tagName === 'details'), 'no nested full diff component in a step');
const groups = context.window.RNAssistantLibraryGroups;
const tree = groups.tree([{ value: { Id: 'html.data.read' } }, { value: { Id: 'core.write' } }], 'tools', row => ({ kind: 'leaf', title: row.value.Id }), () => true);
assert.equal(tree[0].title, 'Core');
assert.equal(tree[1].children[0].title, 'data');
assert.equal(tree[1].children[0].children[0].title, 'html.data.read');
assert.equal(groups.path('skills.excel.reporting', 'skills').join('.'), 'excel');
console.log('PASS universal tools: bounded inert inline diff, unknown effect and dotted namespace tree');
