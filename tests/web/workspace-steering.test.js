'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
const fixture = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
assert.equal(fixture.accepted.payload.inputReceipt.Steering, true);
assert.equal(fixture.accepted.payload.inputReceipt.DeliveryRecorded, false);
const target = {workspaceId:'preview-data-workspace', sessionId:'preview-project-review'};

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
        'window.steerDeliver = emit; window.chrome.webview = {')});
    });
    await page.goto(address + 'preview/web/?scenario=ready');
    async function install(stage) {
      await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
      await page.evaluate(({fixture, target, stage}) => {
        window.steer = {fixture, target, stage, requests:[], receipts:false};
        const original = chrome.webview.postMessage;
        steer.snapshot = (stage, receiptId) => {
          const source = stage === 'accepted' ? fixture.accepted.payload : fixture[stage];
          const snapshot = JSON.parse(JSON.stringify(source), (key, value) =>
            value === source.sessionId ? target.sessionId : value === source.project.workspaceId ? target.workspaceId : value);
          delete snapshot.inputReceipt;
          if (receiptId && steer.receipts) snapshot.inputReceipt = Object.assign({}, fixture.accepted.payload.inputReceipt,
            {SessionId:target.sessionId, RequestId:receiptId});
          return snapshot;
        };
        chrome.webview.postMessage = request => {
          if (request.type === 'submitProjectChat') { steer.requests.push(request); return; }
          if (request.type === 'getProjectChat' && request.payload.workspaceId === target.workspaceId && request.payload.sessionId === target.sessionId) {
            steerDeliver({id:request.id, ok:true, payload:steer.snapshot(steer.stage, request.payload.receiptRequestId)}); return;
          }
          original(request);
        };
        steer.ack = () => {
          const request = steer.requests[steer.requests.length - 1];
          steer.stage = 'accepted'; steer.receipts = true;
          steerDeliver({id:request.id, ok:true, payload:steer.snapshot('accepted', request.payload.input.requestId)});
        };
      }, {fixture, target, stage});
    }
    async function open() { await page.evaluate(t => RNAssistantProjectChats.open(t.workspaceId, t.sessionId), target); }
    async function submit(text) {
      await page.locator('#chatInput').fill(text);
      assert.equal(await page.locator('#sendButton').isDisabled(), false, 'active workspace accepts text steering');
      await page.locator('#chatInput').press('Enter');
      await page.waitForFunction(() => steer.requests.length === 1);
    }
    async function settled() {
      await page.waitForFunction(() => !RNAssistantProjectChats.isBusy() &&
        !Object.values(state.pending).some(item => item.type === 'submitProjectChat'));
    }
    await install('waiting'); await open();
    await submit('STEER_UNIQUE_MESSAGE');
    assert.equal(await page.evaluate(() => steer.requests[0].payload.input.SteerRunId), fixture.waiting.state.RunId);
    await page.evaluate(() => {
      const request = steer.requests[0];
      steerDeliver({id:request.id, type:'progress', payload:{workspaceId:steer.target.workspaceId, sessionId:steer.target.sessionId,
        inputReceipt:Object.assign({}, steer.fixture.accepted.payload.inputReceipt, {
          SessionId:steer.target.sessionId, RequestId:request.payload.input.requestId, RunId:'foreign'})}});
    });
    assert.equal(await page.locator('#chatInput').inputValue(), 'STEER_UNIQUE_MESSAGE', 'foreign run cannot acknowledge steering');
    await page.evaluate(() => steer.ack()); await settled();
    assert.equal(await page.locator('#chatInput').inputValue(), '', 'durable intake clears its draft without waiting for the tool');
    assert.equal(await page.locator('[data-input-id]').count(), 1);
    assert.match(await page.locator('[data-input-id]').textContent(), /Уточнение сохранено, но ещё не применено/);
    assert.equal(await page.locator('#stopButton').isVisible(), true, 'stop remains independent');
    await page.evaluate(() => { steer.stage = 'completed'; }); await open();
    assert.equal(await page.locator('[data-input-id]').count(), 0, 'delivered input replaces pending projection');

    // Restored uncertain submission keeps its exact run and is observed without resubmitting.
    await page.evaluate(() => { steer.stage = 'waiting'; }); await open();
    await page.locator('#chatInput').fill('STEER_UNIQUE_MESSAGE');
    const saved = await page.evaluate(async target => {
      const request = RNAssistantChatDrafts.prepareWorkspaceInput(target, 'STEER_UNIQUE_MESSAGE', {
        Mode:'agent', ReasoningEnabled:false, SteerRunId:steer.fixture.waiting.state.RunId});
      await RNAssistantChatDrafts.flushWorkspace(target); return request;
    }, target);
    await page.reload(); await install('accepted');
    await page.evaluate(() => { steer.receipts = true; }); await open();
    assert.equal(await page.locator('#chatInput').inputValue(), '', 'lost acknowledgement recovered after reload');
    assert.equal(await page.evaluate(() => steer.requests.length), 0, 'reconnect does not launch another request');
    assert.equal(await page.evaluate(t => RNAssistantChatDrafts.pendingWorkspaceInput(t), target), null);
    assert.equal(saved.SteerRunId, fixture.waiting.state.RunId);

    // A stopped, unapplied message remains inspectable and restoring it preserves another draft.
    await page.evaluate(() => { steer.stage = 'stopped'; }); await open();
    await page.locator('#chatInput').fill('Другой черновик');
    await page.locator('[data-input-id] button').click();
    assert.equal(await page.locator('#chatInput').inputValue(), 'Другой черновик\nSTEER_UNIQUE_MESSAGE');

    // Native acceptance from a background workspace never clears the Office composer.
    await page.reload(); await install('waiting'); await open();
    await submit('STEER_UNIQUE_MESSAGE');
    await page.evaluate(() => RNAssistantProjectChats.setScope('office'));
    await page.locator('#chatInput').fill('Черновик Office');
    await page.evaluate(() => steer.ack()); await settled();
    assert.equal(await page.locator('#chatInput').inputValue(), 'Черновик Office');
    assert.equal(await page.evaluate(t => RNAssistantChatDrafts.readWorkspace(t).text, target), '');
    assert.deepEqual(errors, []);
    console.log('PASS workspace steering: exact run, durable pending input, reload without replay, stop recovery, Office isolation');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => {console.error(error); process.exitCode = 1;});
