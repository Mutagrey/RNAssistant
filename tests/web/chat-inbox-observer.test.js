'use strict';
const assert = require('node:assert/strict'), path = require('node:path');
const {spawn} = require('node:child_process'), {chromium} = require('playwright');
(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], {cwd: path.resolve(__dirname, '../..')});
  server.stderr.resume(); let browser;
  try {
    const url = await new Promise(resolve => server.stdout.on('data', bytes => {
      const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(bytes)); if (match) resolve(match[1]);
    }));
    browser = await chromium.launch({headless: true, ...(process.env.BROWSER_EXECUTABLE ? {executablePath: process.env.BROWSER_EXECUTABLE} : {})});
    const page = await browser.newPage(), errors = []; page.setDefaultTimeout(7000);
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(url + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(() => RNAssistantConversationPanel.open({source: 'office', sessionId: 'preview-summary'}, {direction: 'right'}));
    await page.waitForFunction(() => !RNAssistantConversationPanel.office('preview-summary').loading);
    await page.evaluate(async () => {
      window.inboxA = RNAssistantConversationPanel.office('preview-chat');
      window.inboxB = RNAssistantConversationPanel.office('preview-summary');
      await Promise.all([inboxA.pollInbox(), inboxB.pollInbox()]);
      window.inboxSend = send; window.inboxCalls = []; window.inboxHistory = [];
      window.inboxReply = {chatId: 'preview-chat', epoch: 'observer-test', revision: 1, running: true, phase: 'thinking', items: []};
      applyChatInbox(inboxReply, true);
      applyChatInbox({chatId: 'preview-summary', epoch: 'observer-test', revision: 19, running: false, items: []}, true);
      send = function(type, payload, ...args) {
        if (type === 'getChatInbox') { inboxCalls.push(payload); return Promise.resolve(payload.chatId === 'preview-chat' ? inboxReply : {
          chatId: payload.chatId, epoch: 'observer-test', revision: 20, running: false, items: []}); }
        if (type === 'getChatState') return new Promise(resolve => inboxHistory.push({payload, resolve}));
        return inboxSend(type, payload, ...args);
      };
      inboxReply = {...inboxReply, phase: 'executing'};
      await inboxA.pollInbox();
    });
    assert.deepEqual(await page.evaluate(() => [chatInbox('preview-chat').phase, inboxHistory.length]), ['executing', 0],
      'phase-only polling does not reload history');
    await page.evaluate(async () => {
      const remote = {Id: 'remote-input', OperationId: 'remote-operation', Revision: 1, Status: 'Pending', Text: 'Из другого окна'};
      applyChatInbox({...inboxReply, revision: 2, items: [remote]}, true);
      inboxReply = {...inboxReply, revision: 3, items: [], receipts: [{...remote, Status: 'Removed', Revision: 2}]};
      await inboxA.pollInbox();
    });
    assert.equal(await page.evaluate(() => inboxCalls.at(-1).receiptOperationIds.includes('remote-operation')), true);
    assert.deepEqual(await page.evaluate(() => [chatInputEchoes('preview-chat').length, inboxHistory.length]), [0, 0],
      'remote removal retires its echo without fetching history');
    await page.evaluate(async () => {
      const delivered = {Id: 'delivered-input', OperationId: 'delivered-operation', Revision: 1, Status: 'Pending', Text: 'Сохранить до истории'};
      applyChatInbox({...inboxReply, revision: 4, items: [delivered], receipts: []}, true);
      inboxReply = {...inboxReply, revision: 5, receipts: [{Id: delivered.Id, OperationId: delivered.OperationId, Revision: 2, Status: 'Applied'}]};
      await inboxA.pollInbox();
    });
    assert.deepEqual(await page.evaluate(() => [chatInputEchoes('preview-chat')[0].Text, inboxHistory.map(read => read.payload.chatId)]),
      ['Сохранить до истории', ['preview-chat']], 'compact applied receipt preserves text and reads only its own history');
    await page.evaluate(async () => {
      await inboxB.pollInbox();
      reconcileChatInputEchoes('preview-chat', [{Id: 'delivered-input'}]);
      inboxA.deactivate();
      const before = inboxCalls.length; await inboxA.pollInbox(); window.hiddenCalls = inboxCalls.length - before;
      inboxA.activate();
    });
    assert.equal(await page.evaluate(() => chatInbox('preview-summary').revision), 20, 'B reads while A history is stalled');
    assert.equal(await page.evaluate(() => hiddenCalls), 0, 'hidden view does not poll');
    await page.waitForFunction(() => inboxHistory.length === 2);
    await page.evaluate(async () => {
      const snapshot = await inboxSend('getChatState', {chatId: 'preview-chat'});
      inboxHistory[1].resolve({...snapshot, inbox: inboxReply});
      await new Promise(resolve => setTimeout(resolve, 0));
      inboxHistory[0].resolve({...snapshot, inbox: {...inboxReply, revision: 1}});
    });
    await page.waitForFunction(() => !inboxA.loading);
    assert.equal(await page.evaluate(() => chatInbox('preview-chat').revision), 5, 'history from the previous activation cannot downgrade inbox');
    await page.evaluate(async () => {
      inboxReply = {...inboxReply, revision: 6, running: false, receipts: []};
      await inboxA.pollInbox();
    });
    await page.waitForFunction(() => inboxHistory.length === 3);
    await page.evaluate(() => RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key(inboxA.target)));
    await page.waitForFunction(() => !inboxA.isCurrent());
    await page.evaluate(async () => {
      inboxHistory[2].resolve(await inboxSend('getChatState', {chatId: 'preview-chat'}));
      await new Promise(resolve => setTimeout(resolve, 0));
    });
    assert.equal(await page.evaluate(() => !!RNAssistantConversationPanel.office('preview-chat')), false, 'terminal history cannot reopen a closed view');
    assert.deepEqual(errors, []);
    console.log('PASS real Office inbox observers: receipts, phase/terminal reads, independent views, hide/reactivate/close');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
