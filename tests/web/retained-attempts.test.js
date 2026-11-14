'use strict';
const assert=require('node:assert/strict');
const {withPreviewPage}=require('./preview-session');
withPreviewPage(async ({page,errors}) => {
  page.setDefaultTimeout(7000);
  await page.waitForFunction(() => state.activeChatId==='preview-chat' && !state.initializePromise);
  await page.evaluate(async () => {
    await RNAssistantProjectChats.open('preview-data-workspace','preview-project-review');
    window.targets=[{source:'office',sessionId:'preview-chat'},
      {source:'workspace',workspaceId:'preview-data-workspace',sessionId:'preview-project-review'}];
    for(const target of targets) RNAssistantConversationPanel.open(target);
    window.attempt={Body:{AttemptId:'retained-body',TransportOutcome:2,TransportEnded:true,Message:'Прерванный ответ',
      Reason:'Видимая причина',Decision:'Видимое решение',Reasoning:'Полученное рассуждение',Refusal:'Отказ',ReasoningTruncated:true},
      Protocol:0,Runtime:1,Explanation:'Запрос отменён; действия не выполнялись.'};
    for(const target of targets) {
      const panel=RNAssistantConversationPanel.find(target), snapshot=panel.snapshot();
      const message={Id:'attempt_retained-body',Role:'assistant',RunId:'retained-run',ExcludeFromModelContext:true,
        RetainedAttempt:attempt,Activity:{Kind:'model_attempt',StepId:'step',RunId:'retained-run',ModelAttemptId:'retained-body',Title:'Ответ не принят',Status:'retained'}};
      const next={...snapshot,messages:[message],messageStartIndex:0,messageTotalCount:1};
      if(target.source==='office') {next.sessionRevision=(snapshot.sessionRevision||0)+10; next.runViewState=null;}
      else next.state={...snapshot.state,revision:(snapshot.state.revision||0)+10,status:'cancelled',runId:'retained-run'};
      panel.accept(next);
    }
  });
  for(const source of ['office','workspace']) {
    await page.evaluate(source=>RNAssistantConversationPanel.open(targets.find(t=>t.source===source)),source);
    const panel=page.locator('.conversation-panel').filter({has:page.locator(`[data-chat-node="chatForm"][data-owner="${source==='office'?'office':'project'}"]`)});
    const disclosure=panel.locator('.kind-model_attempt details').first();
    await disclosure.locator('summary').click();
    assert.match(await disclosure.innerText(),/Прерванный ответ/);
    assert.match(await disclosure.innerText(),/Видимая причина/);
    assert.match(await disclosure.innerText(),/Полученное рассуждение/);
    assert.match(await disclosure.innerText(),/Сохранённый текст усечён/);
  }
  assert.equal(await page.evaluate(() => {
    RNAssistantConversationPanel.open(targets[0]);
    const panel=RNAssistantConversationPanel.find(targets[0]), snapshot=panel.snapshot();
    const unsaved={...attempt,Body:{...attempt.Body,AttemptId:'unsaved-body',Message:'Не потерять этот текст'}};
    applyOfficeChatProgress({chatId:targets[0].sessionId,runId:'failed-run',phase:'attempt_unsaved',
      activity:{UnsavedAttempt:unsaved}},null,'inbox:'+targets[0].sessionId);
    const next={...snapshot,messages:snapshot.messages.filter(m=>(m.id||m.Id)!=='attempt_unsaved-body'),sessionRevision:snapshot.sessionRevision+1};
    panel.accept(next);
    return panel.node('messages').textContent.includes('Не сохранено') && panel.node('messages').textContent.includes('Не потерять этот текст');
  }),true,'refresh keeps an uncommitted body available and explicitly unsaved');
  if(process.env.RNA_RETAINED_REVIEW) await page.screenshot({path:process.env.RNA_RETAINED_REVIEW,fullPage:true});
  assert.deepEqual(errors,[]);
  console.log('PASS retained attempts: shared run disclosure, visible fields, truncation, unsaved refresh');
}).catch(error=>{console.error(error);process.exitCode=1;});
