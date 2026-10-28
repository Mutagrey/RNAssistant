'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
const fixture = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
assert.ok(fixture.waiting.pendingId);
assert.equal(fixture.admitted.payload.state.Status, 'running');
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
        'window.approvalDeliver = emit; window.chrome.webview = {')});
    });
    await page.goto(address + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(data => {
      window.approvalRequest = null; window.approvalCompleted = false; window.approvalPolls = 0;
      const original = chrome.webview.postMessage;
      function snapshot() {
        const native = approvalCompleted ? data.completed : approvalRequest ? data.admitted.payload : data.waiting;
        return JSON.parse(JSON.stringify(native), (key, value) =>
          value === native.sessionId ? 'preview-project-review' :
          value === native.project.workspaceId ? 'preview-data-workspace' : value);
      }
      chrome.webview.postMessage = request => {
        if (request.type === 'resolveProjectChat') {
          if (approvalRequest) throw Error('Duplicate approval dispatch');
          approvalRequest = request;
          approvalDeliver({id:request.id, ok:true, payload:snapshot()});
        } else if (request.type === 'getProjectChat' && request.payload.sessionId === 'preview-project-review') {
          if (approvalRequest) approvalPolls++;
          approvalDeliver({id:request.id, ok:true, payload:snapshot()});
        } else original(request);
      };
    }, fixture);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    await page.locator('#workspaceChatApprove').waitFor({state:'visible'});
    await page.locator('#chatInput').fill('Следующая задача');
    await page.locator('#workspaceChatApprove').click();
    await page.waitForFunction(() => approvalRequest && !state.pending[approvalRequest.id] && !RNAssistantProjectChats.isBusy());
    assert.equal(await page.evaluate(() => approvalRequest.payload.pendingId), fixture.waiting.pendingId);
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.snapshot().state.Status), 'running');
    assert.equal(await page.locator('#workspaceChatControl').isVisible(), false, 'consumed approval is removed');
    assert.equal(await page.locator('#chatInput').inputValue(), 'Следующая задача', 'approval never clears the draft');
    assert.equal(await page.locator('#sendButton').isEnabled(), true,
      'the admitted continuation still accepts an exact-run clarification');
    const readsAfterAdmission = await page.evaluate(() => approvalPolls);
    await page.waitForTimeout(1500);
    assert.equal(await page.evaluate(() => approvalPolls), readsAfterAdmission,
      'approval continuation does not reread the entire chat every 1.2 seconds');
    assert.equal(await page.evaluate(() => Object.values(state.pending).filter(item => item.type === 'resolveProjectChat').length), 0,
      'approval has no pending bridge request during model wait');
    await page.evaluate(() => {
      approvalCompleted = true;
      const snapshot = RNAssistantProjectChats.snapshot();
      approvalDeliver({type:'progress', id:approvalRequest.id, payload:{
        workspaceId:approvalRequest.payload.workspaceId,
        sessionId:approvalRequest.payload.sessionId,
        runId:snapshot.state.RunId || snapshot.state.runId,
        phase:'session_changed', cursor:'approval-terminal-notification'
      }});
    });
    await page.waitForFunction(() => RNAssistantProjectChats.snapshot().state.Status === 'completed');
    assert.equal(await page.locator('#sendButton').isEnabled(), true);
    assert.equal(await page.locator('#chatInput').inputValue(), 'Следующая задача');
    assert.deepEqual(errors, []);
    console.log('PASS workspace approval lifetime: native admission, push completion and draft retention');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => {console.error(error); process.exitCode = 1;});
