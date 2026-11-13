'use strict';
const assert = require('node:assert/strict'), fs = require('node:fs/promises');
const {withPreviewPage} = require('./preview-session');
(async()=>withPreviewPage(async({page,errors})=>{
  page.setDefaultTimeout(8000);
  await page.waitForFunction(()=>state.activeChatId==='preview-chat' && !state.initializePromise);
  await page.evaluate(async()=>{
    const original=send;window.inspectionCalls=[];window.inspectionHold=null;
    const summaries=['preview-chat','inspect-b'].map((Id,i)=>({Id,Title:i?'Chat B':'Chat A',DocumentAuthorityId:i?'doc-b':'doc-a',DocumentTitle:i?'Book B.xlsx':'Book A.xlsx',IsCurrentDocument:true,Mode:'agent',Revision:100}));
    state.chats=summaries;
    send=async(type,p,...args)=>{
      inspectionCalls.push({type,p});
      if(type==='getAgentArsenal') { if(p.chatId==='preview-chat' && inspectionHold)await inspectionHold;
        return {type:'rnassistant.agentArsenal',contractVersion:1,chatId:p.chatId,prepared:true,details:{Selected:[{Id:'skill-'+p.chatId,Name:'Arsenal '+p.chatId,Status:'selected'}],Tools:[]}}; }
      const result=await original(type,type==='getChatState'?{...p,chatId:'preview-chat'}:p,...args);
      if(type==='getChatState')Object.assign(result,{activeChatId:p.chatId,sessionRevision:100,chats:summaries,
        context:{Notes:[{Title:'Note '+p.chatId,Host:'Excel',Kind:'text'}]},contextUsage:{lastPromptTokens:p.chatId==='preview-chat'?111:222,contextWindowTokens:1000},
        runViewState:{runId:'run-'+p.chatId,lifecycle:'completed',executionHealth:'unknown'}});
      if(type==='inspectPromptContext')result.chatId=p.chatId;
      return result;
    };
    await RNAssistantConversationPanel.office('preview-chat').refresh();
    RNAssistantChatDrafts.saveOffice('preview-chat','Draft A',[],true);
    RNAssistantConversationPanel.office('preview-chat').restoreDraft();
    RNAssistantConversationPanel.open({source:'office',sessionId:'inspect-b'});
  });
  await page.waitForFunction(()=>RNAssistantConversationPanel.office('inspect-b')?.snapshot().sessionRevision===100);
  await page.evaluate(()=>{RNAssistantChatDrafts.saveOffice('inspect-b','Draft B',[],true);RNAssistantConversationPanel.office('inspect-b').restoreDraft();RNAssistantConversationPanel.office('preview-chat').focus();state.activeChatId='inspect-b';RNAssistantChatInspector.setOpen(true);});
  const view=async name=>{await page.locator('#chatInspectorSwitcher summary').click();await page.locator('[data-chat-inspector-view="'+name+'"]').click();};
  await view('context');assert.match(await page.locator('#chatInspectorScope').textContent(),/Book A.xlsx.*Chat A/);
  assert.match(await page.locator('#chatInspectorBody').textContent(),/Note preview-chat/);assert.doesNotMatch(await page.locator('#chatInspectorBody').textContent(),/Note inspect-b/);
  await page.evaluate(()=>{inspectionHold=new Promise(resolve=>window.releaseInspection=resolve);});await view('abilities');
  await page.waitForFunction(()=>inspectionCalls.some(call=>call.type==='getAgentArsenal' && call.p.chatId==='preview-chat'));
  await page.evaluate(()=>RNAssistantConversationPanel.office('inspect-b').focus());
  await page.getByText('Arsenal inspect-b',{exact:true}).waitFor();await page.evaluate(()=>{releaseInspection();inspectionHold=null;});
  await page.waitForTimeout(100);assert.doesNotMatch(await page.locator('#chatInspectorBody').textContent(),/Arsenal preview-chat/);
  await page.evaluate(()=>{RNAssistantConversationPanel.office('preview-chat').focus();state.activeChatId='inspect-b';});await view('usage');
  assert.match(await page.locator('#chatInspectorBody').textContent(),/111/);
  await page.locator('[data-inspector-key="estimate"] > summary').click();await page.locator('.chat-inspector-composition-chart').waitFor();
  const usage=await page.evaluate(()=>inspectionCalls.filter(c=>c.type==='inspectPromptContext').at(-1).p);assert.equal(usage.chatId,'preview-chat');assert.equal(usage.text,'Draft A');
  await page.evaluate(()=>{RNAssistantChatInspector.openActivity({owner:'office',chatId:'preview-chat',runId:'old-a',stepId:'step-a'});RNAssistantConversationPanel.office('inspect-b').focus();});
  assert.match(await page.locator('#chatInspectorScope').textContent(),/Chat A.*old-a.*step-a/);
  assert.match(await page.locator('#chatInspectorScopeMode').textContent(),/Закреплённый/);
  await fs.mkdir('docs/ui/review',{recursive:true});await page.screenshot({path:'docs/ui/review/inspector-selected-wide.png'});
  await page.getByRole('button',{name:'Запрос выбранного шага',exact:true}).click();
  await page.waitForFunction(()=>inspectionCalls.some(c=>c.type==='getModelContext' && c.p.chatId==='preview-chat'));
  assert.ok(await page.locator('#modelContextScope').textContent().then(text=>text.includes('old-a') || text.includes('Chat A')));
  await page.evaluate(()=>RNAssistantChatInspector.setOpen(true));await page.getByRole('button',{name:'Следовать выбранному чату',exact:true}).click();
  assert.match(await page.locator('#chatInspectorScope').textContent(),/Chat B/);
  await page.evaluate(()=>{RNAssistantConversationPanel.office('preview-chat').focus();RNAssistantChatInspector.openUsage();});
  await page.getByRole('button',{name:'Предпросмотр следующего запроса',exact:true}).click();
  await page.waitForFunction(()=>inspectionCalls.some(c=>c.type==='inspectModelContextPreview' && c.p.chatId==='preview-chat'));
  assert.equal(await page.evaluate(()=>inspectionCalls.filter(c=>c.type==='inspectModelContextPreview').at(-1).p.text),'Draft A');
  await page.evaluate(()=>{RNAssistantChatInspector.openActivity({owner:'office',chatId:'preview-chat',runId:'old-a',stepId:'step-a'});RNAssistantConversationPanel.closeDeleted({source:'office',sessionId:'preview-chat'});RNAssistantChatInspector.setOpen(true);});
  assert.match(await page.locator('#chatInspectorBody').textContent(),/Состояние недоступно/);
  assert.equal(await page.evaluate(()=>RNAssistantDiagnostics.context({owner:'office',chatId:'preview-chat'}).available),false,'closed exact scope never falls back to global B');
  await page.evaluate(()=>{RNAssistantConversationPanel.office('inspect-b').focus();RNAssistantChatInspector.openActivity({owner:'office',chatId:'preview-chat',runId:'old-a',stepId:'step-a',label:'Chat A',document:'Book A.xlsx'});});
  await page.setViewportSize({width:420,height:800});await page.evaluate(async()=>{document.documentElement.style.zoom='1.25';RNAssistantShellLayout.refresh();await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));});
  await page.evaluate(()=>RNAssistantChatInspector.setOpen(true));
  await page.waitForFunction(()=>document.getElementById('chatInspectorScope').getBoundingClientRect().right<=innerWidth+1);
  await page.screenshot({path:'docs/ui/review/inspector-selected-narrow.png'});
  const scope=await page.locator('#chatInspectorScope').boundingBox();assert.ok(scope && scope.x>=0 && scope.x+scope.width<=421,JSON.stringify(scope));
  const before=await page.evaluate(()=>inspectionCalls.filter(c=>['inspectPromptContext','getAgentArsenal'].includes(c.type)).length);
  await page.evaluate(()=>{RNAssistantChatInspector.setOpen(false);RNAssistantChatInspector.render();});
  assert.equal(await page.evaluate(()=>inspectionCalls.filter(c=>['inspectPromptContext','getAgentArsenal'].includes(c.type)).length),before);
  assert.deepEqual(errors,[]);
  console.log('PASS Inspector: two exact Office chats, scoped context/abilities/usage/preview, late refusal, pinned run/step, closed target, hidden reads and narrow layout');
}))().catch(error=>{console.error(error);process.exitCode=1;});
