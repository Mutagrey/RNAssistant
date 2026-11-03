'use strict';
// Synthetic exact artifact packages and plugin releases through production Browser/Dockview.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{spawn}=require('node:child_process');
const {chromium}=require('playwright');
(async()=>{
  const root=process.env.BROWSER_TEST_ROOT||path.resolve(__dirname,'../..'),baseline=!!process.env.BROWSER_BASELINE;
  const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd:root});server.stderr.resume();let browser;
  try{
    const url=await new Promise((resolve,reject)=>{server.once('error',reject);server.once('exit',code=>reject(Error('preview '+code)));server.stdout.on('data',data=>{const m=/RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(data));if(m)resolve(m[1]);});});
    browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
    const page=await browser.newPage({viewport:{width:1280,height:900},reducedMotion:'reduce'}),errors=[];
    page.setDefaultTimeout(8000);page.on('pageerror',e=>errors.push(e.message));
    await page.goto(url+'preview/web/?scenario=ready');await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise,null,{timeout:15000}).catch(error=>{throw new Error(error.message+'; page errors: '+errors.join(' | '));});
    await page.evaluate(()=>{
      window.savedTest={calls:[],reads:0,nextLease:0,loads:0};
      const original=send,read=RNAssistantResourceDownload.read;
      const uri=id=>'rna://chat/preview-chat/artifact/saved-'+id+'/revision/1';
      savedTest.items=['A','B'].map(id=>({title:'Сохранённый HTML '+id,kind:'html_workspace',resourceUri:uri(id),revision:1,
        documentId:id==='A'?'preview-document':'other-document',documentTitle:id==='A'?'Продажи.xlsx':'Другой документ.xlsx',linked:false,
        canTransfer:true,mimeType:'application/json',updatedUtc:'2026-11-02T12:00:00Z'}));
      const html='<h1>Точная версия</h1><label>Ввод <input value="initial"></label>';
      const source='/* сохранённый большой исходник */\n'.repeat(64000);
      savedTest.bytes=new TextEncoder().encode(JSON.stringify({Project:{EntryPath:'index.html',Files:[{Path:'index.html',Kind:'html',Content:html},{Path:'styles.css',Kind:'css',Content:source}],Data:[{Name:'saved'}],ExternalDependencies:[]},Views:[{Name:'saved',Reference:{uri:'rna://chat/preview-chat/snapshot/saved',revision:'r1'},View:'text',Path:'',Text:'Точные сохранённые данные </script>'}]}));
      RNAssistantResourceDownload.read=async function(data,options){if(!data.synthetic)return read(data,options);savedTest.reads++;return savedTest.bytes;};
      send=function(command,payload){savedTest.calls.push({command,payload});
        if(command==='listArtifactCatalog')return Promise.resolve({chatId:state.activeChatId,sessionRevision:1,documentId:'preview-document',items:savedTest.items,documents:[]});
        if(command==='exportArtifact'&&payload.resourceUri.includes('/saved-')){
          const value={contentType:'application/json',fileName:'project.rna',data:{synthetic:true,leaseId:'saved-'+(++savedTest.nextLease),payload:{byteLength:savedTest.large?17*1024*1024:savedTest.bytes.length}}};
          if(savedTest.delayExport)return new Promise(resolve=>{savedTest.lateLease=value.data.leaseId;savedTest.releaseExport=()=>resolve(value);});return Promise.resolve(value);
        }
        if(command==='closeArtifactTransfer')return Promise.resolve({});
        if(command==='getPluginVersions')return Promise.resolve({items:[1,2].map(n=>({releaseId:'saved-release-'+n,version:String(n),name:'Плагин',capturedUtc:'2026-11-02T12:00:00Z',active:false})),nextCursor:null});
        if(command==='openPlugin'&&payload.releaseId){
          const value={instanceId:'saved-instance-'+(++savedTest.nextLease),pluginId:payload.pluginId,releaseId:payload.releaseId,preview:true,entryUrl:'/preview/mock-plugin.html?name='+payload.releaseId};
          if(savedTest.delayPlugin)return new Promise(resolve=>{savedTest.lateInstance=value.instanceId;savedTest.releasePlugin=()=>resolve(value);});return Promise.resolve(value);
        }
        if(command==='restorePluginVersion'){savedTest.restored=payload;
          if(!savedTest.restoreAllowed)return Promise.reject(new Error('Активная версия изменилась. Обновите список версий.'));
          return original('getPlugins',{}).then(value=>Object.assign({},value.items.find(item=>item.pluginId===payload.pluginId),{releaseId:payload.releaseId}));}

        return original(command,payload);
      };
      RNAssistantChatInspector.setOpen(false);switchTab('artifacts');RNAssistantArtifactCatalog.show('all');
    });
    const oldHtml='#artifactCatalogPreviewBody iframe',htmlTitle=id=>'Сохранённый HTML '+id;
    const surface=title=>page.locator('.browser-object-view').filter({has:page.locator('.preview-surface-title').getByText(title,{exact:true})});
    const htmlSelector=id=>baseline?oldHtml:'.browser-object-view[aria-label="'+htmlTitle(id)+'"] iframe';
    const openHtml=async id=>{await page.locator('#htmlWorkspaceTree').getByText(htmlTitle(id),{exact:true}).click();await page.frameLocator(htmlSelector(id)).locator('h1').waitFor({state:'attached'});};
    await page.locator('#artifactCatalogOverviewRows tr').first().waitFor();assert.equal(await page.evaluate(()=>savedTest.reads),0);
    if(!baseline)await page.evaluate(()=>{state.htmlWorkspaceDirty=true;});
    const times=[];
    for(const id of ['A','B','A','B','A']){const start=performance.now();await openHtml(id);times.push(performance.now()-start);if(id==='A'&&times.length===1)await page.frameLocator(htmlSelector(id)).getByRole('textbox').fill('retained');}
    if(!baseline)assert.equal(await page.evaluate(()=>RNAssistantArtifactCatalog.tree([],[],{selected:state.htmlWorkspaceSelection}).selectedKey),'catalog::'+await page.evaluate(()=>savedTest.items[0].resourceUri),'dirty editor does not retarget source selection');
    await page.evaluate(()=>{state.htmlWorkspaceDirty=false;});
    const retained=await page.frameLocator(htmlSelector('A')).getByRole('textbox').inputValue();
    assert.equal(await page.frameLocator(htmlSelector('A')).locator('body').evaluate(async()=>{const resource=await RN.resources.open('saved');const batch=await resource.read({limit:100});await resource.close();return batch.text;}),'Точные сохранённые данные </script>');
    const htmlReads=await page.evaluate(()=>savedTest.reads);
    assert.equal(htmlReads,baseline?5:2);assert.equal(retained,baseline?'initial':'retained');
    const packageBytes=await page.evaluate(()=>savedTest.bytes.byteLength);
    let sourceCharacters,sourceMs;
    if(baseline){
      const start=performance.now();await page.locator('#artifactCatalogPreviewBody').getByRole('button',{name:'Код',exact:true}).click();
      await page.locator('#artifactCatalogPreviewBody select').selectOption('styles.css');sourceMs=performance.now()-start;
      sourceCharacters=await page.locator('#artifactCatalogPreviewBody pre').evaluate(n=>n.textContent.length);
    }
    if(!baseline){
      const a=surface(htmlTitle('A'));
      const sourceStart=performance.now();
      await a.getByRole('button',{name:'Исходник',exact:true}).click();await a.getByRole('combobox',{name:'Файл проекта'}).selectOption('styles.css');
      sourceMs=performance.now()-sourceStart;sourceCharacters=await a.locator('.CodeMirror').evaluate(n=>n.CodeMirror.getValue().length);
      assert.ok(sourceCharacters<=32000);
      if(process.env.BROWSER_SCREENSHOT)await page.screenshot({path:process.env.BROWSER_SCREENSHOT+'-source1280.png'});
      await a.getByRole('button',{name:'Следующая страница',exact:true}).click();assert.match(await a.locator('.rn-text-viewer-status').innerText(),/32001/);
      await openHtml('B');await openHtml('A');assert.equal(await a.getByRole('combobox',{name:'Файл проекта'}).inputValue(),'styles.css');
      assert.equal(await a.getByRole('button',{name:'Исходник',exact:true}).getAttribute('aria-pressed'),'true');
      await a.getByRole('button',{name:'Страница',exact:true}).click();assert.equal(await page.frameLocator(htmlSelector('A')).getByRole('textbox').inputValue(),'retained');
      await page.locator('#workspaceLayout').click();await page.getByRole('menuitem',{name:'Сохранить раскладку',exact:true}).click();
      await page.locator('#workspaceLayout').click();await page.getByRole('menuitem',{name:'Вернуть сохранённую',exact:true}).click();
      assert.equal(await page.frameLocator(htmlSelector('A')).getByRole('textbox').inputValue(),'retained');
      await page.evaluate(()=>{state.activeChatId='another-chat';state.chatNavigationVersion++;});
      await a.getByRole('button',{name:'Повторить загрузку',exact:true}).click();await page.frameLocator(htmlSelector('A')).getByRole('heading',{name:'Точная версия'}).waitFor();
      assert.deepEqual(await page.evaluate(()=>{const p=savedTest.calls.filter(c=>c.command==='exportArtifact').at(-1).payload;return [p.chatId,p.documentId];}),['preview-chat','preview-document']);
      await page.evaluate(()=>{savedTest.large=true;});await a.getByRole('button',{name:'Повторить загрузку',exact:true}).click();await a.getByRole('alert').filter({hasText:'16 МиБ'}).waitFor();
      for(const [width,theme] of [[1280,'light'],[420,'dark']]){
        await page.setViewportSize({width,height:900});await page.evaluate(theme=>{document.documentElement.dataset.theme=theme;document.documentElement.style.zoom='1.25';},theme);
        await page.waitForTimeout(150);assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
        const box=await a.boundingBox();assert.ok(box.width<width&&box.x>=0&&box.x+box.width<=width+1,'Browser geometry follows Dock at zoom');
        await page.locator('.browser-view-anchor:visible').focus();await page.locator('.browser-view-anchor:visible').press('Enter');
        assert.ok(await a.evaluate(root=>root.contains(document.activeElement)),'keyboard enters the active Browser surface');
        await a.getByRole('button',{name:'Действия с материалом',exact:true}).focus();await page.keyboard.press('Enter');
        await page.getByRole('menuitem',{name:'Копировать точную ссылку',exact:true}).waitFor();await page.keyboard.press('Escape');
        assert.equal(await a.getByRole('button',{name:'Действия с материалом',exact:true}).evaluate(node=>node===document.activeElement),true);

        if(process.env.BROWSER_SCREENSHOT)await page.screenshot({path:process.env.BROWSER_SCREENSHOT+'-'+theme+width+'.png'});
      }
      await page.evaluate(()=>{document.documentElement.style.zoom='';savedTest.large=false;state.activeChatId='preview-chat';state.chatNavigationVersion++;savedTest.delayExport=true;
        savedTest.lateTarget=RNAssistantMaterialView.target(Object.assign({},savedTest.items[0],{title:'Поздняя версия',resourceUri:savedTest.items[0].resourceUri+'-late'}),{chatId:'preview-chat'});
        RNAssistantDockWorkspace.openTarget(savedTest.lateTarget);});
      await page.waitForFunction(()=>savedTest.releaseExport);
      await page.evaluate(()=>{RNAssistantDockWorkspace.close(RNAssistantMaterialView.key(savedTest.lateTarget));savedTest.releaseExport();savedTest.delayExport=false;});
      await page.waitForFunction(()=>savedTest.calls.some(c=>c.command==='closeArtifactTransfer'&&c.payload.leaseId===savedTest.lateLease));
      assert.equal(await surface('Поздняя версия').count(),0);
    }
    await page.setViewportSize({width:1280,height:900});
    await page.evaluate(()=>RNAssistantPlugins.select('data-templates'));await page.locator('[data-plugin-node="PreviewFrameHost"] iframe').waitFor();
    const openRelease=async n=>{
      await page.evaluate(()=>RNAssistantPlugins.select('data-templates'));
      await page.locator('[data-plugin-node="Versions"] summary').click();await page.locator('[data-plugin-node="VersionItems"]').getByRole('button',{name:new RegExp('^v'+n+' ')}).click();
      if(baseline)await page.waitForFunction(n=>document.querySelector('[data-plugin-node="PreviewFrameHost"] iframe')?.src.includes('saved-release-'+n),n);
      else await page.locator('.browser-object-view iframe[src*="saved-release-'+n+'"]').waitFor({state:'visible'});
    };
    const pluginTimes=[];
    for(const n of [1,2,1,2,1]){const start=performance.now();await openRelease(n);pluginTimes.push(performance.now()-start);}
    const opens=await page.evaluate(()=>savedTest.calls.filter(c=>c.command==='openPlugin'&&c.payload.releaseId).length);assert.equal(opens,baseline?5:2);
    if(!baseline){
      const release=page.locator('.browser-object-view').filter({has:page.locator('iframe[src*="saved-release-1"]')});
      await release.getByRole('button',{name:'Действия с версией',exact:true}).click();await page.getByRole('menuitem',{name:'Восстановить эту версию',exact:true}).click();
      await release.getByRole('alert').filter({hasText:'Активная версия изменилась'}).waitFor();
      assert.ok(await page.evaluate(()=>savedTest.restored.releaseId==='saved-release-1'&&!!savedTest.restored.expectedActiveReleaseId));
      await page.evaluate(()=>{savedTest.restoreAllowed=true;savedTest.pinnedFrame=document.querySelector('.browser-object-view iframe[src*="saved-release-1"]');});
      await release.getByRole('button',{name:'Действия с версией',exact:true}).click();await page.getByRole('menuitem',{name:'Восстановить эту версию',exact:true}).click();
      await release.getByRole('status').filter({hasText:'Версия восстановлена.'}).waitFor();
      assert.ok(await page.evaluate(()=>savedTest.pinnedFrame===document.querySelector('.browser-object-view iframe[src*="saved-release-1"]')),'restoration does not replace the saved preview');
      assert.ok(await page.evaluate(()=>savedTest.calls.some(c=>c.command==='closePlugin'&&c.payload.instanceId.startsWith('preview-plugin-instance'))),'restoration invalidates the old mutable development instance');

      await page.evaluate(()=>{savedTest.delayPlugin=true;savedTest.pluginTarget={view:'plugin-release',pluginId:'data-templates',releaseId:'saved-release-late',expectedActiveReleaseId:'active',title:'Поздний релиз'};RNAssistantDockWorkspace.openTarget(savedTest.pluginTarget);});
      await page.waitForFunction(()=>savedTest.releasePlugin);
      await page.evaluate(()=>{RNAssistantDockWorkspace.close(RNAssistantPluginReleaseView.key(savedTest.pluginTarget));savedTest.releasePlugin();});
      await page.waitForFunction(()=>savedTest.calls.some(c=>c.command==='closePlugin'&&c.payload.instanceId===savedTest.lateInstance));
      assert.equal(await surface('Поздний релиз').count(),0);
      assert.equal(await page.evaluate(()=>savedTest.calls.some(c=>/sendMessage|startAgent|runModelDiagnostic/.test(c.command))),false);
      assert.equal(await page.evaluate(()=>RNAssistantWorkspaceChat.owner()),'office');
    }
    assert.deepEqual(errors,[]);
    const result={mode:baseline?'before':'after',fixture:{packageBytes,selections:5},html:{reads:htmlReads,returnInput:retained,actionMs:times,sourceCharacters,sourceMs},plugin:{opens,actionMs:pluginTimes}};
    if(process.env.BROWSER_RESULT)fs.writeFileSync(process.env.BROWSER_RESULT,JSON.stringify(result,null,2)+'\n');
    console.log(JSON.stringify(result));
  }finally{if(browser)await browser.close();server.kill('SIGTERM');}
})().catch(e=>{console.error(e);process.exitCode=1;});
