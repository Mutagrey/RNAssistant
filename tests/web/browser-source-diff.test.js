'use strict';
const assert=require('node:assert/strict');
const {withPreviewPage}=require('./preview-session');
(async()=>withPreviewPage(async({page,errors})=>{
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  await page.evaluate(()=>{
    window.diffTest={calls:[],pages:new Map(),pending:null};const old=send;
    send=(type,p,...rest)=>{
      if(!['browserCommand','readBrowserSource','getBrowserVersionState','getBrowserVersions','compareBrowserSources'].includes(type))return old(type,p,...rest);
      diffTest.calls.push({type,payload:structuredClone(p)});
      if(type==='getBrowserVersionState')return Promise.resolve({target:p.target,capabilities:p.target.capabilities,stateRevision:'catalog',contentRevision:'revision'});
      if(type==='getBrowserVersions')return Promise.resolve({stateRevision:'catalog',items:[{versionId:'v2',ordinal:2,createdUtc:'2026-11-02'},{versionId:'v1',ordinal:1,createdUtc:'2026-11-01'}]});
      if(type==='compareBrowserSources') {
        const result={beforeId:p.beforeId,afterId:p.afterId,beforeLabel:'v1',afterLabel:'v2',path:p.path,files:[{path:'index.html',change:'Changed'},{path:'image.png',change:'Unchanged'}],
          complete:p.path!=='image.png',message:p.path==='image.png'?'Этот файл не является текстом UTF-8.':null,beforeText:'<h1>Before</h1>',afterText:'<h1>After</h1>'};
        if(diffTest.defer)return new Promise(resolve=>{diffTest.pending=()=>resolve(result);});
        return Promise.resolve(result);
      }
      if(type==='readBrowserSource')return Promise.resolve({files:['index.html'],path:'index.html',text:'<h1>After</h1>',offset:0,totalCharacters:14,revision:'sha',requestGeneration:p.requestGeneration});
      let value=diffTest.pages.get(p.browserTabId);
      if(p.action==='Open') {const plugin=p.ownerScope.scopeId==='plugin';const target={kind:plugin?'PluginRelease':'WorkspaceHtmlProject',workspaceId:p.ownerScope.scopeId,
        pluginId:plugin?'plugin':null,ownerScope:p.ownerScope,state:'Ready',savedVersionId:'v2',immutable:true,displayLocator:p.locator,entryPath:'index.html',capabilities:{source:true,versions:true}};
        value={browserTabId:p.browserTabId,sessionId:'session-'+p.browserTabId,revision:1,documentGeneration:1,ownerScope:p.ownerScope,url:p.locator,title:'Diff fixture',status:'Unavailable',target};diffTest.pages.set(p.browserTabId,value);
      }else if(p.action==='Close')value.status='Closed';value.revision++;return Promise.resolve(structuredClone(value));
    };
    RNAssistantBrowser.open('/projects/project/versions/v2/index.html',{kind:'Workspace',scopeId:'project'},{browserTabId:'diff-project'});
  });
  async function compare(){
    const root=page.locator('.browser-object-view:visible');await root.getByRole('button',{name:'Сравнение',exact:true}).click();
    await root.getByRole('button',{name:'Сравнить с версией',exact:true}).click();await page.getByRole('menuitem',{name:'v1',exact:true}).click();
    await root.locator('.browser-source-diff-output').getByText('After',{exact:false}).waitFor();return root;
  }
  let root=await compare();
  assert.ok(await page.evaluate(()=>diffTest.calls.filter(c=>c.type==='compareBrowserSources').every(c=>c.payload.target.workspaceId==='project'&&c.payload.beforeId==='v1'&&c.payload.afterId==='v2')));
  await root.getByRole('combobox',{name:'Файл для сравнения'}).selectOption('image.png');await root.getByText('Этот файл не является текстом UTF-8.',{exact:true}).waitFor();
  assert.equal(await root.locator('.browser-source-diff-output').textContent(),'','binary refusal does not retain stale diff');
  await page.evaluate(()=>{diffTest.defer=true;});await root.getByRole('combobox',{name:'Файл для сравнения'}).selectOption('index.html');
  await page.waitForFunction(()=>!!diffTest.pending);await page.evaluate(()=>RNAssistantDockWorkspace.close('browser:diff-project'));
  await page.evaluate(()=>{diffTest.pending();diffTest.defer=false;RNAssistantBrowser.open('/plugins/plugin/versions/v2/index.html',{kind:'Workspace',scopeId:'plugin'},{browserTabId:'diff-plugin'});});
  root=await compare();assert.ok(await page.evaluate(()=>diffTest.calls.some(c=>c.type==='compareBrowserSources'&&c.payload.target.pluginId==='plugin')));
  await page.setViewportSize({width:420,height:800});await page.evaluate(()=>document.documentElement.style.zoom='1.25');await page.waitForTimeout(150);
  assert.ok(await root.evaluate(n=>n.scrollWidth<=n.clientWidth+1));
  assert.deepEqual(errors,[]);console.log('PASS common checkpoint comparison: exact provider/pair, binary refusal, late close and narrow layout');
}))().catch(error=>{console.error(error);process.exitCode=1;});
