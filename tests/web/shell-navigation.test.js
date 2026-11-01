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
  const rail=page.locator('.section-rail [data-section=chat]'),toggle=page.locator('#productMobileMenuButton'),scrim=page.locator('#productNavigationScrim');
  const opened=()=>page.evaluate(()=>RNAssistantShellLayout.isNavigationOpen());
  const settled=()=>page.waitForFunction(()=>!document.querySelector('.shell').classList.contains('navigation-transition'));
  await page.locator('#chatInput').fill('Черновик остаётся на месте');
  await page.evaluate(()=>window.shellNodes=['messages','chatForm','chatInspector'].map(id=>document.getElementById(id)));
  assert.equal(await opened(),true);await rail.click();assert.equal(await opened(),false);await rail.click();assert.equal(await opened(),true);
  const actions=await page.locator('.dock-group-actions').first().evaluate(n=>[...n.children].map(n=>[n.offsetWidth,n.offsetHeight,n.firstElementChild.getBoundingClientRect().width]));
  assert.deepEqual(actions,Array(4).fill([25,25,14]),'shared control size must not stretch Demo buttons to 34px');
  // A topbar toggle hides the tool group, retaining its DOM and dimensions.
  const inspectorWidth=(await page.locator('#chatInspector').boundingBox()).width;
  await page.locator('#toggleChatInspectorButton').click();
  assert.equal(await page.locator('[data-tab-panel-id=inspector]').count(),1);
  assert.equal(await page.locator('#chatInspector').isVisible(),false);
  await page.locator('#toggleChatInspectorButton').click();
  assert.ok(Math.abs((await page.locator('#chatInspector').boundingBox()).width-inspectorWidth)<2);
  assert.deepEqual(await page.evaluate(()=>shellNodes.map(n=>n===document.getElementById(n.id))),[true,true,true]);
  for(const width of [900,420,360]) {
   await page.setViewportSize({width,height:800});await page.waitForFunction(()=>RNAssistantShellLayout.mode()!=='wide');await settled();
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
   assert.equal(await page.locator('#chatInput').inputValue(),'Черновик остаётся на месте');
   await toggle.click();assert.equal(await opened(),true);await page.keyboard.press('Escape');assert.equal(await opened(),false);
   await toggle.click();await scrim.click({position:{x:width-90,y:360}});assert.equal(await opened(),false);
   assert.equal(await page.locator('#chatDockHost').evaluate(n=>n.inert),false);
   await page.locator('.workspace-topbar [data-section=settings]').click();assert.equal(await opened(),true);
   await page.locator('#settingsNavTree [data-tree-key="settings:interface"] .navigation-tree-label').click();
   assert.equal(await opened(),false);await page.locator('[data-settings-page=interface].active').waitFor();
   await rail.click();assert.equal(await opened(),true);await rail.click();assert.equal(await opened(),false);
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
  }
  // Mixed/narrow groups are not hidden by the tools toggle.
  await page.locator('#toggleChatInspectorButton').click();await page.locator('#toggleChatInspectorButton').click();
  assert.equal(await page.locator('[data-tab-panel-id=conversation]').count(),1);
  assert.equal(await page.locator('#chatInspector').isVisible(),true);
  await page.setViewportSize({width:1280,height:900});await page.waitForFunction(()=>RNAssistantShellLayout.mode()==='wide');await settled();
  assert.equal(await opened(),true,'wide preference survives temporary overlay use');
  await toggle.click();await page.setViewportSize({width:900,height:800});await toggle.click();await page.keyboard.press('Escape');
  await page.setViewportSize({width:1280,height:900});await page.waitForFunction(()=>RNAssistantShellLayout.mode()==='wide');assert.equal(await opened(),false,'explicit wide collapse is also retained');
  // The only remaining tool group cannot be hidden into an empty workspace.
  await page.locator('[data-tab-panel-id=conversation]').getByRole('button').click();
  await page.locator('#toggleChatInspectorButton').click();assert.equal(await page.locator('#chatInspector').isVisible(),true);
  assert.match(await page.locator('#workspaceLayoutNotice').textContent(),/единственная рабочая область/);
  assert.deepEqual(errors,[]);console.log('PASS shell navigation: wide/overlay, rail toggle, mouse/keyboard selection, folder, scrim/Escape, settings, draft/DOM, Inspector hide/restore/guard, Demo icon metrics');
 }catch(e){if(browser){const p=browser.contexts()[0]?.pages()[0];if(p){await p.screenshot({path:'/tmp/rna-shell-failure.png'});console.error(await p.evaluate(()=>({focus:document.activeElement.outerHTML.slice(0,300),mode:RNAssistantShellLayout.mode(),shell:document.querySelector('.shell').className})));}}throw e;}
 finally{if(browser)await browser.close();server.kill();}
})().catch(e=>{console.error(e);process.exitCode=1});
