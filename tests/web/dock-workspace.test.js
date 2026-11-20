'use strict';
const assert=require('node:assert/strict');
const {withPreviewPage}=require('./preview-session');
(async()=>withPreviewPage(async({page,errors})=>{
  page.setDefaultTimeout(8000);
  await page.setViewportSize({width:1440,height:900});
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  const chat=page.locator('[data-tab-panel-id^="chat:"]').first(), inspector=page.locator('[data-tab-panel-id=inspector]');
  const input=page.locator('[data-chat-node=chatInput]');
  await input.fill('Черновик при переносе');
  await page.evaluate(()=>{window.dockProbe={nodes:['chatForm','messages','chatInput'].map(id=>RNAssistantConversationPanel.office('preview-chat').node(id)),calls:[]};const old=send;send=(type,...args)=>{dockProbe.calls.push(type);return old(type,...args);};});
  async function groupAction(tab,name){await page.locator('.dv-groupview').filter({has:tab}).getByRole('button',{name:'Действия с вкладкой',exact:true}).click();await page.getByRole('menuitem',{name,exact:true}).click();}
  async function split(tab,name){await page.locator('.dv-groupview').filter({has:tab}).getByRole('button',{name,exact:true}).click();}
  const before=(await input.boundingBox()).width;
  await groupAction(chat,'Развернуть группу');await page.waitForFunction(width=>document.querySelector('[data-chat-node=chatInput]').getBoundingClientRect().width>width+200,before);
  await groupAction(chat,'Восстановить группу');
  await groupAction(chat,'Собрать в одну группу');
  await split(chat,'Открыть экран справа');
  await page.locator('.dock-screen-chooser input').fill('Инспектор');await page.locator('.dock-screen-chooser input').press('Enter');
  assert.equal(await inspector.count(),1);assert.equal(await page.locator('.dock-group-actions').count(),2);
  assert.equal(await page.locator('[data-tab-panel-id^="choose-"]').count(),0);
  await split(chat,'Открыть экран снизу');await page.locator('.dock-screen-chooser input').fill('Инспектор');
  await page.locator('.dock-screen-chooser').getByRole('button',{name:'Инспектор',exact:true}).click();
  await page.waitForFunction(()=>document.getElementById('chatInspector').getBoundingClientRect().top>document.querySelector('[data-chat-node=chatForm]').getBoundingClientRect().top);
  const boxes=await Promise.all([page.locator('[data-chat-node=chatForm]').boundingBox(),page.locator('#chatInspector').boundingBox()]);assert.ok(boxes[1].y>boxes[0].y);
  assert.equal(await input.inputValue(),'Черновик при переносе');
  assert.ok(await page.evaluate(()=>dockProbe.nodes.every(n=>n===document.getElementById(n.id))));
  assert.deepEqual(await page.evaluate(()=>dockProbe.calls.filter(t=>!['saveChatDraft','reportClientTiming'].includes(t))),[],'placement performs no domain reads');
  await groupAction(inspector,'Закрыть группу');assert.equal(await inspector.count(),0);assert.equal(await chat.count(),1);
  await page.locator('#toggleChatInspectorButton').click();await inspector.waitFor();
  await page.locator('#workspaceLayout').click();await page.getByRole('menuitem',{name:'Сохранить раскладку',exact:true}).click();
  await chat.click();await input.focus();await page.setViewportSize({width:420,height:800});
  await page.waitForFunction(()=>document.querySelectorAll('.dock-group-actions').length===1);
  assert.equal(await input.inputValue(),'Черновик при переносе');assert.ok(await input.evaluate(n=>document.activeElement===n));
  await page.setViewportSize({width:1440,height:900});await page.waitForFunction(()=>document.querySelectorAll('.dock-group-actions').length===2);
  await page.waitForFunction(()=>!!JSON.parse(localStorage.getItem('rnassistant.ui.dock.s1.v1'))?.layout.panels.inspector);
  await page.reload();await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  assert.equal(await page.locator('.dock-group-actions').count(),2);assert.equal(await inspector.count(),1);
  const groupOf = id => page.evaluate(id => {
    const key = RNAssistantConversationPanel.key({source:'office',sessionId:id});
    return document.querySelector('[data-tab-panel-id="' + key + '"]')?.closest('.dv-groupview')
      ?.querySelector('.dock-group-actions')?.dataset.groupId;
  }, id);
  await page.evaluate(() => createChat());
  const second = await page.evaluate(() => state.activeChatId);
  assert.notEqual(second, 'preview-chat');
  assert.notEqual(await groupOf(second), await groupOf('preview-chat'), 'new chat opens beside its origin');
  await page.evaluate(() => createChat());
  const third = await page.evaluate(() => state.activeChatId);
  assert.notEqual(third, second);
  assert.equal(await groupOf(third), await groupOf('preview-chat'), 'next chat reuses a neighboring conversation group');
  await page.setViewportSize({width:420,height:800});
  await page.waitForFunction(() => document.querySelectorAll('.dock-group-actions').length === 1);
  await page.evaluate(second => {
    RNAssistantDockWorkspace.show('settings');
    RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key({source:'office',sessionId:second}));
  }, second);
  await page.setViewportSize({width:1440,height:900});
  await page.waitForFunction(() => document.querySelectorAll('.dock-group-actions').length > 1);
  assert.equal(await page.locator('[data-tab-panel-id=settings]').count(), 1, 'narrow additions survive expansion');
  assert.equal(await groupOf(second), undefined, 'narrow close survives expansion');
  assert.ok(await groupOf(third));
  await split(page.locator('[data-tab-panel-id=settings]'), 'Открыть экран снизу');
  const chosenGroup = await page.locator('[data-tab-panel-id^="choose-"]').evaluate(tab =>
    tab.closest('.dv-groupview').querySelector('.dock-group-actions').dataset.groupId);
  await page.locator('.dock-screen-chooser').getByRole('button', {name:'Открыть чат',exact:true}).click();
  assert.equal(await page.locator('[data-tab-panel-id^="choose-"]').count(), 1);
  await page.locator('#chatSessionList [data-tree-key="chat:' + third + '"] .navigation-tree-row').click();
  await page.waitForFunction(() => !document.querySelector('[data-tab-panel-id^="choose-"]'));
  assert.equal(await groupOf(third), chosenGroup, 'choosing from the tree honors the explicit Dock group');
  assert.deepEqual(errors,[]);console.log('PASS Dock direct commands, split, singleton movement, group close, retained draft/DOM, narrow focus and restore');
}))().catch(e=>{console.error(e);process.exitCode=1;});
