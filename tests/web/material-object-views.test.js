'use strict';
// Production Dockview + typed readers over the existing synthetic bridge. No Office/models.
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), {spawn} = require('node:child_process');
const {chromium} = require('playwright');
(async () => {
  const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd:path.resolve(__dirname,'../..')});
  server.stderr.resume(); let browser;
  try {
    const address=await new Promise((resolve,reject)=>{
      server.once('error',reject);server.once('exit',code=>reject(Error('preview exit '+code)));
      server.stdout.on('data',data=>{const m=/RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(data));if(m)resolve(m[1]);});
    });
    browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
    const page=await browser.newPage({viewport:{width:1280,height:900},reducedMotion:'reduce'}), errors=[];
    page.setDefaultTimeout(8000);page.on('pageerror',e=>errors.push(e.stack||e.message));
    await page.route('**/preview/fixtures.js',async route=>{
      const response=await route.fetch();await route.fulfill({response,body:await response.text()+
        '\nPrototypeFixtures.report += "\\n\\n" + "Проверка сохранения прокрутки.\\n\\n".repeat(300);'+
        '\nPrototypeFixtures.materialSources.text = "Строка большого исходника\\n".repeat(30000);'});
    });
    await page.goto(address+'preview/web/?scenario=ready');
    await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
    await page.evaluate(()=>{
      RNAssistantChatInspector.setOpen(false);
      window.materialTest={calls:[],urls:new Set(),revoked:new Set(),delayImage:false};
      const original=send;send=function(command,payload){
        materialTest.calls.push({command,payload});const request=original(command,payload);
        if(command!=='readArtifactImage'||!materialTest.delayImage)return request;
        const delayed=request.then(value=>new Promise(resolve=>{materialTest.lateLease=value.data.leaseId;materialTest.releaseImage=()=>resolve(value);}));
        delayed.requestId=request.requestId;return delayed;
      };
      const make=URL.createObjectURL.bind(URL),revoke=URL.revokeObjectURL.bind(URL);
      URL.createObjectURL=function(blob){const url=make(blob);materialTest.urls.add(url);return url;};
      URL.revokeObjectURL=function(url){materialTest.revoked.add(url);return revoke(url);};
    });
    const source=page.locator('#htmlWorkspaceTree');
    const material=name=>page.locator('.material-object-view').filter({has:page.locator('.resource-editor-title').getByText(name,{exact:true})});
    const tab=name=>page.locator('[data-tab-panel-id]').filter({has:page.locator('.dv-default-tab-content').getByText(name,{exact:true})});
    const open=async name=>{await source.getByText(name,{exact:true}).click();await material(name).waitFor({state:'visible'});};
    const reads=()=>page.evaluate(()=>materialTest.calls.filter(c=>/^readArtifact|^exportArtifact/.test(c.command)).length);
    await page.locator('.rail-button[data-tab="artifacts"]').click();
    await page.locator('#artifactCatalogOverviewRows tr').first().waitFor();
    assert.equal(await reads(),0,'opening a source loads metadata only');
    await open('Итоги сентября.md');
    const md=material('Итоги сентября.md');await md.getByRole('button',{name:'Исходник',exact:true}).click();
    await page.evaluate(()=>{const root=document.querySelector('.material-object-view');materialTest.first=root;materialTest.source=root.querySelector('.rn-text-viewer-source');root.querySelector('.material-object-content').scrollTop=230;materialTest.scroll=root.querySelector('.material-object-content').scrollTop;});
    await open('Продажи по регионам.json');await material('Продажи по регионам.json').locator('.artifact-viewer-host > *').first().waitFor();
    await open('Методика расчёта.pdf');await material('Методика расчёта.pdf').locator('.rn-pdf-viewer-page img').waitFor().catch(async()=>await material('Методика расчёта.pdf').locator('img').first().waitFor());
    const before=await reads();await tab('Итоги сентября.md').click();
    assert.equal(await reads(),before,'return to a ready view does not read again');
    assert.ok(await page.evaluate(()=>materialTest.first===document.querySelector('.material-object-view')&&materialTest.first.contains(materialTest.source)),'view and source DOM retained');
    assert.equal(await md.locator('.material-object-content').evaluate(n=>n.scrollTop),await page.evaluate(()=>materialTest.scroll));
    await open('Итоги сентября.md');assert.equal(await md.count(),1,'same target reuses its view');
    await page.evaluate(()=>{state.htmlWorkspaceDirty=true;openArtifactResource(state.artifacts[0]);state.htmlWorkspaceDirty=false;});
    assert.equal(await md.count(),1,'chat card and catalog reuse the same exact view; reading does not discard an editor draft');
    assert.ok(await page.evaluate(()=>materialTest.calls.filter(c=>/^readArtifact/.test(c.command)).every(c=>c.payload.chatId==='preview-chat'&&c.payload.documentId==='preview-document')));
    // A different current chat never changes the original view's reader scope.
    await page.evaluate(async()=>{const value=await send('selectChat',{chatId:'preview-summary'});applyChatStateForChat(value,'preview-summary');state.activeChatId='preview-summary';state.chatNavigationVersion++;});
    await md.getByRole('button',{name:'Действия с материалом',exact:true}).click();await page.getByRole('menuitem',{name:'Повторить чтение',exact:true}).click();
    await md.getByRole('button',{name:'Исходник',exact:true}).waitFor();
    assert.equal(await page.evaluate(()=>materialTest.calls.filter(c=>c.command==='readArtifactViewerPage').at(-1).payload.chatId),'preview-chat');
    // Separate revision, same view factory. Closing a pending image also closes its late lease.
    await page.evaluate(()=>{const t={view:'material',chatId:'preview-chat',documentId:'preview-document',kind:'markdown',mimeType:'text/markdown',title:'Итоги · v2',resourceUri:'rna://chat/preview-chat/artifact/report/revision/2'};materialTest.revision=t;RNAssistantDockWorkspace.openTarget(t);});
    const failed=material('Итоги · v2');await failed.getByRole('button',{name:'Повторить чтение',exact:true}).waitFor();
    assert.equal(await md.count(),1,'different exact URI never replaces a previous revision');
    await page.evaluate(()=>RNAssistantDockWorkspace.close(RNAssistantMaterialView.key(materialTest.revision)));
    await page.evaluate(()=>{materialTest.delayImage=true;materialTest.image={view:'material',chatId:'preview-chat',documentId:'preview-document',kind:'image',mimeType:'image/png',title:'Диаграмма.png',resourceUri:'rna://chat/preview-chat/artifact/image/revision/1'};RNAssistantDockWorkspace.openTarget(materialTest.image);});
    await page.waitForFunction(()=>materialTest.releaseImage);
    await page.evaluate(()=>{RNAssistantDockWorkspace.close(RNAssistantMaterialView.key(materialTest.image));materialTest.releaseImage();});
    await page.waitForFunction(()=>materialTest.calls.some(c=>c.command==='resourceDataClose'&&c.payload.leaseId===materialTest.lateLease));
    assert.equal(await material('Диаграмма.png').count(),0,'late response cannot resurrect a closed view');
    await page.evaluate(()=>{materialTest.delayImage=false;materialTest.beforeImageUrls=[...materialTest.urls];RNAssistantDockWorkspace.openTarget(materialTest.image);});
    await material('Диаграмма.png').locator('.viewer-canvas img').waitFor();
    await page.evaluate(()=>RNAssistantDockWorkspace.close(RNAssistantMaterialView.key(materialTest.image)));
    await page.waitForFunction(()=>[...materialTest.urls].filter(url=>!materialTest.beforeImageUrls.includes(url)).every(url=>materialTest.revoked.has(url)));
    // Long source: bounded pages, no automatic full read, bounded retained page window.
    await page.evaluate(()=>{materialTest.large={view:'material',chatId:'preview-chat',documentId:'preview-document',kind:'file',mimeType:'text/plain',title:'Большой исходник.txt',resourceUri:'rna://chat/preview-chat/artifact/text/revision/1'};RNAssistantDockWorkspace.openTarget(materialTest.large);});
    const large=material('Большой исходник.txt');await large.getByRole('button',{name:'Следующая страница',exact:true}).waitFor();
    assert.equal(await large.getByRole('button',{name:'Загрузить полностью',exact:true}).count(),0);
    assert.ok((await large.locator('.rn-text-viewer-source').innerText()).length<40000);
    for(let i=0;i<17;i++){await large.getByRole('button',{name:'Следующая страница',exact:true}).click();await page.waitForFunction(count=>materialTest.calls.filter(c=>c.command==='readArtifactViewerPage'&&c.payload.resourceUri.endsWith('/text/revision/1')).length===count,i+2);await large.getByRole('button',{name:'Следующая страница',exact:true}).waitFor();}
    await large.getByRole('status').filter({hasText:'В памяти последние 16 страниц'}).waitFor();
    assert.ok((await large.locator('.rn-text-viewer-source').innerText()).length<40000);
    await page.evaluate(()=>{
      materialTest.sequence={view:'material',chatId:'preview-chat',documentId:'preview-other-document',kind:'sequence',mimeType:'application/json',title:'Коллекция',resourceUri:'rna://chat/preview-chat/artifact/sequence/revision/1'};
      RNAssistantDockWorkspace.openTarget(materialTest.sequence);
    });
    const sequence=material('Коллекция');await sequence.getByRole('button',{name:'JSON',exact:true}).click();
    await sequence.locator('.rn-json-viewer:visible').first().waitFor();
    await page.evaluate(()=>{materialTest.task={view:'material',chatId:'preview-chat',documentId:'preview-document',kind:'task_list',mimeType:'application/vnd.rnassistant.task-list+json',title:'Задачи',resourceUri:'rna://chat/preview-chat/artifact/tasks/revision/1'};RNAssistantDockWorkspace.openTarget(materialTest.task);});
    await material('Задачи').locator('.rn-task-list-viewer').waitFor();
    const capped=await page.evaluate(()=>RNAssistantDockWorkspace.openTarget({...materialTest.large,title:'Седьмой объект',resourceUri:'rna://chat/preview-chat/artifact/extra/revision/1'}));
    assert.equal(capped,false,'instance limit is enforced before body reads');
    await page.evaluate(()=>{RNAssistantDockWorkspace.close(RNAssistantMaterialView.key(materialTest.task));RNAssistantDockWorkspace.close(RNAssistantMaterialView.key(materialTest.sequence));});
    // Save/restore does not reconstruct already-mounted readers.
    await tab('Итоги сентября.md').click();await md.getByRole('button',{name:'Исходник',exact:true}).click();
    await page.evaluate(()=>{materialTest.restored=document.querySelector('.material-object-view .rn-text-viewer-source');});
    await page.locator('#workspaceLayout').click();await page.getByRole('menuitem',{name:'Сохранить раскладку',exact:true}).click();
    const beforeRestore=await reads();
    await page.locator('#workspaceLayout').click();await page.getByRole('menuitem',{name:'Вернуть сохранённую',exact:true}).click();
    assert.equal(await reads(),beforeRestore);assert.ok(await page.evaluate(()=>document.contains(materialTest.restored)));
    // Move the object through Dockview's actual pointer path, retaining its controller.
    await page.locator('.dock-group-actions').first().getByRole('button',{name:'Открыть экран справа',exact:true}).click();
    await page.locator('.dock-screen-chooser').getByRole('button',{name:'Инспектор',exact:true}).click();
    const moving=await tab('Итоги сентября.md').boundingBox(), destination=await page.locator('[data-tab-panel-id="inspector"]').boundingBox();
    await page.mouse.move(moving.x+30,moving.y+15);await page.mouse.down();await page.mouse.move(moving.x+30,moving.y+45,{steps:5});await page.waitForTimeout(180);
    await page.mouse.move(destination.x+25,destination.y+15,{steps:12});await page.mouse.up();
    await page.waitForFunction(()=>document.querySelector('.material-object-view').getBoundingClientRect().x>600);
    assert.ok(await page.evaluate(()=>document.contains(materialTest.restored)),'moving an object preserves its reader DOM');
    await page.locator('#workspaceLayout').click();await page.getByRole('menuitem',{name:'Одна группа вкладок',exact:true}).click();
    for(const theme of ['light','dark']){
      await page.evaluate(theme=>applyUiTheme({UiTheme:theme},false),theme);
      for(const width of [1280,420]){
        await page.setViewportSize({width,height:width===420?760:900});await page.waitForFunction(width=>innerWidth===width,width);
        await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
        await md.getByRole('button',{name:'Действия с материалом',exact:true}).click();
        const box=await page.getByRole('menu').boundingBox();assert.ok(box.x>=0&&box.x+box.width<=width+1&&box.y+box.height<=900);
        await page.keyboard.press('Escape');assert.equal(await md.getByRole('button',{name:'Действия с материалом',exact:true}).evaluate(n=>n===document.activeElement),true);
        assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
        if(process.env.MATERIAL_SCREENSHOTS){fs.mkdirSync(process.env.MATERIAL_SCREENSHOTS,{recursive:true});await page.screenshot({path:path.join(process.env.MATERIAL_SCREENSHOTS,`s3b2-${theme}-${width}.png`)});}
      }
    }
    await page.evaluate(()=>document.body.style.zoom='1.25');assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
    await page.evaluate(()=>{document.body.style.zoom='';switchTab('chat');});
    assert.equal(await tab('Итоги сентября.md').count(),1,'object views stay accessible with another source selected');
    await tab('Итоги сентября.md').click();await md.waitFor({state:'visible'});
    assert.deepEqual(errors,[]);console.log('PASS material views: exact scope, independent formats, retained DOM, late lease cleanup, bounded pages, restore, narrow/themes/keyboard');
  }finally{if(browser)await browser.close();server.kill();}
})().catch(error=>{console.error(error);process.exitCode=1;});
