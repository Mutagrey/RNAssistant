'use strict';
// Addressed live plugin views, rail selection and the shared appearance form. Mock bridge only.
const assert=require('node:assert/strict'),path=require('node:path'),fs=require('node:fs'),{spawn}=require('node:child_process');
const {chromium}=require('playwright');
(async()=>{
  const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd:path.resolve(__dirname,'../..')});server.stderr.resume();let browser,page;const errors=[];
  try{
    const url=await new Promise((resolve,reject)=>{server.once('error',reject);server.stdout.on('data',data=>{const m=/RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(data));if(m)resolve(m[1]);});});
    browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
    page=await browser.newPage({viewport:{width:1280,height:900}});page.setDefaultTimeout(7000);page.on('pageerror',e=>errors.push(e.message));
    await page.addInitScript({content:'if(location.pathname.endsWith("/data-templates/index.html")) {'+
      fs.readFileSync(path.resolve(__dirname,'../../web/js/app-visual-inspector.js'),'utf8')+';RNAssistantVisualInspector.install(1);}'});
    await page.goto(url+'preview/web/?scenario=ready');await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
    await page.waitForFunction(()=>RNAssistantConversationPanel.selected()&&!RNAssistantConversationPanel.selected().loading);
    const other=await page.evaluate(async()=>{
      window.pluginProbe={calls:[]};const original=send;send=async function(command,payload){pluginProbe.calls.push({command,payload});if(command==='stagePluginVisualSelection')return {Id:'plugin-view-annotation',FileName:'selection.json',Kind:'file',Size:180,Status:'ready'};const result=await original(command,payload);
        if(command==='openPlugin'&&pluginProbe.delay)return new Promise(resolve=>{pluginProbe.release=()=>resolve(result);pluginProbe.late=result.instanceId;});return result;};
      const item=await send('createPlugin',{name:'Второй плагин',description:'',iconId:'chart',iconColor:'original'});await RNAssistantPlugins.refresh();return item.pluginId;
    });
    const selector=id=>'.plugin-workbench[data-plugin-id="'+id+'"]';
    const ids=['data-templates',other],frames=[];
    for(const id of ids){
      await page.locator('#pluginRail [data-plugin-id="'+id+'"]').click();
      await page.frameLocator(selector(id)+' iframe').locator('body').waitFor();
      await page.frameLocator(selector(id)+' iframe').locator('body').evaluate(node=>{const input=document.createElement('input');input.id='retainedInput';input.value='retained';node.appendChild(input);});
      frames.push(await page.locator(selector(id)+' iframe').evaluateHandle(n=>n));
    }
    const opened=await page.evaluate(()=>pluginProbe.calls.filter(c=>c.command==='openPlugin').length);
    for(const id of ['data-templates',other,'data-templates']){
      await page.locator('#pluginRail [data-plugin-id="'+id+'"]').click();
      assert.equal(await page.locator('#pluginRail [aria-current="page"]').getAttribute('data-plugin-id'),id);
      assert.equal(await page.locator('.section-rail [aria-current="page"]').count(),1,'one rail destination is selected');
      assert.equal(await page.frameLocator(selector(id)+' iframe').locator('#retainedInput').inputValue(),'retained');
    }
    assert.equal(await page.evaluate(()=>pluginProbe.calls.filter(c=>c.command==='openPlugin').length),opened,'switching views retains instances');
    await page.locator('#workspaceLayout').click();await page.getByRole('menuitem',{name:'Сохранить раскладку',exact:true}).click();
    await page.locator('#workspaceLayout').click();await page.getByRole('menuitem',{name:'Вернуть сохранённую',exact:true}).click();
    for(let i=0;i<ids.length;i++)assert.ok(await page.locator(selector(ids[i])+' iframe').evaluate((node,old)=>node===old,frames[i]),'restore retains both iframe elements');
    const from=await page.locator('[data-tab-panel-id="plugin:data-templates"]').boundingBox(),to=await page.locator('[data-tab-panel-id="inspector"]').boundingBox();
    await page.mouse.move(from.x+30,from.y+15);await page.mouse.down();await page.mouse.move(to.x+35,to.y+15,{steps:15});await page.mouse.up();
    assert.ok(await page.locator(selector('data-templates')+' iframe').evaluate((node,old)=>node===old,frames[0]),'Dock move retains live iframe');
    assert.equal(await page.frameLocator(selector('data-templates')+' iframe').locator('#retainedInput').inputValue(),'retained');
    assert.equal(await page.locator('#pluginRail [data-plugin-id="data-templates"]').evaluate(n=>getComputedStyle(n,'::before').content),'none');
    assert.ok(await page.evaluate(()=>!!RNAssistantConversationPanel.selected().node('pluginAnnotationDrafts')),'annotation container belongs to the common chat template');
    const plugin=page.locator(selector('data-templates'));
    const discuss=plugin.getByRole('button',{name:'Обсудить в чате рядом',exact:true,includeHidden:true});
    if(!await discuss.isVisible())await plugin.getByRole('button',{name:'Инструменты страницы',exact:true}).click();
    await discuss.click();
    await page.waitForFunction(()=>RNAssistantWorkspaceChat.owner()==='plugin');
    await page.evaluate(()=>{pluginProbe.recipient={workspaceId:RNAssistantProjectChats.workspaceId(),sessionId:RNAssistantProjectChats.snapshot().sessionId};});
    await page.locator('#pluginRail [data-plugin-id="'+other+'"]').click();
    assert.equal(await page.locator(selector(other)+' .browser-annotate').isEnabled(),false,'another plugin cannot annotate this source chat');
    await page.locator('#pluginRail [data-plugin-id="data-templates"]').click();
    await page.evaluate(()=>RNAssistantConversationPanel.open({source:'office',sessionId:'preview-chat'}));
    await page.locator('#pluginRail [data-plugin-id="data-templates"]').click();
    await page.waitForFunction(()=>!document.querySelector('.plugin-workbench[data-plugin-id="data-templates"] .browser-annotate').disabled);
    await page.locator(selector('data-templates')+' .browser-annotate').click();
    await page.frameLocator(selector('data-templates')+' iframe').getByRole('heading',{name:'Источники',exact:true}).click();
    await page.locator(selector('data-templates')+' .visual-annotation-editor textarea').fill('Адресная аннотация');
    await page.locator(selector('data-templates')+' .visual-annotation-editor .primary').click();
    await page.waitForFunction(()=>RNAssistantChatDrafts.readWorkspace(pluginProbe.recipient).attachments.some(item=>item.Id==='plugin-view-annotation'));
    assert.ok(await page.evaluate(()=>pluginProbe.calls.some(c=>c.command==='stagePluginVisualSelection'&&c.payload.workspaceId===pluginProbe.recipient.workspaceId&&c.payload.instanceId&&c.payload.sessionId===pluginProbe.recipient.sessionId)), 'switching the active chat does not retarget an annotation');
    assert.ok(await page.evaluate(()=>!(readOfficeDraft('preview-chat')?.attachments||[]).some(item=>item.Id==='plugin-view-annotation')));
    await page.locator('#pluginAdd').click();
    await page.locator('#pluginName').fill('Название сверху');
    assert.equal(await page.locator('.section-rail [aria-current="page"]').getAttribute('id'),'pluginAdd');
    assert.equal(await page.locator('#pluginCreateTitle').count(),0,'plugin name is not repeated in a heading');
    assert.equal(await page.locator('#pluginIconEditor').isVisible(),true);
    assert.equal(await page.locator('#pluginIconColorMode').inputValue(),'original');
    assert.ok((await page.locator('#pluginName').boundingBox()).y<(await page.locator('#pluginIconEditor').boundingBox()).y);
    await page.locator('#pluginIconColorMode').selectOption('custom');await page.locator('#pluginIconColor').fill('#dd2244');
    assert.equal(await page.locator('#pluginBuiltinIcons [data-icon-id="chart"] svg').evaluate(n=>getComputedStyle(n).color),'rgb(221, 34, 68)');
    await page.locator('#pluginBuiltinIcons [data-icon-id="chart"]').click();
    assert.equal(await page.locator('#pluginIconColorMode').inputValue(),'original');assert.equal(await page.locator('#pluginIconEditor').isVisible(),true);
    const importButton=page.locator('.plugin-icon-import summary');assert.ok(await importButton.locator('svg').count());
    await importButton.click();await page.locator('#pluginIconImportName').fill('Цветной SVG');
    assert.equal(await page.getByLabel('Название SVG',{exact:true}).count(),1);
    await page.locator('#pluginIconImportSvg').fill('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9" fill="#ff0000"/><circle cx="8" cy="8" r="3" fill="#00ff00"/></svg>');
    if(process.env.RNA_PLUGIN_SCREENSHOTS)await page.screenshot({path:process.env.RNA_PLUGIN_SCREENSHOTS+'-import.png'});
    await page.locator('#pluginIconImportButton').click();await page.locator('#pluginSavedIcons .plugin-icon-option[aria-pressed="true"] img').waitFor();
    await page.locator('#pluginIconColorMode').selectOption('custom');assert.ok(await page.locator('#pluginSavedIcons .plugin-icon-tint').count());
    await page.locator('#pluginSavedIcons .plugin-icon-option').last().click();assert.equal(await page.locator('#pluginIconColorMode').inputValue(),'original');
    if(process.env.RNA_PLUGIN_SCREENSHOTS)await page.screenshot({path:process.env.RNA_PLUGIN_SCREENSHOTS+'-wide.png'});
    await page.setViewportSize({width:420,height:900});await page.evaluate(()=>{document.documentElement.dataset.theme='dark';document.documentElement.style.zoom='1.25';});
    assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
    if(process.env.RNA_PLUGIN_SCREENSHOTS)await page.screenshot({path:process.env.RNA_PLUGIN_SCREENSHOTS+'-narrow.png'});
    await page.setViewportSize({width:1280,height:900});await page.evaluate(()=>{document.documentElement.style.zoom='';});
    await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
    await page.evaluate(id=>{RNAssistantDockWorkspace.close(RNAssistantPluginView.key({pluginId:id}));},other);
    await page.waitForFunction(id=>!RNAssistantDockWorkspace.getView(RNAssistantPluginView.key({pluginId:id})),other);
    await page.evaluate(id=>{pluginProbe.delay=true;RNAssistantPlugins.select(id);},other);
    await page.waitForFunction(()=>pluginProbe.release);
    await page.evaluate(id=>{RNAssistantDockWorkspace.close(RNAssistantPluginView.key({pluginId:id}));},other);
    await page.waitForFunction(id=>!RNAssistantDockWorkspace.getView(RNAssistantPluginView.key({pluginId:id})),other);
    await page.evaluate(()=>pluginProbe.release());
    await page.waitForFunction(()=>pluginProbe.calls.some(c=>c.command==='closePlugin'&&c.payload.instanceId===pluginProbe.late));
    assert.equal(await page.locator(selector(other)).count(),0);assert.deepEqual(errors,[]);
    console.log('PASS independent plugin views: retained frames, rail selection, move/restore, scoped annotations, late close, SVG defaults and palette');
  }catch(error){console.log("page errors",errors);if(page){console.log(await page.evaluate(()=>({target:RNAssistantDockWorkspace.activeTarget(),panels:[...document.querySelectorAll('.panel.active')].map(n=>n.id),plugins:[...document.querySelectorAll('.plugin-workbench')].map(n=>({id:n.dataset.pluginId,hidden:n.hidden,style:n.getAttribute('style')}))})));await page.screenshot({path:'/tmp/rna-plugin-view-failure.png'});}throw error;}finally{if(browser)await browser.close();server.kill();}
})().catch(error=>{console.error(error);process.exitCode=1;});
