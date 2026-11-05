'use strict';
const assert=require('node:assert/strict'), path=require('node:path'), {spawn}=require('node:child_process'), {chromium}=require('playwright');
(async()=>{
 const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd:path.resolve(__dirname,'../..')});
 const url=await new Promise(resolve=>server.stdout.on('data',data=>{const m=data.toString().match(/http:\/\/127\.0\.0\.1:\d+\//);if(m)resolve(m[0]);}));
 let browser;
 try {
  browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
  const page=await browser.newPage({viewport:{width:1280,height:900}}),errors=[];
  page.setDefaultTimeout(7000);page.on('pageerror',error=>errors.push(error.message));
  page.on('dialog',dialog=>dialog.accept(dialog.type()==='prompt'?'Новая группа':undefined));
  await page.goto(url+'preview/web/?scenario=ready');
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise&&RNAssistantConversationPanel.office('preview-chat'));
  await page.evaluate(async()=>{
   const header=state.chats.find(chat=>chat.Id==='preview-chat');
   const plugin=await send('getPluginChat',{pluginId:'data-templates'});
   window.groupPlugin={source:'workspace',workspaceId:plugin.project.workspaceId,sessionId:plugin.sessionId};
   function groups(a,b){return [{id:'cg_chess',name:'Шахматы',revision:'r1',members:[a,b]},
    {id:'cg_review',name:'Проверка',revision:'r1',members:[b]}];}
   window.groupFixtures=[{host:header.Host,documentKey:header.DocumentKey,generation:1,groups:groups('preview-chat','preview-summary')},
    {host:'Workspace',documentKey:'preview-data-workspace',workspaceId:'preview-data-workspace',generation:1,
     groups:groups('preview-project-review','preview-project-clean')},
    {host:'Workspace',documentKey:groupPlugin.workspaceId,workspaceId:groupPlugin.workspaceId,generation:1,
     groups:[{id:'cg_plugin',name:'Плагин',revision:'r1',members:[groupPlugin.sessionId]}]}];
   window.groupCalls=[]; const original=send, clone=value=>JSON.parse(JSON.stringify(value));
   window.send=async(type,payload,progress)=>{
    if(type==='changeChatGroup'){
     groupCalls.push({type,payload:clone(payload)});
     if(window.failGroup){failGroup=false;throw new Error('Ответ потерян');}
     const scope=groupFixtures.find(item=>(item.workspaceId||null)===(payload.workspaceId||null));
     const group=scope.groups.find(item=>item.id===payload.groupId);
     if(group&&payload.revision!==group.revision)throw new Error('Группа изменилась');
     if(payload.action==='Create')scope.groups.push({id:'cg_new',name:payload.name,revision:'r1',members:[]});
     if(payload.action==='Delete')scope.groups=scope.groups.filter(item=>item!==group);
     if(payload.action==='Remove')group.members=group.members.filter(id=>!payload.members.includes(id));
     if(payload.action==='Add')group.members=Array.from(new Set(group.members.concat(payload.members)));
     if(payload.action==='Rename')group.name=payload.name;
     scope.generation++;if(group)group.revision='r'+scope.generation;
     return {changed:true,scope:clone(scope)};
    }
    const response=await original(type,payload,progress);
    if(['listChats','getChatState','getProjectChats','getProjectChat'].includes(type))response.groupScopes=clone(groupFixtures);
    const chats=response.chats||response.projects?.flatMap(project=>project.chats)||[];
    chats.forEach(chat=>{if(chat.Id==='preview-summary')chat.CreatorChatId='preview-chat';
     if(chat.Id==='preview-project-clean')chat.CreatorChatId='preview-project-review';});
    return response;
   };
   applyChatCatalogState(await send('listChats',{})); await RNAssistantProjectChats.refresh();
  });
  const row=key=>page.locator('[data-tree-key='+JSON.stringify(key)+'] > .navigation-tree-row');
  const chessChild='chat:preview-summary:group:cg_chess',reviewChild='chat:preview-summary:group:cg_review';
  await row(chessChild).waitFor();
  const marker=key=>row(key).locator('.agent-color-dot');
  assert.equal(await row(chessChild).locator('.navigation-tree-icon svg').count(),0,'chats have no chat icon');
  assert.equal(await marker(chessChild).evaluate(node=>getComputedStyle(node).width),'6px');
  assert.equal(await marker(chessChild).evaluate(node=>getComputedStyle(node).backgroundColor),await marker(reviewChild).evaluate(node=>getComputedStyle(node).backgroundColor),'one agent keeps its colour across groups');
  assert.equal(await row(chessChild).evaluate(node=>node.parentElement.dataset.parentKey),'chat:preview-chat:group:cg_chess');
  assert.ok((await row(reviewChild).evaluate(node=>node.parentElement.dataset.parentKey)).startsWith('chat-group:'),'missing creator remains a root in the other group');
  await row(chessChild).locator('.navigation-tree-label').click();
  await page.waitForFunction(()=>RNAssistantConversationPanel.office('preview-summary'));
  await page.evaluate(()=>window.firstGroupPanel=RNAssistantConversationPanel.office('preview-summary'));
  await row(reviewChild).locator('.navigation-tree-label').click();
  assert.equal(await page.evaluate(()=>firstGroupPanel===RNAssistantConversationPanel.office('preview-summary')),true,'all links open the same chat component');
  assert.equal(await page.locator('#chatSessionList .navigation-tree-row.selected').count(),1,'only the chosen occurrence is selected');
  async function action(key){await row(key).hover();await row(key).locator('[data-tree-action]').click();}
  async function menu(key,label){await action(key);await page.getByRole('menuitem',{name:label,exact:true}).click();}
  await menu(chessChild,'Убрать из группы');
  await row(chessChild).waitFor({state:'detached'});await row(reviewChild).waitFor();
  assert.equal(await page.evaluate(()=>groupCalls.at(-1).payload.revision),'r1','UI echoes the exact displayed revision');
  await menu(reviewChild,'Убрать из группы');
  await row('chat:preview-summary').waitFor();
  assert.equal(await row('chat:preview-summary').locator('.navigation-tree-icon').count(),0,'ungrouped chats have no icon or colour dot');
  // Ungrouped child is visible even when its creator remains in a group.
  assert.ok((await row('chat:preview-summary').evaluate(node=>node.parentElement.dataset.parentKey)).startsWith('document:'));
  await menu('chat:preview-summary','Группы чата…');
  await page.getByRole('menuitemcheckbox',{name:'Шахматы',exact:true}).click();
  await row(chessChild).waitFor();
  await page.evaluate(()=>{RNAssistantChatGroups.accept([Object.assign({},groupFixtures[0],{generation:0,groups:[]})]);renderChatSessions({navigationOnly:true});});
  await row(chessChild).waitFor();
  const groupKey=await page.locator('#chatSessionList [data-tree-key^="chat-group:"]').filter({has:page.locator(':scope > .navigation-tree-row .navigation-tree-label', {hasText:'Шахматы'})}).first().getAttribute('data-tree-key');
  await row(groupKey).locator('[data-tree-toggle]').click();assert.equal(await row(chessChild).isVisible(),false);
  await row(groupKey).locator('[data-tree-toggle]').click();await row(chessChild).waitFor();
  await action(groupKey);await page.keyboard.press('Escape');
  assert.equal(await page.locator('.rna-action-menu-popup').count(),0,'Escape returns from the existing shared action menu');
  const metrics=await page.evaluate(()=>Array.from(document.querySelectorAll('#chatSessionList .navigation-tree-row')).filter(node=>node.offsetHeight).map(node=>{
   const css=getComputedStyle(node);return {height:css.height,font:css.fontSize,minHeight:css.minHeight};}));
  assert.equal(new Set(metrics.map(x=>JSON.stringify(x))).size,1,'groups and chats share NavigationTree geometry');
  const demo=await browser.newPage({viewport:{width:1280,height:900}});demo.setDefaultTimeout(7000);demo.on('pageerror',error=>errors.push('Demo: '+error.message));await demo.goto(url);
  const reference=await demo.locator('.nav-row').first().evaluate(node=>{const css=getComputedStyle(node);return {minHeight:css.minHeight,font:css.fontSize};});
  assert.equal(metrics[0].minHeight,reference.minHeight,'row minimum matches the accepted Demo at the same viewport');
  assert.equal(metrics[0].font,reference.font,'tree typography matches the accepted Demo');await demo.close();
  await page.screenshot({path:'/tmp/rnassistant-agent-groups-wide.png'});
  await page.evaluate(()=>document.documentElement.style.zoom='1.25');await page.setViewportSize({width:420,height:800});
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  await page.locator('#productMobileMenuButton').click();await row(chessChild).waitFor();
  const bounds=await page.locator('#chatSessionList').evaluate(node=>({width:node.clientWidth,scroll:node.scrollWidth}));
  assert.ok(bounds.width>0&&bounds.scroll<=bounds.width+1,'nested groups fit the narrow tree at 125%');
  await page.screenshot({path:'/tmp/rnassistant-agent-groups-narrow.png'});
  await page.evaluate(()=>document.documentElement.style.zoom='1');await page.setViewportSize({width:1280,height:900});
  await menu(groupKey,'Удалить группу');await row(groupKey).waitFor({state:'detached'});
  assert.equal(await page.evaluate(()=>!!RNAssistantConversationPanel.office('preview-summary')),true,'group deletion retains the open chat');
  assert.equal(await page.evaluate(()=>state.chats.some(chat=>chat.Id==='preview-summary')),true,'group deletion preserves the catalog');
  await page.evaluate(()=>window.failGroup=true);
  await action('chat:preview-summary');
  await page.getByRole('menuitem',{name:'Группы чата…',exact:true}).click();
  await page.getByRole('menuitemcheckbox',{name:'Проверка',exact:true}).click();
  await page.getByText('Группы недоступны',{exact:true}).waitFor();
  const calls=await page.evaluate(()=>groupCalls.length);
  await page.evaluate(()=>renderChatSessions({navigationOnly:true}));
  assert.equal(await page.evaluate(()=>groupCalls.length),calls,'uncertain mutation never retries during render');
  await page.evaluate(async()=>applyChatCatalogState(await send('listChats',{})));
  await page.getByText('Группы недоступны',{exact:true}).waitFor({state:'detached'});
  await page.locator('#chatScopeSwitcher > summary').click();await page.locator('#chatScopeSwitcher [data-workspace-scope="projects"]').click();
  const projectChild='project-chat:preview-data-workspace:preview-project-clean:group:cg_chess';
  await row(projectChild).waitFor();
  await menu(projectChild,'Убрать из группы');await row(projectChild).waitFor({state:'detached'});
  assert.equal(await page.evaluate(()=>groupCalls.at(-1).payload.workspaceId),'preview-data-workspace','Workspace uses the same group command with exact scope');
  await page.evaluate(()=>RNAssistantConversationPanel.open(groupPlugin));
  await page.waitForFunction(()=>RNAssistantConversationPanel.find(groupPlugin));
  const pluginKey=await page.evaluate(()=>'project-chat:'+groupPlugin.workspaceId+':'+groupPlugin.sessionId+':group:cg_plugin');
  await row(pluginKey).waitFor();await menu(pluginKey,'Убрать из группы');
  assert.equal(await page.evaluate(()=>groupCalls.at(-1).payload.workspaceId),await page.evaluate(()=>groupPlugin.workspaceId),'plugin chat uses the common Workspace tree');
  assert.deepEqual(errors,[]);
  console.log('PASS groups: creator nesting, same chat, memberships, delete, stale/unknown, narrow/125%, Office/Workspace/plugin');
 } finally {if(browser)await browser.close();server.kill();}
})().catch(error=>{console.error(error);process.exitCode=1;});
