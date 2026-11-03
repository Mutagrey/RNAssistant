'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], {cwd: path.resolve(__dirname, '../..')});
  server.stderr.resume(); let browser;
  try {
    const url = await new Promise(resolve => server.stdout.on('data', bytes => {
      const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(bytes)); if (match) resolve(match[1]);
    }));
    browser = await chromium.launch({headless: true, ...(process.env.BROWSER_EXECUTABLE ? {executablePath: process.env.BROWSER_EXECUTABLE} : {})});
    const page = await browser.newPage({viewport: {width: 1440, height: 900}}), errors = [];
    page.setDefaultTimeout(7000); page.on('pageerror', error => { errors.push(error.message); console.error(error.message); });
    await page.goto(url + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(() => RNAssistantConversationPanel.open({source: 'office', sessionId: 'preview-summary'}, {direction: 'right'}));
    await page.waitForFunction(() => !RNAssistantConversationPanel.office('preview-summary').loading);
    await page.evaluate(() => RNAssistantConversationPanel.open({source: 'office', sessionId: 'preview-chat'}));
    await page.evaluate(() => {
      window.parallelSend = send;
      window.parallelSubmissions = [];
      send = function(type, payload, ...args) {
        if (type === 'submitChatInput') {
          parallelSubmissions.push(payload);
          if (payload.chatId === 'preview-chat') return parallelSend(type, payload, ...args).then(value =>
            new Promise(resolve => { window.releaseInput = () => resolve(value); }));
        }
        if (type === 'selectChat') return parallelSend(type, payload, ...args).then(value =>
          new Promise(resolve => { window.releaseNavigation = () => resolve(value); }));
        return parallelSend(type, payload, ...args);
      };
      const a = RNAssistantConversationPanel.office('preview-chat');
      a.node('chatInput').value = 'Запрос A'; a.node('chatInput').dispatchEvent(new Event('input'));
      window.inputA = a.composer.dispatch('submit');
    });
    await page.waitForFunction(() => !!window.releaseInput);
    await page.locator('#chatSessionList [data-tree-key="chat:preview-summary"] .navigation-tree-row').click();
    await page.waitForFunction(() => !!window.releaseNavigation && !RNAssistantConversationPanel.office('preview-summary').loading);
    const b = page.locator('.conversation-panel').filter({has: page.locator('[data-chat-node="chatInput"]')}).last();
    await b.locator('[data-chat-node="chatInput"]').fill('Запрос B');
    await page.evaluate(() => RNAssistantConversationPanel.office('preview-summary').composer.dispatch('submit'));
    assert.deepEqual(await page.evaluate(() => parallelSubmissions.map(value => value.chatId)), ['preview-chat', 'preview-summary']);
    assert.equal(await page.evaluate(() => new Set(parallelSubmissions.map(value => value.operationId)).size), 2);
    assert.equal(await page.evaluate(() => state.chatNavigationPending > 0), true, 'B submits through its exact source while native navigation is delayed');
    await page.evaluate(() => RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key({source: 'office', sessionId: 'preview-chat'})));
    await page.waitForFunction(() => !RNAssistantConversationPanel.office('preview-chat'));
    await page.evaluate(() => RNAssistantConversationPanel.open({source: 'office', sessionId: 'preview-chat'}, {direction: 'right'}));
    await page.waitForFunction(() => !RNAssistantConversationPanel.office('preview-chat').loading);
    await page.evaluate(() => {
      const a = RNAssistantConversationPanel.office('preview-chat');
      a.node('chatInput').value = 'Новый черновик A'; a.node('chatInput').dispatchEvent(new Event('input'));
      RNAssistantConversationPanel.open({source: 'office', sessionId: 'preview-summary'});
    });
    await page.evaluate(async () => { releaseInput(); releaseNavigation(); await inputA; });
    await page.waitForFunction(() => !state.chatNavigationPending);
    assert.deepEqual(await page.evaluate(() => [RNAssistantConversationPanel.selected().target.sessionId,
      RNAssistantConversationPanel.office('preview-chat').node('chatInput').value]), ['preview-summary', 'Новый черновик A'],
    'late A acknowledgement does not overwrite a replacement draft or B focus');
    await page.evaluate(() => { send = parallelSend; });
    await page.waitForFunction(() => ['preview-chat', 'preview-summary'].every(id => !chatInbox(id).running &&
      RNAssistantConversationPanel.office(id).snapshot().messages.some(message => (message.Id || '').startsWith('preview-reply-'))));

    // Two visible streams, a large catalog and stalled reads must leave navigation usable.
    await page.evaluate(() => {
      send = function(type, ...args) {
        if (type === 'getChatInbox' || type === 'getChatState' || type === 'selectChat') return new Promise(() => {});
        return parallelSend(type, ...args);
      };
      RNAssistantConversationPanel.open({source: 'office', sessionId: 'preview-chat'}, {direction: 'right'});
      RNAssistantConversationPanel.open({source: 'office', sessionId: 'preview-summary'});
      state.chats = state.chats.concat(Array.from({length: 500}, (_, i) => Object.assign({}, state.chats[0], {
        Id: 'parallel-catalog-' + i, Title: 'История ' + i, LastActivityUtc: '2025-01-01T00:00:00Z'
      })));
      ['preview-chat', 'preview-summary'].forEach(id => {
        state.activeSends[id] = {requestId: 'inbox:' + id};
        applyOfficeChatProgress({chatId: id, runId: 'parallel-' + id, phase: 'thinking', contentReset: true, contentDelta: 'Начало'}, null, 'inbox:' + id);
      });
      renderChatSessions();
      window.parallelTree = document.querySelector('#chatSessionList .chat-navigation-tree');
      window.parallelControls = 0; window.parallelInspector = 0;
      RNAssistantConversationPanel.each(component => { const controls = component.controls; component.controls = function() { parallelControls++; return controls(); }; });
      const inspector = RNAssistantChatInspector.render;
      RNAssistantChatInspector.render = function() { parallelInspector++; return inspector(); };
    });
    const metrics = await page.evaluate(async () => {
      const started = performance.now(), gaps = []; let previous = started;
      for (let i = 0; i < 60; i++) {
        ['preview-chat', 'preview-summary'].forEach(id => applyOfficeChatProgress({
          chatId: id, runId: 'parallel-' + id, phase: 'thinking', contentDelta: ' токен'
        }, null, 'inbox:' + id));
        await new Promise(resolve => setTimeout(resolve, 20));
        const now = performance.now(); gaps.push(now - previous); previous = now;
      }
      return {elapsedMs: Math.round(performance.now() - started), maxTickMs: Math.round(Math.max(...gaps)),
        retained: parallelTree === document.querySelector('#chatSessionList .chat-navigation-tree'),
        controls: parallelControls, inspector: parallelInspector};
    });
    assert.equal(metrics.retained, true, 'background tokens retain the complete navigation DOM');
    assert.equal(metrics.controls, 0, 'background tokens do not resize unrelated composers');
    assert.equal(metrics.inspector, 0, 'background tokens do not rebuild the selected Inspector');
    await page.evaluate(() => {
      window.navigationTicks = 0;
      window.navigationStreamTimer = setInterval(() => {
        navigationTicks++;
        ['preview-chat', 'preview-summary'].forEach(id => applyOfficeChatProgress({
          chatId: id, runId: 'parallel-' + id, phase: 'thinking', contentDelta: ' поток'
        }, null, 'inbox:' + id));
      }, 20);
    });
    await page.locator('#chatSessionList [data-tree-key="chat:preview-chat"] .navigation-tree-row').click();
    assert.equal(await page.evaluate(() => RNAssistantConversationPanel.selected().target.sessionId), 'preview-chat');
    assert.equal(await page.evaluate(() => parallelTree === document.querySelector('#chatSessionList .chat-navigation-tree')), true,
      'switching between open chats changes selection without rebuilding 500 rows');
    const inputId = await page.evaluate(() => RNAssistantConversationPanel.office('preview-chat').node('chatInput').id);
    await page.locator('#' + inputId).fill('Ввод во время двух потоков');
    assert.equal(await page.evaluate(() => RNAssistantConversationPanel.office('preview-chat').node('chatInput').value), 'Ввод во время двух потоков');
    await page.waitForFunction(() => navigationTicks > 0);
    await page.evaluate(() => clearInterval(navigationStreamTimer));
    assert.deepEqual(errors, []);
    console.log('PASS parallel chat navigation, delayed ACK and large-catalog streaming', JSON.stringify(metrics));
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
