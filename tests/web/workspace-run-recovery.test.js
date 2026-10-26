'use strict';
// Native envelopes come from the lease/recovery Application + bridge Harness check.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
const fixture = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
assert.equal(fixture.owned.payload.state.Status, 'running');
assert.equal(fixture.recovered.payload.state.Status, 'interrupted');

(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], {cwd:path.resolve(__dirname, '../..')});
  let browser;
  try {
    const address = await new Promise((resolve, reject) => {
      server.stdout.on('data', chunk => {
        const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(chunk));
        if (match) resolve(match[1]);
      });
      server.once('exit', code => reject(Error('Preview exited: ' + code)));
    });
    browser = await chromium.launch({headless:true,
      ...(process.env.BROWSER_EXECUTABLE ? {executablePath:process.env.BROWSER_EXECUTABLE} : {})});
    const page = await browser.newPage(), errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/mock-bridge.js', async route => {
      const response = await route.fetch(), source = await response.text();
      assert.ok(source.includes('window.chrome.webview = {'));
      await route.fulfill({response, body:source.replace('window.chrome.webview = {',
        'window.recoveryDeliver = emit; window.chrome.webview = {')});
    });
    await page.goto(address + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(data => {
      window.recoveryStage = 'owned'; window.recoverySubmissions = [];
      const original = chrome.webview.postMessage;
      chrome.webview.postMessage = request => {
        if (request.type === 'getProjectChat' && request.payload.workspaceId === 'preview-data-workspace') {
          const source = data[recoveryStage];
          const envelope = JSON.parse(JSON.stringify(source), (key, value) =>
            value === source.payload.sessionId ? request.payload.sessionId :
            value === source.payload.project.workspaceId ? request.payload.workspaceId : value);
          envelope.id = request.id;
          recoveryDeliver(envelope);
        } else if (request.type === 'submitProjectChat') recoverySubmissions.push(request);
        else original(request);
      };
      window.recoveryCanFork = () => {
        const snapshot = RNAssistantProjectChats.snapshot();
        return RNAssistantWorkspaceChat.canMessageAction('fork', snapshot.messages[0], 0);
      };
    }, fixture);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    await page.locator('#chatInput').fill('Продолжить после остановки');
    assert.equal(await page.locator('#sendButton').isEnabled(), false, 'occupied lease keeps submit blocked');
    assert.equal(await page.evaluate(() => recoveryCanFork()), false, 'occupied lease keeps fork blocked');
    await page.evaluate(() => { recoveryStage = 'recovered'; });
    await page.waitForFunction(() => RNAssistantProjectChats.snapshot()?.state.Status === 'interrupted');
    assert.equal(await page.locator('#chatInput').inputValue(), 'Продолжить после остановки', 'recovery preserves the draft');
    assert.equal(await page.locator('#sendButton').isEnabled(), true, 'canonical recovery unlocks input');
    assert.equal(await page.evaluate(() => recoveryCanFork()), true, 'canonical recovery unlocks fork');
    assert.doesNotMatch(await page.locator('#workspaceChatNotice').textContent(), /Агент работает/);
    const count = await page.evaluate(() => RNAssistantProjectChats.snapshot().messages.length);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.snapshot().messages.length), count, 'reopening preserves one recovered history');
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => recoverySubmissions.length === 1);
    assert.equal(await page.evaluate(() => recoverySubmissions[0].payload.input.message), 'Продолжить после остановки');
    assert.deepEqual(errors, []);
    console.log('PASS workspace recovery: native bridge state, occupied lease, polling recovery, retained draft, input and fork');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => {console.error(error); process.exitCode = 1;});
