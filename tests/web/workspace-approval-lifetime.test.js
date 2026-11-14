'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const {withPreviewPage} = require('./preview-session');
const fixture = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
assert.ok(fixture.waiting.pendingId);
assert.equal(fixture.admitted.payload.state.Status, 'running');
assert.equal(fixture.completed.state.Status, 'completed');

withPreviewPage(async ({page, errors, chatNode}) => {
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
          RNAssistantMockBridge.emit({id:request.id, ok:true, payload:snapshot()});
        } else if (request.type === 'getProjectChat' && request.payload.sessionId === 'preview-project-review') {
          if (approvalRequest) approvalPolls++;
          RNAssistantMockBridge.emit({id:request.id, ok:true, payload:snapshot()});
        } else original(request);
      };
    }, fixture);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    await chatNode('agentApprovalDock').getByRole('button', {name:'Подтвердить', exact:true}).waitFor({state:'visible'});
    await chatNode('chatInput').fill('Следующая задача');
    await chatNode('agentApprovalDock').getByRole('button', {name:'Подтвердить', exact:true}).click();
    await page.waitForFunction(() => approvalRequest && !state.pending[approvalRequest.id] && !RNAssistantProjectChats.isBusy());
    assert.equal(await page.evaluate(() => approvalRequest.payload.pendingId), fixture.waiting.pendingId);
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.snapshot().state.Status), 'running');
    assert.equal(await chatNode('agentApprovalDock').isVisible(), false, 'consumed approval is removed');
    assert.equal(await chatNode('chatInput').inputValue(), 'Следующая задача', 'approval never clears the draft');
    assert.equal(await chatNode('sendButton').isEnabled(), true,
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
      RNAssistantMockBridge.emit({type:'progress', id:approvalRequest.id, payload:{
        workspaceId:approvalRequest.payload.workspaceId,
        sessionId:approvalRequest.payload.sessionId,
        runId:snapshot.state.RunId || snapshot.state.runId,
        phase:'session_changed', cursor:'approval-terminal-notification'
      }});
    });
    await page.waitForFunction(() => RNAssistantProjectChats.snapshot().state.Status === 'completed');
    assert.equal(await chatNode('sendButton').isEnabled(), true);
    assert.equal(await chatNode('chatInput').inputValue(), 'Следующая задача');
    assert.deepEqual(errors, []);
    console.log('PASS workspace approval lifetime: native admission, push completion and draft retention');
}).catch(error => {console.error(error); process.exitCode = 1;});
