'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.resolve(__dirname, '../..');
const result = {textContent: ''};
const active = {classList: {contains: name => name === 'active'}};
let longTaskCallback;
const context = vm.createContext({
  window: {},
  $: id => id === 'performanceDiagnosticsResults' ? result : active,
  document: {
    getElementById: id => id === 'performanceDiagnosticsResults' ? result : active,
    querySelector: () => active
  },
  PerformanceObserver: class {
    constructor(callback) { longTaskCallback = callback; }
    observe(options) { assert.deepEqual(Array.from(options.entryTypes), ['longtask']); }
  }
});
vm.runInContext(fs.readFileSync(path.join(root, 'web/js/app-diagnostics.js'), 'utf8'), context);
const diagnostics = context.window.RNAssistantPerformanceDiagnostics;
assert.ok(diagnostics);

const navigation = diagnostics.beginNavigation('selectChat');
assert.match(result.textContent, /в очереди/);
diagnostics.updateNavigation(navigation, {phase: 'waiting', queueMs: 320});
assert.match(result.textContent, /ожидание bridge/);
diagnostics.updateNavigation(navigation, {phase: 'completed', queueMs: 320, bridgeMs: 1250,
  renderMs: 80, messages: 75});
assert.match(result.textContent, /очередь 320 мс, bridge 1\.3 с, рендер 80 мс, сообщений 75/);

diagnostics.recordBridge({operation: 'GetProjectChat', handleMs: 980, serializeMs: 270,
  responseChars: 102400, stages: [{name: 'load', elapsedMs: 600},
    {name: 'state', elapsedMs: 110}, {name: 'messages', elapsedMs: 40}]});
diagnostics.recordHostPost({elapsedMs: 430, responseChars: 102400});
diagnostics.recordModel({requestId: 'request-1', phase: 'sending', elapsedMs: 200,
  preparationMs: 200, requestBytes: 1024});
assert.match(result.textContent, /Модель · request-1: ожидание заголовков/);
diagnostics.recordModel({requestId: 'request-1', phase: 'completed', elapsedMs: 4200,
  totalMs: 4200, preparationMs: 200, responseHeadersMs: 3000, firstChunkMs: 3400,
  statusCode: 200});
assert.equal(result.textContent.split('Модель · request-1').length, 2,
  'phase updates replace one request row');
assert.match(result.textContent, /заголовки 2\.8 с, первые данные 400 мс/);
assert.match(result.textContent, /Bridge · GetProjectChat: работа 980 мс, JSON 270 мс/);
assert.match(result.textContent, /Этапы: хранилище 600 мс, состояние 110 мс, сообщения 40 мс, прочее 230 мс/);
assert.match(result.textContent, /Office UI → WebView: 430 мс/);

diagnostics.observeLongTasks();
longTaskCallback({getEntries: () => [{duration: 150}, {duration: 55}]});
assert.match(result.textContent, /Интерфейс · долгая задача: 150 мс/);
assert.doesNotMatch(result.textContent, /55 мс/);
console.log('PASS performance diagnostics: live model phase, navigation split, bridge, host post and long task');
