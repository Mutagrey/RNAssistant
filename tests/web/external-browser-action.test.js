'use strict';
const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path');
const { chromium } = require('playwright');
const script = fs.readFileSync(path.join(__dirname, '../../src/RNAssistant.Office/WebView/ExternalBrowserSemanticRead.js'), 'utf8');
const probe = fs.readFileSync(path.join(__dirname, '../../web/browser-probe/index.html'), 'utf8');
const url = 'https://rna-browser-probe.invalid/index.html';
const visual = fs.readFileSync(path.join(__dirname, '../../web/js/app-browser-action-view.js'), 'utf8');
const styles = fs.readFileSync(path.join(__dirname, '../../web/css/app-product-tokens.css'), 'utf8').replace(/:root/g, ':host') +
  fs.readFileSync(path.join(__dirname, '../../web/css/app-browser-action-view.css'), 'utf8');
const review = process.env.BROWSER_ACTION_REVIEW;
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
      await evaluate(visual + ';globalThis.__rnaBrowserVisual=RNAssistantBrowserActionView.create(' + JSON.stringify(styles) + ');true');
    }
    const fill = { action: 'fill', name: 'Ввод и Tab', value: 'Пример ввода' };
    const click = { action: 'click', name: 'Нажатия: 0', expectedText: 'Нажатия: 1' };
    async function prepare(request) {
      const id = 'prepared-' + (++serial);
      const result = await evaluate(`globalThis.__rnaBrowser.prepare(${JSON.stringify(observed.observationId)},${JSON.stringify(id)},${JSON.stringify(request)})`);
      return { id, ...result };
    }
    async function act(plan) { return evaluate(`globalThis.__rnaBrowser.act(${JSON.stringify(plan.id)})`); }
    async function show(plan, visible = true) { return evaluate(`globalThis.__rnaBrowser.showAction(${JSON.stringify(plan.id)},${visible})`); }
    async function finish(plan, effect) { return evaluate(`globalThis.__rnaBrowser.completeAction(${JSON.stringify(plan.id)},${JSON.stringify(effect)})`); }
    async function inspect(className) {
      const { root } = await cdp.send('DOM.getDocument', { depth: -1, pierce: true });
      function find(node) {
        const attributes = node.attributes || [];
        for (let index = 0; index < attributes.length; index += 2)
          if (attributes[index] === 'class' && attributes[index + 1].split(' ').includes(className)) return node;
        for (const child of [...(node.children || []), ...(node.shadowRoots || [])]) { const found = find(child); if (found) return found; }
      }
      const node = find(root); if (!node) return null;
      const { object } = await cdp.send('DOM.resolveNode', { nodeId: node.nodeId });
      const result = await cdp.send('Runtime.callFunctionOn', { objectId: object.objectId, returnByValue: true,
        functionDeclaration: 'function(){var s=getComputedStyle(this),r=this.getBoundingClientRect();return {text:this.textContent,transform:s.transform,transition:s.transitionDuration,opacity:s.opacity,pointer:s.pointerEvents,color:s.color,left:r.left,right:r.right,top:r.top,bottom:r.bottom}}' });
      await cdp.send('Runtime.releaseObject', { objectId: object.objectId }); return result.result.value;
    }
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
    await reset(); plan = await prepare(fill);
    const initialNodes = observed.nodes, focusBefore = await page.evaluate(() => document.activeElement.tagName);
    if (review) { fs.mkdirSync(path.dirname(review), { recursive: true }); await page.screenshot({ path: review + '-baseline.png' }); }
    assert.equal(await show(plan), 220);
    const first = await inspect('action-pointer'); await page.waitForTimeout(100);
    const moving = await inspect('action-pointer'); await page.waitForTimeout(160);
    const arrived = await inspect('action-pointer');
    assert.notEqual(first.transform, moving.transform, 'cursor moves instead of jumping');
    assert.notEqual(moving.transform, arrived.transform, 'short motion reaches the exact target');
    assert.equal(arrived.pointer, 'none');
    assert.equal(await page.evaluate(() => document.activeElement.tagName), focusBefore, 'decoration never steals focus');
    assert.equal((await inspect('action-caption')).text, 'Заполняю');
    const filled = await act(plan);
    assert.equal((await inspect('action-caption')).text, 'Проверяю', 'renderer read-back alone cannot paint runtime success');
    await finish(plan, filled.effect);
    assert.equal((await inspect('action-caption')).text, 'Проверено');
    assert.deepEqual((await read()).nodes, initialNodes, 'closed-shadow decoration does not enter semantic capture');
    if (review) { fs.mkdirSync(path.dirname(review), { recursive: true }); await page.screenshot({ path: review + '-wide.png' }); }
    await page.waitForTimeout(2780);
    assert.equal(await inspect('action-pointer'), null, 'idle cursor fades and is removed');

    plan = await prepare(click); await show(plan); await page.waitForTimeout(240);
    await finish({ id: 'older-operation' }, 'Unknown');
    assert.equal((await inspect('action-caption')).text, 'Нажимаю', 'late terminal cannot replace another action');
    const clicked = await act(plan); await finish(plan, clicked.effect);
    assert.equal((await inspect('action-caption')).text, 'Проверено');
    await page.mouse.move(50, 50);
    assert.equal(await inspect('action-pointer'), null, 'manual pointer movement hides the agent cursor');
    await reset(); plan = await prepare(fill); await show(plan);
    await page.locator('input').fill('manual input during animation');
    assert.equal((await act(plan)).effect, 'Rejected'); await finish(plan, 'Rejected');
    assert.equal(await inspect('action-pointer'), null, 'manual interaction prevents a late decoration from reappearing');

    await reset(); plan = await prepare({ ...click, expectedText: 'not produced' }); await show(plan);
    const uncertain = await act(plan); await finish(plan, uncertain.effect);
    assert.equal((await inspect('action-caption')).text, 'Результат неизвестен');
    await evaluate('globalThis.__rnaBrowser.hideAction()'); await finish(plan, 'VerifiedChange');
    assert.equal(await inspect('action-pointer'), null, 'hide/late result never replays a decoration');
    await reset(); plan = await prepare(fill); assert.equal(await show(plan, false), 0);
    assert.equal(await inspect('action-pointer'), null, 'hidden native view does not animate');

    await page.emulateMedia({ reducedMotion: 'reduce' });
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 336, height: 576, deviceScaleFactor: 1.25, mobile: false });
    await reset(); plan = await prepare(fill);
    assert.equal(await show(plan), 0, 'reduced motion introduces no visual wait');
    assert.equal((await inspect('action-pointer')).transition, '0s');
    await finish(plan, (await act(plan)).effect);
    const label = await inspect('action-label');
    assert.ok(label.left >= 0 && label.right <= 336 && label.top >= 0 && label.bottom <= 576, 'badge fits narrow/125% viewport');
    // Playwright screenshot resets a raw CDP viewport override; capture its actual 420 px / 125% surface.
    if (review) {
      const shot = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
      fs.writeFileSync(review + '-narrow.png', Buffer.from(shot.data, 'base64'));
    }
    await page.setViewportSize({ width: 500, height: 700 });
    await page.waitForTimeout(50); assert.equal(await inspect('action-pointer'), null, 'resize clears obsolete coordinates');
    await reset('https://example.test/'); await refused('browser_probe_only');
    console.log('PASS external Browser actions: local fill/click/read-back, exact element and user guards, single-use preparation, unknown without replay, animated/idle cursor, runtime status, narrow/reduced motion');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
