'use strict';
// Consume envelopes exported by the actual Application/WebView bridge Harness test.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
const fixture = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
assert.equal(fixture.accepted.payload.inputReceipt.DeliveryRecorded, false);
assert.equal(fixture.applied.payload.inputReceipt.DeliveryRecorded, true);

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
        'window.receiptDeliver = emit; window.chrome.webview = {')});
    });
    await page.goto(address + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(data => {
      window.receiptFixture = data; window.receiptRequests = [];
      const original = chrome.webview.postMessage;
      chrome.webview.postMessage = request => {
        if (request.type === 'submitProjectChat') receiptRequests.push(request);
        else original(request);
      };
      window.receiptEnvelope = (stage, changes = {}) => {
        const request = receiptRequests[receiptRequests.length - 1];
        const event = JSON.parse(JSON.stringify(receiptFixture[stage]));
        event.id = request.id;
        event.payload.workspaceId = request.payload.workspaceId;
        event.payload.sessionId = request.payload.sessionId;
        Object.assign(event.payload.inputReceipt, {SessionId:request.payload.sessionId,
          RequestId:request.payload.input.requestId}, changes);
        return event;
      };
      window.receiptFinish = async delivered => {
        const request = receiptRequests[receiptRequests.length - 1];
        const snapshot = await send('getProjectChat', request.payload);
        snapshot.inputReceipt = receiptEnvelope(delivered ? 'applied' : 'accepted').payload.inputReceipt;
        receiptDeliver({id:request.id, ok:true, payload:snapshot});
      };
    }, fixture);
    let submissions = 0;
    async function submit(text) {
      await page.locator('#chatInput').fill(text);
      await page.locator('#chatInput').press('Enter');
      await page.waitForFunction(n => receiptRequests.length === n, ++submissions);
    }
    async function settled() {
      await page.waitForFunction(() => !state.pending[receiptRequests[receiptRequests.length - 1].id] && !RNAssistantProjectChats.isBusy());
    }

    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    await submit('Первый ввод');
    for (const [stage, changes] of [['accepted', {}], ['applied', {SessionId:'foreign'}],
      ['applied', {RequestId:'foreign'}]]) {
      await page.evaluate(([stage, changes]) => receiptDeliver(receiptEnvelope(stage, changes)), [stage, changes]);
      assert.equal(await page.locator('#chatInput').inputValue(), 'Первый ввод', 'only matching history delivery clears input');
    }
    await page.evaluate(() => receiptDeliver(receiptEnvelope('applied')));
    assert.equal(await page.locator('#chatInput').inputValue(), '', 'input clears while submit still awaits the model');
    assert.equal(await page.evaluate(() => !!state.pending[receiptRequests[0].id]), true);
    await page.locator('#chatInput').fill('Новый ввод');
    await page.locator('#chatInput').fill('Первый ввод');
    await page.evaluate(() => receiptDeliver(receiptEnvelope('applied')));
    await page.evaluate(() => receiptFinish(true)); await settled();
    assert.equal(await page.locator('#chatInput').inputValue(), 'Первый ввод', 'repeated/terminal ack cannot erase a newer identical draft');

    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    await page.waitForFunction(() => RNAssistantWorkspaceChat.owner() === 'plugin' && RNAssistantProjectChats.snapshot()?.sessionId);
    await submit('Фоновый ввод');
    const target = await page.evaluate(() => receiptRequests[receiptRequests.length - 1].payload);
    await page.evaluate(() => RNAssistantProjectChats.setScope('office'));
    await page.locator('#chatInput').fill('Черновик Office');
    await page.evaluate(() => receiptDeliver(receiptEnvelope('applied')));
    assert.equal(await page.locator('#chatInput').inputValue(), 'Черновик Office', 'background receipt leaves Office composer alone');
    assert.equal(await page.evaluate(target => RNAssistantChatDrafts.readWorkspace(target).text, target), '', 'background source draft is acknowledged');
    await page.evaluate(() => receiptFinish(true)); await settled();
    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    await page.waitForFunction(() => RNAssistantWorkspaceChat.owner() === 'plugin');
    assert.equal(await page.locator('#chatInput').inputValue(), '', 'returning to the source shows its cleared draft');

    await submit('Неподтверждённая доставка');
    const requestId = await page.evaluate(() => receiptRequests[receiptRequests.length - 1].payload.input.requestId);
    await page.evaluate(() => receiptFinish(false)); await settled();
    assert.equal(await page.locator('#chatInput').inputValue(), 'Неподтверждённая доставка');
    assert.match(await page.locator('#workspaceChatNotice').textContent(), /Доставка сообщения не подтверждена/);
    await submit('Неподтверждённая доставка');
    assert.equal(await page.evaluate(() => receiptRequests[receiptRequests.length - 1].payload.input.requestId), requestId);
    await page.evaluate(() => receiptFinish(true)); await settled();
    assert.equal(await page.locator('#chatInput').inputValue(), '', 'terminal receipt recovers a lost progress event');
    assert.deepEqual(errors, []);
    console.log('PASS workspace receipts: real bridge envelopes, early delivery, identity guards, background Office, newer draft and lost progress recovery');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => {console.error(error); process.exitCode = 1;});
