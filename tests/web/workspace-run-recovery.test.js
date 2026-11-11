'use strict';
// Native envelopes come from the lease/recovery Application + bridge Harness check.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const {withPreviewPage} = require('./preview-session');
const fixture = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
assert.equal(fixture.owned.payload.state.Status, 'running');
assert.equal(fixture.recovered.payload.state.Status, 'interrupted');

withPreviewPage(async ({page, errors, chatNode}) => {
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
          RNAssistantMockBridge.emit(envelope);
        } else if (request.type === 'submitProjectChat') recoverySubmissions.push(request);
        else original(request);
      };
      window.recoveryCanFork = () => {
        const snapshot = RNAssistantProjectChats.snapshot();
        return RNAssistantConversationPanel.selected().composer.capture().invoke('canMessageAction',
          {name:'fork', message:snapshot.messages[0], index:0});
      };
    }, fixture);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    await chatNode('chatInput').fill('Продолжить после остановки');
    assert.equal(await chatNode('sendButton').isEnabled(), false, 'occupied lease keeps submit blocked');
    assert.equal(await page.evaluate(() => recoveryCanFork()), false, 'occupied lease keeps fork blocked');
    await page.evaluate(() => { recoveryStage = 'recovered'; });
    await page.waitForFunction(() => RNAssistantProjectChats.snapshot()?.state.Status === 'interrupted');
    assert.equal(await chatNode('chatInput').inputValue(), 'Продолжить после остановки', 'recovery preserves the draft');
    assert.equal(await chatNode('sendButton').isEnabled(), true, 'canonical recovery unlocks input');
    assert.equal(await page.evaluate(() => recoveryCanFork()), true, 'canonical recovery unlocks fork');
    assert.doesNotMatch(await chatNode('inboxStatus').textContent(), /Агент работает/);
    const count = await page.evaluate(() => RNAssistantProjectChats.snapshot().messages.length);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.snapshot().messages.length), count, 'reopening preserves one recovered history');
    await chatNode('chatInput').press('Enter');
    await page.waitForFunction(() => recoverySubmissions.length === 1);
    assert.equal(await page.evaluate(() => recoverySubmissions[0].payload.input.message), 'Продолжить после остановки');
    assert.deepEqual(errors, []);
    console.log('PASS workspace recovery: native bridge state, occupied lease, polling recovery, retained draft, input and fork');
}).catch(error => {console.error(error); process.exitCode = 1;});
