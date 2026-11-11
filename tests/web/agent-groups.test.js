'use strict';
const assert=require('node:assert/strict'), path=require('node:path'), {spawn}=require('node:child_process'), {chromium}=require('playwright');
(async()=>{
 const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd:path.resolve(__dirname,'../..')});
 server.stderr.resume();
 const url=await new Promise((resolve,reject)=>{
  server.once('error',reject);server.once('exit',code=>reject(new Error('preview exit '+code)));
  server.stdout.on('data',data=>{const m=data.toString().match(/http:\/\/127\.0\.0\.1:\d+\//);if(m)resolve(m[0]);});
 });
 let browser;
 try {
  browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
  const page=await browser.newPage({viewport:{width:1280,height:900}}),errors=[];
  page.setDefaultTimeout(7000);page.on('pageerror',error=>errors.push(error.message));
  // A native dialog is a regression, not the interaction fixture for group commands.
  page.on('dialog',dialog=>{errors.push('Unexpected native '+dialog.type()+': '+dialog.message());dialog.dismiss();});
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
   window.groupCalls=[];window.groupSequence=0;const original=send, clone=value=>JSON.parse(JSON.stringify(value));
   window.send=async(type,payload,progress)=>{
    if(type==='changeChatGroup'){
     groupCalls.push({type,payload:clone(payload)});
     if(window.failGroup){failGroup=false;throw new Error('Ответ потерян');}
     const scope=groupFixtures.find(item=>(item.workspaceId||null)===(payload.workspaceId||null));
     const group=scope.groups.find(item=>item.id===payload.groupId);
     if(group&&payload.revision!==group.revision)throw new Error('Группа изменилась');
     if(payload.action==='Create')scope.groups.push({id:'cg_new_'+(++groupSequence),name:payload.name,revision:'r1',members:[]});
     if(payload.action==='Delete')scope.groups=scope.groups.filter(item=>item!==group);
     if(payload.action==='Remove')group.members=group.members.filter(id=>!payload.members.includes(id));
     if(payload.action==='Add')group.members=Array.from(new Set(group.members.concat(payload.members)));
     if(payload.action==='Rename')group.name=payload.name;
     scope.generation++;if(group)group.revision='r'+scope.generation;
     const response={changed:true,scope:clone(scope)},lost=!!window.failGroupAfterWrite;
     window.failGroupAfterWrite=false;
     if(window.deferGroupResponse){
      window.deferGroupResponse=false;
      await new Promise(resolve=>{window.releaseGroupResponse=()=>{window.releaseGroupResponse=null;resolve();};});
     }
     if(lost)throw new Error('Ответ потерян после записи');
     return response;
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
  const calls=()=>page.evaluate(()=>groupCalls.length);
  const paint=()=>page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  const focused=locator=>locator.evaluate(node=>node===document.activeElement);
  async function opened(title){
   const dialog=page.getByRole('dialog',{name:title,exact:true});await dialog.waitFor();
   assert.equal(await page.getByRole('dialog').count(),1,'only one shared dialog is open');return dialog;
  }
  async function closeDialog(dialog){
   await dialog.getByRole('button',{name:/^(Отмена|Закрыть)$/}).click();await dialog.waitFor({state:'hidden'});await paint();
  }
  async function refreshGroups(){await page.evaluate(async()=>applyChatCatalogState(await send('listChats',{})));}
  async function assertDialogBounds(dialog){
   const bounds=await dialog.evaluate(node=>{
    const rect=node.getBoundingClientRect();
    return {left:rect.left,top:rect.top,right:rect.right,bottom:rect.bottom,width:rect.width,
     viewportWidth:innerWidth,viewportHeight:innerHeight,overflow:node.scrollWidth>node.clientWidth+1,
     clippedByDock:!!node.closest('.dv-content-container,.dv-render-overlay'),
     controlsFit:Array.from(node.querySelectorAll('input,button')).every(control=>{
      const box=control.getBoundingClientRect();return box.left>=rect.left&&box.right<=rect.right+1&&box.top>=rect.top&&box.bottom<=rect.bottom+1;
     })};
   });
   assert.ok(bounds.width>0&&bounds.left>=0&&bounds.top>=0&&bounds.right<=bounds.viewportWidth+1&&bounds.bottom<=bounds.viewportHeight+1,JSON.stringify(bounds));
   assert.equal(bounds.overflow,false,'dialog has no horizontal overflow');
   assert.equal(bounds.clippedByDock,false,'shared dialog is outside clipped Dockview content');
   assert.equal(bounds.controlsFit,true,'input and both actions remain inside the dialog');
  }
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
  const documentKey=await row(groupKey).evaluate(node=>node.parentElement.dataset.parentKey);
  const createButton=()=>row(documentKey).locator('[data-tree-action]');
  async function createDialog(){await menu(documentKey,'Новая группа чатов…');return opened('Новая группа чатов');}
  let before=await calls(),dialog=await createDialog();
  assert.equal(await focused(dialog.getByRole('textbox',{name:'Название группы',exact:true})),true,'create focuses the name input');
  await closeDialog(dialog);
  assert.equal(await calls(),before,'Cancel does not create a group');
  assert.equal(await focused(createButton()),true,'Cancel restores the invoking tree control');
  dialog=await createDialog();
  const nameInput=()=>dialog.getByRole('textbox',{name:'Название группы',exact:true});
  await nameInput().fill('Черновик IME');
  const imeEnterSuppressed=await nameInput().evaluate(node=>{
   node.dispatchEvent(new CompositionEvent('compositionstart',{bubbles:true,data:'字'}));
   const suppressed=!node.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',keyCode:229,isComposing:true,bubbles:true,cancelable:true}));
   node.form.requestSubmit();return suppressed;
  });
  await paint();assert.equal(imeEnterSuppressed,true,'IME Enter is consumed without submitting');
  assert.equal(await calls(),before,'form submission while composition is active sends no command');
  assert.equal(await dialog.isVisible(),true,'composition keeps the dialog open');
  await nameInput().dispatchEvent('compositionend',{data:'字'});
  await nameInput().fill('   ');await nameInput().press('Enter');await paint();
  assert.equal(await calls(),before,'blank trimmed name sends no command');
  assert.equal(await dialog.isVisible(),true,'validation preserves the dialog and draft');
  await dialog.getByRole('alert').waitFor();
  await nameInput().fill('  Новая группа  ');await nameInput().press('Enter');
  await dialog.waitFor({state:'hidden'});
  await page.locator('#chatSessionList').getByRole('treeitem',{name:'Новая группа',exact:true}).waitFor();
  await paint();
  assert.deepEqual(await page.evaluate(()=>({action:groupCalls.at(-1).payload.action,name:groupCalls.at(-1).payload.name,
   members:groupFixtures[0].groups.find(group=>group.name==='Новая группа').members})),{action:'Create',name:'Новая группа',members:[]},'Enter creates an empty group with a trimmed name');
  assert.equal(await focused(createButton()),true,'successful create restores focus after navigation repaint');

  before=await calls();await menu(groupKey,'Переименовать группу');dialog=await opened('Переименовать группу');
  assert.equal(await nameInput().inputValue(),'Шахматы','rename starts with the current name');
  assert.equal(await focused(nameInput()),true);
  assert.deepEqual(await nameInput().evaluate(node=>[node.selectionStart,node.selectionEnd]),[0,'Шахматы'.length],'rename selects the original name');
  for(let index=0;index<5;index++){
   await page.keyboard.press(index%2?'Tab':'Shift+Tab');
   assert.equal(await dialog.evaluate(node=>node.contains(document.activeElement)),true,'Tab and Shift+Tab stay inside the modal');
  }
  await page.keyboard.press('Escape');await dialog.waitFor({state:'hidden'});await paint();
  assert.equal(await calls(),before,'Escape cancels rename');
  assert.equal(await focused(row(groupKey).locator('[data-tree-action]')),true,'Escape returns from dialog to its menu trigger');
  await menu(groupKey,'Переименовать группу');dialog=await opened('Переименовать группу');
  await nameInput().fill('  Шахматы  ');await nameInput().press('Enter');await dialog.waitFor({state:'hidden'});
  assert.equal(await calls(),before,'unchanged trimmed rename closes without a mutation');
  const shownRevision=await page.evaluate(()=>groupFixtures[0].groups.find(group=>group.id==='cg_chess').revision);
  await menu(groupKey,'Переименовать группу');dialog=await opened('Переименовать группу');
  await nameInput().fill('  Шахматный анализ  ');await nameInput().press('Enter');await dialog.waitFor({state:'hidden'});
  assert.deepEqual(await page.evaluate(()=>({action:groupCalls.at(-1).payload.action,name:groupCalls.at(-1).payload.name,revision:groupCalls.at(-1).payload.revision})),
   {action:'Rename',name:'Шахматный анализ',revision:shownRevision},'rename echoes the exact revision displayed before user input');
  await row(groupKey).getByRole('button',{name:'Шахматный анализ',exact:true}).waitFor();

  // The shared controls inherit the same geometry outside the dialog as inside it.
  await menu(groupKey,'Переименовать группу');dialog=await opened('Переименовать группу');
  const geometry=await dialog.evaluate(node=>Array.from(node.querySelectorAll('input,button')).map(control=>{
   const reference=document.createElement(control.tagName);
   reference.type=control.type;reference.className=control.tagName==='INPUT'?'rna-form-control':'button';
   if(control.classList.contains('compact'))reference.classList.add('compact');
   if(control.classList.contains('primary'))reference.classList.add('primary');
   if(control.classList.contains('danger'))reference.classList.add('danger');
   reference.textContent=control.textContent;document.body.appendChild(reference);
   const read=element=>{const css=getComputedStyle(element);return [css.minHeight,css.paddingTop,css.paddingRight,css.fontSize,css.borderRadius];};
   const result={actual:read(control),shared:read(reference)};reference.remove();return result;
  }));
  geometry.forEach(value=>assert.deepEqual(value.actual,value.shared,'dialog reuses shared input/button geometry'));
  const palette=[];
  for(const theme of ['light','dark']){
   await page.evaluate(theme=>document.documentElement.setAttribute('data-theme',theme),theme);await paint();
   const colors=await dialog.evaluate(node=>{
    const reference=document.createElement('span');reference.style.cssText='background:var(--surface);color:var(--ink);border:1px solid var(--line)';
    document.body.appendChild(reference);
    const read=element=>{const css=getComputedStyle(element);return [css.backgroundColor,css.color,css.borderTopColor];};
    const result={actual:read(node),shared:read(reference)};reference.remove();return result;
   });
   assert.deepEqual(colors.actual,colors.shared,'dialog follows '+theme+' surface, text and border tokens');palette.push(colors.actual);
   if(theme==='dark')await page.screenshot({path:'/tmp/rnassistant-agent-group-dialog-dark.png'});
  }
  assert.notEqual(palette[0][0],palette[1][0],'light and dark dialog surfaces differ');
  await page.evaluate(()=>document.documentElement.setAttribute('data-theme','light'));await paint();
  await page.screenshot({path:'/tmp/rnassistant-agent-group-dialog-wide.png'});
  await page.keyboard.press('Escape');await dialog.waitFor({state:'hidden'});

  // A response held after dispatch cannot cause duplicate writes or steal later focus.
  await page.evaluate(()=>window.deferGroupResponse=true);
  before=await calls();dialog=await createDialog();await nameInput().fill('Ожидающая группа');await nameInput().press('Enter');
  await page.waitForFunction(()=>typeof releaseGroupResponse==='function');
  assert.equal(await dialog.getByRole('button',{name:'Создать',exact:true}).isDisabled(),true);
  assert.equal(await nameInput().isDisabled(),true);
  await page.keyboard.press('Enter');await paint();assert.equal(await calls(),before+1,'repeated Enter during dispatch sends only once');
  await dialog.getByRole('button',{name:'Закрыть',exact:true}).click();await dialog.waitFor({state:'hidden'});
  await page.evaluate(()=>{window.groupLaterFocus=RNAssistantConversationPanel.office('preview-summary').node('chatInput');groupLaterFocus.focus();releaseGroupResponse();});
  await page.locator('#chatSessionList').getByRole('treeitem',{name:'Ожидающая группа',exact:true}).waitFor();await paint();
  assert.equal(await page.evaluate(()=>document.activeElement===groupLaterFocus),true,'closing pending presentation does not cancel the write or capture later focus');
  assert.equal(await page.getByRole('dialog').count(),0,'late success does not reopen the dialog');

  // Revision authority stays in the runtime. Editing the name cannot silently refresh it.
  before=await calls();await menu(groupKey,'Переименовать группу');dialog=await opened('Переименовать группу');
  const staleRevision=await page.evaluate(()=>{const group=groupFixtures[0].groups.find(group=>group.id==='cg_chess'),prior=group.revision;group.revision='external-revision';return prior;});
  await nameInput().fill('Конфликтующее имя');await nameInput().press('Enter');
  await dialog.getByRole('alert').filter({hasText:'Группа изменилась'}).waitFor();
  assert.equal(await page.evaluate(()=>groupCalls.at(-1).payload.revision),staleRevision);
  assert.equal(await nameInput().inputValue(),'Конфликтующее имя','error retains the entered name');
  assert.equal(await dialog.getByRole('button',{name:'Сохранить',exact:true}).isDisabled(),true,'failed mutation has no implicit retry action');
  await page.evaluate(()=>renderChatSessions({navigationOnly:true}));await paint();
  assert.equal(await calls(),before+1,'revision refusal never repeats the command');
  await closeDialog(dialog);await refreshGroups();

  // Simulate a committed mutation whose reply is lost, rather than only a pre-write error.
  await page.evaluate(()=>window.failGroupAfterWrite=true);
  before=await calls();dialog=await createDialog();await nameInput().fill('Группа с потерянным ответом');await nameInput().press('Enter');
  await dialog.getByRole('alert').filter({hasText:'Ответ потерян после записи'}).waitFor();
  assert.equal(await dialog.getByRole('button',{name:'Создать',exact:true}).isDisabled(),true);
  await page.evaluate(()=>renderChatSessions({navigationOnly:true}));await paint();assert.equal(await calls(),before+1);
  await closeDialog(dialog);await refreshGroups();
  assert.equal(await page.evaluate(()=>groupFixtures[0].groups.filter(group=>group.name==='Группа с потерянным ответом').length),1,'read refresh observes one creation and does not replay it');

  // An unsubmitted dialog is discarded when Office/Workspace context changes.
  before=await calls();dialog=await createDialog();await nameInput().fill('Не отправлять после перехода');
  await page.evaluate(()=>RNAssistantProjectChats.setScope('projects'));
  await dialog.waitFor({state:'hidden'});assert.equal(await calls(),before);
  const projectCreate=()=>menu('project:preview-data-workspace','Новая группа чатов…');
  await projectCreate();dialog=await opened('Новая группа чатов');
  await nameInput().fill('Не отправлять в Office');
  await page.evaluate(()=>RNAssistantProjectChats.setScope('office'));
  await dialog.waitFor({state:'hidden'});assert.equal(await calls(),before,'switching either way never submits a stale form');

  // A failed accepted Office command must not paint an error or restore focus in Workspace.
  await page.evaluate(()=>{window.deferGroupResponse=true;window.failGroupAfterWrite=true;});
  before=await calls();await menu(groupKey,'Переименовать группу');dialog=await opened('Переименовать группу');
  await nameInput().fill('Позднее имя');await nameInput().press('Enter');await page.waitForFunction(()=>typeof releaseGroupResponse==='function');
  await page.evaluate(()=>RNAssistantProjectChats.setScope('projects',{workspaceId:'preview-data-workspace',sessionId:'preview-project-review'}));
  await dialog.waitFor({state:'hidden'});
  await page.waitForFunction(()=>{
   const panel=RNAssistantConversationPanel.find({source:'workspace',workspaceId:'preview-data-workspace',sessionId:'preview-project-review'});
   return panel&&panel===RNAssistantConversationPanel.selected()&&!panel.loading&&!panel.node('chatInput').disabled&&panel.node('chatInput').getClientRects().length>0;
  });
  await page.evaluate(()=>{window.groupLaterFocus=RNAssistantConversationPanel.find({source:'workspace',workspaceId:'preview-data-workspace',sessionId:'preview-project-review'}).node('chatInput');groupLaterFocus.focus();releaseGroupResponse();});
  await paint();
  assert.equal(await page.getByRole('dialog').count(),0);
  assert.equal(await page.evaluate(()=>document.activeElement===groupLaterFocus),true,'late Office error cannot steal Workspace focus');
  assert.equal(await page.locator('[data-tree-key="workspace-section:projects"]').getByText('Группы недоступны',{exact:true}).count(),0,'late Office error cannot poison Workspace catalog');
  assert.equal(await calls(),before+1);
  await page.evaluate(()=>RNAssistantProjectChats.setScope('office'));await refreshGroups();

  // Reset invalidates both an open form and a response already dispatched in its old lifetime.
  before=await calls();dialog=await createDialog();await nameInput().fill('Старый черновик');
  await page.evaluate(()=>{RNAssistantChatGroups.reset();RNAssistantChatGroups.accept(JSON.parse(JSON.stringify(groupFixtures)));renderChatSessions({navigationOnly:true});});
  await dialog.waitFor({state:'hidden'});assert.equal(await calls(),before,'reset closes an unsubmitted form');
  await page.evaluate(()=>{window.groupBeforeReset=JSON.parse(JSON.stringify(groupFixtures));window.deferGroupResponse=true;});
  dialog=await createDialog();await nameInput().fill('Ответ из прежней сессии');await nameInput().press('Enter');
  await page.waitForFunction(()=>typeof releaseGroupResponse==='function');
  await page.evaluate(()=>{RNAssistantChatGroups.reset();RNAssistantChatGroups.accept(groupBeforeReset);renderChatSessions({navigationOnly:true});releaseGroupResponse();});
  await dialog.waitFor({state:'hidden'});await paint();
  assert.equal(await page.locator('#chatSessionList').getByRole('treeitem',{name:'Ответ из прежней сессии',exact:true}).count(),0,'late response after reset cannot replace the new catalog');
  assert.equal(await calls(),before+1);await refreshGroups();

  const metrics=await page.evaluate(()=>Array.from(document.querySelectorAll('#chatSessionList .navigation-tree-row')).filter(node=>node.offsetHeight).map(node=>{
   const css=getComputedStyle(node);return {height:css.height,font:css.fontSize,minHeight:css.minHeight};}));
  assert.equal(new Set(metrics.map(x=>JSON.stringify(x))).size,1,'groups and chats share NavigationTree geometry');
  const demo=await browser.newPage({viewport:{width:1280,height:900}});demo.setDefaultTimeout(7000);demo.on('pageerror',error=>errors.push('Demo: '+error.message));await demo.goto(url);
  const reference=await demo.locator('.nav-row').first().evaluate(node=>{const css=getComputedStyle(node);return {minHeight:css.minHeight,font:css.fontSize};});
  assert.equal(metrics[0].minHeight,reference.minHeight,'row minimum matches the accepted Demo at the same viewport');
  assert.equal(metrics[0].font,reference.font,'tree typography matches the accepted Demo');await demo.close();
  await page.screenshot({path:'/tmp/rnassistant-agent-groups-wide.png'});
  await page.evaluate(()=>RNAssistantDockWorkspace.show('inspector'));
  assert.equal(await page.locator('.dv-groupview:visible').count()>1,true,'dialog is tested over split Dockview groups');
  const inspectorWidth=await page.locator('[data-tab-panel-id="inspector"]').evaluate(node=>node.closest('.dv-groupview').getBoundingClientRect().width);
  assert.ok(inspectorWidth>0&&inspectorWidth<=340,'actual Inspector Dock group is narrow: '+inspectorWidth+'px');
  await menu(groupKey,'Переименовать группу');dialog=await opened('Переименовать группу');await assertDialogBounds(dialog);
  assert.ok(await dialog.evaluate(node=>node.getBoundingClientRect().width)>inspectorWidth,'dialog is not clipped to the narrow Dock group');
  await page.keyboard.press('Escape');await dialog.waitFor({state:'hidden'});
  await page.evaluate(()=>document.documentElement.style.zoom='1.25');await page.setViewportSize({width:420,height:800});
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  await page.locator('#productMobileMenuButton').click();await row(chessChild).waitFor();
  const bounds=await page.locator('#chatSessionList').evaluate(node=>({width:node.clientWidth,scroll:node.scrollWidth}));
  assert.ok(bounds.width>0&&bounds.scroll<=bounds.width+1,'nested groups fit the narrow tree at 125%');
  await page.screenshot({path:'/tmp/rnassistant-agent-groups-narrow.png'});
  await menu(groupKey,'Переименовать группу');dialog=await opened('Переименовать группу');await assertDialogBounds(dialog);
  await page.screenshot({path:'/tmp/rnassistant-agent-group-dialog-narrow.png'});
  // Runtime permits up to 120 characters; a long unbroken/HTML-looking name must stay text.
  const longName='Группа '+('Ж'.repeat(90))+' <img src=x>';
  assert.ok(longName.length<=120);
  await nameInput().fill(longName);await nameInput().press('Enter');await dialog.waitFor({state:'hidden'});
  await menu(groupKey,'Удалить группу');dialog=await opened('Удалить группу');await assertDialogBounds(dialog);
  assert.ok((await dialog.innerText()).includes(longName),'confirmation shows the complete name as text');
  assert.equal(await dialog.locator('img,script').count(),0,'group name never becomes markup');
  await page.setViewportSize({width:418,height:800});await paint();await assertDialogBounds(dialog);
  assert.equal(await focused(dialog.getByRole('button',{name:'Отмена',exact:true})),true,'destructive confirmation focuses Cancel');
  before=await calls();await page.keyboard.press('Enter');await dialog.waitFor({state:'hidden'});
  assert.equal(await calls(),before,'Enter on the initial Cancel cannot delete a group');
  await page.evaluate(()=>document.documentElement.style.zoom='1');await page.setViewportSize({width:1280,height:900});
  await menu(groupKey,'Удалить группу');dialog=await opened('Удалить группу');
  assert.match(await dialog.innerText(),/Чаты и их история сохранятся/);
  await dialog.getByRole('button',{name:'Удалить',exact:true}).click();await dialog.waitFor({state:'hidden'});await row(groupKey).waitFor({state:'detached'});
  assert.equal(await page.evaluate(()=>!!RNAssistantConversationPanel.office('preview-summary')),true,'group deletion retains the open chat');
  assert.equal(await page.evaluate(()=>state.chats.some(chat=>chat.Id==='preview-summary')),true,'group deletion preserves the catalog');
  await page.evaluate(()=>window.failGroup=true);
  await action('chat:preview-summary');
  await page.getByRole('menuitem',{name:'Группы чата…',exact:true}).click();
  await page.getByRole('menuitemcheckbox',{name:'Проверка',exact:true}).click();
  await page.getByText('Группы недоступны',{exact:true}).waitFor();
  const unknownCalls=await calls();
  await page.evaluate(()=>renderChatSessions({navigationOnly:true}));
  assert.equal(await calls(),unknownCalls,'uncertain mutation never retries during render');
  await page.evaluate(async()=>applyChatCatalogState(await send('listChats',{})));
  await page.getByText('Группы недоступны',{exact:true}).waitFor({state:'detached'});
  await page.evaluate(()=>RNAssistantProjectChats.setScope('projects'));
  const projectChild='project-chat:preview-data-workspace:preview-project-clean:group:cg_chess';
  await row(projectChild).waitFor();
  await projectCreate();dialog=await opened('Новая группа чатов');
  await nameInput().fill('Группа проекта');await nameInput().press('Enter');await dialog.waitFor({state:'hidden'});
  assert.deepEqual(await page.evaluate(()=>({action:groupCalls.at(-1).payload.action,workspaceId:groupCalls.at(-1).payload.workspaceId,
   sessionId:groupCalls.at(-1).payload.sessionId})),{action:'Create',workspaceId:'preview-data-workspace',sessionId:'preview-project-review'},'Workspace dialog addresses the exact existing scope');
  await menu(projectChild,'Убрать из группы');await row(projectChild).waitFor({state:'detached'});
  assert.equal(await page.evaluate(()=>groupCalls.at(-1).payload.workspaceId),'preview-data-workspace','Workspace uses the same group command with exact scope');
  await page.evaluate(()=>RNAssistantConversationPanel.open(groupPlugin));
  await page.waitForFunction(()=>RNAssistantConversationPanel.find(groupPlugin));
  const pluginKey=await page.evaluate(()=>'project-chat:'+groupPlugin.workspaceId+':'+groupPlugin.sessionId+':group:cg_plugin');
  await row(pluginKey).waitFor();
  const pluginGroupKey=await row(pluginKey).evaluate(node=>node.parentElement.dataset.parentKey);
  await menu(pluginGroupKey,'Переименовать группу');dialog=await opened('Переименовать группу');
  await nameInput().fill('Группа плагина');await nameInput().press('Enter');await dialog.waitFor({state:'hidden'});
  assert.deepEqual(await page.evaluate(()=>({action:groupCalls.at(-1).payload.action,workspaceId:groupCalls.at(-1).payload.workspaceId,
   revision:groupCalls.at(-1).payload.revision})),{action:'Rename',workspaceId:await page.evaluate(()=>groupPlugin.workspaceId),revision:'r1'},'plugin rename uses the shared dialog and displayed revision');
  await menu(pluginKey,'Убрать из группы');
  assert.equal(await page.evaluate(()=>groupCalls.at(-1).payload.workspaceId),await page.evaluate(()=>groupPlugin.workspaceId),'plugin chat uses the common Workspace tree');
  assert.deepEqual(errors,[]);
  console.log('PASS groups: shared dialogs create/rename/delete, IME/keyboard/focus/cancel, revision conflict, no duplicate or uncertain retries, context/reset lifetime, narrow Dockview/light/dark/420+418px/125%, safe long names, creator nesting, same chat, memberships, Office/Workspace/plugin');
 } finally {if(browser)await browser.close();server.kill();}
})().catch(error=>{console.error(error);process.exitCode=1;});
