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
const diagnostics = new Element('div');
context.window.RNAssistantRunChanges.renderInline(diagnostics, { complete: true, diagnostic: { code: 'invalid_edit', message: 'Invalid old', nextAction: 'fix_patch' }, items: [
  { title: 'broken.js', targetStatus: 'Rejected', availability: 'rejected', diagnostic: { edit: 2, code: 'ambiguous_context', message: '<img src=x onerror=alert(1)>', nextAction: 'refine_context', candidates: Array.from({length: 9}, (_, i) => ({line: i + 1, context: '<script>' + 'x'.repeat(500)})) } },
  { title: 'unknown.js', targetStatus: 'Unknown', availability: 'unverified', diagnostic: { nextAction: 'verify_effect', message: 'Read-back unconfirmed' } }
] });
const texts = node => [node.textContent || '', ...(node.children || []).flatMap(texts)];
const diagnosticText = texts(diagnostics).join('\n');
assert.match(diagnosticText, /Правка №2/);
assert.match(diagnosticText, /Уточнить якоря/);
assert.match(diagnosticText, /Проверить фактический результат/);
assert.match(diagnosticText, /Исправить правку/);
assert.equal(texts(diagnostics).filter(t => t.startsWith('Возможное место')).length, 5);
assert(!diagnosticText.includes('x'.repeat(200)));
assert(diagnosticText.includes('<img src=x onerror=alert(1)>'), 'diagnostic is inert text');
const groups = context.window.RNAssistantLibraryGroups;
const tree = groups.tree(['html.data.read', 'html.assets.import', 'core.write', 'excel.range.write'].map(Id => ({ value: { Id } })), 'tools', row => ({ kind: 'leaf', title: row.value.Id }), () => true);
assert.equal(tree[0].title, 'Core');
const html = tree.find(group => group.title === 'HTML');
assert.equal(html.children.map(row => row.title).join(','), 'html.assets.import,html.data.read');
assert(tree.every(group => group.children.every(row => row.kind === 'leaf')), 'only one namespace grouping level');
assert.equal(groups.path('skills.excel.reporting', 'skills').join('.'), 'excel');
assert.equal(groups.path('skills.vba.code.editing', 'skills').join('.'), 'vba');
console.log('PASS universal tools: bounded inert inline diff, unknown effect and first-namespace groups');
