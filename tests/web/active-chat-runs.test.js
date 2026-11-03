'use strict';
const assert=require('node:assert/strict'), path=require('node:path'), {spawn}=require('node:child_process'), {chromium}=require('playwright');
(async()=>{
 const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd:path.resolve(__dirname,'../..')});
 const url=await new Promise(resolve=>server.stdout.on('data',data=>{const m=data.toString().match(/http:\/\/127\.0\.0\.1:\d+\//);if(m)resolve(m[0]);}));
 const browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
 try{
  const page=await browser.newPage({viewport:{width:1440,height:900}}), errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.goto(url+'preview/web/?scenario=ready');
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  await page.evaluate(async()=>{
   const plugin=await send('getPluginChat',{pluginId:'data-templates'});
   window.runTargets=[{source:'office',sessionId:'preview-chat'}, {source:'workspace',workspaceId:plugin.project.workspaceId,sessionId:plugin.sessionId}];
   RNAssistantConversationPanel.open(runTargets[1],{direction:'right'});
   window.liveRuns=runTargets.map((t,i)=>({...t,runId:'run-'+i,title:i?'Исправить плагин':'Отчёт за сентябрь',scopeTitle:i?'Данные и шаблоны':'Продажи.xlsx',phase:'thinking',startedUtc:new Date().toISOString()}));
   const original=send; window.stopRequests=[]; window.pauseStop=true;
   window.send=async(type,payload,progress)=>{
    if(type==='getActiveChatRuns')return {runs:liveRuns.map(r=>({...r}))};
    if(type==='stopActiveChatRun'){
     stopRequests.push({...payload});
     if(window.failNextStop){window.failNextStop=false;throw new Error('Не удалось остановить запрос');}
     if(pauseStop){pauseStop=false;await new Promise(resolve=>{window.finishStop=resolve;});}
     const index=liveRuns.findIndex(r=>r.source===payload.source&&r.sessionId===payload.sessionId&&r.runId===payload.runId&&r.workspaceId===payload.workspaceId);
     if(index>=0)liveRuns.splice(index,1);
     return {requested:index>=0};
    }
    return original(type,payload,progress);
   };
   await RNAssistantActiveRuns.refresh();
  });
  await page.waitForFunction(()=>runTargets.every(t=>{const p=RNAssistantConversationPanel.find(t);return p&&!p.loading;}));
  await page.getByRole('button',{name:'Активные запросы: 2',exact:true}).click();
  await page.getByRole('menu',{name:'Активные запросы чатов'}).waitFor();
  const compactWidth=(await page.getByRole('menu',{name:'Активные запросы чатов'}).boundingBox()).width;
  assert.ok(compactWidth<330,'short titles shrink the menu below its maximum width');
  assert.equal(await page.locator('.active-chat-runs-menu .rna-action-menu-row > button:first-child svg').count(),0,'chat rows have no redundant icons');
  await page.screenshot({path:'/tmp/rna-active-chat-runs.png'});
  await page.evaluate(async()=>{liveRuns[0].title='Очень длинное название активного запроса для проверки ограничения ширины меню';await RNAssistantActiveRuns.refresh();});
  // A refresh can coalesce with an already pending metadata read. Measure the
  // menu only once it has actually rendered the requested title.
  await page.getByRole('menuitem',{name:'Очень длинное название активного запроса для проверки ограничения ширины меню',exact:true}).waitFor();
  const longWidth=(await page.getByRole('menu',{name:'Активные запросы чатов'}).boundingBox()).width;
  assert.ok(longWidth>compactWidth&&longWidth<=340,'long labels grow only to the token maximum: compact='+compactWidth+', long='+longWidth);
  assert.ok(await page.locator('.active-chat-runs-menu .rna-action-menu-label').first().evaluate(el=>el.scrollWidth>el.clientWidth),'long title is truncated');
  await page.evaluate(async()=>{liveRuns[0].title='Отчёт за сентябрь';await RNAssistantActiveRuns.refresh();});
  await page.getByRole('menuitem',{name:'Отчёт за сентябрь',exact:true}).waitFor();
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('#activeChatRunsButton').evaluate(el=>el===document.activeElement),true,'Escape restores trigger focus');
  await page.evaluate(()=>runTargets.forEach(t=>RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key(t))));
  assert.equal(await page.evaluate(()=>stopRequests.length),0,'closing active views sends no cancellation');
  await page.locator('#activeChatRunsButton').click();
  await page.getByRole('menuitem',{name:'Отчёт за сентябрь',exact:true}).click();
  await page.waitForFunction(()=>!!RNAssistantConversationPanel.find(runTargets[0]));
  assert.equal(await page.evaluate(()=>!!RNAssistantConversationPanel.find(runTargets[1])),false,'jump opens only the chosen conversation');
  await page.locator('#activeChatRunsButton').click();
  await page.getByRole('menuitem',{name:'Остановить: Исправить плагин',exact:true}).click();
  await page.waitForFunction(()=>!!window.finishStop);
  await page.locator('#activeChatRunsButton').click();
  assert.equal(await page.getByRole('menuitem',{name:'Остановить: Исправить плагин',exact:true}).isDisabled(),true,'duplicate Stop is disabled while awaiting acknowledgement');
  await page.evaluate(()=>finishStop());
  await page.waitForFunction(()=>document.getElementById('activeChatRunsCount').textContent==='1');
  assert.equal(await page.evaluate(()=>!!RNAssistantConversationPanel.find(runTargets[1])),false,'Stop works without mounting its chat');
  assert.deepEqual(await page.evaluate(()=>stopRequests[0]),await page.evaluate(()=>({...runTargets[1],runId:'run-1'})));
  // A row may become stale while the menu is open. Never cancel its replacement run.
  await page.evaluate(()=>{liveRuns[0].runId='replacement-run';});
  await page.getByRole('menuitem',{name:'Остановить: Отчёт за сентябрь',exact:true}).click();
  await page.waitForFunction(()=>stopRequests.length===2);
  assert.equal(await page.evaluate(()=>liveRuns[0].runId),'replacement-run');
  assert.equal(await page.evaluate(()=>stopRequests[1].runId),'run-0');
  await page.evaluate(()=>{window.failNextStop=true;});
  await page.locator('#activeChatRunsButton').click();
  await page.getByRole('menuitem',{name:'Остановить: Отчёт за сентябрь',exact:true}).click();
  await page.waitForFunction(()=>document.getElementById('activeChatRunsButton').title==='Не удалось остановить запрос');
  await page.evaluate(()=>RNAssistantActiveRuns.refresh());
  assert.equal(await page.locator('#activeChatRunsCount').textContent(),'1','failed Stop does not hide a live run');
  await page.locator('#activeChatRunsButton').click();
  assert.equal(await page.getByRole('menuitem',{name:'Не удалось остановить запрос',exact:true}).isVisible(),true,'polling cannot erase a Stop failure');
  await page.keyboard.press('Escape');
  await page.setViewportSize({width:390,height:760});
  await page.evaluate(()=>{document.documentElement.style.zoom='1.25';});
  await page.locator('#activeChatRunsButton').click();
  const bounds=await page.getByRole('menu',{name:'Активные запросы чатов'}).boundingBox();
  assert.ok(bounds.x>=0&&bounds.x+bounds.width<=391&&bounds.y>=0&&bounds.y+bounds.height<=761,'menu fits narrow/zoomed viewport');
  await page.screenshot({path:'/tmp/rna-active-chat-runs-narrow.png'});
  await page.getByRole('menuitem',{name:'Остановить: Отчёт за сентябрь',exact:true}).click();
  await page.waitForFunction(()=>document.getElementById('activeChatRunsCount').textContent==='0');
  await page.locator('#activeChatRunsButton').click();
  assert.equal(await page.getByRole('menuitem',{name:'Активных запросов нет',exact:true}).isVisible(),true);
  assert.deepEqual(errors,[]);
  console.log('PASS active requests: background close, exact jump/Stop, pending and stale run guard, keyboard, narrow and zoom');
 }finally{await browser.close();server.kill();}
})().catch(e=>{console.error(e);process.exitCode=1;});
