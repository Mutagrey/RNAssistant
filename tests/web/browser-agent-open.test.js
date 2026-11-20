'use strict';
const assert = require('node:assert/strict');
const { withPreviewPage } = require('./preview-session');

(async () => withPreviewPage(async ({ page, errors }) => {
  page.setDefaultTimeout(8000);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
  const input = page.locator('.conversation-panel:visible [data-chat-node="chatInput"]');
  await input.fill('Черновик чата во время работы Browser');
  await input.evaluate(node => node.setSelectionRange(2, 7));
  await page.evaluate(() => {
    const original = send;
    const fixture = window.agentOpen = { calls: [], pages: new Map(), inline: 0 };
    window.send = (type, payload, ...rest) => {
      if (type === 'readBrowserSource') return Promise.resolve({ requestGeneration: payload.requestGeneration, files: ['index.html'],
        path: 'index.html', text: '<p>Исходник страницы</p>', offset: 0, totalCharacters: 23 });
      if (type !== 'browserCommand') return original(type, payload, ...rest);
      fixture.calls.push(structuredClone(payload));
      let value = fixture.pages.get(payload.browserTabId);
      if (payload.action === 'OpenInline') {
        if (value && value.status !== 'Closed') throw new Error('Previous inline session is still open');
        value = { browserTabId: payload.browserTabId, operationId: payload.operationId, sessionId: 'inline-' + ++fixture.inline,
          ownerScope: payload.ownerScope, chatBound: false, url: payload.locator, title: payload.title, revision: 0, documentGeneration: 1,
          status: 'Ready', target: { kind: 'InlineHtml', state: 'Ready', ownerScope: payload.ownerScope, capabilities: {} } };
        fixture.pages.set(payload.browserTabId, value);
      }
      if (!value) throw new Error('Unexpected Browser command: ' + payload.action);
      if (payload.action === 'Attach') {
        value.attached = true;
        value.revision++;
        return Promise.resolve({ ...structuredClone(value), visibleAttachment: payload.bounds.visible });
      }
      if (payload.action === 'Close') { value.status = 'Closed'; value.attached = false; }
      value.revision++;
      return Promise.resolve(structuredClone(value));
    };
    fixture.request = (browserTabId, operationId) => {
      let value = fixture.pages.get(browserTabId);
      if (!value) {
        const owner = { kind: 'Document', scopeId: 'preview-document', chatId: 'preview-chat' };
        value = { browserTabId, operationId, sessionId: 'native-' + browserTabId, ownerScope: owner, chatBound: true,
          url: '/resources/' + browserTabId, title: 'Страница агента', revision: 1, documentGeneration: 1,
          status: 'Unavailable', reason: 'NativeUnavailable',
          target: { kind: 'HtmlArtifact', state: 'Ready', ownerScope: owner, capabilities: {} } };
        fixture.pages.set(browserTabId, value);
      }
      return { origin: 'Agent', browserTabId, operationId, locator: value.url, title: value.title,
        ownerScope: value.ownerScope, page: structuredClone(value) };
    };
    fixture.first = fixture.request('runtime-tab', 'exact-operation');
    if (!RNAssistantBrowser.openRequested(fixture.first)) throw new Error('Browser request was refused');
  });
  await page.waitForFunction(() => agentOpen.calls.some(call => call.action === 'Attach'));
  const groups = () => page.evaluate(() => {
    const conversation = document.querySelector('.conversation-panel [data-chat-node="chatInput"]');
    const browser = document.querySelector('[data-tab-panel-id="browser:runtime-tab"]');
    return { chat: conversation?.closest('.dv-groupview')?.getBoundingClientRect().toJSON(),
      browser: browser?.closest('.dv-groupview')?.getBoundingClientRect().toJSON() };
  });
  let placement = await groups();
  assert.ok(placement.chat && placement.browser.x > placement.chat.x, 'agent page opens on the right');
  assert.deepEqual(await input.evaluate(node => [document.activeElement === node, node.selectionStart, node.selectionEnd]), [true, 2, 7]);
  const first = await page.evaluate(() => agentOpen.calls.find(call => call.action === 'Attach'));
  assert.equal(first.operationId, 'exact-operation');
  assert.equal(first.sessionId, 'native-runtime-tab');
  assert.equal(first.generation, 1);
  assert.ok(first.bounds.visible && first.bounds.width > 0 && first.bounds.height > 0);

  await page.evaluate(() => {
    RNAssistantBrowser.openRequested(agentOpen.first);
    RNAssistantBrowser.openRequested(agentOpen.request('runtime-tab', 'second-operation'));
  });
  await page.waitForFunction(() => agentOpen.calls.some(call => call.operationId === 'second-operation'));
  assert.equal(await page.locator('[data-tab-panel-id="browser:runtime-tab"]').count(), 1);
  assert.deepEqual(await page.evaluate(() => agentOpen.calls.filter(call => call.action === 'Attach').map(call => call.operationId)),
    ['exact-operation', 'second-operation']);
  assert.equal(await page.evaluate(() => agentOpen.calls.filter(call => call.action === 'Open').length), 0, 'Dock adopts the exact runtime session');

  const browserGroup = page.locator('.dv-groupview').filter({ has: page.locator('[data-tab-panel-id="browser:runtime-tab"]') });
  await browserGroup.getByRole('button', { name: 'Выбрать экран', exact: true }).click();
  await page.evaluate(() => {
    RNAssistantBrowser.openRequested(agentOpen.request('closed-early', 'cancelled-operation'));
    RNAssistantDockWorkspace.close('browser:closed-early');
  });
  await page.waitForFunction(() => agentOpen.calls.some(call => call.action === 'Close' && call.browserTabId === 'closed-early'));
  assert.equal(await page.evaluate(() => agentOpen.calls.filter(call => call.action === 'Attach' && call.browserTabId === 'closed-early').length), 0);
  assert.equal(await page.locator('[data-tab-panel-id^="choose-"]').count(), 1, 'background open keeps the unrelated chooser');
  await page.evaluate(() => {
    RNAssistantDockWorkspace.close(document.querySelector('[data-tab-panel-id^="choose-"]').dataset.tabPanelId);
    RNAssistantConversationPanel.open({ source: 'office', sessionId: 'preview-chat' });
  });
  await input.focus();
  await page.setViewportSize({ width: 420, height: 800 });
  await page.evaluate(() => { document.documentElement.style.zoom = '1.25'; });
  await page.waitForFunction(() => {
    const conversation = document.querySelector('.conversation-panel')?.getBoundingClientRect();
    const tab = document.querySelector('[data-tab-panel-id="browser:runtime-tab"]')?.getBoundingClientRect();
    return conversation?.height > 0 && tab?.y > conversation.y;
  });
  placement = await groups();
  assert.ok(placement.browser.y > placement.chat.y, 'tall narrow Dock puts Browser under the chat');
  if (process.env.RNA_DOCK_REVIEW) await page.screenshot({ path: process.env.RNA_DOCK_REVIEW + '/agent-narrow.png' });
  await page.evaluate(() => {
    RNAssistantShellLayout.setNavigationOpen(true, false);
    RNAssistantBrowser.openRequested(agentOpen.request('runtime-tab', 'drawer-operation'));
  });
  await page.waitForFunction(() => document.querySelector('[data-browser-tab-id="runtime-tab"]').hidden);
  assert.equal(await page.evaluate(() => agentOpen.calls.some(call => call.operationId === 'drawer-operation')), false);
  await page.evaluate(() => RNAssistantShellLayout.setNavigationOpen(false, false));
  await page.waitForFunction(() => agentOpen.calls.some(call => call.action === 'Attach' && call.operationId === 'drawer-operation'));

  await page.setViewportSize({ width: 420, height: 460 });
  await page.waitForFunction(() => document.querySelectorAll('.dock-group-actions').length === 1);
  await page.evaluate(() => RNAssistantBrowser.openRequested(agentOpen.request('runtime-tab', 'one-group-operation')));
  await page.waitForFunction(() => agentOpen.calls.some(call => call.action === 'Attach' && call.operationId === 'one-group-operation'));
  assert.equal(await page.evaluate(() => RNAssistantDockWorkspace.activeTarget().view), 'browser');

  const preferenceKey = 'rnassistant.ui.dock.s1.v1';
  await page.waitForFunction(key => !!JSON.parse(localStorage.getItem(key))?.layout.panels['browser:runtime-tab'], preferenceKey);
  const persisted = await page.evaluate(key => localStorage.getItem(key), preferenceKey);
  assert.ok(!persisted.includes('exact-operation') && !persisted.includes('native-runtime-tab'), 'runtime identities stay out of layout preferences');

  const inlineTab = await page.evaluate(() => {
    RNAssistantBrowser.bootstrap(true);
    RNAssistantBrowser.openHtmlPreview('<p>Первый запуск</p>', { blockId: 'message:html:0' });
    return RNAssistantDockWorkspace.activeTarget().browserTabId;
  });
  await page.waitForFunction(id => agentOpen.calls.some(call => call.browserTabId === id && call.action === 'Attach'), inlineTab);
  assert.equal(await page.evaluate(() => RNAssistantBrowser.openHtmlPreview('<p>Повторный запуск</p>', { blockId: 'message:html:0' })), true);
  const inlineCalls = await page.evaluate(id => agentOpen.calls.filter(call => call.browserTabId === id &&
    ['OpenInline', 'Close'].includes(call.action)), inlineTab);
  assert.deepEqual(inlineCalls.map(call => call.action), ['OpenInline', 'Close', 'OpenInline'], 'Play restarts the isolated session in place');
  assert.notEqual(inlineCalls[0].operationId, inlineCalls[2].operationId);
  assert.equal(await page.locator('[data-tab-panel-id="browser:' + inlineTab + '"]').count(), 1);
  await page.waitForFunction(key => !localStorage.getItem('rnassistant.ui.dock.s1.v1')?.includes(key), inlineTab);
  assert.deepEqual(errors, []);
  console.log('PASS agent Browser opens beside the chat with preserved focus, independent attachments and narrow fallback');
}))().catch(error => { console.error(error); process.exitCode = 1; });
