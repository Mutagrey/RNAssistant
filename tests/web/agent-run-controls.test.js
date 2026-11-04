'use strict';
const assert = require('node:assert/strict'), path = require('node:path'), {spawn} = require('node:child_process'), {chromium} = require('playwright');
(async () => {
 const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd:path.resolve(__dirname,'../..')});
 const url=await new Promise(resolve=>server.stdout.on('data',data=>{const match=data.toString().match(/http:\/\/127\.0\.0\.1:\d+\//);if(match)resolve(match[0]);}));
 let browser;
 try {
  browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
  const page=await browser.newPage({viewport:{width:1280,height:900}}), errors=[];
  page.setDefaultTimeout(7000); page.on('pageerror',error=>errors.push(error.message));
  await page.goto(url+'preview/web/?scenario=ready');
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise&&RNAssistantConversationPanel.office('preview-chat'));
  await page.evaluate(async()=>{
   const projects=await send('getProjectChats',{}), project=projects.projects.find(item=>item.available&&item.chats.length);
   const plugin=await send('getPluginChat',{pluginId:'data-templates'});
   window.controlTargets=[{source:'office',sessionId:'preview-chat'},
    {source:'workspace',workspaceId:project.workspaceId,sessionId:project.chats[0].Id},
    {source:'workspace',workspaceId:plugin.project.workspaceId,sessionId:plugin.sessionId}];
   window.controlFixtures=new Map(); window.controlCalls=[];
   window.setControlFixture=(index,patch)=>{
    const target=controlTargets[index], prior=controlFixtures.get(target.sessionId);
    const value=Object.assign({pending:true,paused:true,live:false,revision:7,runId:'saved-'+target.sessionId},prior||{},patch);
    value.revision=(prior?prior.revision:7)+1; controlFixtures.set(target.sessionId,value);
    return RNAssistantConversationPanel.find(target)?.refresh();
   };
   const original=send;
   function waitTarget(id,value){return value.pending?{sessionId:id,runId:value.runId,revision:value.revision}:null;}
   function inbox(id,value){return {chatId:id,epoch:'saved-controls',revision:value.revision,items:[],receipts:[],hasPendingWait:value.pending,
    runPaused:value.pending&&value.paused,paused:value.pending&&!value.live,running:value.live,activeRun:value.live,
    waitTarget:waitTarget(id,value),pauseReason:value.live?null:'После перезапуска'};}
   window.send=async(type,payload,progress)=>{
    const id=payload?.chatId||payload?.sessionId, value=controlFixtures.get(id);
    if(type==='getChatInbox'&&value)return inbox(id,value);
    if(type==='cancelAgentWait'||type==='resumeAgentWaits'){
     controlCalls.push({type,payload:JSON.parse(JSON.stringify(payload))});
     const target=type==='cancelAgentWait'?payload.target:payload.targets[0], current=controlFixtures.get(target.sessionId);
     if(window.holdRunControl){holdRunControl=false;await new Promise(resolve=>window.releaseRunControl=resolve);}
     if(window.failRunControl){failRunControl=false;throw new Error('Ответ потерян');}
     const status=window.controlStatus|| (type==='cancelAgentWait'?'Cancelled':'Requested'); window.controlStatus=null;
     if(status==='Cancelled'||status==='Requested'){current.pending=false;current.live=false;}
     current.revision++;
     const receipt={sessionId:target.sessionId,runId:target.runId,status};
     return type==='cancelAgentWait'?receipt:{receipts:[receipt]};
    }
    const response=await original(type,payload,progress);
    if(type==='getChatState'&&value)response.inbox=inbox(id,value);
    if(type==='getProjectChat'&&value)Object.assign(response.state,{status:value.pending?'running':'cancelled',runId:value.runId,
     runViewState:null,modelProgress:null,revision:value.revision,hasPendingWait:value.pending,runPaused:value.pending&&value.paused,
     canSteer:value.live,waitTarget:waitTarget(id,value),pendingInputs:[]});
    return response;
   };
  });
  async function open(index,patch={}){
   await page.evaluate(async({index,patch})=>{
    await setControlFixture(index,patch); await RNAssistantConversationPanel.open(controlTargets[index]);
    const panel=RNAssistantConversationPanel.find(controlTargets[index]); await panel.refresh();
    if(index===0)await panel.pollInbox();
   },{index,patch});
   await page.locator('.conversation-panel:not([inert]) .inbox-pause').filter({visible:true}).waitFor();
  }
  function activePanel(){return page.locator('.conversation-panel').filter({has:page.locator('.inbox-pause')}).filter({visible:true}).last();}
  async function geometry(){return page.evaluate(()=>{
   const view=RNAssistantConversationPanel.selected(), note=view.element.querySelector('.inbox-pause'), body=view.element.closest('.dv-content-container');
   const area=body.getBoundingClientRect(), box=note.getBoundingClientRect();
   return {left:box.left,right:box.right,areaLeft:area.left,areaRight:area.right,width:note.clientWidth,scroll:note.scrollWidth,
    buttons:Array.from(note.querySelectorAll('button')).map(node=>{const css=getComputedStyle(node);return {height:css.minHeight,padding:css.padding,font:css.fontSize};})};
  });}
  await open(0);
  let panel=activePanel();
  await panel.getByText('Выполнение приостановлено',{exact:true}).waitFor();
  await panel.locator('[data-chat-node="chatInput"]').fill('Не терять черновик');
  const wide=await geometry();
  assert.deepEqual(wide.buttons[0],wide.buttons[1],'resume and cancel reuse the same Inbox button geometry');
  await page.screenshot({path:'/tmp/rnassistant-agent-run-controls-wide.png'});
  await page.evaluate(()=>document.documentElement.style.zoom='1.25'); await page.setViewportSize({width:420,height:800});
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  const narrow=await geometry();
  assert.ok(narrow.left>=narrow.areaLeft-1&&narrow.right<=narrow.areaRight+1&&narrow.scroll<=narrow.width+1,'controls stay inside the chat at narrow/125%');
  assert.deepEqual(narrow.buttons,wide.buttons,'zoom/narrow does not fork component tokens');
  await page.screenshot({path:'/tmp/rnassistant-agent-run-controls-narrow.png'});
  await page.evaluate(()=>document.documentElement.style.zoom='1'); await page.setViewportSize({width:1280,height:900});
  await page.evaluate(()=>window.controlStatus='Changed');
  await panel.getByRole('button',{name:'Остановить',exact:true}).click();
  await panel.locator('[data-chat-node="inboxStatus"]').filter({hasText:'Запуск изменился'}).waitFor();
  assert.equal(await page.evaluate(()=>controlCalls.length),1,'stale target is not retried automatically');
  const staleCall=await page.evaluate(()=>controlCalls[0]);
  assert.deepEqual(staleCall.payload,{source:'office',sessionId:'preview-chat',target:{sessionId:'preview-chat',runId:'saved-preview-chat',revision:8}});
  await page.evaluate(()=>window.failRunControl=true);
  await panel.getByRole('button',{name:'Остановить',exact:true}).click();
  await panel.locator('[data-chat-node="inboxStatus"]').filter({hasText:'Ответ потерян'}).waitFor();
  assert.equal(await page.evaluate(()=>controlCalls.length),2,'lost result does not replay');
  await panel.getByRole('button',{name:'Остановить',exact:true}).click();
  await panel.locator('.inbox-pause').waitFor({state:'detached'});
  assert.equal(await page.evaluate(()=>RNAssistantConversationPanel.office('preview-chat').node('chatInput').value),'Не терять черновик');
  await open(1,{pending:true,paused:true,live:true}); panel=activePanel();
  await panel.getByText('Выполнение приостановлено',{exact:true}).waitFor();
  assert.equal(await panel.getByRole('button',{name:'Остановить',exact:true}).count(),0,'live pause reuses the existing composer Stop');
  await panel.getByRole('button',{name:'Продолжить',exact:true}).click();
  await panel.locator('.inbox-pause').waitFor({state:'detached'});
  assert.equal(await page.evaluate(()=>controlCalls.at(-1).type),'resumeAgentWaits','live user pause uses exact same-run control');
  await open(1,{pending:true,paused:false,live:false}); panel=activePanel();
  await panel.getByRole('button',{name:'Продолжить ожидание',exact:true}).waitFor();
  await panel.getByRole('button',{name:'Остановить',exact:true}).click();
  await panel.locator('.inbox-pause').waitFor({state:'detached'});
  await open(2,{pending:true,paused:true,live:false}); panel=activePanel();
  await page.evaluate(()=>window.holdRunControl=true);
  await panel.getByRole('button',{name:'Остановить',exact:true}).click();
  await page.waitForFunction(()=>!!window.releaseRunControl);
  assert.equal(await panel.getByRole('button',{name:'Продолжить',exact:true}).isDisabled(),true);
  assert.equal(await panel.getByRole('button',{name:'Остановить',exact:true}).isDisabled(),true);
  const beforeClose=await page.evaluate(()=>controlCalls.length);
  await page.evaluate(()=>{
   RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key(controlTargets[2])); releaseRunControl();
  });
  await page.waitForFunction(()=>!RNAssistantConversationPanel.find(controlTargets[2]));
  assert.equal(await page.evaluate(()=>controlCalls.length),beforeClose,'closing a view does not repeat or cancel the admitted command');
  assert.deepEqual(await page.evaluate(()=>{const call=controlCalls.at(-1);return [call.payload.source,call.payload.workspaceId,call.payload.target.sessionId];}),
   await page.evaluate(()=>[controlTargets[2].source,controlTargets[2].workspaceId,controlTargets[2].sessionId]));
  assert.deepEqual(errors,[]);
  console.log('PASS saved Run controls: exact scope/revision, live pause, stale/unknown, drafts, narrow/125%, busy/close cleanup');
  console.log(JSON.stringify({wide,narrow}));
 } finally {if(browser)await browser.close();server.kill();}
})().catch(error=>{console.error(error);process.exitCode=1;});
