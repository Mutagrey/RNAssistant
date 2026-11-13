'use strict';
// Shell interaction against the real product DOM and the existing mock bridge.
const assert=require('node:assert/strict'),path=require('node:path'),fs=require('node:fs');
const {spawn}=require('node:child_process'),{chromium}=require('playwright');
(async()=>{
 const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd:path.resolve(__dirname,'../..')});server.stderr.resume();
 let browser;
 try {
  const address=await new Promise((resolve,reject)=>{server.once('error',reject);server.stdout.on('data',b=>{const m=/RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(b));if(m)resolve(m[1]);});});
  browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
  const page=await browser.newPage({viewport:{width:1280,height:900}}),errors=[];page.setDefaultTimeout(8000);page.on('pageerror',e=>errors.push(e.message));
  await page.goto(address+'preview/web/?scenario=ready');await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  assert.equal(await page.evaluate(()=>!!document.activeElement.closest('#chatSessionList')),false,'initial selection must not steal focus into the tree');
  // Compare Shell chrome with Demo; tree indentation follows the newer production review.
  const demo=await browser.newPage({viewport:{width:1280,height:900}});
  await demo.goto(address+'index.html');await demo.locator('.dv-tab').first().waitFor();
  const sample=(p,selectors)=>p.evaluate(selectors=>Object.fromEntries(Object.entries(selectors).map(([key,selector])=>{
   const n=document.querySelector(selector),s=getComputedStyle(n),r=n.getBoundingClientRect();
   return [key,{width:r.width,height:r.height,padding:s.padding,marginLeft:s.marginLeft,lineHeight:s.lineHeight,gap:s.gap,borderLeftWidth:s.borderLeftWidth}];
  })),selectors);
  const expected=await sample(demo,{nav:'.navigation',split:'.navigation-resize',dock:'.workspace-region',group:'.dv-groupview',tab:'.dv-tab',actions:'.group-actions',parent:'.tree-group > summary',child:'.nav-row',guide:'.tree-children'});
  const actual=await sample(page,{nav:'#chatLayout > .global-nav',split:'#chatLayout > .global-nav + .splitter',dock:'#chatDockHost',group:'.dv-groupview',tab:'.dv-tab',actions:'.dock-group-actions',parent:'#chatSessionList .navigation-tree-row',child:'#chatSessionList [aria-selected=true] > .navigation-tree-row',guide:'#chatSessionList .navigation-tree-list .navigation-tree-list'});
  for(const key of ['nav','split','dock','group']) for(const field of ['width','height','padding']) assert.equal(actual[key][field],expected[key][field],key+' '+field+' matches Demo');
  for(const field of ['height','padding']) assert.equal(actual.actions[field],expected.actions[field],'group controls '+field);
  for(const key of ['tab']) for(const field of ['height','padding','lineHeight']) assert.equal(actual[key][field],expected[key][field],key+' '+field+' matches Demo');
  for(const field of ['height','lineHeight']) assert.equal(actual.parent[field],expected.parent[field],'parent '+field+' matches Demo');
  assert.equal(actual.guide.marginLeft,"0px","unified production tree owns its indentation");
  assert.deepEqual([actual.guide.padding,actual.guide.borderLeftWidth],['0px','0px'],'production tree has no extended guide or extra inset');
  await page.evaluate(()=>{window.initialFontSize=document.documentElement.style.fontSize;document.documentElement.style.fontSize='18px';});
  assert.equal(await page.locator('.dv-tab').first().evaluate(n=>getComputedStyle(n).padding),expected.tab.padding,'chat font scale does not change Dockview spacing');
  await page.evaluate(()=>{document.documentElement.style.fontSize=window.initialFontSize;});
  // Width limits and keyboard reset are shared by every section, like Demo.
  const divider=page.locator('#chatLayout > .global-nav + .splitter');
  await demo.locator('#navigation-resize').press('ArrowRight');await divider.press('ArrowRight');
  assert.equal((await page.locator('#chatLayout > .global-nav').boundingBox()).width,(await demo.locator('.navigation').boundingBox()).width);
  await divider.press('End');assert.equal((await page.locator('#chatLayout > .global-nav').boundingBox()).width,340);
  assert.ok(await page.locator('[data-navigation]').evaluateAll(nodes=>nodes.every(n=>n.style.getPropertyValue('--split-left')==='340px')));
  await divider.press('Home');assert.equal((await page.locator('#chatLayout > .global-nav').boundingBox()).width,expected.nav.width);
  // The chooser and menus also belong to the approved Shell, not a new screen design.
  await demo.locator('[data-group-add]').first().click();
  await page.locator('.dock-group-actions').first().getByRole('button',{name:'Выбрать экран',exact:true}).click();
  const chooserStyle=(p,selector)=>p.locator(selector).first().evaluate(n=>{const s=getComputedStyle(n);return[s.padding,s.fontSize,s.lineHeight,s.borderRadius,s.backgroundColor];});
  for(const [reference,production] of [['.chooser-body','.start-surface-body'],['.chooser-body h2','.dock-screen-chooser h2'],['.chooser-body input','.dock-screen-chooser input']])
   assert.deepEqual(await chooserStyle(page,production),await chooserStyle(demo,reference),'chooser geometry: '+reference);
  await page.locator('.dock-screen-chooser input').fill('неизвестный экран');
  assert.equal(await page.locator('.dock-screen-chooser [role=status]').textContent(),'Ничего не найдено');
  await page.locator('[data-tab-panel-id^="choose-"]').getByRole('button').click();
  await page.locator('[data-tab-panel-id^="chat:"]').click();
  await demo.locator('[data-group-menu]').first().click();
  await page.locator('.dock-group-actions').first().getByRole('button',{name:'Действия с вкладкой',exact:true}).click();
  const menuGeometry=(p,selector)=>p.locator(selector).evaluate(n=>({width:n.getBoundingClientRect().width,iconInset:n.querySelector('button svg').getBoundingClientRect().left-n.getBoundingClientRect().left}));
  assert.deepEqual(await menuGeometry(page,'.rna-action-menu-popup'),await menuGeometry(demo,'#group-menu'),'group menu width and left-aligned icons match Demo');
  await page.keyboard.press('Escape');
  await page.locator('#workspaceLayout').click();
  assert.equal((await page.getByRole('menu').boundingBox()).width,248);
  await page.keyboard.press('Escape');assert.equal(await page.evaluate(()=>document.activeElement.id),'workspaceLayout');
  await demo.close();
  const rail=page.locator('.section-rail [data-section=chat]'),toggle=page.locator('#productMobileMenuButton'),scrim=page.locator('#productNavigationScrim');
  const opened=()=>page.evaluate(()=>RNAssistantShellLayout.isNavigationOpen());
  const settled=()=>page.waitForFunction(()=>!document.querySelector('.shell').classList.contains('navigation-transition'));
  await page.locator('[data-chat-node="chatInput"]').fill('Черновик остаётся на месте');
  await page.evaluate(()=>window.shellNodes=['messages','chatForm','chatInspector'].map(id=>document.getElementById(id)||RNAssistantConversationPanel.office('preview-chat').node(id)));
  assert.equal(await opened(),true);await rail.click();assert.equal(await opened(),false);await rail.click();assert.equal(await opened(),true);await settled();
  const actions=await page.locator('.dock-group-actions').first().evaluate(n=>[...n.children].map(n=>[n.offsetWidth,n.offsetHeight,n.firstElementChild.getBoundingClientRect().width]));
  assert.deepEqual(actions,Array(2).fill([25,25,14]),'shared control size must not stretch Demo buttons to 34px');
  // A topbar toggle hides the tool group, retaining its DOM and dimensions.
  const inspectorWidth=(await page.locator('#chatInspector').boundingBox()).width;
  await page.locator('#toggleChatInspectorButton').click();
  assert.equal(await page.locator('[data-tab-panel-id=inspector]').count(),1);
  assert.equal(await page.locator('#chatInspector').isVisible(),false);
  await page.locator('#toggleChatInspectorButton').click();
  assert.ok(Math.abs((await page.locator('#chatInspector').boundingBox()).width-inspectorWidth)<2);
  assert.deepEqual(await page.evaluate(()=>shellNodes.map(n=>n===document.getElementById(n.id))),[true,true,true]);
  for(const width of [900,420,360]) {
   await page.setViewportSize({width,height:800});await page.waitForFunction(()=>RNAssistantShellLayout.mode()!=='wide'&&document.querySelector('.shell').classList.contains('navigation-'+RNAssistantShellLayout.mode()));await settled();
   assert.equal(await opened(),false);assert.equal(await rail.isVisible(),true,'rail stays available in compact and narrow mode');
   const before=await page.locator('#chatDockHost').boundingBox();
   await rail.click();assert.equal(await opened(),true);await scrim.waitFor();
   const after=await page.locator('#chatDockHost').boundingBox();assert.equal(after.width,before.width,'navigation overlays without squeezing the workspace');
   assert.equal(await page.locator('#chatDockHost').evaluate(n=>n.inert),true);
   const nav=await page.locator('#chatLayout > .global-nav').boundingBox();assert.ok(nav.width<width-60&&nav.width>=180);
   if(process.env.SHELL_SCREENSHOTS){fs.mkdirSync(process.env.SHELL_SCREENSHOTS,{recursive:true});await page.screenshot({path:path.join(process.env.SHELL_SCREENSHOTS,'s1b-navigation-'+width+'.png')});}
   const expander=page.locator('#chatSessionList .navigation-tree-expander').first();
   await expander.click();assert.equal(await opened(),true,'expanding a folder is not navigation');await expander.click();
   await page.locator('#chatSessionList [data-tree-key="chat:preview-chat"] .navigation-tree-label').press('Enter');
   assert.equal(await opened(),false,'keyboard selection closes the overlay, including synchronous tree rerenders');
   assert.equal(await page.locator('[data-chat-node="chatInput"]').inputValue(),'Черновик остаётся на месте');
   await toggle.click();assert.equal(await opened(),true);await page.keyboard.press('Escape');assert.equal(await opened(),false);
   await toggle.click();await scrim.click({position:{x:width-90,y:360}});assert.equal(await opened(),false);
   assert.equal(await page.locator('#chatDockHost').evaluate(n=>n.inert),false);
   await page.locator('.section-rail [data-section=settings]').click();assert.equal(await opened(),false,'Settings does not open the global sidebar');
   await page.locator('#settingsPageSelect').selectOption('interface');
   assert.equal(await opened(),false);await page.locator('[data-settings-page=interface].active').waitFor();
   await rail.click();assert.equal(await opened(),true);await rail.click();assert.equal(await opened(),false);
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
  }
  // Mixed/narrow groups are not hidden by the tools toggle.
  await page.locator('#toggleChatInspectorButton').click();await page.locator('#toggleChatInspectorButton').click();
  assert.equal(await page.locator('[data-tab-panel-id^="chat:"]').count(),1);
  assert.equal(await page.locator('#chatInspector').isVisible(),true);
  await page.setViewportSize({width:1280,height:900});await page.waitForFunction(()=>document.querySelector('.shell').classList.contains('navigation-wide'));await settled();
  assert.equal(await opened(),true,'wide preference survives temporary overlay use');
  await toggle.click();await page.setViewportSize({width:900,height:800});await toggle.click();await page.keyboard.press('Escape');
  await page.setViewportSize({width:1280,height:900});await page.waitForFunction(()=>document.querySelector('.shell').classList.contains('navigation-wide'));assert.equal(await opened(),false,'explicit wide collapse is also retained');
  // The only remaining tool group cannot be hidden into an empty workspace.
  await page.evaluate(()=>RNAssistantDockWorkspace.close('settings'));
  await page.locator('[data-tab-panel-id^="chat:"]').getByRole('button').click();
  await page.locator('#toggleChatInspectorButton').click();assert.equal(await page.locator('#chatInspector').isVisible(),true);
  assert.match(await page.locator('#workspaceLayoutNotice').textContent(),/единственная рабочая область/);
  assert.deepEqual(errors,[]);console.log('PASS shell navigation: wide/overlay, rail toggle, mouse/keyboard selection, folder, scrim/Escape, settings, draft/DOM, Inspector hide/restore/guard, Demo icon metrics');
 }catch(e){if(browser){const p=browser.contexts()[0]?.pages()[0];if(p){await p.screenshot({path:'/tmp/rna-shell-failure.png'});console.error(await p.evaluate(()=>({focus:document.activeElement.outerHTML.slice(0,300),mode:RNAssistantShellLayout.mode(),shell:document.querySelector('.shell').className})));}}throw e;}
 finally{if(browser)await browser.close();server.kill();}
})().catch(e=>{console.error(e);process.exitCode=1});
