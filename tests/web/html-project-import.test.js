'use strict';
// Production Browser / project navigation; synthetic exact snapshots and typed import outcomes.
const assert=require('node:assert/strict'),path=require('node:path'),{spawn}=require('node:child_process');
const {chromium}=require('playwright');
(async()=>{
  const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd:path.resolve(__dirname,'../..')});server.stderr.resume();let browser;
  try{
    const url=await new Promise((resolve,reject)=>{server.once('error',reject);server.once('exit',code=>reject(Error('preview '+code)));server.stdout.on('data',data=>{const m=/RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(data));if(m)resolve(m[1]);});});
    browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
    const page=await browser.newPage({viewport:{width:1280,height:900},reducedMotion:'reduce'}),errors=[];
    page.setDefaultTimeout(8000);page.on('pageerror',e=>errors.push(e.stack||e.message));
    await page.goto(url+'preview/web/?scenario=ready');await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
    await page.evaluate(()=>{
      window.projectTest={calls:[],pending:{},created:[],targets:{}};
      const original=send,read=RNAssistantResourceDownload.read;
      const bytes=new TextEncoder().encode(JSON.stringify({Project:{EntryPath:'index.html',Files:[{Path:'index.html',Kind:'html',Content:'<h1>Исходный снимок</h1><input value="initial">'}],Data:[],ExternalDependencies:[]},Views:[]}));
      RNAssistantResourceDownload.read=(data,options)=>data.importFixture?Promise.resolve(bytes):read(data,options);
      send=(command,payload)=>{
        projectTest.calls.push({command,payload});
        if(command==='exportArtifact'&&payload.resourceUri.includes('/project-import-'))return Promise.resolve({browserTarget:{kind:'HtmlArtifact',state:'Ready',chatId:payload.chatId,documentId:payload.documentId,resourceUri:payload.resourceUri,artifactId:'import',revision:1,capabilities:{preview:true,source:true,edit:false,versions:true,restore:false,exportProject:true,createProject:true,annotations:false}},data:{importFixture:true,leaseId:payload.resourceUri}});
        if(command==='closeArtifactTransfer')return Promise.resolve({closed:true});
        if(command==='getSavedHtmlHistory')return Promise.resolve({items:[],hasMore:false,totalCount:1});
        if(command==='createHtmlProjectFromArtifact')return new Promise(resolve=>{projectTest.pending[payload.resourceUri]=resolve;});
        if(command==='getProjectChats')return original(command,payload).then(value=>Object.assign({},value,{projects:value.projects.concat(projectTest.created)}));
        return original(command,payload);
      };
      projectTest.open=id=>{
        const target=projectTest.targets[id]||(projectTest.targets[id]=RNAssistantMaterialView.target({title:'Проект '+id,kind:'html_workspace',revision:1,
          resourceUri:'rna://chat/preview-chat/artifact/project-import-'+id+'/revision/1',documentId:'preview-document',documentTitle:'Источник.docx',canTransfer:true},{chatId:'preview-chat'}));
        return RNAssistantDockWorkspace.openTarget(target);
      };
      projectTest.finish=(id,status)=>{
        const item={workspaceId:'ws_imported_'+id,name:'Самостоятельный '+id,rootPath:'/managed/'+id,available:true,chats:[]};
        if(status==='Created')projectTest.created.push(item);
        const uri=projectTest.targets[id].resourceUri,resolve=projectTest.pending[uri];delete projectTest.pending[uri];
        resolve({status,backing:'Managed',...item,entryPath:'index.html',message:status==='Rejected'?'Перенос SDK пока не поддерживается.':'Проверьте папку /managed/'+id+' перед повторным созданием.'});
      };
    });
    const surface=id=>page.locator('.browser-object-view[aria-label="Проект '+id+'"]');
    async function action(id,name){
      await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
      const root=surface(id),button=root.getByRole('button',{name,exact:true,includeHidden:true});
      if(!await button.isVisible())await root.getByRole('button',{name:'Инструменты страницы',exact:true}).click();
      await button.click();
    }
    async function open(id){
      await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
      await page.evaluate(id=>projectTest.open(id),id);await surface(id).locator('iframe').waitFor({state:'attached'});
      await surface(id).waitFor({state:'visible'});
      // Import/menu behavior is independent of the separately tracked Chromium pointer gap above iframe.
      const source=surface(id).getByRole('button',{name:'Исходник',exact:true,includeHidden:true});
      if(await source.getAttribute('aria-pressed')!=='true')await action(id,'Исходник');
    }
    async function menu(id){await action(id,'Сохранение проекта');await page.getByRole('menuitem',{name:'Создать проект из снимка',exact:true}).waitFor();}
    async function begin(id){await menu(id);await page.getByRole('menuitem',{name:'Создать проект из снимка',exact:true}).click();await page.waitForFunction(id=>!!projectTest.pending[projectTest.targets[id].resourceUri],id);}
    const calls=()=>page.evaluate(()=>projectTest.calls.filter(c=>c.command==='createHtmlProjectFromArtifact'));
    await open('A');
    await page.evaluate(()=>{state.activeChatId='other-chat';state.chatNavigationVersion++;});
    await menu('A');
    if(process.env.BROWSER_SCREENSHOT)await page.screenshot({path:process.env.BROWSER_SCREENSHOT+'-wide.png'});
    await page.getByRole('menuitem',{name:'Создать проект из снимка',exact:true}).click();
    await page.waitForFunction(()=>!!projectTest.pending[projectTest.targets.A.resourceUri]);
    assert.equal(await surface('A').getByRole('button',{name:'Сохранение проекта',includeHidden:true}).isDisabled(),true,'pending cannot dispatch a second import');
    assert.deepEqual((await calls()).map(c=>c.payload),[{chatId:'preview-chat',documentId:'preview-document',resourceUri:'rna://chat/preview-chat/artifact/project-import-A/revision/1',backing:'Managed'}]);
    await page.evaluate(()=>projectTest.finish('A','Created'));
    await page.waitForFunction(()=>RNAssistantProjectChats.workspaceId()==='ws_imported_A');
    await open('A');await action('A','Сохранение проекта');
    assert.equal(await page.getByRole('menuitem',{name:'Создать проект из снимка',exact:true}).count(),0,'created workspace is reused');
    await page.getByRole('menuitem',{name:'Открыть созданный проект',exact:true}).click();
    assert.equal((await calls()).length,1,'opening the project does not create another copy');

    await open('B');await begin('B');await open('A');
    const selected=await page.evaluate(()=>RNAssistantProjectChats.workspaceId());
    await page.evaluate(()=>projectTest.finish('B','Created'));
    await page.waitForFunction(()=>!document.querySelector('.browser-object-view[aria-label="Проект B"] .preview-surface-actions button[aria-label="Сохранение проекта"]').disabled);
    assert.equal(await page.evaluate(()=>RNAssistantProjectChats.workspaceId()),selected,'late success does not take navigation away');
    assert.equal(await surface('A').isVisible(),true,'selected browser remains active');
    await open('B');await action('B','Сохранение проекта');await page.getByRole('menuitem',{name:'Открыть созданный проект',exact:true}).waitFor();await page.keyboard.press('Escape');

    await open('C');await begin('C');await page.evaluate(()=>projectTest.finish('C','Rejected'));
    await surface('C').getByText('Перенос SDK пока не поддерживается.',{exact:true}).waitFor();
    await menu('C');assert.equal(await page.getByRole('menuitem',{name:'Создать проект из снимка',exact:true}).isEnabled(),true,'known refusal allows a new explicit attempt');
    await page.getByRole('menuitem',{name:'Создать проект из снимка',exact:true}).click();
    await page.waitForFunction(()=>!!projectTest.pending[projectTest.targets.C.resourceUri]);
    await page.evaluate(()=>projectTest.finish('C','Unknown'));await surface('C').getByText(/Проверьте папку \/managed\/C/).waitFor();
    await page.setViewportSize({width:420,height:900});await page.evaluate(()=>{document.documentElement.style.zoom='1.25';});
    await menu('C');assert.equal(await page.getByRole('menuitem',{name:'Создать проект из снимка',exact:true}).isDisabled(),true,'unknown effect is not offered as retry');
    assert.ok(await page.locator('.rna-action-menu-popup').evaluate(n=>{const r=n.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth+1;}));
    if(process.env.BROWSER_SCREENSHOT)await page.screenshot({path:process.env.BROWSER_SCREENSHOT+'-narrow.png'});
    await page.keyboard.press('Escape');

    await page.setViewportSize({width:1280,height:900});await page.evaluate(()=>{document.documentElement.style.zoom='';});
    await open('D');await begin('D');
    await page.evaluate(()=>RNAssistantDockWorkspace.close(RNAssistantMaterialView.key(projectTest.targets.D)));
    await open('A');await page.evaluate(()=>projectTest.finish('D','Created'));
    assert.equal(await surface('D').count(),0,'late mutation reply never reopens a closed view');
    assert.equal((await calls()).length,5,'no automatic retry after rejection, unknown or close');
    assert.equal(await page.evaluate(()=>projectTest.calls.some(c=>/submit.*Chat|runModel|createProjectChat/.test(c.command))),false,'project import does not start a chat/run');
    assert.deepEqual(errors,[]);
    console.log('PASS html-project-import: exact source, pending/reuse, project navigation, stale/close, rejected/unknown, narrow/zoom');
  }finally{if(browser)await browser.close();server.kill();}
})().catch(error=>{console.error(error);process.exitCode=1;});
