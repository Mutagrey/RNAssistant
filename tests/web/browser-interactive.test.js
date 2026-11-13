'use strict';
// Chromium checks the shipped assembly, binding client and isolated semantic adapter.
// Native WebView2 routing / physical takeover remain a separate Windows gate.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');
(async () => {
  let browser, temporary, html = '', hits = 0;
  const batch = { resource: { uri: 'rna://test/data', revision: 'r1' }, view: 'table', offset: 0, nextOffset: 1, done: true,
    rows: [{ amount: 42 }], columns: [{ key: 'amount', type: 'number' }], coverage: { kind: 'record-range', start: 0, end: 1, fields: ['amount'] } };
  const server = http.createServer((request, response) => {
    if (request.url.startsWith('/data')) { hits++; response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify(batch)); }
    else { response.setHeader('Content-Type', 'text/html; charset=utf-8'); response.end(html); }
  });
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const origin = 'http://127.0.0.1:' + server.address().port;
    browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const context = await browser.newContext(); const page = await context.newPage(); page.setDefaultTimeout(6000); await page.goto(origin);
    for (const file of ['app-html-vendor-catalog.js', 'app-html-vendor-runtime.js', 'app-html-resource-export.js', 'app-html-workspace-preview.js'])
      await page.addScriptTag({ path: 'web/js/' + file });
    const built = await page.evaluate(async ({ origin, batch }) => {
      const files = [{ id: 'main', path: 'index.html', kind: 'html', content: '<!doctype html><html><head><title>Локально</title></head><body><label>Имя<input id="name"></label><button id="button">Загрузить</button><p id="result">Ожидание</p><label>Пароль<input type="password"></label></body></html>' },
        { id: 'js', path: 'main.js', kind: 'js', content: `document.getElementById('button').onclick=async function(event){window.trusted=event.isTrusted;var data=await RN.resources.open('totals');var batch=await data.read({limit:10});document.getElementById('result').textContent=document.getElementById('name').value+': '+batch.rows[0].amount;await data.close();};RN.resources.subscribe(function(change){window.changed=change.names;});` }];
      const dataSources = [{ name: 'totals' }];
      const source = { generations: { 'document:test': 1 }, bindings: [{ name: 'totals', lease: { leaseId: 'test', url: 'https://rnassistant.local-resource/v1/' + 'a'.repeat(64), descriptor: { reference: batch.resource }, view: 'table', maxBatchItems: 100, maxBatchBytes: 32000 } }] };
      const snapshot = await RNAssistantHtmlResourceExport.capture(source, { fetch: async () => new Response(JSON.stringify(batch)), isCurrent: () => true });
      return { native: RNAssistantHtmlWorkspacePreview.build({ files, activeFileId: 'main', dataSources, hostBridge: true, nativeHost: true }),
        offline: RNAssistantHtmlWorkspacePreview.build({ files, activeFileId: 'main', dataSources, hostBridge: false, resourceSnapshot: snapshot }) };
    }, { origin, batch });
    html = built.native;
    await context.addInitScript(({ origin, batch }) => {
      const callbacks = []; window.hostCalls = [];
      window.chrome = { webview: { addEventListener(type, listener) { if (type === 'message') callbacks.push(listener); },
        postMessage(json) { const request = JSON.parse(json); hostCalls.push(request);
          const value = request.operation === 'open' ? { leaseId: 'test', url: origin + '/data', descriptor: { reference: batch.resource }, view: 'table', maxBatchItems: 100, maxBatchBytes: 32000 } : { closed: true };
          queueMicrotask(() => callbacks.forEach(listener => listener({ data: { type: 'rnassistant-resource-result', requestId: request.requestId, ok: true, value } }))); }
      } };
      window.notifyBinding = () => callbacks.forEach(listener => listener({ data: { type: 'rnassistant-resource-changed', names: ['totals'], generation: 2 } }));
    }, { origin, batch });
    await page.goto(origin);
    const cdp = await context.newCDPSession(page);
    await cdp.send('Page.enable');
    const tree = await cdp.send('Page.getFrameTree');
    const world = await cdp.send('Page.createIsolatedWorld', { frameId: tree.frameTree.frame.id, worldName: 'RNAssistantLocal' });
    async function evaluate(expression) {
      const result = await cdp.send('Runtime.evaluate', { expression, contextId: world.executionContextId, returnByValue: true, awaitPromise: true });
      if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
      return result.result.value;
    }
    await evaluate(await fs.readFile('src/RNAssistant.Office/WebView/LocalBrowserAutomation.js', 'utf8'));
    async function prepare(id, request) {
      await evaluate(`__rnaLocalBrowser.read(${JSON.stringify(id)})`);
      return evaluate(`__rnaLocalBrowser.prepare(${JSON.stringify(id)},${JSON.stringify(id)},${JSON.stringify(request)})`);
    }
    async function click(point) {
      await cdp.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: point.x, y: point.y, button: 'left', clickCount: 1 });
      await cdp.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: point.x, y: point.y, button: 'left', clickCount: 1 });
    }
    const input = await prepare('fill', { action: 'fill', name: 'Имя', value: 'Локально' });
    await evaluate('__rnaLocalBrowser.admit("fill")'); await click(input);
    await cdp.send('Input.insertText', { text: 'Локально' });
    assert.equal((await evaluate('__rnaLocalBrowser.verify("fill")')).effect, 'VerifiedChange');
    const button = await prepare('click', { action: 'click', name: 'Загрузить', expectedText: 'Локально: 42' });
    await evaluate('__rnaLocalBrowser.admit("click")'); await click(button);
    await page.waitForFunction(() => document.getElementById('result').textContent === 'Локально: 42');
    assert.equal((await evaluate('__rnaLocalBrowser.verify("click")')).effect, 'VerifiedChange');
    assert.equal(await page.evaluate(() => trusted), true);
    assert.equal(hits, 1); assert.deepEqual(await page.evaluate(() => hostCalls.map(call => call.operation)), ['open', 'close']);
    await page.evaluate(() => notifyBinding()); assert.deepEqual(await page.evaluate(() => changed), ['totals']);
    await assert.rejects(prepare('private', { action: 'fill', name: 'Пароль', value: 'secret' }), /browser_target_ambiguous/);
    await prepare('stale', { action: 'click', name: 'Загрузить' });
    await page.evaluate(() => document.getElementById('button').textContent = 'Changed');
    await assert.rejects(evaluate('__rnaLocalBrowser.admit("stale")'), /browser_layout_changed/);
    assert.equal((await evaluate('__rnaLocalBrowser.privacy()')).safe, false);
    temporary = await fs.mkdtemp(path.join(os.tmpdir(), 'rna-offline-html-'));
    const target = path.join(temporary, 'project.html'); await fs.writeFile(target, built.offline);
    const offline = await browser.newPage(); const network = [];
    offline.on('request', request => { if (/^https?:/.test(request.url())) network.push(request.url()); });
    await offline.goto(pathToFileURL(target).href);
    await offline.getByLabel('Имя', { exact: true }).fill('Экспорт'); await offline.getByRole('button', { name: 'Загрузить' }).click();
    await offline.waitForFunction(() => document.getElementById('result').textContent === 'Экспорт: 42');
    assert.equal(await offline.evaluate(() => trusted), true); assert.deepEqual(network, []);
    assert.equal(hits, 1, 'file:// binding reads use the captured snapshot, never the host');
    console.log('PASS interactive Browser: isolated native input, binding open/read/close/subscribe, stale/private refusal, real file:// snapshot without HTTP');
  } finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); if (temporary) await fs.rm(temporary, { recursive: true, force: true }); }
})().catch(error => { console.error(error); process.exitCode = 1; });
