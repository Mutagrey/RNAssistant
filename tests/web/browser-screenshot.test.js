'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const { createHash } = require('node:crypto');
const { withPreviewPage } = require('./preview-session');

(async () => withPreviewPage(async ({ page, errors }) => {
  page.setDefaultTimeout(8000);
  await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
  const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6XkAAAAASUVORK5CYII=', 'base64');
  const payload = { sha256: createHash('sha256').update(png).digest('hex'), byteLength: png.length, contentType: 'image/png' };
  await page.evaluate(({ pixels, payload }) => {
    window.showSaveFilePicker = undefined;
    const original = send, originalFetch = fetch;
    const leaseId = '3'.repeat(64), url = 'https://rnassistant.local-resource/v1/download/' + leaseId;
    const probe = window.screenshotProbe = { calls: [], pages: new Map(), late: false, private: false, pending: null };
    window.fetch = (value, ...rest) => {
      if (String(value).startsWith(url)) {
        const query = new URL(value).searchParams;
        return Promise.resolve(new Response(new Uint8Array(pixels).slice(Number(query.get('offset')), Number(query.get('offset')) + Number(query.get('count'))),
          { headers: { 'Content-Type': 'image/png' } }));
      }
      return originalFetch(value, ...rest);
    };
    window.send = (type, p, ...rest) => {
      probe.calls.push({ type, p: structuredClone(p) });
      if (type === 'captureBrowserScreenshot') {
        const state = probe.pages.get(p.browserTabId);
        if (!state.bounds.visible || state.sessionId !== p.sessionId || state.documentGeneration !== p.generation)
          return Promise.reject(new Error('Capture before exact visible placement'));
        if (probe.private) return Promise.reject(new Error('Приватные поля: снимок недоступен.'));
        const value = { fileName: 'rnassistant-page.png', contentType: 'image/png', kind: 'browser_screenshot', data: { leaseId, url, payload, maxChunkBytes: 256 * 1024 } };
        return probe.late ? new Promise(resolve => { probe.pending = () => resolve(value); }) : Promise.resolve(value);
      }
      if (type === 'closeBrowserScreenshot') return Promise.resolve({ closed: true });
      if (type === 'readBrowserSource') return Promise.resolve({ files: ['index.html'], path: 'index.html', text: '<p>Page</p>', offset: 0, totalCharacters: 11, requestGeneration: p.requestGeneration });
      if (type !== 'browserCommand') return original(type, p, ...rest);
      let value = probe.pages.get(p.browserTabId);
      if (p.action === 'Open') {
        value = { browserTabId: p.browserTabId, operationId: p.operationId, sessionId: 'screen-session', revision: 1, documentGeneration: 1,
          ownerScope: p.ownerScope, chatBound: false, url: p.locator, title: 'Local page', status: 'Ready',
          target: { kind: 'WorkspaceHtmlProject', state: 'Ready', ownerScope: p.ownerScope, workspaceId: 'screen-project', displayLocator: p.locator,
            capabilities: { source: true, preview: true, media: true } } };
        probe.pages.set(p.browserTabId, value);
      }
      if (p.action === 'Attach') value.attached = true;
      if (p.bounds) value.bounds = p.bounds;
      if (p.action === 'Close') value.status = 'Closed';
      value.revision++; return Promise.resolve(structuredClone(value));
    };
    RNAssistantBrowser.open('/projects/screen-project/source/index.html', { kind: 'Workspace', scopeId: 'screen-project' }, { browserTabId: 'screen-user', chatBound: false });
  }, { pixels: Array.from(png), payload });
  const root = page.locator('.browser-object-view:visible');
  const actions = root.getByRole('button', { name: 'Действия страницы', exact: true });
  const screenshot = page.getByRole('menuitem', { name: 'Скачать снимок страницы', exact: true });
  await actions.click(); await screenshot.waitFor();
  assert.ok(await screenshot.isEnabled());
  await page.screenshot({ path: 'docs/ui/review/browser-screenshot-wide.png' });
  const downloading = page.waitForEvent('download'); await screenshot.click();
  const download = await downloading;
  assert.equal(download.suggestedFilename(), 'rnassistant-page.png');
  assert.deepEqual(await fs.readFile(await download.path()), png);
  await page.waitForFunction(() => screenshotProbe.calls.filter(c => c.type === 'closeBrowserScreenshot').length === 1);
  const calls = await page.evaluate(() => screenshotProbe.calls);
  const at = calls.findIndex(c => c.type === 'captureBrowserScreenshot');
  assert.equal(calls[at - 1].p.action, 'Layout'); assert.equal(calls[at - 1].p.bounds.visible, true);
  assert.equal(calls[at].p.sessionId, 'screen-session'); assert.equal(calls[at].p.generation, 1);
  await root.getByRole('button', { name: 'Исходник', exact: true }).click();
  await actions.click(); assert.ok(await screenshot.isDisabled()); await page.keyboard.press('Escape');
  await root.getByRole('button', { name: 'Страница', exact: true }).click();
  await page.evaluate(() => { screenshotProbe.private = true; });
  await actions.click(); await screenshot.click();
  await root.getByText('Приватные поля: снимок недоступен.', { exact: true }).waitFor();
  assert.equal(await page.evaluate(() => screenshotProbe.calls.filter(c => c.type === 'closeBrowserScreenshot').length), 1);
  await page.evaluate(() => { screenshotProbe.private = false; screenshotProbe.late = true; });
  await page.setViewportSize({ width: 420, height: 800 });
  await page.evaluate(() => { document.documentElement.style.zoom = '1.25'; });
  await root.getByRole('button', { name: 'Инструменты страницы', exact: true }).click();
  await actions.click(); await screenshot.waitFor();
  await page.screenshot({ path: 'docs/ui/review/browser-screenshot-narrow.png' });
  await screenshot.click();
  await page.waitForFunction(() => !!screenshotProbe.pending).catch(async error => {
    console.error(await page.evaluate(() => ({ calls: screenshotProbe.calls.slice(-5), status: document.querySelector('.preview-surface-status')?.textContent,
      focused: document.activeElement?.outerHTML, blockers: Array.from(document.querySelectorAll('.preview-surface-toolbar.is-address-focused, .preview-surface-completion:not([hidden]), .rna-action-menu-popup')).map(node => node.className) })));
    throw error;
  });
  let lateDownloads = 0; page.on('download', () => lateDownloads++);
  await page.evaluate(() => { RNAssistantDockWorkspace.close('browser:screen-user'); screenshotProbe.pending(); });
  await page.waitForFunction(() => screenshotProbe.calls.filter(c => c.type === 'closeBrowserScreenshot').length === 2);
  assert.equal(lateDownloads, 0); assert.deepEqual(errors, []);
  console.log('PASS Browser screenshot: exact visible placement, unbound download, real PNG bytes, source/privacy refusal, narrow menu and late-close cleanup');
}))().catch(error => { console.error(error); process.exitCode = 1; });
