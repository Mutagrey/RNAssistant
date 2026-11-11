'use strict';
// Production Dock/toolbar; native HWND delivery remains Windows-only evidence.
const assert = require('node:assert/strict');
const {withPreviewPage} = require('./preview-session');
(async () => {
  await withPreviewPage(async ({page, errors}) => {
    page.setDefaultTimeout(8000);
    await page.setViewportSize({width: 1280, height: 900});
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(() => {
      window.externalProbe = {calls: [], next: 0, live: null, delay: false};
      const original = send;
      window.send = (type, payload, ...rest) => {
        if (type !== 'externalBrowserCommand') return original(type, payload, ...rest);
        const p = externalProbe; p.calls.push(structuredClone(payload));
        if (payload.action === 'Open') {
          p.live = {sessionId: 'native-' + ++p.next, revision: 1, status: 'Ready', url: 'https://example.com/', title: 'P5 · fixture', message: 'Synthetic native; Windows evidence open', diagnostic: 'Fixture: HWND delivery is not exercised', canGoBack: false, canGoForward: false};
          const result = {...p.live};
          if (p.delay) { p.delay = false; return new Promise(resolve => { p.release = () => resolve(result); }); }
          return Promise.resolve(result);
        }
        if (payload.action === 'Close') { if (p.live?.sessionId === payload.sessionId) p.live = null; return Promise.resolve({sessionId: payload.sessionId, status: 'Closed'}); }
        assertCurrent();
        function assertCurrent() { if (payload.sessionId !== p.live?.sessionId) throw new Error('stale native session'); }
        if (payload.action === 'Navigate') { p.live.url = payload.url; p.live.revision++; }
        if (payload.action === 'Layout' && p.delayLayout) {
          p.delayLayout = false; const result = {...p.live}; return new Promise(resolve => { p.releaseLayout = () => resolve(result); });
        }
        return Promise.resolve({...p.live});
      };
      RNAssistantExternalBrowser.bootstrap(true);
    });
    await page.getByRole('button', {name: 'Выбрать экран', exact: true}).first().click();
    await page.getByRole('button', {name: 'Внешний браузер · эксперимент', exact: true}).click();
    const root = page.locator('section[aria-label="Внешний браузер"]');
    await page.waitForFunction(() => externalProbe.calls.some(c => c.action === 'Layout' && c.bounds.visible));
    const initial = await page.evaluate(() => externalProbe.live.sessionId);
    const beforeInvalidation = await page.evaluate(() => externalProbe.calls.filter(c => c.action === 'Layout').length);
    await page.evaluate(() => { externalProbe.live.layoutRevision = 1; RNAssistantExternalBrowser.apply({...externalProbe.live}); });
    await page.waitForFunction(count => externalProbe.calls.filter(c => c.action === 'Layout').length > count, beforeInvalidation);
    await page.getByRole('button', {name: 'Открыть экран справа', exact: true}).first().click();
    await page.getByRole('button', {name: 'Внешний браузер · эксперимент', exact: true}).click();
    assert.equal(await page.evaluate(() => externalProbe.live.sessionId), initial, 'moving into a split does not reopen native page');
    for (const action of ['Сохранить раскладку', 'Вернуть сохранённую']) {
      await page.locator('#workspaceLayout').click(); await page.getByRole('menuitem', {name: action, exact: true}).click();
    }
    assert.equal(await page.evaluate(() => externalProbe.calls.filter(c => c.action === 'Open').length), 1, 'layout restore retains native identity');
    const address = root.getByRole('textbox', {name: 'Адрес материала'});
    await address.fill('https://example.org/'); await address.press('Enter');
    await page.waitForFunction(() => externalProbe.calls.some(c => c.action === 'Navigate' && c.url === 'https://example.org/'));
    await root.getByRole('button', {name: 'Сведения о странице', exact: false}).click();
    await page.waitForFunction(() => externalProbe.calls.filter(c => c.action === 'Layout').at(-1).bounds.visible === false);
    await page.keyboard.press('Escape');
    await page.waitForFunction(() => externalProbe.calls.filter(c => c.action === 'Layout').at(-1).bounds.visible);
    await page.evaluate(() => RNAssistantDockWorkspace.show('settings'));
    await page.waitForFunction(() => externalProbe.calls.filter(c => c.action === 'Layout').at(-1).bounds.visible === false);
    await page.evaluate(() => RNAssistantDockWorkspace.show('external-browser'));
    await page.waitForFunction(() => externalProbe.calls.filter(c => c.action === 'Layout').at(-1).bounds.visible);
    assert.equal(await page.evaluate(() => externalProbe.live.sessionId), initial, 'hide/show retains native page');
    await root.locator('.browser-object-content').press('Enter');
    assert.ok(await page.evaluate(() => externalProbe.calls.some(c => c.action === 'Focus')), 'keyboard entry routes to native focus');
    if (process.env.BROWSER_P5_SCREENSHOT) await page.screenshot({path: process.env.BROWSER_P5_SCREENSHOT + '-wide.png'});
    await page.setViewportSize({width: 420, height: 900});
    await page.evaluate(() => { document.documentElement.style.zoom = '1.25'; RNAssistantDockWorkspace.show('external-browser'); });
    await page.waitForFunction(() => externalProbe.calls.filter(c => c.action === 'Layout').at(-1).bounds.viewportWidth === innerWidth).catch(async error => {
      throw new Error(error.message + '; geometry ' + JSON.stringify(await root.evaluate(el => ({rect: el.getBoundingClientRect().toJSON(), content: el.querySelector('.browser-object-content').getBoundingClientRect().toJSON(), calls: externalProbe.calls.slice(-4), active: RNAssistantDockWorkspace.isVisible('external-browser')}))));
    });
    assert.ok(await root.evaluate(el => el.scrollWidth <= el.clientWidth + 1), 'narrow/zoom has no horizontal overflow');
    if (process.env.BROWSER_P5_SCREENSHOT) await page.screenshot({path: process.env.BROWSER_P5_SCREENSHOT + '-narrow.png'});
    await page.evaluate(() => document.documentElement.classList.add('workspace-dragging'));
    await page.waitForFunction(() => !externalProbe.calls.filter(c => c.action === 'Layout').at(-1).bounds.visible);
    await page.evaluate(() => document.documentElement.classList.remove('workspace-dragging'));
    await page.waitForFunction(() => externalProbe.calls.filter(c => c.action === 'Layout').at(-1).bounds.visible);
    await page.evaluate(() => { externalProbe.delayLayout = true; RNAssistantDockWorkspace.show('settings'); });
    await page.waitForFunction(() => !!externalProbe.releaseLayout);
    await page.evaluate(() => RNAssistantDockWorkspace.close('external-browser'));
    await page.waitForFunction(() => externalProbe.live === null);
    await page.evaluate(() => RNAssistantDockWorkspace.show('external-browser'));
    await page.waitForFunction(() => externalProbe.live && externalProbe.live.sessionId !== 'native-1');
    assert.equal(await page.evaluate(() => externalProbe.calls.filter(c => c.action === 'Open').length), 2, 'close/reopen creates a fresh page');
    await page.waitForFunction(() => externalProbe.calls.some(c => c.action === 'Layout' && c.sessionId === 'native-2' && c.bounds.visible));
    await page.evaluate(() => externalProbe.releaseLayout());
    await page.evaluate(() => {
      RNAssistantExternalBrowser.apply({sessionId: 'native-1', revision: 999, status: 'Ready', url: 'https://stale.invalid/'});
    });
    assert.notEqual(await address.inputValue(), 'https://stale.invalid/', 'old session cannot replace current UI');
    await page.evaluate(() => RNAssistantDockWorkspace.close('external-browser'));
    await page.waitForFunction(() => externalProbe.live === null);
    await page.evaluate(() => { externalProbe.delay = true; RNAssistantDockWorkspace.show('external-browser'); });
    await page.waitForFunction(() => !!externalProbe.release);
    await page.evaluate(() => RNAssistantDockWorkspace.close('external-browser'));
    await page.evaluate(() => externalProbe.release());
    await page.waitForFunction(() => externalProbe.live === null);
    assert.equal(await page.locator('[data-tab-panel-id="external-browser"]').count(), 0, 'late open does not resurrect closed view');
    assert.deepEqual(errors, []);
    console.log('PASS external Browser: navigation, native bounds, overlay/drag/hide, narrow zoom, fresh reopen, stale/late cleanup');
  });
})().catch(error => { console.error(error); process.exitCode = 1; });
