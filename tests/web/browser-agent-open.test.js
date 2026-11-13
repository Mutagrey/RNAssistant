'use strict';
const assert=require('node:assert/strict');
const {withPreviewPage}=require('./preview-session');
(async()=>withPreviewPage(async({page,errors})=>{
  page.setDefaultTimeout(8000);
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  await page.evaluate(()=>{
    const original=send;window.agentOpen={calls:[],pages:new Map(),pending:null,delay:false};const p=agentOpen;
    window.send=(type,payload,...rest)=>{
      if(type==='readBrowserSource')return Promise.resolve({requestGeneration:payload.requestGeneration,files:['index.html'],path:'index.html',text:'source only',offset:0,totalCharacters:11});
      if(type!=='browserCommand')return original(type,payload,...rest);
      p.calls.push(structuredClone(payload));let value=p.pages.get(payload.browserTabId);
      if(payload.action==='Open'){
        if(!value){value={browserTabId:payload.browserTabId,operationId:payload.operationId,sessionId:'native-'+payload.browserTabId,ownerScope:payload.ownerScope,
          url:payload.locator,title:'Agent source',revision:0,documentGeneration:1,status:'Unavailable',reason:'NativeUnavailable',
          target:{kind:'WorkspaceHtmlProject',state:'Ready',ownerScope:payload.ownerScope,workspaceId:payload.ownerScope.scopeId,capabilities:{source:true}}};p.pages.set(payload.browserTabId,value);}
        if(p.delay)return new Promise(resolve=>{p.pending=()=>resolve(structuredClone(value));});
      }
      if(payload.action==='Close')value.status='Closed';
      if(payload.action==='Attach')value.attached=true;
      value.revision++;return Promise.resolve(structuredClone(value));
    };
    agentOpen.request={browserTabId:'runtime-tab',operationId:'exact-operation',ownerScope:{kind:'Workspace',scopeId:'project',chatId:'source-chat'},locator:'/projects/project/source/index.html'};
    RNAssistantBrowser.openRequested(agentOpen.request);
  });
  await page.waitForFunction(()=>agentOpen.calls.some(c=>c.action==='Attach'));
  await page.evaluate(()=>RNAssistantBrowser.openRequested(agentOpen.request));
  assert.equal(await page.evaluate(()=>agentOpen.calls.filter(c=>c.action==='Open').length),1,'duplicate request keeps the same Dock/native tab');
  const attach=await page.evaluate(()=>agentOpen.calls.find(c=>c.action==='Attach'));
  assert.equal(attach.operationId,'exact-operation');assert.equal(attach.sessionId,'native-runtime-tab');assert.equal(attach.bounds.visible,true);
  assert.ok(attach.bounds.width>0&&attach.bounds.height>0);
  await page.waitForFunction(()=>!!localStorage.getItem('rnassistant.ui.dock.s1.v1'));
  const persisted=await page.evaluate(()=>localStorage.getItem('rnassistant.ui.dock.s1.v1'));
  assert.ok(!persisted.includes('exact-operation')&&!persisted.includes('native-runtime-tab'),'runtime receipts never persist in layout');
  await page.evaluate(()=>{agentOpen.delay=true;RNAssistantBrowser.openRequested({...agentOpen.request,browserTabId:'closed-early',operationId:'cancelled-operation'});});
  await page.waitForFunction(()=>!!agentOpen.pending);
  await page.evaluate(()=>{RNAssistantDockWorkspace.close('browser:closed-early');agentOpen.pending();});
  await page.waitForFunction(()=>agentOpen.calls.some(c=>c.action==='Close'&&c.browserTabId==='closed-early'));
  assert.equal(await page.evaluate(()=>agentOpen.calls.filter(c=>c.action==='Attach'&&c.browserTabId==='closed-early').length),0,'late source result cannot attach a closed view');
  assert.equal(await page.locator('section[aria-label="Browser"] iframe').count(),0);
  assert.deepEqual(errors,[]);console.log('PASS agent Browser open: exact visible attachment, operation reuse, ephemeral bindings and close-before-ack cleanup');
}))().catch(error=>{console.error(error);process.exitCode=1;});
