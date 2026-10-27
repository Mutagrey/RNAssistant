'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
const fixture = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
assert.equal(fixture.accepted.payload.inputReceipt.DeliveryRecorded, false);
assert.equal(fixture.applied.payload.inputReceipt.DeliveryRecorded, true);
assert.equal(fixture.unknown.payload.inputReceipt, undefined);
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
        'window.reconnectDeliver = emit; window.chrome.webview = {')});
    });
    await page.goto(address + 'preview/web/?scenario=ready');
    async function install(mode) {
      await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise).catch(async error => {
        error.message += JSON.stringify({errors, state:await page.evaluate(() => ({active:state.activeChatId,
          initializing:!!state.initializePromise, unavailable:state.bridgeUnavailable,
          pending:Object.values(state.pending).map(item => item.type)}))});
        throw error;
      });
      await page.evaluate(({data, target, mode}) => {
        window.reconnect = {mode, reads:[], submits:[], saves:[], held:[], target};
        const original = chrome.webview.postMessage;
        reconnect.reply = request => {
          const stage = !request.payload.receiptRequestId ? 'unknown' : reconnect.mode === 'foreign' ? 'applied' : reconnect.mode;
          const native = data[stage === 'hold' ? 'applied' : stage].payload;
          const snapshot = JSON.parse(JSON.stringify(native), (key, value) =>
            value === native.sessionId ? target.sessionId : value === native.project.workspaceId ? target.workspaceId : value);
          if (snapshot.inputReceipt) {
            snapshot.inputReceipt.RequestId = request.payload.receiptRequestId;
            if (reconnect.mode === 'foreign') snapshot.inputReceipt.SessionId = 'foreign';
          }
          reconnectDeliver({id:request.id, ok:true, payload:snapshot});
        };
        chrome.webview.postMessage = request => {
          if (request.type === 'saveChatDraft') reconnect.saves.push(JSON.parse(JSON.stringify(request.payload)));
          if (request.type === 'submitProjectChat') {
            reconnect.submits.push(request); // Deliberately lose the reply.
            if (reconnect.progressReceipt) reconnectDeliver({id:request.id, type:'progress', payload:{
              workspaceId:target.workspaceId, sessionId:target.sessionId, phase:'input_receipt',
              inputReceipt:Object.assign({}, data.applied.payload.inputReceipt,
                {SessionId:target.sessionId, RequestId:request.payload.input.requestId}) }});
          }
          else if (request.type === 'getProjectChat' && request.payload.workspaceId === target.workspaceId &&
            request.payload.sessionId === target.sessionId) {
            reconnect.reads.push(request);
            if (reconnect.mode === 'hold') reconnect.held.push(request);
            else reconnect.reply(request);
          } else original(request);
        };
      }, {data:fixture, target, mode});
    }
    async function open() { await page.evaluate(t => RNAssistantProjectChats.open(t.workspaceId, t.sessionId), target); }
    async function prepare(edit) {
      return page.evaluate(async ({target, edit}) => {
        const input = RNAssistantChatDrafts.prepareWorkspaceInput(target, 'Received input',
          Object.assign({Mode:'agent', ReasoningEnabled:false}, edit || {}));
        await RNAssistantChatDrafts.flushWorkspace(target);
        return input.requestId;
      }, {target, edit});
    }
    async function reload(mode) { await page.reload(); await install(mode); await open(); }
    async function pending() { return page.evaluate(t => RNAssistantChatDrafts.pendingWorkspaceInput(t), target); }

    await install('unknown'); await open();
    await page.locator('#chatInput').fill('Received input');
    const originalId = await prepare();
    await reload('accepted');
    assert.equal((await pending()).input.requestId, originalId);
    assert.equal(await page.locator('#chatInput').inputValue(), 'Received input', 'admission alone preserves the restored draft');
    await page.evaluate(() => { reconnect.mode = 'unknown'; }); await open();
    assert.equal((await pending()).input.requestId, originalId, 'unknown receipt does not erase retry identity');
    await page.evaluate(() => { reconnect.mode = 'foreign'; }); await open();
    assert.equal(await page.locator('#chatInput').inputValue(), 'Received input', 'foreign receipt cannot clear this draft');
    await page.evaluate(() => { reconnect.mode = 'applied'; reconnect.saves = []; }); await open();
    assert.equal(await page.locator('#chatInput').inputValue(), '', 'reconnect clears the exact submitted version');
    assert.equal(await pending(), null);
    await page.evaluate(t => RNAssistantChatDrafts.flushWorkspace(t), target);
    assert.equal(await page.evaluate(() => reconnect.saves.some(item => item.Owner === 'workspace' &&
      item.Text === 'Received input' && !item.PendingInput)), false, 'acknowledgement and clearing are saved atomically');
    assert.equal(await page.evaluate(() => reconnect.submits.length), 0, 'reconnect is read-only');
    await reload('applied');
    assert.equal(await page.locator('#chatInput').inputValue(), '', 'acknowledgement survives the next reload');
    assert.equal(await page.evaluate(() => reconnect.reads.some(item => item.payload.receiptRequestId)), false,
      'resolved receipts are not queried repeatedly');

    await page.locator('#chatInput').fill('Received input'); await prepare();
    await page.locator('#chatInput').fill('New draft');
    await page.locator('#chatInput').fill('Received input');
    await page.evaluate(t => RNAssistantChatDrafts.flushWorkspace(t), target);
    await reload('applied');
    assert.equal(await page.locator('#chatInput').inputValue(), 'Received input', 'a newer identical draft survives reload and receipt');
    assert.equal(await pending(), null, 'the old input is acknowledged independently of the newer draft');

    await page.locator('#chatInput').fill('Unsent base draft');
    await prepare({EditMessageId:'original-user-message', ExpectedRevision:4});
    await reload('applied');
    assert.equal(await page.locator('#chatInput').inputValue(), 'Unsent base draft', 'recovered edit receipt preserves the base composer draft');
    assert.equal(await pending(), null);

    await page.locator('#chatInput').fill('Received input'); await prepare();
    await page.evaluate(t => { reconnect.mode = 'hold'; window.reconnecting = RNAssistantProjectChats.open(t.workspaceId, t.sessionId); }, target);
    await page.waitForFunction(() => reconnect.held.length > 0);
    await page.evaluate(() => RNAssistantProjectChats.setScope('office'));
    await page.locator('#chatInput').fill('Office draft');
    await page.evaluate(async () => { reconnect.mode = 'applied'; reconnect.held.forEach(reconnect.reply); await reconnecting; });
    assert.equal(await page.locator('#chatInput').inputValue(), 'Office draft', 'late reconnect leaves the selected Office composer alone');
    assert.equal(await page.evaluate(t => RNAssistantChatDrafts.readWorkspace(t).text, target), '', 'background receipt clears only its own draft');

    await open();
    await page.locator('#chatInput').fill('Received input');
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => reconnect.submits.length === 1);
    await page.waitForFunction(() => !RNAssistantProjectChats.isBusy() &&
      !state.pending[reconnect.submits[0].id]);
    assert.equal(await page.locator('#chatInput').inputValue(), '', 'polling recovers when the original bridge response never arrives');
    assert.equal(await pending(), null);
    assert.equal(await page.evaluate(() => reconnect.submits.length), 1, 'no automatic resubmit or second run');
    await page.locator('#chatInput').fill('Next task');
    assert.equal(await page.locator('#sendButton').isEnabled(), true, 'known receipt removes the stale transport busy state');
    await page.evaluate(() => { reconnect.progressReceipt = true; });
    await page.locator('#chatInput').fill('Received input'); await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => reconnect.submits.length === 2 && !document.querySelector('#chatInput').value);
    await page.locator('#chatInput').fill('Newer draft after progress');
    await page.waitForFunction(() => !RNAssistantProjectChats.isBusy() && !state.pending[reconnect.submits[1].id]);
    assert.equal(await page.locator('#chatInput').inputValue(), 'Newer draft after progress',
      'receipt lookup also settles a lost reply after progress already removed the retry record');
    assert.deepEqual(errors, []);
    console.log('PASS workspace receipt reconnect: native query, reload, identical later drafts, edit/Office isolation and lost reply recovery without resubmit');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => {console.error(error); process.exitCode = 1;});
