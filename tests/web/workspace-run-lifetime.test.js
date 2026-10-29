'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
// Exported by the native Application/bridge check with a held HTTP response.
const fixture = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
assert.equal(fixture.terminal.payload.state.Status, 'running');
assert.equal(fixture.completed.state.Status, 'completed');

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
        'window.lifetimeDeliver = emit; window.chrome.webview = {')});
    });
    await page.goto(address + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(data => {
      window.lifetimeRequest = null; window.lifetimeCompleted = false;
      window.lifetimeTitle = false; window.lifetimePolls = 0;
      const original = chrome.webview.postMessage;
      function snapshot() {
        const native = lifetimeCompleted ? data.completed : data.terminal.payload;
        const result = JSON.parse(JSON.stringify(native), (key, value) =>
          value === native.sessionId ? lifetimeRequest.payload.sessionId :
          value === native.project.workspaceId ? lifetimeRequest.payload.workspaceId : value);
        if (result.inputReceipt) result.inputReceipt.RequestId = lifetimeRequest.payload.input.requestId;
        if (lifetimeTitle) {
          result.state.Title = 'Название после завершения';
          result.state.Revision = (result.state.Revision || result.state.revision || 0) + 1;
          result.state.Cursor = 'title-notification';
        }
        return result;
      }
      chrome.webview.postMessage = request => {
        if (request.type === 'submitProjectChat') {
          lifetimeRequest = request;
          lifetimeDeliver({id:request.id, ok:true, payload:snapshot()});
        } else if (request.type === 'getProjectChat' && lifetimeRequest &&
          request.payload.sessionId === lifetimeRequest.payload.sessionId) {
          lifetimePolls++;
          lifetimeDeliver({id:request.id, ok:true, payload:snapshot()});
        } else original(request);
      };
    }, fixture);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    await page.locator('#chatInput').fill('Долгий запрос');
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => lifetimeRequest && !state.pending[lifetimeRequest.id] && !RNAssistantProjectChats.isBusy());
    assert.equal(await page.locator('#chatInput').inputValue(), '', 'durable admission clears the submitted draft');
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.snapshot().state.Status), 'running');
    await page.locator('#chatInput').fill('Новый черновик');
    assert.equal(await page.locator('#sendButton').isEnabled(), true,
      'the current run still accepts an exact-run clarification');
    const readsAfterAdmission = await page.evaluate(() => lifetimePolls);
    await page.waitForTimeout(1500);
    assert.equal(await page.evaluate(() => lifetimePolls), readsAfterAdmission,
      'a locally observed run does not reread the entire chat every 1.2 seconds');
    assert.equal(await page.evaluate(() => Object.values(state.pending).filter(item => item.type === 'submitProjectChat').length), 0,
      'model wait has no outstanding submit request or its idle timeout');
    await page.evaluate(() => {
      lifetimeCompleted = true;
      const snapshot = RNAssistantProjectChats.snapshot();
      lifetimeDeliver({type:'progress', id:lifetimeRequest.id, payload:{
        workspaceId:lifetimeRequest.payload.workspaceId,
        sessionId:lifetimeRequest.payload.sessionId,
        runId:snapshot.state.RunId || snapshot.state.runId,
        phase:'session_changed', cursor:'terminal-notification'
      }});
    });
    await page.waitForFunction(() => RNAssistantProjectChats.snapshot().state.Status === 'completed');
    assert.equal(await page.locator('#chatInput').inputValue(), 'Новый черновик', 'terminal notification leaves a newer draft alone');
    assert.equal(await page.locator('#sendButton').isEnabled(), true, 'terminal state unlocks submission');
    const readsBeforeTitle = await page.evaluate(() => lifetimePolls);
    await page.evaluate(() => {
      lifetimeTitle = true;
      const current = RNAssistantProjectChats.snapshot();
      lifetimeDeliver({type:'progress', id:lifetimeRequest.id, payload:{
        workspaceId:lifetimeRequest.payload.workspaceId,
        sessionId:lifetimeRequest.payload.sessionId,
        runId:current.state.RunId || current.state.runId,
        phase:'session_changed', cursor:'title-notification'
      }});
    });
    await page.locator('#projectChatTitle').getByText('Название после завершения').waitFor();
    assert.equal(await page.evaluate(() => lifetimePolls), readsBeforeTitle + 1,
      'durable title notice refreshes the selected idle chat once');
    await page.waitForTimeout(2200);
    assert.equal(await page.evaluate(() => lifetimePolls), readsBeforeTitle + 1,
      'idle title update leaves no full-chat title polling timer');
    assert.deepEqual(errors, []);
    console.log('PASS workspace run lifetime: native admission, push completion/title without full polling and draft retention');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => {console.error(error); process.exitCode = 1;});
