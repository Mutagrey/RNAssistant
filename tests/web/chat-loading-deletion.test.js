'use strict';
// Delayed native replies against the shipped DOM, including a layout without a Chat View.
const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path');
const { spawn } = require('node:child_process'), { chromium } = require('playwright');

(async () => {
  const root = path.resolve(__dirname, '../..');
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], { cwd: root });
  server.stderr.resume(); let browser;
  try {
    const base = await new Promise((resolve, reject) => {
      server.once('error', reject);
      server.stdout.on('data', data => {
        const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(data));
        if (match) resolve(match[1]);
      });
    });
    browser = await chromium.launch({ headless: true,
      ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } }), errors = [];
    page.setDefaultTimeout(7000);
    page.on('pageerror', error => errors.push(error.message));
    page.on('dialog', dialog => dialog.accept());
    await page.addInitScript(() => {
      window.bootFailures = [];
      document.addEventListener('DOMContentLoaded', () => {
        const fail = window.applyBridgeUnavailableState;
        window.applyBridgeUnavailableState = error => { bootFailures.push(error.message); return fail(error); };
      });
    });
    await page.goto(base + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(() => {
      RNAssistantDockWorkspace.show('settings');
      Array.from(document.querySelectorAll('[data-tab-panel-id]'), node => node.dataset.tabPanelId)
        .filter(id => id !== 'settings').forEach(id => RNAssistantDockWorkspace.close(id));
    });
    await page.waitForFunction(() => {
      const saved = JSON.parse(localStorage.getItem('rnassistant.ui.dock.s1.v1'));
      return saved && Object.keys(saved.layout.panels).length === 1 && saved.layout.panels.settings;
    });
    let mock = fs.readFileSync(path.join(root, 'prototypes/ui/mock-bridge.js'), 'utf8');
    const init = 'prompts: promptLibrary(), hasApiKey: true, hasHistorySecret: false }, chatState())';
    assert.ok(mock.includes(init), 'fixture bootstrap marker');
    mock = mock.replace(init, 'prompts: promptLibrary(), hasApiKey: true, hasHistorySecret: false, ' +
      'detailDeferred: true, activeChatId: "", chats: [], documents: [] })');
    mock = mock.replace('if (type === "getChatState") requireArtifactReader(payload);',
      'if (type === "getChatState") { if (!payload.chatId) await new Promise(resolve => window.releaseHistory = resolve); else requireArtifactReader(payload); }');
    mock = mock.replace('if (type === "init") return', `if (type === "deleteChat") {
      window.deleteRequests = (window.deleteRequests || 0) + 1;
      await new Promise((resolve, reject) => { window.finishDelete = resolve; window.failDelete = () => reject(new Error("Удаление не выполнено")); });
      chats = chats.filter(chat => chat.Id !== payload.chatId);
      delete messagesByChat[payload.chatId];
      if (activeChatId === payload.chatId) activeChatId = chats[0].Id;
      revision++;
      return chatState();
    }
    if (type === "init") return`);
    await page.route('**/preview/mock-bridge.js', route => route.fulfill({ contentType: 'application/javascript', body: mock }));
    await page.reload();
    await page.waitForFunction(() => state.bridgeUnavailable || window.releaseHistory);
    assert.deepEqual(await page.evaluate(() => bootFailures), [], 'deferred startup must not render an absent chat');
    assert.equal(await page.evaluate(() => state.bridgeUnavailable), false);
    assert.equal(await page.locator('.conversation-panel').count(), 0, 'loading does not manufacture a chat tab');
    assert.match(await page.locator('#chatSessionList').innerText(), /Загрузка чатов/);
    assert.equal(await page.locator('#baseUrlInput').isEnabled(), true, 'settings remain available while history loads');
    if (process.env.CHAT_REVIEW_DIR) await page.screenshot({ path: path.join(process.env.CHAT_REVIEW_DIR, 'loading.png') });
    await page.evaluate(() => releaseHistory());
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    assert.equal(await page.evaluate(() => state.bridgeUnavailable), false);
    assert.ok(await page.locator('.conversation-panel').count() > 0);

    const row = page.locator('#chatSessionList [data-tree-key="chat:preview-summary"]');
    await page.evaluate(() => {
      state.chatSyncPromise = new Promise(resolve => window.releaseSync = resolve);
    });
    await row.hover();
    await row.getByRole('button', { name: 'Удалить чат', exact: true }).click();
    await page.waitForFunction(() => state.chatDeletions['preview-summary']);
    assert.match(await row.innerText(), /Удаление…/, 'pending state starts before the navigation queue drains');
    assert.equal(await row.getByRole('button', { name: 'Удаление…', exact: true }).isDisabled(), true);
    assert.equal(await page.evaluate(() => window.deleteRequests || 0), 0);
    await page.evaluate(() => deleteChat('preview-summary'));
    await page.evaluate(() => { state.chatSyncPromise = null; releaseSync(); });
    await page.waitForFunction(() => window.deleteRequests === 1);
    await page.evaluate(() => renderChatSessions({ navigationOnly: true }));
    assert.match(await row.innerText(), /Удаление…/, 'background renders preserve pending feedback');
    if (process.env.CHAT_REVIEW_DIR) await page.screenshot({ path: path.join(process.env.CHAT_REVIEW_DIR, 'deleting.png') });
    await page.evaluate(() => failDelete());
    await page.waitForFunction(() => !state.chatDeletions['preview-summary']);
    assert.equal(await row.count(), 1, 'a failed delete must keep the chat');
    assert.equal(await row.getByRole('button', { name: 'Удалить чат', exact: true }).isEnabled(), true);
    assert.match(await page.locator('#logBox').textContent(), /Удаление не выполнено/);

    await row.getByRole('button', { name: 'Удалить чат', exact: true }).click();
    await page.waitForFunction(() => window.deleteRequests === 2);
    assert.equal(await row.count(), 1, 'the row stays until native confirmation');
    await page.evaluate(() => finishDelete());
    await page.waitForFunction(() => !state.chatDeletions['preview-summary']);
    assert.equal(await row.count(), 0);
    assert.equal(await page.evaluate(() => state.chats.some(chat => chatId(chat) === 'preview-summary')), false);
    assert.deepEqual(errors, []);
    console.log('PASS delayed startup without a chat view; queued deletion, duplicate suppression, failure and confirmed removal');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
