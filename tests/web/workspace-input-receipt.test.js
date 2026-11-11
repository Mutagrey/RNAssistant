'use strict';
// Consume envelopes exported by the actual Application/WebView bridge Harness test.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const {withPreviewPage} = require('./preview-session');
const fixture = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
assert.equal(fixture.accepted.payload.inputReceipt.DeliveryRecorded, false);
assert.equal(fixture.applied.payload.inputReceipt.DeliveryRecorded, true);

withPreviewPage(async ({page, errors, chatNode}) => {
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
        RNAssistantMockBridge.emit({id:request.id, ok:true, payload:snapshot});
      };
    }, fixture);
    let submissions = 0;
    async function submit(text) {
      await chatNode('chatInput').fill(text);
      await chatNode('chatInput').press('Enter');
      await page.waitForFunction(n => receiptRequests.length === n, ++submissions);
    }
    async function settled() {
      await page.waitForFunction(() => !state.pending[receiptRequests[receiptRequests.length - 1].id] && !RNAssistantProjectChats.isBusy());
    }
    async function openPluginChat() {
      await page.evaluate(async () => {
        const snapshot = await send('getPluginChat', {pluginId:'data-templates'});
        RNAssistantConversationPanel.open({source:'workspace', workspaceId:snapshot.project.workspaceId, sessionId:snapshot.sessionId});
      });
      await page.waitForFunction(() => RNAssistantWorkspaceChat.owner() === 'plugin' && RNAssistantProjectChats.snapshot()?.sessionId);
    }

    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    await submit('Первый ввод');
    for (const [stage, changes] of [['accepted', {}], ['applied', {SessionId:'foreign'}],
      ['applied', {RequestId:'foreign'}]]) {
      await page.evaluate(([stage, changes]) => RNAssistantMockBridge.emit(receiptEnvelope(stage, changes)), [stage, changes]);
      assert.equal(await chatNode('chatInput').inputValue(), 'Первый ввод', 'only matching history delivery clears input');
    }
    await page.evaluate(() => RNAssistantMockBridge.emit(receiptEnvelope('applied')));
    assert.equal(await chatNode('chatInput').inputValue(), '', 'input clears while submit still awaits the model');
    assert.equal(await page.evaluate(() => !!state.pending[receiptRequests[0].id]), true);
    await chatNode('chatInput').fill('Новый ввод');
    await chatNode('chatInput').fill('Первый ввод');
    await page.evaluate(() => RNAssistantMockBridge.emit(receiptEnvelope('applied')));
    await page.evaluate(() => receiptFinish(true)); await settled();
    assert.equal(await chatNode('chatInput').inputValue(), 'Первый ввод', 'repeated/terminal ack cannot erase a newer identical draft');

    await openPluginChat();
    await submit('Фоновый ввод');
    const target = await page.evaluate(() => receiptRequests[receiptRequests.length - 1].payload);
    await page.evaluate(() => RNAssistantConversationPanel.open({source:'office', sessionId:'preview-chat'}));
    await chatNode('chatInput').fill('Черновик Office');
    await page.evaluate(() => RNAssistantMockBridge.emit(receiptEnvelope('applied')));
    assert.equal(await chatNode('chatInput').inputValue(), 'Черновик Office', 'background receipt leaves Office composer alone');
    assert.equal(await page.evaluate(target => RNAssistantChatDrafts.readWorkspace(target).text, target), '', 'background source draft is acknowledged');
    await page.evaluate(() => receiptFinish(true)); await settled();
    await openPluginChat();
    assert.equal(await chatNode('chatInput').inputValue(), '', 'returning to the source shows its cleared draft');

    await submit('Неподтверждённая доставка');
    const requestId = await page.evaluate(() => receiptRequests[receiptRequests.length - 1].payload.input.requestId);
    await page.evaluate(() => receiptFinish(false)); await settled();
    assert.equal(await chatNode('chatInput').inputValue(), 'Неподтверждённая доставка');
    assert.match(await chatNode('inboxStatus').textContent(), /Доставка сообщения не подтверждена/);
    await submit('Неподтверждённая доставка');
    assert.equal(await page.evaluate(() => receiptRequests[receiptRequests.length - 1].payload.input.requestId), requestId);
    await page.evaluate(() => receiptFinish(true)); await settled();
    assert.equal(await chatNode('chatInput').inputValue(), '', 'terminal receipt recovers a lost progress event');
    assert.deepEqual(errors, []);
    console.log('PASS workspace receipts: real bridge envelopes, early delivery, identity guards, background Office, newer draft and lost progress recovery');
}).catch(error => {console.error(error); process.exitCode = 1;});
