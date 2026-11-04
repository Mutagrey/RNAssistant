'use strict';
const assert = require('node:assert/strict'), path = require('node:path'), {spawn} = require('node:child_process'), {chromium} = require('playwright');
(async () => {
 const server = spawn('python3', ['-B','prototypes/serve_ui.py','--port','0'], {cwd:path.resolve(__dirname,'../..')});
 const url = await new Promise(resolve => server.stdout.on('data', data => {const match=data.toString().match(/http:\/\/127\.0\.0\.1:\d+\//); if(match)resolve(match[0]);}));
 let browser;
 try {
  browser = await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
  const page = await browser.newPage({viewport:{width:1280,height:900}}), errors=[];
  page.on('pageerror', error=>errors.push(error.message));
  await page.goto(url+'preview/web/?scenario=ready');
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise&&RNAssistantConversationPanel.office('preview-chat'));
  await page.evaluate(()=>{
   const original=send; window.recoveryCalls=[];
   window.send=async(type,payload,progress)=>{
    if(type==='getRecoverableAgentWaits'){
     recoveryCalls.push({type,payload});
     if(window.holdRecoveryList) {holdRecoveryList=false;await new Promise(resolve=>window.releaseRecoveryList=resolve);}
     const ids=payload.afterSessionId?['C']:['A','B'];
     return {participants:ids.map((id,index)=>({sessionId:id,agentId:'a-'+id,title:index===0?'Разработка шахматной игры':'Проверка правил',
      target:{sessionId:id,runId:'run-'+id,revision:7}})),nextAfterSessionId:payload.afterSessionId?null:'B'};
    }
    if(type==='resumeAgentWaits'){
     recoveryCalls.push({type,payload});
     if(window.failRecovery) throw new Error('Соединение прервано');
     return {receipts:payload.targets.map((target,index)=>({sessionId:target.sessionId,runId:target.runId,
      status:index?'Changed':'Requested'}))};
    }
    return original(type,payload,progress);
   };
  });
  const menu=page.locator('.rna-action-menu-popup[aria-label="Продолжить совместную работу"]');
  const office=page.locator('.conversation-panel').filter({has:page.locator('[data-chat-node="activeChatTitle"]', {hasText:'Что изменилось за сентябрь'})});
  const plus=office.locator('[data-chat-node="composerAddButton"]');
  const ordinary=office.locator('[data-chat-node="composerAddMenu"]');
  async function openOffice(){
   await plus.click();
   assert.equal(await menu.count(),0,'plus always returns to ordinary chat actions');
   assert.equal(await ordinary.getByRole('button',{name:'Файл с компьютера',exact:true}).isVisible(),true);
   await ordinary.getByRole('button',{name:'Продолжить совместную работу…',exact:true}).click();
   await menu.getByRole('menuitemcheckbox',{name:'Проверка правил',exact:true}).waitFor();
  }
  assert.equal(await page.evaluate(()=>recoveryCalls.length),0,'no background recovery enumeration');
  await openOffice();
  await plus.click();
  assert.equal(await menu.count(),0,'recovery cannot cover the ordinary plus menu');
  assert.equal(await ordinary.getByRole('button',{name:'Материал чата',exact:true}).isVisible(),true);
  await page.evaluate(()=>{
   const c=RNAssistantConversationPanel.office('preview-chat');
   c.snapshot().messages.push({Id:'compaction-menu-fixture',Role:'user',Content:'Third message makes compaction available'});
   c.render();
  });
  assert.equal(await ordinary.getByRole('button',{name:'Сжать контекст',exact:true}).isVisible(),true,'eligible compaction remains in ordinary menu');
  await page.screenshot({path:'/tmp/rnassistant-chat-plus-restored.png'});
  const chooser=page.waitForEvent('filechooser');
  await ordinary.getByRole('button',{name:'Файл с компьютера',exact:true}).click();await chooser;
  await openOffice();
  assert.equal(await menu.locator('[aria-checked="true"]').count(),0,'no implicit selection of unrelated chats');
  await menu.getByRole('menuitemcheckbox',{name:'Разработка шахматной игры',exact:true}).click();
  await menu.getByRole('menuitem',{name:'Следующие чаты',exact:true}).click();
  await page.waitForFunction(()=>recoveryCalls.filter(c=>c.type==='getRecoverableAgentWaits').at(-1).payload.afterSessionId==='B');
  await menu.getByRole('menuitemcheckbox',{name:'Разработка шахматной игры',exact:true}).last().click();
  assert.equal(await menu.locator('[aria-checked="true"]').count(),2,'selected rows stay reviewable across bounded pages');
  await menu.getByRole('menuitem',{name:'Продолжить выбранных (2)',exact:true}).click();
  await menu.getByText('Продолжение принято',{exact:true}).waitFor();
  assert.equal(await menu.getByText('Состояние изменилось · обновите список',{exact:true}).count(),1);
  assert.deepEqual(await page.evaluate(()=>recoveryCalls.find(c=>c.type==='resumeAgentWaits').payload),{
   source:'office',sessionId:'preview-chat',targets:[{sessionId:'A',runId:'run-A',revision:7},{sessionId:'C',runId:'run-C',revision:7}]
  },'one explicit exact command, no unselected B');
  assert.equal(await menu.getByRole('menuitem',{name:'Продолжить выбранных (0)',exact:true}).isDisabled(),true,'no blind retry');
  await menu.getByRole('menuitem',{name:'Обновить список',exact:true}).click();
  await menu.getByRole('menuitemcheckbox',{name:'Проверка правил',exact:true}).waitFor();
  await page.evaluate(()=>document.documentElement.style.zoom='1.25');
  await page.setViewportSize({width:640,height:800});
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  await openOffice();
  await menu.getByRole('menuitem',{name:'Выбрать показанные',exact:true}).click();
  assert.equal(await menu.locator('[aria-checked="true"]').count(),2);
  const bounds=await menu.evaluate(node=>{const r=node.getBoundingClientRect();return {left:r.left,right:r.right,width:node.clientWidth,scroll:node.scrollWidth};});
  assert.ok(bounds.left>=0&&bounds.right<=640&&bounds.scroll<=bounds.width+1,'common menu fits narrow/zoom');
  await page.screenshot({path:'/tmp/rnassistant-agent-recovery-narrow.png'});
  await page.evaluate(()=>window.failRecovery=true);
  await menu.getByRole('menuitem',{name:'Продолжить выбранных (2)',exact:true}).click();
  await menu.getByText(/Соединение прервано/).waitFor();
  assert.equal(await menu.getByRole('menuitem',{name:'Продолжить выбранных (0)',exact:true}).isDisabled(),true,'uncertain response requires fresh inspection');
  await page.keyboard.press('Escape');
  await page.evaluate(()=>{document.documentElement.style.zoom='1';window.failRecovery=false;});
  await page.setViewportSize({width:1280,height:900});
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  await openOffice();
  await page.screenshot({path:'/tmp/rnassistant-agent-recovery.png'});
  await page.keyboard.press('Escape');
  assert.equal(await menu.count(),0,'Escape closes and restores keyboard focus');
  assert.equal(await page.evaluate(()=>document.activeElement===RNAssistantConversationPanel.office('preview-chat').node('composerAddButton')),true);
  await page.evaluate(async()=>{
   const plugin=await send('getPluginChat',{pluginId:'data-templates'});
   window.recoveryTarget={source:'workspace',workspaceId:plugin.project.workspaceId,sessionId:plugin.sessionId};
   await RNAssistantConversationPanel.open(recoveryTarget);
  });
  await page.waitForFunction(()=>{const c=RNAssistantConversationPanel.find(recoveryTarget);return c&&!c.loading;});
  await page.evaluate(()=>{const c=RNAssistantConversationPanel.find(recoveryTarget);c.node('resumeAgentWaitsButton').click();});
  await menu.getByRole('menuitemcheckbox',{name:'Проверка правил',exact:true}).waitFor();
  assert.deepEqual(await page.evaluate(()=>{const p=recoveryCalls.filter(c=>c.type==='getRecoverableAgentWaits').at(-1).payload;delete p.afterSessionId;return p;}),
   await page.evaluate(()=>recoveryTarget),'workspace/plugin shares exact scoped command');
  await page.keyboard.press('Escape');
  await page.evaluate(()=>{holdRecoveryList=true;const c=RNAssistantConversationPanel.find(recoveryTarget);c.node('resumeAgentWaitsButton').click();});
  await page.waitForFunction(()=>!!window.releaseRecoveryList);
  await page.evaluate(()=>{RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key(recoveryTarget));releaseRecoveryList();});
  assert.equal(await menu.count(),0,'closed view does not retain a recovery popup');
  assert.equal(await page.evaluate(()=>recoveryCalls.filter(c=>c.type==='resumeAgentWaits').length),2,'closing/refreshing does not dispatch extra resumes');
  assert.deepEqual(errors,[]);
  console.log('PASS agent recovery UI: exact selection, pagination, partial/uncertain receipts, shared scope, narrow/zoom, keyboard and cleanup');
 } finally {if(browser)await browser.close();server.kill();}
})().catch(error=>{console.error(error);process.exitCode=1;});
