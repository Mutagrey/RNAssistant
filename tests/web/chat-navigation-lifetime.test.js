'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');

(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], {cwd: path.resolve(__dirname, '../..')});
  server.stderr.resume();
  let browser;
  try {
    const url = await new Promise((resolve, reject) => {
      server.once('error', reject);
      server.stdout.on('data', data => { const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(data)); if (match) resolve(match[1]); });
    });
    browser = await chromium.launch({headless: true, ...(process.env.BROWSER_EXECUTABLE ? {executablePath: process.env.BROWSER_EXECUTABLE} : {})});
    const page = await browser.newPage({viewport: {width: 1440, height: 900}}), errors = [];
    page.setDefaultTimeout(7000);
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(url + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    const close = async id => {
      await page.evaluate(id => RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key({source: 'office', sessionId: id})), id);
      await page.waitForFunction(id => !RNAssistantConversationPanel.office(id), id);
    };
    const selected = () => page.evaluate(() => RNAssistantConversationPanel.selected()?.target.sessionId || null);
    const clickChat = id => page.locator('#chatSessionList [data-tree-key="chat:' + id + '"] .navigation-tree-row').click();
    await close('preview-chat');
    await page.evaluate(() => {
      window.navigationCalls = [];
      window.navigationSend = send;
      send = function(type, ...args) { navigationCalls.push(type); return navigationSend(type, ...args); };
      state.chatSyncPromise = new Promise(resolve => window.releaseSync = resolve);
    });
    await clickChat('preview-summary');
    assert.equal(await selected(), 'preview-summary', 'tree selection is visible before the native queue drains');
    assert.equal(await page.evaluate(() => state.activeChatId), 'preview-summary', 'the tree and view select the same target immediately');
    assert.equal(await page.evaluate(() => !!RNAssistantConversationPanel.office('preview-chat')), false, 'selection never resurrects the previous tab');
    assert.equal(await page.evaluate(() => navigationCalls.includes('selectChat')), false, 'native selection still waits for its required drain');
    await page.waitForFunction(() => !RNAssistantConversationPanel.office('preview-summary').loading);
    if (process.env.NAV_REVIEW_DIR) await page.screenshot({path: path.join(process.env.NAV_REVIEW_DIR, 'selection-while-waiting.png')});
    await page.evaluate(() => { state.chatSyncPromise = null; releaseSync(); });
    await page.waitForFunction(() => !state.chatNavigationPending);
    await close('preview-summary');
    await page.evaluate(() => synchronizeChatState(true));
    assert.equal(await selected(), null);
    assert.equal(await page.locator('.conversation-panel').count(), 0, 'catalog sync keeps all tabs closed');

    // An in-flight native reply and its selection preview cannot outlive close.
    await page.evaluate(() => {
      state.chats.push(Object.assign({}, state.chats[0], {Id: 'navigation-transient', Title: 'Временный чат'}));
      send = function(type, ...args) {
        if (type !== 'selectChat') return navigationSend(type, ...args);
        return navigationSend(type, ...args).then(value => new Promise(resolve => {
          window.releaseSelection = () => resolve(Object.assign({}, value, {catalogChanges: {removedTransientChatIds: ['navigation-transient']}}));
        }));
      };
      window.pendingSelection = selectChat('preview-chat');
    });
    await page.waitForFunction(() => !!window.releaseSelection && !RNAssistantConversationPanel.office('preview-chat').loading);
    assert.deepEqual(await page.evaluate(() => {
      const component = RNAssistantConversationPanel.office('preview-chat'), message = component.node('messages').firstChild;
      state.pending.navigationPreview = {type: 'selectChat', payload: {chatId: 'preview-chat'}};
      applyPushedChatState({id: 'navigationPreview', scope: 'selection', payload: {
        activeChatId: 'preview-chat', sessionRevision: component.snapshot().sessionRevision, detailDeferred: true
      }});
      delete state.pending.navigationPreview;
      return [component.loading, component.node('messages').firstChild === message, component.snapshot().messages.length > 0];
    }), [false, true, true], 'a selection preview retains loaded history while its full reply is pending');
    await close('preview-chat');
    await page.evaluate(async () => {
      state.pending.navigationPreview = {type: 'selectChat', payload: {chatId: 'preview-chat'}};
      applyPushedChatState({id: 'navigationPreview', scope: 'selection', payload: {activeChatId: 'preview-chat', detailDeferred: true}});
      delete state.pending.navigationPreview;
      releaseSelection(); await pendingSelection;
      window.transientRetained = state.chats.some(chat => chat.Id === 'navigation-transient');
      send = navigationSend;
      await refreshChatAfterSendFailure('preview-chat');
    });
    assert.equal(await page.locator('.conversation-panel').count(), 0, 'late preview, reply and failure refresh never reopen the closed view');
    assert.equal(await page.evaluate(() => transientRetained), false, 'late selection still delivers native catalog removals');

    // Native persisted selection can differ from the active Dockview tab.
    await page.evaluate(() => RNAssistantConversationPanel.open({source: 'office', sessionId: 'preview-summary'}));
    await page.waitForFunction(() => !RNAssistantConversationPanel.office('preview-summary').loading);
    await page.evaluate(() => synchronizeChatState(true));
    assert.equal(await selected(), 'preview-summary', 'catalog refresh does not follow the backend selection');
    assert.equal(await page.evaluate(() => !!RNAssistantConversationPanel.office('preview-chat')), false);
    await page.evaluate(async () => {
      const init = await navigationSend('init', {});
      send = function(type, ...args) {
        if (type !== 'selectChat') return navigationSend(type, ...args);
        return navigationSend(type, ...args).then(value => new Promise(resolve => {
          window.releaseSecondSelection = () => resolve({init: Object.assign({}, init, value)});
        }));
      };
      window.secondSelection = selectChat('preview-chat');
    });
    await page.waitForFunction(() => !!window.releaseSecondSelection && !RNAssistantConversationPanel.office('preview-chat').loading);
    await page.evaluate(() => RNAssistantConversationPanel.open({source: 'office', sessionId: 'preview-summary'}));
    await page.evaluate(async () => { releaseSecondSelection(); await secondSelection; send = navigationSend; });
    assert.equal(await selected(), 'preview-summary', 'a late native reply cannot steal Dockview focus');

    // Refresh the selected view by its own revision, then close during that read.
    await page.evaluate(async () => {
      window.detailReads = [];
      send = function(type, payload, ...args) {
        if (type === 'listChats') return navigationSend(type, payload, ...args).then(value => {
          value.chats.find(chat => chat.Id === 'preview-summary').Revision += 100;
          return value;
        });
        if (type === 'getChatState') {
          detailReads.push(payload.chatId);
          return navigationSend(type, payload, ...args).then(value => new Promise(resolve => { window.releaseDetail = () => resolve(value); }));
        }
        return navigationSend(type, payload, ...args);
      };
      window.refreshing = synchronizeChatState(true);
    });
    await page.waitForFunction(() => !!window.releaseDetail);
    assert.deepEqual(await page.evaluate(() => detailReads), ['preview-summary'], 'sync reads the open selected view, not the persisted backend target');
    await close('preview-summary');
    await page.evaluate(async () => { releaseDetail(); await refreshing; send = navigationSend; });
    assert.equal(await page.evaluate(() => !!RNAssistantConversationPanel.office('preview-summary')), false, 'late catalog detail cannot recreate the view');
    await page.setViewportSize({width: 600, height: 800});
    await page.evaluate(async () => {
      document.documentElement.style.zoom = '1.25';
      await new Promise(requestAnimationFrame); await new Promise(requestAnimationFrame);
    });
    await page.locator('#productMobileMenuButton').click();
    const summary = page.locator('#chatSessionList [data-tree-key="chat:preview-summary"]');
    await summary.focus(); await summary.press('Enter');
    await page.waitForFunction(() => RNAssistantConversationPanel.office('preview-summary') && !state.chatNavigationPending);
    assert.equal(await selected(), 'preview-summary', 'keyboard tree navigation works in the narrow layout at 125% zoom');
    assert.equal(await page.locator('.shell').evaluate(node => node.classList.contains('navigation-open')), false,
      'selection closes the overlay even when its original tree root was replaced');
    if (process.env.NAV_REVIEW_DIR) await page.screenshot({path: path.join(process.env.NAV_REVIEW_DIR, 'selection-narrow.png')});
    assert.deepEqual(errors, []);
    console.log('PASS chat navigation lifetime: immediate exact selection, all-closed sync, late preview/reply/failure/read and newer Dockview focus');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
