'use strict';
const assert = require('node:assert/strict');
const {withPreviewPage} = require('./preview-session');
withPreviewPage(async ({page, errors}) => {
  page.setDefaultTimeout(7000); await page.setViewportSize({width:1440,height:900});
  await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
  await page.evaluate(async () => {
    await RNAssistantProjectChats.open('preview-data-workspace','preview-project-review');
    window.officeTarget = {source:'office',sessionId:'preview-chat'};
    window.projectTarget = {source:'workspace',workspaceId:'preview-data-workspace',sessionId:'preview-project-review'};
    RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key(officeTarget));
  });
  await page.waitForFunction(() => !RNAssistantConversationPanel.find(officeTarget));
  await page.evaluate(() => RNAssistantConversationPanel.open(officeTarget,{direction:'right'}));
  await page.waitForFunction(() => [officeTarget,projectTarget].every(t => RNAssistantConversationPanel.find(t)?.hasSnapshot));
  await page.evaluate(() => {
    window.controlCalls = [];
    window.setApproval = (target, pendingId, lifecycle='awaiting_confirmation') => {
      const panel = RNAssistantConversationPanel.find(target), data = panel.snapshot();
      const run = {RunId:'run-'+target.sessionId,TurnId:'turn',Lifecycle:lifecycle,ExecutionHealth:'clean',
        GoalVerification:'not_assessed',SuccessfulReads:0,VerifiedWrites:0,NoChangeWrites:0,UnverifiedWrites:0,FailedCalls:0,UnknownEffects:0,
        StartedUtc:new Date(Date.now()-6000).toISOString(),CurrentAction:'Изменение документа',
        PendingConfirmation:pendingId ? {PendingId:pendingId,ToolCallId:'call',ToolName:'Общее действие'} : null};
      const snapshot = target.source === 'office' ? {...data,sessionRevision:data.sessionRevision+1,runViewState:run}
        : {...data,pendingId,state:{...data.state,revision:(data.state.revision||0)+1,status:lifecycle,runId:run.RunId,runViewState:run}};
      panel.accept(snapshot); return snapshot;
    };
    setApproval(officeTarget,'office-pending'); setApproval(projectTarget,'project-pending');
    const original=send;
    window.send = async (name,payload,...args) => {
      if (['cancelAgentTool','resolveProjectChat'].includes(name)) {
        controlCalls.push({name,payload});
        return setApproval(name==='cancelAgentTool'?officeTarget:projectTarget,null,'cancelled');
      }
      return original(name,payload,...args);
    };
  });
  assert.deepEqual(await page.evaluate(() => [officeTarget,projectTarget].map(t => {
    const panel=RNAssistantConversationPanel.find(t);
    return [panel.node('agentApprovalDock').querySelector('.agent-approval-title').textContent,
      panel.node('agentApprovalDock').querySelectorAll('button').length,
      panel.node('chatForm').contains(panel.node('contextMeter')),panel.node('chatbarActions').contains(panel.node('contextMeter'))];
  })), [['Общее действие',2,true,false],['Общее действие',2,true,false]]);
  assert.equal(await page.evaluate(() => {
    const panel=RNAssistantConversationPanel.find(projectTarget), approval=panel.node('agentApprovalDock').firstChild;
    for(let i=0;i<20;i++) { panel.node('chatInput').value='Черновик '+i; panel.node('chatInput').dispatchEvent(new Event('input')); }
    return panel.node('agentApprovalDock').firstChild === approval;
  }),true,'20 edits replace zero unchanged approval panels');
  await page.evaluate(() => {
    const a=RNAssistantConversationPanel.find(officeTarget), b=RNAssistantConversationPanel.find(projectTarget);
    window.oldApproval=a.node('agentApprovalDock').querySelector('button');
    setApproval(officeTarget,'new-pending'); b.focus(); oldApproval.click();
    a.node('contextMeter').click();
  });
  assert.equal(await page.evaluate(() => controlCalls.length),0,'stale pending button cannot resolve a replacement');
  assert.equal(await page.evaluate(() => RNAssistantChatInspector.target().chatId),'preview-chat','usage captures the originating panel');
  await page.evaluate(() => {
    RNAssistantChatInspector.setOpen(false);
    RNAssistantConversationPanel.find(officeTarget).node('chatActivityButton').click();
  });
  assert.equal(await page.evaluate(() => RNAssistantChatInspector.target().chatId),'preview-chat');
  await page.evaluate(() => {
    RNAssistantChatInspector.setOpen(false);
    for (const target of [officeTarget,projectTarget]) RNAssistantConversationPanel.find(target).node('agentApprovalDock').querySelector('button').click();
  });
  await page.waitForFunction(() => controlCalls.length===2);
  assert.deepEqual(await page.evaluate(() => controlCalls.map(c=>[c.name,c.payload.chatId||c.payload.sessionId,c.payload.pendingId])),
    [['cancelAgentTool','preview-chat','new-pending'],['resolveProjectChat','preview-project-review','project-pending']]);
  assert.deepEqual(await page.evaluate(() => {
    const a=RNAssistantConversationPanel.find(officeTarget), b=RNAssistantConversationPanel.find(projectTarget);
    const data=a.transcript(), source={Id:'shared-resource',Kind:'file',Title:'Ресурс исходного чата',MimeType:'text/plain',ResourceUri:'rna://artifact/source/revision/1'};
    data.resourceSnapshot={...data.resourceSnapshot,artifacts:[source],artifactLibrary:{heads:[]}};
    const presentation=createMessagePresentation(a.view,data,a.composer.capture());
    const originalOpen=RNAssistantMaterialView.openArtifact, originalId=state.activeChatId, originalArtifacts=state.artifacts;
    const calls=[]; RNAssistantMaterialView.openArtifact=(artifact,context)=>{calls.push([artifact.Title,context.chatId]);return true;};
    state.activeChatId='foreign-chat';state.artifacts=[{...source,Title:'Чужой ресурс'}];
    try {
      const host=document.createElement('div'), other=document.createElement('div');
      appendMessageArtifactCards(host,{ArtifactIds:['shared-resource']},null,presentation);
      host.querySelector('button').click();
      appendMessageArtifactCards(other,{ArtifactIds:['shared-resource']},null,createMessagePresentation(b.view,b.transcript(),b.composer.capture()));
      return {calls,unsupportedCards:other.children.length};
    } finally {RNAssistantMaterialView.openArtifact=originalOpen;state.activeChatId=originalId;state.artifacts=originalArtifacts;}
  }),{calls:[['Ресурс исходного чата','preview-chat']],unsupportedCards:0},'resource cards use source snapshot/target and capabilities');
  if (process.env.RNA_CHAT_REVIEW) await page.screenshot({path:process.env.RNA_CHAT_REVIEW+'-wide.png'});
  await page.evaluate(() => RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key(officeTarget)));
  await page.setViewportSize({width:420,height:800});
  await page.evaluate(() => { document.documentElement.style.zoom='1.25'; RNAssistantConversationPanel.open(projectTarget); });
  await page.waitForFunction(() => !RNAssistantConversationPanel.find(projectTarget).node('chatActivityButton').getClientRects().length);
  assert.deepEqual(await page.evaluate(() => {
    const panel=RNAssistantConversationPanel.find(projectTarget), bar=panel.node('chatbarActions').parentElement;
    return [bar.scrollWidth<=bar.clientWidth+1, panel.node('chatActivityButton').getClientRects().length,
      panel.node('chatForm').querySelectorAll('[data-chat-node="contextMeter"]').length];
  }),[true,0,1]);
  await page.getByRole('button',{name:'Действия чата',exact:true}).click();
  await page.getByRole('menuitem',{name:'Ход работы',exact:true}).click();
  assert.equal(await page.evaluate(() => RNAssistantChatInspector.target().sessionId),'preview-project-review');
  await page.evaluate(() => RNAssistantChatInspector.setOpen(false));
  if (process.env.RNA_CHAT_REVIEW) await page.screenshot({path:process.env.RNA_CHAT_REVIEW+'-narrow.png'});
  assert.deepEqual(await page.evaluate(() => {
    const panel=RNAssistantConversationPanel.find(projectTarget), data=setApproval(projectTarget,null,'failed');
    data.messages=[{Id:'failed-message',Role:'assistant',Content:'Полученный ответ',RunId:data.state.runId,RunViewState:data.state.runViewState}];
    data.messageStartIndex=0;data.messageTotalCount=1;data.state.revision++;
    panel.accept(data,{latestMessages:true});
    return [panel.node('chatRunNotice').hidden,panel.node('messages').querySelectorAll('.agent-run-notice').length];
  }),[true,1],'retained run failure has one full presentation');
  assert.deepEqual(errors,[]);
  console.log('PASS common approvals/header: exact A/B callbacks, stale pending guard, one usage control, narrow/125% menu without focusable duplicates');
}).catch(error => {console.error(error);process.exitCode=1;});
