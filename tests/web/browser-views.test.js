'use strict';
// Production Dockview + common Browser with synthetic HTML/plugin sources. No Office/models.
const assert=require('node:assert/strict'), fs=require('node:fs'), path=require('node:path'), {spawn}=require('node:child_process');
const {chromium}=require('playwright');
(async()=>{
  const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd:path.resolve(__dirname,'../..')});server.stderr.resume();let browser;
  try{
    const address=await new Promise((resolve,reject)=>{server.once('error',reject);server.once('exit',code=>reject(Error('preview exit '+code)));server.stdout.on('data',data=>{const m=/RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(data));if(m)resolve(m[1]);});});
    browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
    const page=await browser.newPage({viewport:{width:1280,height:900},reducedMotion:'reduce'}),errors=[];
    await page.addInitScript({content:'if(location.pathname.endsWith("/data-templates/index.html")) {'+
      fs.readFileSync(path.resolve(__dirname,'../../web/js/app-visual-inspector.js'),'utf8')+';RNAssistantVisualInspector.install(1);}'});
    page.setDefaultTimeout(8000);page.on('pageerror',e=>errors.push(e.stack||e.message));
    await page.goto(address+'preview/web/?scenario=ready');
    await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
    await page.locator('[data-chat-node="chatInput"]').fill('Черновик Office отдельно от просмотра');
    await page.evaluate(()=>{
      window.browserTest={calls:[],loads:0,delayOpen:false};const original=send;
      send=function(command,payload){browserTest.calls.push({command,payload});if(command==='stageHtmlVisualSelection')return Promise.resolve({resource:{Id:'browser-html-annotation',FileName:'selection.json',Kind:'file',Size:180,Status:'ready'}});
        if(command==='stagePluginVisualSelection')return Promise.resolve({Id:'browser-plugin-annotation',FileName:'selection.json',Kind:'file',Size:180,Status:'ready'});
        const result=original(command,payload);
        if(command==='getPluginChat'&&browserTest.delayChat)return result.then(value=>new Promise(resolve=>{browserTest.releaseChat=()=>resolve(value);}));
        if(command==='openPlugin'&&browserTest.delayOpen)return result.then(value=>new Promise(resolve=>{browserTest.late=value;browserTest.release=()=>resolve(value);}));return result;};
      const html='<main><h1>Проверка HTML</h1><label>Состояние страницы <input value="initial"></label><button>Обычный клик</button></main>';
      const file={id:'file1',path:'index.html',kind:'html',content:html,source:{uri:'rna://chat/preview-chat/html/file1',revision:'r1'},sourceReadKey:'rna://chat/preview-chat/html/file1@r1',sha256:'a'.repeat(64),byteLength:html.length,characters:html.length};
      state.activeHtmlArtifactId='html-r1';state.artifacts=[{Id:'html-r1',Kind:'html_workspace',Title:'Проверка HTML',Revision:1}];
      state.htmlWorkspace={revisionArtifactId:'html-r1',activeFileId:'file1',files:[file],dataSources:[],history:[],redoHistory:[],redoBranches:[],recovery:{status:'empty',canMutate:true,candidates:[]}};
      state.htmlWorkspaceSelection={type:'file',id:'file1'};state.htmlWorkspaceMode='preview';
      document.getElementById('htmlWorkspacePreviewFrame').addEventListener('load',()=>browserTest.loads++);
      RNAssistantHtmlWorkspaceSurface.openCurrent();
    });
    const html=page.frameLocator('#htmlWorkspacePreviewFrame');await html.getByRole('heading',{name:'Проверка HTML'}).waitFor();
    await page.waitForFunction(()=>!document.getElementById('inspectHtmlWorkspaceButton').disabled);
    await page.locator('#inspectHtmlWorkspaceButton').click();
    await html.getByRole('heading',{name:'Проверка HTML'}).click();
    await page.locator('#htmlWorkspacePreview .visual-annotation-editor textarea').fill('Проверка адресной HTML-аннотации');
    await page.locator('#htmlWorkspacePreview .visual-annotation-editor .primary').click();
    await page.waitForFunction(()=>state.draftAttachments.some(item=>item.Id==='browser-html-annotation'));
    assert.deepEqual(await page.evaluate(()=>{const p=browserTest.calls.find(c=>c.command==='stageHtmlVisualSelection').payload;return [p.chatId,p.artifactId,p.artifactRevision];}),['preview-chat','html-r1',1]);
    if(await page.locator('#inspectHtmlWorkspaceButton').getAttribute('aria-pressed')==='true')await page.locator('#inspectHtmlWorkspaceButton').click();
    await html.getByRole('textbox').fill('retained');
    const baseline=await page.evaluate(()=>browserTest.loads);
    async function layout(name){await page.locator('#workspaceLayout').click();await page.getByRole('menuitem',{name,exact:true}).click();}
    await layout('Сохранить раскладку');
    for(let i=0;i<5;i++){await layout('Вернуть сохранённую');await html.getByRole('textbox').waitFor();}
    assert.equal(await page.evaluate(()=>browserTest.loads),baseline,'saved-layout restore never reloads iframe');
    assert.equal(await html.getByRole('textbox').inputValue(),'retained');
    await page.evaluate(()=>{switchTab('artifacts');RNAssistantDockWorkspace.show('html-browser');});
    await html.getByRole('textbox').waitFor();assert.equal(await html.getByRole('textbox').inputValue(),'retained','source-section host move retains iframe');
    await page.evaluate(()=>{switchTab('chat');RNAssistantDockWorkspace.show('html-browser');});
    // Actual Dockview drag, not a synthetic component move.
    const from=await page.locator('[data-tab-panel-id="html-browser"]').boundingBox(),to=await page.locator('[data-tab-panel-id="inspector"]').boundingBox();
    await page.mouse.move(from.x+30,from.y+15);await page.mouse.down();await page.mouse.move(to.x+40,to.y+15,{steps:15});await page.mouse.up();
    await html.getByRole('textbox').waitFor();assert.equal(await html.getByRole('textbox').inputValue(),'retained','Dock drag retains iframe document');
    assert.equal(await page.evaluate(()=>browserTest.loads),baseline);
    // Independent plugin selection leaves the exact current conversation and draft intact.
    await page.evaluate(()=>RNAssistantPlugins.select('data-templates'));
    const plugin=page.frameLocator('[data-plugin-node="PreviewFrameHost"] iframe');await plugin.getByRole('heading',{name:'Источники',exact:true}).waitFor();
    assert.equal(await page.evaluate(()=>RNAssistantWorkspaceChat.owner()),'office');
    assert.equal(await page.locator('[data-chat-node="chatInput"]').inputValue(),'Черновик Office отдельно от просмотра');
    assert.equal(await page.evaluate(()=>browserTest.calls.some(c=>c.command==='getPluginChat')),false,'preview does not read/retarget source chat');
    await plugin.locator('body').evaluate(node=>{const input=document.createElement('input');input.id='browserStateProbe';input.value='plugin-retained';node.appendChild(input);});
    await page.evaluate(()=>{browserTest.pluginFrame=document.querySelector('[data-plugin-node="PreviewFrameHost"] iframe');browserTest.pluginDocument=browserTest.pluginFrame.contentDocument;});
    await layout('Сохранить раскладку');await layout('Вернуть сохранённую');
    assert.equal(await plugin.locator('#browserStateProbe').inputValue(),'plugin-retained');
    await page.locator('[data-plugin-node="Workbench"]').getByRole('button',{name:'Чат исходников',exact:true}).click();
    await page.waitForFunction(()=>RNAssistantWorkspaceChat.owner()==='plugin');
    await page.evaluate(()=>RNAssistantPlugins.select('data-templates'));
    assert.equal(await plugin.locator('#browserStateProbe').inputValue(),'plugin-retained','moving workspace host to project chat retains plugin document');
    await page.waitForFunction(()=>!document.querySelector('[data-plugin-node="Workbench"] .browser-annotate').disabled);
    await page.locator('[data-plugin-node="Workbench"] .browser-annotate').click();
    await plugin.getByRole('heading',{name:'Источники',exact:true}).click();
    await page.locator('[data-plugin-node="PreviewFrameHost"] .visual-annotation-editor textarea').fill('Проверка адресной plugin-аннотации');
    await page.locator('[data-plugin-node="PreviewFrameHost"] .visual-annotation-editor .primary').click();
    await page.waitForFunction(()=>RNAssistantChatDrafts.readWorkspace({workspaceId:RNAssistantProjectChats.workspaceId(),sessionId:RNAssistantProjectChats.snapshot().sessionId}).attachments.some(item=>item.Id==='browser-plugin-annotation'));
    assert.ok(await page.evaluate(()=>browserTest.calls.some(c=>c.command==='stagePluginVisualSelection'&&c.payload.workspaceId==='preview-plugin-workspace'&&c.payload.instanceId&&c.payload.sessionId)));
    if(await page.locator('[data-plugin-node="Workbench"] .browser-annotate').getAttribute('aria-pressed')==='true')await page.locator('[data-plugin-node="Workbench"] .browser-annotate').click();
    // Inspector is an independent view. Show it in the other group without closing Browser.
    await page.evaluate(()=>RNAssistantChatInspector.setOpen(true));
    await page.evaluate(()=>RNAssistantPlugins.select('data-templates'));
    assert.ok(await page.evaluate(()=>browserTest.pluginFrame===document.querySelector('[data-plugin-node="PreviewFrameHost"] iframe')&&browserTest.pluginDocument===browserTest.pluginFrame.contentDocument));
    // Existing provider error is visible and does not claim the old page is current.
    await page.evaluate(()=>{const original=send;send=(command,payload)=>command==='checkPlugin'?Promise.reject(Error('Ошибка пакета: index.html отсутствует')):original(command,payload);browserTest.restoreSend=()=>{send=original;};});
    await page.locator('[data-plugin-node="Preview"]').click();await page.locator('[data-plugin-node="PreviewMount"] .preview-surface-status').getByText(/Предпросмотр не обновлён/).waitFor();
    await page.evaluate(()=>browserTest.restoreSend());
    assert.equal(await plugin.locator('#browserStateProbe').inputValue(),'plugin-retained');
    // Every surface tracks its placeholder at narrow widths, dark theme and CSS zoom.
    async function geometry(){return page.evaluate(()=>{const surface=document.querySelector('.browser-view-surface:not([hidden])');
      const a=surface.getBoundingClientRect();return {left:a.left,right:a.right,width:a.width,viewport:innerWidth,overflow:surface.scrollWidth-surface.clientWidth};});}
    const captures=process.env.RNA_BROWSER_SCREENSHOTS;if(captures){fs.mkdirSync(captures,{recursive:true});await page.screenshot({path:path.join(captures,'s3b3a-light-1280.png')});}
    await page.setViewportSize({width:420,height:900});
    await page.evaluate(()=>{document.documentElement.dataset.theme='dark';RNAssistantShellLayout.setNavigationOpen(false);RNAssistantPlugins.select('data-templates');});
    await page.locator('[data-plugin-node="Workbench"]').waitFor();await page.waitForFunction(()=>document.querySelector('[data-plugin-node="Workbench"]').getBoundingClientRect().right<=innerWidth+1);let rect=await geometry();assert.ok(rect.left>=0&&rect.right<=rect.viewport+1&&rect.overflow<=1,JSON.stringify(rect));
    if(captures)await page.screenshot({path:path.join(captures,'s3b3a-dark-420.png')});
    await page.evaluate(()=>{document.body.style.zoom='1.25';});await page.waitForTimeout(100);rect=await geometry();assert.ok(rect.right<=rect.viewport+1,JSON.stringify(rect));
    await page.evaluate(()=>{document.body.style.zoom='';RNAssistantShellLayout.setNavigationOpen(true);});
    assert.equal(await page.locator('[data-plugin-node="Workbench"]').isVisible(),false,'navigation overlay does not expose or intercept Browser');
    await page.evaluate(()=>RNAssistantShellLayout.setNavigationOpen(false));
    await page.locator('.browser-view-anchor:visible').focus();
    await page.locator('.browser-view-anchor:visible').press('Enter');
    assert.ok(await page.evaluate(()=>document.querySelector('[data-plugin-node="Workbench"]').contains(document.activeElement)));
    // A late source-chat lookup cannot take navigation back after another conversation was selected.
    await page.evaluate(()=>{browserTest.delayChat=true;});
    await page.locator('[data-plugin-node="Workbench"]').getByRole('button',{name:'Чат исходников',exact:true}).click();
    await page.waitForFunction(()=>!!browserTest.releaseChat);
    await page.evaluate(()=>{RNAssistantProjectChats.setScope('office');RNAssistantConversationPanel.open({source:'office',sessionId:'preview-chat'});browserTest.releaseChat();});
    await page.waitForFunction(()=>!RNAssistantPlugins.isChatBusy());
    assert.equal(await page.evaluate(()=>RNAssistantWorkspaceChat.owner()),'office');
    // Closing a ready view disposes the existing runtime instance; late open cannot resurrect it.
    await page.evaluate(()=>RNAssistantDockWorkspace.close(RNAssistantPluginView.key({pluginId:'data-templates'})));
    await page.waitForFunction(()=>!document.querySelector('[data-plugin-node="PreviewFrameHost"] iframe'));
    assert.ok(await page.evaluate(()=>browserTest.calls.some(c=>c.command==='closePlugin')));
    await page.evaluate(()=>{browserTest.delayOpen=true;RNAssistantPlugins.select('data-templates');});
    await page.waitForFunction(()=>!!browserTest.release);await page.evaluate(()=>{RNAssistantDockWorkspace.close(RNAssistantPluginView.key({pluginId:'data-templates'}));});
    await page.evaluate(()=>browserTest.release());
    await page.waitForFunction(()=>browserTest.calls.some(c=>c.command==='closePlugin'&&c.payload.instanceId===browserTest.late.instanceId));
    assert.equal(await page.locator('[data-plugin-node="PreviewFrameHost"] iframe').count(),0);
    await page.evaluate(()=>{state.activeHtmlArtifactId='different-source';renderHtmlWorkspace();RNAssistantDockWorkspace.show('html-browser');});
    assert.equal(await page.evaluate(()=>RNAssistantHtmlWorkspaceSurface.hasSource()),false,'active source change clears binding instead of retargeting');
    await page.locator('#htmlWorkspacePreview .preview-surface-status').getByText(/Выберите HTML/).waitFor();
    assert.equal(await page.evaluate(()=>browserTest.calls.some(c=>/submit.*Chat|runModel|diagnoseModel/.test(c.command))),false);
    assert.deepEqual(errors,[]);
    console.log('PASS browser-views: stable document, separate chat, restore/move/scope, late instance cleanup, error/narrow/theme/zoom');
  }finally{if(browser)await browser.close();server.kill();}
})().catch(error=>{console.error(error);process.exitCode=1;});
