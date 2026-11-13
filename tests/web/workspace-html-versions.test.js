'use strict';
const assert=require('node:assert/strict');
const {withPreviewPage}=require('./preview-session');
(async()=>withPreviewPage(async({page,errors})=>{
  await page.setViewportSize({width:1280,height:900});
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  await page.evaluate(()=>{
    const original=send, scope={kind:'Workspace',scopeId:'ws_fixture'};
    window.htmlProbe={calls:[],definition:null,version:null,page:null};const p=htmlProbe;
    function target(url){return {kind:'WorkspaceHtmlProject',state:'Ready',workspaceId:scope.scopeId,ownerScope:scope,
      displayLocator:url,entryPath:'index.html',relativePath:'index.html',title:'HTML project',immutable:false,
      capabilities:{source:true,versions:true,saveVersion:!!p.definition,saveReason:p.definition?'None':'ProjectDefinitionRequired'}};}
    function state(t){return {target:t,capabilities:t.capabilities,stateRevision:'catalog',contentRevision:'source',definitionRevision:p.definition&&'defined'};}
    window.send=(type,payload,...rest)=>{
      if(!['browserCommand','readBrowserSource','getBrowserVersionState','getBrowserVersions','browserVersionCommand','getHtmlProjectDefinition','setHtmlProjectDefinition'].includes(type))return original(type,payload,...rest);
      p.calls.push({type,...structuredClone(payload)});
      if(type==='getHtmlProjectDefinition')return Promise.resolve({definition:p.definition,fingerprint:p.definition&&'defined'});
      if(type==='setHtmlProjectDefinition'){p.definition=payload.definition;return Promise.resolve({definition:p.definition,fingerprint:'defined'});}
      if(type==='getBrowserVersionState')return Promise.resolve(state(target(payload.target.displayLocator)));
      if(type==='getBrowserVersions')return Promise.resolve({stateRevision:'catalog',items:p.version?[p.version]:[]});
      if(type==='browserVersionCommand'){p.version={versionId:'checkpoint',ordinal:1,createdUtc:'2026-11-13',canDelete:true};return Promise.resolve({outcome:'Saved',version:p.version,state:state(target(p.page.url))});}
      if(type==='readBrowserSource')return Promise.resolve({requestGeneration:payload.requestGeneration,path:'index.html',files:p.definition&&p.definition.members||['index.html'],revision:'exact',text:'<h1>HTML</h1>',offset:0,totalCharacters:13});
      if(payload.action==='Open')p.page={browserTabId:payload.browserTabId,sessionId:'html-session',ownerScope:scope,url:payload.locator,title:'HTML project',status:'Unavailable',revision:0,documentGeneration:0};
      if(payload.action==='Navigate')p.page.url=payload.locator;
      p.page.target=target(p.page.url);p.page.revision++;p.page.documentGeneration++;return Promise.resolve(structuredClone(p.page));
    };
    RNAssistantBrowser.open('/projects/ws_fixture/source/index.html',scope,{browserTabId:'html-definition'});
  });
  const root=page.locator('section[aria-label="Browser"]:visible'),save=root.getByRole('button',{name:'Сохранить версию проекта',exact:true});
  await save.waitFor();assert.equal(await save.isDisabled(),true);assert.match(await save.getAttribute('title'),/состав проекта/);
  assert.equal(await root.getByRole('button',{name:'Применить сохранённую версию',exact:true}).isVisible(),false);
  await root.getByRole('button',{name:'Версии проекта',exact:true}).click();await page.getByRole('menuitem',{name:'Состав проекта…',exact:true}).click();
  const form=page.getByRole('dialog',{name:'Состав HTML-проекта'});
  await form.getByLabel('Файлы проекта').fill('index.html\nassets/site.css');
  await page.setViewportSize({width:420,height:800});await page.evaluate(()=>document.documentElement.style.zoom='1.25');
  await page.screenshot({path:'/tmp/rna-workspace-definition-narrow.png'});
  const box=await form.boundingBox();assert.ok(box.x>=0&&box.x+box.width<=420,JSON.stringify(box));
  await form.getByRole('button',{name:'Сохранить состав',exact:true}).click();await form.waitFor({state:'detached'});
  await page.waitForFunction(()=>!document.querySelector('[aria-label="Сохранить версию проекта"]').disabled);
  await save.click();await page.waitForFunction(()=>htmlProbe.calls.some(c=>c.type==='browserVersionCommand'));
  const commands=await page.evaluate(()=>htmlProbe.calls.filter(c=>c.type==='setHtmlProjectDefinition'||c.type==='browserVersionCommand'));
  assert.equal(commands[0].expectedFingerprint,null);assert.deepEqual(commands[0].definition,{entryPath:'index.html',members:['index.html','assets/site.css']});
  assert.equal(commands[1].expectedDefinitionRevision,'defined');assert.equal(commands[1].action,'Save');
  await page.screenshot({path:'/tmp/rna-workspace-versions-narrow.png'});
  await page.evaluate(()=>RNAssistantDockWorkspace.close('browser:html-definition'));
  assert.deepEqual(errors,[]);console.log('PASS Workspace definition recovery, common checkpoint Save and narrow dialog');
}))().catch(error=>{console.error(error);process.exitCode=1;});
