'use strict';
// Production UI on the existing synthetic bridge; no Office/model execution.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], {cwd:path.resolve(__dirname,'../..')});
  server.stderr.resume();
  let browser;
  try {
    const address = await new Promise((resolve,reject) => {
      server.once('error',reject); server.once('exit',code=>reject(Error('preview exit '+code)));
      server.stdout.on('data',bytes=>{const match=/RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(bytes));if(match)resolve(match[1]);});
    });
    browser = await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
    const page = await browser.newPage({viewport:{width:1280,height:900}}), errors=[];
    page.setDefaultTimeout(10000);
    page.on('pageerror',e=>errors.push(e.stack||e.message));
    await page.goto(address+'preview/web/?scenario=ready');
    await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
    await page.evaluate(()=>RNAssistantChatInspector.setOpen(false));
    await page.evaluate(()=>{
      window.dockTest={calls:[],nodes:['chatForm','messages','chatInspector','workspaceChatView'].map(id=>document.getElementById(id)),inspectorChanges:0};
      const original=send; send=function(type,...args){dockTest.calls.push(type);return original(type,...args)};
      new MutationObserver(()=>dockTest.inspectorChanges++).observe(document.getElementById('chatInspectorBody'),{childList:true,subtree:true});
    });
    const chatTab=page.locator('[data-tab-panel-id=conversation]');
    const inspectorTab=page.locator('[data-tab-panel-id=inspector]');
    await page.locator('#chatInput').fill('Сохранить черновик при переносе');
    await page.locator('#toggleChatInspectorButton').click();
    await inspectorTab.waitFor();
    assert.equal(await page.locator('.dock-group-actions').count(),2);
    assert.equal(await page.locator('.workspace-panel-controls #toggleChatInspectorButton').count(),1,'one top control');
    const original=await page.locator('#chatDropZone').boundingBox();
    await chatTab.click();
    await page.locator('#workspaceMaximize').click();
    await page.waitForFunction(width=>document.getElementById('chatDropZone').getBoundingClientRect().width>width+200, original.width);
    await page.locator('#workspaceMaximize').click();
    await page.waitForFunction(width=>document.getElementById('chatDropZone').getBoundingClientRect().width<width+5, original.width);
    const mutations=await page.evaluate(()=>dockTest.inspectorChanges);
    await page.setViewportSize({width:1350,height:920});
    await page.waitForFunction(()=>document.getElementById('chatDockHost').offsetWidth>950);
    assert.equal(await page.evaluate(()=>dockTest.inspectorChanges),mutations,'resize does not rebuild Inspector');
    const inspectorWidth=(await page.locator('#chatInspector').boundingBox()).width;
    await inspectorTab.focus(); await page.keyboard.press('Alt+Shift+ArrowLeft');
    await page.waitForFunction(width=>document.getElementById('chatInspector').getBoundingClientRect().width<width-5,inspectorWidth);
    // Exercise Dockview's actual pointer docking, not just the placement commands.
    await inspectorTab.hover();
    const dragTab=await inspectorTab.boundingBox(), center=await chatTab.boundingBox();
    await page.mouse.move(dragTab.x+30,dragTab.y+15); await page.mouse.down();
    await page.mouse.move(dragTab.x-20,dragTab.y+45,{steps:5});
    // Dockview's pointer drag activates after a held press, as a real gesture does.
    await page.waitForTimeout(180);
    await page.mouse.move(center.x+25,center.y+15,{steps:12}); await page.mouse.up();
    await page.waitForFunction(()=>document.querySelectorAll('.dock-group-actions').length===1);
    await page.locator('.dock-group-actions').first().getByRole('button',{name:'Открыть экран справа',exact:true}).click();
    await page.locator('.dock-screen-chooser input').fill('Инспектор'); await page.locator('.dock-screen-chooser input').press('Enter');
    await page.waitForFunction(()=>document.querySelectorAll('.dock-group-actions').length===2);
    // The group actions open a chooser tab; an existing view moves instead of cloning.
    await page.locator('.dock-group-actions').first().getByRole('button',{name:'Открыть экран снизу',exact:true}).click();
    await page.locator('.dock-screen-chooser').getByRole('button',{name:'Инспектор',exact:true}).click();
    await page.waitForFunction(()=>document.getElementById('chatInspector').getBoundingClientRect().top>document.getElementById('chatForm').getBoundingClientRect().top);
    const boxes=await Promise.all([page.locator('#chatForm').boundingBox(),page.locator('#chatInspector').boundingBox()]);
    assert.ok(boxes[1].y>boxes[0].y,'Inspector moved below the chat');
    assert.equal(await page.locator('#chatInspector').count(),1);
    await page.locator('.dock-group-actions').last().getByRole('button',{name:'Действия с вкладкой',exact:true}).click();
    await page.getByRole('menuitem',{name:'Закрыть группу',exact:true}).click();
    assert.equal(await inspectorTab.count(),0); assert.equal(await chatTab.count(),1,'close affects its group only');
    await page.evaluate(()=>{
      state.messages = Array.from({length:30},(_,i)=>({Id:'scroll-'+i,Role:'user',Content:'Сообщение '+i+' для проверки сохранения прокрутки'}));
      renderMessages({fullReset:true,forceScroll:true});
      const list=document.getElementById('messages'); list.scrollTop=180; list.dispatchEvent(new Event('scroll'));
      dockTest.scroll=list.scrollTop;
      document.getElementById('chatInput').setSelectionRange(3,8);
    });
    await chatTab.getByRole('button').click();
    assert.equal(await chatTab.count(),0);
    await page.evaluate(()=>{
      state.messages.push({Id:'while-hidden',Role:'assistant',Content:'Сообщение во время скрытой вкладки'});
      renderMessages();
      RNAssistantWorkspaceChat.mount('office');
    });
    assert.equal(await page.locator('#messages').getByText('Сообщение во время скрытой вкладки').count(),0,'hidden transcript defers rendering');
    await page.locator('#workspacePanels').click();
    await page.getByRole('menu').getByRole('menuitem',{name:'Чат',exact:true}).click();
    await page.getByText('Сообщение во время скрытой вкладки',{exact:true}).waitFor();
    assert.equal(await page.locator('#chatInput').inputValue(),'Сохранить черновик при переносе');
    await page.waitForFunction(()=>Math.abs(document.getElementById('messages').scrollTop-dockTest.scroll)<2);
    assert.ok(await page.evaluate(()=>Math.abs(document.getElementById('messages').scrollTop-dockTest.scroll)<2),'hidden/reopen retains scroll');
    assert.deepEqual(await page.locator('#chatInput').evaluate(n=>[n.selectionStart,n.selectionEnd]),[3,8]);
    assert.deepEqual(await page.evaluate(()=>dockTest.nodes.map(node=>node===document.getElementById(node.id))),[true,true,true,true]);
    assert.deepEqual(await page.evaluate(()=>dockTest.calls.filter(type=>type!=='saveChatDraft'&&type!=='reportClientTiming')),[],'layout makes no domain/data requests');
    await page.locator('#toggleChatInspectorButton').click();
    await page.locator('#workspacePanels').click();
    await page.keyboard.press('Escape'); assert.equal(await page.getByRole('menu').count(),0);
    assert.equal(await page.evaluate(()=>document.activeElement.id),'workspacePanels');
    await page.locator('#chatInput').click();
    await page.setViewportSize({width:420,height:780});
    await page.waitForFunction(()=>document.querySelectorAll('.dock-group-actions').length===1);
    await page.waitForFunction(()=>document.activeElement.id==='chatInput');
    assert.equal(await page.evaluate(()=>document.activeElement.id),'chatInput','responsive fold retains composer focus');
    assert.ok(await inspectorTab.isVisible());
    await page.waitForFunction(()=>document.documentElement.scrollWidth<=innerWidth);
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
    await chatTab.click();
    assert.equal(await page.locator('#chatInput').inputValue(),'Сохранить черновик при переносе');
    await page.evaluate(()=>document.documentElement.setAttribute('data-theme','dark'));
    if(process.env.DOCK_SCREENSHOTS){fs.mkdirSync(process.env.DOCK_SCREENSHOTS,{recursive:true});await page.screenshot({path:path.join(process.env.DOCK_SCREENSHOTS,'s1b-chat-narrow.png')});}
    await page.setViewportSize({width:1280,height:900});
    await page.waitForFunction(()=>document.querySelectorAll('.dock-group-actions').length===2);
    await page.evaluate(()=>document.documentElement.setAttribute('data-theme','light'));
    await page.waitForFunction(()=>!document.querySelector('.shell').classList.contains('navigation-transition'));
    if(process.env.DOCK_SCREENSHOTS)await page.screenshot({path:path.join(process.env.DOCK_SCREENSHOTS,'s1b-chat-office.png')});
    await page.waitForFunction(()=>JSON.parse(localStorage.getItem('rnassistant.ui.dock.s1.v1'))?.layout.panels.inspector);
    await page.reload();
    await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
    assert.equal(await page.locator('.dock-group-actions').count(),2,'layout restores on reload');
    await page.locator('.workspace-topbar [data-section="settings"]').click();
    await page.locator('#tab-settings.active').waitFor();
    await page.locator('[data-section="chat"]').click();
    await page.locator('#chatInput').waitFor();
    await page.evaluate(()=>RNAssistantProjectChats.open('preview-data-workspace','preview-project-review'));
    await page.waitForFunction(()=>RNAssistantWorkspaceChat.owner()==='project');
    assert.equal(await page.locator('#chatDockHost').evaluate(n=>n.parentElement.id),'projectChatLayout');
    assert.equal(await page.locator('#chatForm').count(),1);
    if(process.env.DOCK_SCREENSHOTS)await page.screenshot({path:path.join(process.env.DOCK_SCREENSHOTS,'s1b-chat-workspace.png')});
    assert.deepEqual(errors,[]);
    console.log('PASS Dock workspace: singleton DOM, move/below/maximize, group-local close, deferred paint, draft, zero layout reads, narrow/wide, keyboard, restore, Office/Workspace');
  } catch (error) {
    if(browser){const page=browser.contexts()[0]?.pages()[0];if(page){
      await page.screenshot({path:'/tmp/rna-dock-failure.png'});
      console.error(await page.evaluate(()=>({width:innerWidth,scroll:document.documentElement.scrollWidth,
        overflow:[...document.querySelectorAll('*')].filter(n=>n.getBoundingClientRect().right>innerWidth+1&&n.getClientRects().length).slice(0,8).map(n=>[n.id,n.className,n.getBoundingClientRect().right,getComputedStyle(n).visibility])})));
    }} throw error;
  } finally {if(browser)await browser.close();server.kill();}
})().catch(error=>{console.error(error);process.exitCode=1;});
