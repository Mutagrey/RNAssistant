'use strict';
const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path');
const { chromium } = require('playwright');
const script = fs.readFileSync(path.join(__dirname, '../../src/RNAssistant.Office/WebView/ExternalBrowserSemanticRead.js'), 'utf8');
const probe = fs.readFileSync(path.join(__dirname, '../../web/browser-probe/index.html'), 'utf8');
const url = 'https://rna-browser-probe.invalid/index.html';
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    await page.route('**/*', route => route.fulfill({ contentType: 'text/html', body: probe }));
    const cdp = await page.context().newCDPSession(page);
    let contextId, observed, serial = 0;
    async function evaluate(expression) {
      const result = await cdp.send('Runtime.evaluate', { expression, contextId, returnByValue: true, silent: true, timeout: 1000 });
      assert.ok(!result.exceptionDetails, JSON.stringify(result.exceptionDetails)); return result.result.value;
    }
    async function read() { observed = await evaluate(script); return observed; }
    async function reset(address = url) {
      await page.goto(address);
      const { frameTree } = await cdp.send('Page.getFrameTree');
      ({ executionContextId: contextId } = await cdp.send('Page.createIsolatedWorld', { frameId: frameTree.frame.id, worldName: 'rna-semantic-read' }));
      await read();
    }
    const fill = { action: 'fill', name: 'Ввод и Tab', value: 'PRIVATE_ACTION_VALUE' };
    const click = { action: 'click', name: 'Нажатия: 0', expectedText: 'Нажатия: 1' };
    async function prepare(request) {
      const id = 'prepared-' + (++serial);
      const result = await evaluate(`globalThis.__rnaBrowser.prepare(${JSON.stringify(observed.observationId)},${JSON.stringify(id)},${JSON.stringify(request)})`);
      return { id, ...result };
    }
    async function act(plan) { return evaluate(`globalThis.__rnaBrowser.act(${JSON.stringify(plan.id)})`); }
    async function refused(code, request = fill) { assert.equal((await prepare(request)).code, code); }

    await reset();
    // Page script cannot replace DOM methods in the isolated execution world.
    await page.evaluate(() => { document.createTreeWalker = () => { throw new Error('hostile override'); }; });
    let plan = await prepare(fill);
    assert.equal(plan.code, 'browser_prepared');
    assert.equal((await act(plan)).effect, 'VerifiedChange');
    assert.equal(await page.locator('input').inputValue(), fill.value);
    assert.equal((await act(plan)).effect, 'Rejected', 'prepared operation is single-use');
    await read();
    assert.ok(!JSON.stringify(observed).includes(fill.value), 'read-back never exposes field values');
    assert.equal((await act(await prepare(fill))).effect, 'VerifiedNoChange');
    await read();
    assert.equal((await act(await prepare(click))).effect, 'VerifiedChange');
    assert.ok((await read()).nodes.some(node => node.name === 'Нажатия: 1'), 'real click effect is readable');

    await reset();
    await page.locator('input').fill('user edit');
    await refused('browser_observation_stale');
    await read(); plan = await prepare(fill);
    await page.locator('input').fill('after preparation');
    assert.equal((await act(plan)).code, 'browser_observation_stale', 'trusted interaction invalidates pending confirmation');
    assert.equal(await page.locator('input').inputValue(), 'after preparation');

    await reset(); plan = await prepare(fill);
    await page.evaluate(() => { document.querySelector('input').value = 'programmatic edit'; });
    assert.equal((await act(plan)).code, 'browser_element_stale');
    await reset();
    await page.evaluate(() => { const input = document.querySelector('input'); input.replaceWith(input.cloneNode(true)); });
    await refused('browser_element_stale');
    await reset();
    await page.evaluate(() => { const label = document.querySelector('label'); label.after(label.cloneNode(true)); });
    await refused('browser_element_ambiguous');
    await reset();
    await page.evaluate(() => { document.querySelector('input').disabled = true; });
    await refused('browser_element_not_interactable');
    await reset();
    await page.evaluate(() => { document.body.insertAdjacentHTML('beforeend', '<div style="position:fixed;inset:0;z-index:9999"></div>'); });
    await refused('browser_element_not_interactable');
    await reset();
    await refused('browser_verification_already_present', { ...click, expectedText: 'Нажатия: 0' });
    plan = await prepare(fill); await read();
    assert.equal((await act(plan)).code, 'browser_preparation_stale', 'a new read invalidates an old prepared binding');
    await reset();
    await page.evaluate(() => document.body.appendChild(document.createTextNode('unrelated content')));
    assert.equal((await act(await prepare(fill))).effect, 'VerifiedChange', 'unrelated DOM changes do not invalidate exact elements');

    await reset();
    assert.equal((await act(await prepare({ ...click, expectedText: 'not produced' }))).effect, 'Unknown');
    assert.equal(await page.locator('#count').textContent(), 'Нажатия: 1', 'dispatch succeeded but expected effect is unproven');
    await read(); await refused('browser_effect_unknown', { ...click, name: 'Нажатия: 1', expectedText: 'Нажатия: 2' });
    await reset();
    await page.evaluate(() => document.querySelector('input').addEventListener('change', event => { event.target.value = ''; }));
    assert.equal((await act(await prepare(fill))).effect, 'Unknown', 'fill event handlers cannot create false verified success');
    await read(); await refused('browser_effect_unknown');
    await reset('https://example.test/'); await refused('browser_probe_only');
    console.log('PASS external Browser actions: local fill/click/read-back, exact element and user guards, single-use preparation, unknown without replay');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
