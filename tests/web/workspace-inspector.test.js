'use strict';
const assert=require('node:assert/strict');
const {withPreviewPage}=require('./preview-session');
(async()=>withPreviewPage(async({page,errors})=>{
  page.setDefaultTimeout(8000);await page.waitForFunction(()=>state.activeChatId==='preview-chat' && !state.initializePromise);
  await page.evaluate(()=>{
    const original=send;window.scopeCalls=[];window.scopePages=new Map();
    send=async(type,p,...args)=>{
      scopeCalls.push({type,p});
      if(type==='browserCommand'){
        if(p.action==='Open')scopePages.set(p.browserTabId,{browserTabId:p.browserTabId,sessionId:'home',ownerScope:p.ownerScope,revision:1,documentGeneration:1,status:'Home',url:'/',title:'Home',target:{kind:'Home',state:'Empty',capabilities:{}}});
        const value=scopePages.get(p.browserTabId);value.revision++;return structuredClone(value);
      }
      const result=await original(type,type==='getProjectChat'?{workspaceId:'preview-data-workspace',sessionId:'preview-project-review'}:p,...args);
      if(type==='getProjectChat'){
        result.project={...result.project,workspaceId:p.workspaceId,name:p.workspaceId==='scope-plugin'?'Plugin Project':'Folder Project',pluginId:p.workspaceId==='scope-plugin'?'test-plugin':null};
        result.sessionId=p.sessionId;result.messages=[];result.messageStartIndex=0;result.messageTotalCount=0;
        result.state={...result.state,title:p.sessionId,runId:'run-'+p.sessionId,runViewState:{runId:'run-'+p.sessionId,lifecycle:'completed',executionHealth:'unknown'},
          contextUsage:{RunId:'run-'+p.sessionId,PromptTokens:777,CompletionTokens:22,TotalTokens:799,ContextWindowTokens:2000},
          contextReceipt:{Arsenal:{RunId:'run-'+p.sessionId,StepId:'step',Selected:[{Id:'skill',Name:'Skill '+p.sessionId,Status:'selected'}],Tools:[]},EstimatedTokens:600}};
      }
      return result;
    };
    RNAssistantConversationPanel.open({source:'workspace',workspaceId:'scope-project',sessionId:'project-chat'});
  });
  await page.waitForFunction(()=>RNAssistantDiagnostics.source().available && RNAssistantDiagnostics.source().chatId==='project-chat');
  await page.evaluate(()=>RNAssistantChatInspector.setOpen(true));
  const view=async name=>{await page.locator('#chatInspectorSwitcher summary').click();await page.locator('[data-chat-inspector-view="'+name+'"]').click();};
  assert.match(await page.locator('#chatInspectorScope').textContent(),/Folder Project.*project-chat/);
  await view('abilities');await page.getByText('Skill project-chat',{exact:true}).waitFor();
  await view('usage');assert.match(await page.locator('#chatInspectorBody').textContent(),/777/);
  await page.evaluate(()=>RNAssistantConversationPanel.open({source:'workspace',workspaceId:'scope-plugin',sessionId:'plugin-chat'}));
  await page.waitForFunction(()=>RNAssistantDiagnostics.source().available && RNAssistantDiagnostics.source().owner==='plugin');
  await page.evaluate(()=>RNAssistantChatInspector.setOpen(true));await view('abilities');await page.getByText('Skill plugin-chat',{exact:true}).waitFor();
  await page.evaluate(()=>{RNAssistantChatInspector.openActivity({owner:'project',workspaceId:'scope-project',sessionId:'project-chat',runId:'old-project',stepId:'old-step'});});
  assert.match(await page.locator('#chatInspectorScope').textContent(),/Folder Project.*old-project/);
  await page.getByRole('button',{name:'Запрос выбранного шага',exact:true}).click();
  await page.waitForFunction(()=>scopeCalls.some(c=>c.type==='getModelContext' && c.p.workspaceId==='scope-project' && c.p.chatId==='project-chat' && c.p.runId==='old-project' && c.p.stepId==='old-step'));
  await page.evaluate(()=>{RNAssistantChatInspector.setOpen(true);});await page.getByRole('button',{name:'Следовать выбранному чату'}).click();
  assert.match(await page.locator('#chatInspectorScope').textContent(),/Plugin Project.*plugin-chat/);
  await page.evaluate(()=>RNAssistantBrowser.open('/',{kind:'User',scopeId:'local'},{browserTabId:'inspector-home'}));
  await page.waitForFunction(()=>RNAssistantDockWorkspace.getView('browser:inspector-home'));
  assert.equal(await page.evaluate(()=>RNAssistantDiagnostics.source().chatId),'plugin-chat','Browser does not retarget selected conversation');
  await page.evaluate(()=>RNAssistantChatInspector.setOpen(true));assert.match(await page.locator('#chatInspectorScope').textContent(),/Plugin Project/);
  await page.evaluate(()=>{RNAssistantChatInspector.openActivity({owner:'plugin',workspaceId:'scope-plugin',sessionId:'plugin-chat',runId:'old-plugin'});RNAssistantConversationPanel.closeDeleted({source:'workspace',workspaceId:'scope-plugin',sessionId:'plugin-chat'});});
  assert.equal(await page.evaluate(()=>RNAssistantDiagnostics.context({owner:'plugin',workspaceId:'scope-plugin',sessionId:'plugin-chat'}).available),false);
  assert.match(await page.locator('#chatInspectorScope').textContent(),/old-plugin/);
  assert.equal(await page.evaluate(()=>scopeCalls.filter(c=>c.type==='inspectPromptContext').length),0,'workspace never borrows Office preview');
  assert.deepEqual(errors,[]);
  console.log('PASS Inspector: exact Workspace/plugin scope, retained cross-chat request, Browser focus, closed owner and no Office fallback');
}))().catch(error=>{console.error(error);process.exitCode=1;});
