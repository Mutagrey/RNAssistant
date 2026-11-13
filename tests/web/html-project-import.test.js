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
      window.projectTest={calls:[],pending:{},created:[],targets:{},pages:new Map()};
      const original=send;
      send=(command,payload)=>{
        projectTest.calls.push({command,payload});
        if(command==='browserCommand') {
          let value=projectTest.pages.get(payload.browserTabId);
          if(payload.action==='Open') {
            const match=/project-import-([A-Z])~1/.exec(decodeURIComponent(payload.locator));if(!match)throw Error('Unexpected fixture locator');
            const id=match[1],source=projectTest.targets[id],raw=id==='C';
            const target={kind:raw?'HtmlResource':'HtmlArtifact',state:'Ready',chatId:source.chatId,documentId:source.documentId,ownerScope:payload.ownerScope,
              resourceUri:source.resourceUri,displayLocator:payload.locator,title:source.title,artifactId:'project-import-'+id,revision:1,immutable:true,
              capabilities:{source:true,versions:!raw,exportProject:!raw,createProject:true}};
            value={browserTabId:payload.browserTabId,sessionId:'session-'+id,revision:1,documentGeneration:1,url:payload.locator,title:source.title,
              ownerScope:payload.ownerScope,target,status:'Unavailable',message:RNAssistantBrowser.unavailable};projectTest.pages.set(payload.browserTabId,value);
          } else if(payload.action==='Close')value.status='Closed';else if(payload.action==='Reload')value.documentGeneration++;
          value.revision++;return Promise.resolve(structuredClone(value));
        }
        if(command==='readBrowserSource')return Promise.resolve({files:['index.html'],path:'index.html',text:'<h1>Исходный снимок</h1>',offset:0,totalCharacters:23,revision:'sha',requestGeneration:payload.requestGeneration});
        if(command==='closeArtifactTransfer')return Promise.resolve({closed:true});
        if(command==='getBrowserVersionState')return Promise.resolve({target:payload.target,stateRevision:'catalog',contentRevision:payload.target.resourceUri,capabilities:payload.target.capabilities});
        if(command==='getBrowserVersions')return Promise.resolve({items:[],stateRevision:'catalog'});
        if(command==='createHtmlProjectFromArtifact')return new Promise(resolve=>{projectTest.pending[payload.resourceUri]=resolve;});
        if(command==='getProjectChats')return original(command,payload).then(value=>Object.assign({},value,{projects:value.projects.concat(projectTest.created)}));
        return original(command,payload);
      };
      projectTest.open=id=>{
        const target=projectTest.targets[id]||(projectTest.targets[id]=RNAssistantMaterialView.target({title:'Проект '+id,kind:id==='C'?'file':'html_workspace',mimeType:id==='C'?'text/html':'application/json',revision:1,
          resourceUri:'rna://chat/preview-document/artifact/project-import-'+id+'/revision/1',documentId:'preview-document',documentTitle:'Источник.docx',canTransfer:true},{chatId:'preview-chat'}));
        return RNAssistantDockWorkspace.openTarget(target,{browserTabId:'import-'+id});
      };
      projectTest.finish=(id,status)=>{
        const item={workspaceId:'ws_imported_'+id,name:'Самостоятельный '+id,rootPath:'/managed/'+id,available:true,chats:[]};
        if(status==='Created')projectTest.created.push(item);
        const uri=projectTest.targets[id].resourceUri,resolve=projectTest.pending[uri];delete projectTest.pending[uri];
        resolve({status,backing:'Managed',...item,entryPath:'index.html',message:status==='Rejected'?'Перенос SDK пока не поддерживается.':'Проверьте папку /managed/'+id+' перед повторным созданием.'});
      };
    });
    const surface=id=>page.locator('.browser-object-view[data-browser-tab-id="import-'+id+'"]');
    async function action(id,name){
      await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
      const root=surface(id),button=root.getByRole('button',{name,exact:true,includeHidden:true});
      if(!await button.isVisible())await root.getByRole('button',{name:'Инструменты страницы',exact:true}).click();
      await button.click();
    }
    async function open(id){
      await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
      await page.evaluate(id=>projectTest.open(id),id);await surface(id).locator('.browser-document-source').waitFor({state:'visible'});
      assert.equal(await surface(id).locator('iframe').count(),0,'unqualified authored execution stays source-only');
      await surface(id).waitFor({state:'visible'});
      const source=surface(id).getByRole('button',{name:'Исходник',exact:true,includeHidden:true});
      if(await source.getAttribute('aria-pressed')!=='true')await action(id,'Исходник');
    }
    async function menu(id){await action(id,'Действия страницы');await page.getByRole('menuitem',{name:'Создать проект из снимка',exact:true}).waitFor();}
    async function begin(id){await menu(id);await page.getByRole('menuitem',{name:'Создать проект из снимка',exact:true}).click();await page.waitForFunction(id=>!!projectTest.pending[projectTest.targets[id].resourceUri],id);}
    const calls=()=>page.evaluate(()=>projectTest.calls.filter(c=>c.command==='createHtmlProjectFromArtifact'));
    await open('A');
    await page.evaluate(()=>{state.activeChatId='other-chat';state.chatNavigationVersion++;});
    await menu('A');
    if(process.env.BROWSER_SCREENSHOT)await page.screenshot({path:process.env.BROWSER_SCREENSHOT+'-wide.png'});
    await page.getByRole('menuitem',{name:'Создать проект из снимка',exact:true}).click();
    await page.waitForFunction(()=>!!projectTest.pending[projectTest.targets.A.resourceUri]);
    await menu('A');assert.equal(await page.getByRole('menuitem',{name:'Создать проект из снимка',exact:true}).isDisabled(),true,'pending cannot dispatch a second import');await page.keyboard.press('Escape');
    assert.deepEqual((await calls()).map(c=>c.payload),[{chatId:'preview-chat',documentId:'preview-document',resourceUri:'rna://chat/preview-document/artifact/project-import-A/revision/1',backing:'Managed'}]);
    await page.evaluate(()=>projectTest.finish('A','Created'));
    await page.waitForFunction(()=>RNAssistantProjectChats.workspaceId()==='ws_imported_A');
    await open('A');await action('A','Действия страницы');
    assert.equal(await page.getByRole('menuitem',{name:'Создать проект из снимка',exact:true}).count(),0,'created workspace is reused');
    await page.getByRole('menuitem',{name:'Открыть созданный проект',exact:true}).click();
    assert.equal((await calls()).length,1,'opening the project does not create another copy');

    await open('B');await begin('B');await open('A');
    const selected=await page.evaluate(()=>RNAssistantProjectChats.workspaceId());
    await page.evaluate(()=>projectTest.finish('B','Created'));
    await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(resolve)));
    assert.equal(await page.evaluate(()=>RNAssistantProjectChats.workspaceId()),selected,'late success does not take navigation away');
    assert.equal(await surface('A').isVisible(),true,'selected browser remains active');
    await open('B');await action('B','Действия страницы');await page.getByRole('menuitem',{name:'Открыть созданный проект',exact:true}).waitFor();await page.keyboard.press('Escape');

    await open('C');assert.equal(await surface('C').getByRole('button',{name:'Создать проект',exact:true}).isVisible(),true,'raw HTML has a persistent Create project action');await begin('C');await page.evaluate(()=>projectTest.finish('C','Rejected'));
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
    await page.evaluate(()=>RNAssistantDockWorkspace.close('browser:import-D'));
    await open('A');await page.evaluate(()=>projectTest.finish('D','Created'));
    assert.equal(await surface('D').count(),0,'late mutation reply never reopens a closed view');
    assert.equal((await calls()).length,5,'no automatic retry after rejection, unknown or close');
    assert.equal(await page.evaluate(()=>projectTest.calls.some(c=>/submit.*Chat|runModel|createProjectChat/.test(c.command))),false,'project import does not start a chat/run');
    assert.deepEqual(errors,[]);
    console.log('PASS html-project-import: exact source, pending/reuse, project navigation, stale/close, rejected/unknown, narrow/zoom');
  }finally{if(browser)await browser.close();server.kill();}
})().catch(error=>{console.error(error);process.exitCode=1;});
