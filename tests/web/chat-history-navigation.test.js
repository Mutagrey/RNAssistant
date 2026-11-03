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
    const workspace = await page.evaluate(async () => {
      const catalog = await send('getProjectChats', {}), project = catalog.projects.find(item => item.available && item.chats.length);
      return {source: 'workspace', workspaceId: project.workspaceId, sessionId: project.chats[0].Id};
    });
    for (const target of [{source: 'office', sessionId: 'preview-chat'}, workspace]) {
      await page.evaluate(target => RNAssistantConversationPanel.open(target), target);
      await page.waitForFunction(target => RNAssistantConversationPanel.find(target) && !RNAssistantConversationPanel.find(target).loading, target);
      await page.evaluate(target => {
        window.historyPanel = RNAssistantConversationPanel.find(target);
        window.historyRows = (start, count) => Array.from({length: count}, (_, i) => ({Id: 'paging-' + (start + i), Role: 'user', Content: 'Строка ' + (start + i)}));
        window.historySnapshot = JSON.parse(JSON.stringify(historyPanel.snapshot()));
        if (target.source === 'office') historySnapshot.sessionRevision += 100;
        else historySnapshot.state.revision += 100;
        historySnapshot.messageStartIndex = 80; historySnapshot.messageTotalCount = 160; historySnapshot.messages = historyRows(80, 80);
        window.resetHistorySnapshot = () => historyPanel.accept(JSON.parse(JSON.stringify(historySnapshot)), {latestMessages: true});
        resetHistorySnapshot();
        window.historySend = send; window.historyReads = []; window.latestReads = []; window.holdLatest = false;
        window.pageReply = () => ({chatId: target.sessionId,
          sessionRevision: target.source === 'office' ? historySnapshot.sessionRevision : historySnapshot.state.revision,
          startIndex: 0, totalCount: historySnapshot.messageTotalCount, messages: historyRows(0, 80), artifacts: []});
        send = function(type, payload, ...args) {
          if (type === (target.source === 'office' ? 'getPreviousChatMessages' : 'getPreviousProjectChatMessages'))
            return new Promise((resolve, reject) => historyReads.push({payload, resolve, reject}));
          if (type === (target.source === 'office' ? 'getChatState' : 'getProjectChat')) {
            if (holdLatest) return new Promise(resolve => latestReads.push(resolve));
            return Promise.resolve(JSON.parse(JSON.stringify(historySnapshot)));
          }
          return historySend(type, payload, ...args);
        };
        window.pageA = historyPanel.composer.dispatch('loadPrevious');
        window.pageDuplicate = historyPanel.composer.dispatch('loadPrevious');
      }, target);
      assert.equal(await page.evaluate(() => historyReads.length), 1, target.source + ': duplicate page clicks share one read');
      await page.evaluate(async () => { historyReads[0].resolve(pageReply()); await Promise.all([pageA, pageDuplicate]); });
      assert.deepEqual(await page.evaluate(() => [historyPanel.snapshot().messageStartIndex, historyPanel.snapshot().messages.length]), [0, 160]);
      await page.evaluate(async () => {
        resetHistorySnapshot();
        window.oldPage = historyPanel.composer.dispatch('loadPrevious');
        await historyPanel.composer.dispatch('loadLatest');
        window.newPage = historyPanel.composer.dispatch('loadPrevious');
      });
      assert.equal(await page.evaluate(() => historyReads.length), 3, target.source + ': latest navigation allows a new page without waiting for the old request');
      await page.evaluate(async () => { historyReads[1].resolve(pageReply()); await oldPage; });
      assert.equal(await page.evaluate(() => historyPanel.snapshot().messageStartIndex), 80, target.source + ': old page cannot undo latest navigation at the same revision and offset');
      await page.evaluate(async () => { historyReads[2].resolve(pageReply()); await newPage; });
      assert.equal(await page.evaluate(() => historyPanel.snapshot().messageStartIndex), 0);
      await page.evaluate(() => {
        resetHistorySnapshot(); holdLatest = true;
        window.oldLatest = historyPanel.composer.dispatch('loadLatest');
      });
      await page.waitForFunction(() => latestReads.length === 1);
      await page.evaluate(async () => {
        const read = historyPanel.composer.dispatch('loadPrevious');
        historyReads.at(-1).resolve(pageReply()); await read;
        latestReads[0](JSON.parse(JSON.stringify(historySnapshot))); await oldLatest; holdLatest = false;
      });
      assert.equal(await page.evaluate(() => historyPanel.snapshot().messageStartIndex), 0,
        target.source + ': late latest response cannot undo a newer previous-page intent');
      await page.evaluate(async target => {
        resetHistorySnapshot(); const read = historyPanel.composer.dispatch('loadPrevious'), oldReply = pageReply();
        if (target.source === 'office') historySnapshot.sessionRevision++;
        else historySnapshot.state.revision++;
        resetHistorySnapshot(); historyReads.at(-1).resolve(oldReply); await read;
      }, target);
      assert.equal(await page.evaluate(() => historyPanel.snapshot().messageStartIndex), 80,
        target.source + ': a page cannot cross a transcript revision');
      await page.evaluate(async target => {
        const read = historyPanel.composer.dispatch('loadPrevious');
        if (target.source === 'office') historySnapshot.sessionRevision++;
        else historySnapshot.state.revision++;
        resetHistorySnapshot(); historyReads.at(-1).reject(new Error('Old revision read failed')); await read;
      }, target);
      assert.equal(await page.evaluate(() => historyPanel.error), '', target.source + ': obsolete page failures cannot replace current status');
      await page.evaluate(async () => {
        historySnapshot.messageTotalCount = 400; historySnapshot.messages = historyRows(80, 240);
        resetHistorySnapshot();
        const read = historyPanel.composer.dispatch('loadPrevious');
        historyReads.at(-1).resolve(pageReply()); await read;
      });
      assert.deepEqual(await page.evaluate(() => [historyPanel.snapshot().messageStartIndex, historyPanel.snapshot().messages.length,
        historyPanel.snapshot().messages.at(-1).Id]), [0, 240, 'paging-239'], target.source + ': paging remains bounded');
      await page.evaluate(() => {
        resetHistorySnapshot();
        window.closedPage = historyPanel.composer.dispatch('loadPrevious');
        RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key(historyPanel.target));
      });
      await page.waitForFunction(() => !historyPanel.isCurrent());
      await page.evaluate(async () => { historyReads.at(-1).resolve(pageReply()); await closedPage; send = historySend; });
      assert.equal(await page.evaluate(target => !!RNAssistantConversationPanel.find(target), target), false, target.source + ': a late page keeps the tab closed');
    }
    assert.deepEqual(errors, []);
    console.log('PASS addressed Office/Workspace paging: deduplication, latest/previous race, bounds and close');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
