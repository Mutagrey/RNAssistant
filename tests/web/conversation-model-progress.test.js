'use strict';
const assert = require('node:assert/strict');
const {withPreviewPage} = require('./preview-session');
withPreviewPage(async ({page, errors}) => {
  page.setDefaultTimeout(7000);
  await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
  await page.evaluate(async () => {
    const office = await send('getChatState', {chatId:'preview-chat'});
    await RNAssistantProjectChats.open('preview-data-workspace','preview-project-review');
    const workspace = RNAssistantConversationPanel.selected().snapshot();
    const plugin = await send('getPluginChat', {pluginId:'data-templates'});
    window.progressTargets = [{source:'office',sessionId:'preview-chat'}, ...[workspace,plugin].map(s =>
      ({source:'workspace',workspaceId:s.project.workspaceId,sessionId:s.sessionId}))];
    window.nativePreviews = {};
    [office, workspace, plugin].forEach((source,index) => {
      const target = progressTargets[index], runId = 'preview-' + index;
      const runViewState = {RunId:runId, TurnId:'turn', Lifecycle:'running', ExecutionHealth:'clean',
        SuccessfulReads:0, VerifiedWrites:0, NoChangeWrites:0, UnverifiedWrites:0, FailedCalls:0, UnknownEffects:0};
      const modelProgress = {RunId:runId, StepId:'step', AttemptId:'attempt-' + index, Sequence:1, Revision:1,
        Message:'Полученный текст ' + index, Reason:'Основание ' + index, Decision:'Решение ' + index,
        Refusal:'Поле отказа ' + index, Reasoning:'Рассуждение ' + index, ReasonTruncated:true, TransportEnded:false};
      nativePreviews[target.sessionId] = target.source === 'office'
        ? {...source, messages:[], messageStartIndex:0, messageTotalCount:0, sessionRevision:100, runViewState, modelProgress,
          inbox:{...source.inbox, chatId:target.sessionId, revision:100, running:true, runId, items:[]}}
        : {...source, messages:[], messageStartIndex:0, messageTotalCount:0,
          state:{...source.state, revision:100, runId, status:'running', runViewState, modelProgress}};
    });
    const officeRead = loadChatState, workspaceRead = RNAssistantProjectChats.read;
    window.loadChatState = id => nativePreviews[id] ? Promise.resolve(structuredClone(nativePreviews[id])) : officeRead(id);
    RNAssistantProjectChats.read = (target,component) => nativePreviews[target.sessionId]
      ? Promise.resolve(structuredClone(nativePreviews[target.sessionId])) : workspaceRead(target,component);
    window.progressCancels = [];
    const originalSend = send;
    window.send = (type,payload) => { if (/cancel|stop/i.test(type)) progressCancels.push(type);
      if(type==='getChatInbox' && nativePreviews[payload.chatId]) return Promise.resolve(structuredClone(nativePreviews[payload.chatId].inbox));
      return originalSend(type,payload); };
    for (const target of progressTargets) {
      RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key(target));
      delete state.chatRuns[target.sessionId];
      await new Promise(resolve => setTimeout(resolve, 0));
      RNAssistantConversationPanel.open(target, {direction:"right"});
    }
  });
  await page.waitForFunction(() => progressTargets.every(t => {
    const p = RNAssistantConversationPanel.find(t);
    return p && !p.loading && p.node('messages').textContent.includes('Полученный текст');
  }));
  assert.deepEqual(await page.evaluate(() => progressTargets.map((t,index) => {
    const p = RNAssistantConversationPanel.find(t), text = p.node('messages').textContent;
    const preview = t.source === 'office' ? p.snapshot().liveRun.modelProgress : p.snapshot().state.modelProgress;
    return text.includes('Полученный текст '+index) && text.includes('Рассуждение '+index) &&
      ['Reason','Decision','Refusal'].every(key => preview[key]) &&
      !p.node('messages').querySelector('[data-stream-field]') &&
      !text.includes('Основание '+index) && !text.includes('Поле отказа '+index);
  })), [true,true,true], 'all fields survive delivery; ordinary live UI shows only the answer and reasoning');
  assert.equal(await page.evaluate(async () => {
    for (const target of progressTargets) {
      const p = RNAssistantConversationPanel.find(target), snapshot = nativePreviews[target.sessionId];
      const statePart = target.source === 'office' ? snapshot : snapshot.state;
      const frozen = {...statePart.modelProgress, Sequence:2, Revision:2, Message:statePart.modelProgress.Message+' — конец', TransportEnded:true};
      statePart.modelProgress = frozen;
      function deliver(preview) {
        const payload = {chatId:target.sessionId, workspaceId:target.workspaceId, sessionId:target.sessionId,
          runId:preview.RunId, phase:'model_progress', modelProgress:preview};
        if (target.source === 'office') applyOfficeChatProgress(payload,null,'inbox:'+target.sessionId);
        else p.progress(payload);
      }
      deliver(frozen);
      deliver({...frozen, Sequence:3, Revision:3, Message:'ПОЗДНИЙ ТЕКСТ', Reason:'ПОЗДНЯЯ ПРИЧИНА'});
    }
    await new Promise(requestAnimationFrame);
    return progressTargets.every(t => {
      const text = RNAssistantConversationPanel.find(t).node('messages').textContent;
      return text.includes('— конец') && !text.includes('ПОЗДНИЙ');
    });
  }), true, 'transport-ended freezes message and other visible fields for every consumer');
  await page.evaluate(async () => {
    for (const target of progressTargets) {
      RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key(target));
      delete state.chatRuns[target.sessionId];
      await new Promise(resolve => setTimeout(resolve, 0));
      RNAssistantConversationPanel.open(target, {direction:"right"});
    }
  });
  await page.waitForFunction(() => progressTargets.every(t => RNAssistantConversationPanel.find(t)?.node('messages').textContent.includes('— конец')));
  await page.evaluate(async () => {
    const target = progressTargets[0], snapshot = nativePreviews[target.sessionId];
    snapshot.modelProgress.UnsavedAttempt = {Body:{...snapshot.modelProgress}, Protocol:0, Runtime:1, Explanation:'Запись не выполнена'};
    snapshot.runViewState.Lifecycle = 'failed'; snapshot.inbox.running = false;
    RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key(target));
    delete state.chatRuns[target.sessionId];
    await new Promise(resolve => setTimeout(resolve, 0));
    RNAssistantConversationPanel.open(target, {direction:"right"});
  });
  await page.waitForFunction(() => RNAssistantConversationPanel.find(progressTargets[0])?.node('messages').textContent.includes('Не сохранено'));
  assert.equal(await page.evaluate(() => RNAssistantConversationPanel.find(progressTargets[0]).node('messages').textContent.includes('— конец')),true);
  assert.deepEqual(await page.evaluate(() => progressCancels), [], 'view close never cancels native execution');
  if (process.env.RNA_STREAM_REVIEW) await page.screenshot({path:process.env.RNA_STREAM_REVIEW, fullPage:true});
  assert.deepEqual(errors, []);
  console.log('PASS shared stream: Office/Workspace/plugin native reopen, preserved fields without extra live rows, frozen transport, unsaved reopen, close without cancellation');
}).catch(error => {console.error(error); process.exitCode=1;});
