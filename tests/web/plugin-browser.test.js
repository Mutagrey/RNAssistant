'use strict';
// Rail and failed source retain the same common Browser owner. No authored Shell iframe.
const assert=require('node:assert/strict');
const {withPreviewPage}=require('./preview-session');
(async()=>{await withPreviewPage(async({page,errors})=>{
  page.setDefaultTimeout(7000);
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  await page.evaluate(async()=>{
    const original=send;
    window.pluginBrowser={calls:[],pages:new Map(),bad:true};const p=pluginBrowser;
    const plugins=['alpha','beta'].map(pluginId=>({pluginId,name:pluginId,sourceWorkspaceId:'ws-'+pluginId,entryPath:'index.html'}));
    window.send=(type,payload,...rest)=>{
      if(type==='getPlugins')return Promise.resolve({items:plugins});
      if(!['browserCommand','readBrowserSource','getBrowserVersionState','getBrowserVersions'].includes(type))return original(type,payload,...rest);
      p.calls.push({type,...structuredClone(payload)});
      if(type==='getBrowserVersions')return Promise.resolve({items:[],stateRevision:'1'});
      if(type==='getBrowserVersionState')return Promise.resolve({target:payload.target,stateRevision:'1',contentRevision:p.bad?null:'repaired',capabilities:{saveVersion:!p.bad,saveReason:p.bad?'InvalidContent':'None'}});
      if(type==='readBrowserSource'){
        const target=Array.from(p.pages.values()).find(v=>v.url===payload.locator).target;
        return Promise.resolve({target,files:['plugin.json'],path:'plugin.json',text:p.bad?'{ invalid manifest':'{"name":"repaired"}',offset:0,totalCharacters:25,requestGeneration:payload.requestGeneration});
      }
      let value=p.pages.get(payload.browserTabId);
      if(payload.action==='Open'){
        const pluginId=payload.locator.split('/')[2],scope={kind:'Workspace',scopeId:'ws-'+pluginId};
        value={browserTabId:payload.browserTabId,sessionId:'session-'+payload.browserTabId,operationId:payload.operationId,ownerScope:payload.ownerScope,
          revision:1,documentGeneration:1,url:payload.locator,status:'Unavailable',reason:'IsolationUnqualified',title:pluginId,
          target:{kind:'PluginSource',state:'Ready',pluginId,workspaceId:scope.scopeId,ownerScope:scope,displayLocator:payload.locator,
            entryPath:'plugin.json',capabilities:{source:true,versions:true,saveVersion:false,saveReason:'InvalidContent'}}};p.pages.set(payload.browserTabId,value);
      }else if(payload.action==='Close'){value.status='Closed';}
      else if(payload.action==='Reload'){value.documentGeneration++;value.target.capabilities.saveVersion=!p.bad;}
      else if(payload.action==='Attach')value.attached=true;
      value.revision++;return Promise.resolve(structuredClone(value));
    };
    await RNAssistantPlugins.refresh();
    RNAssistantConversationPanel.selected().node('chatInput').value='draft retained';
    await RNAssistantPlugins.select('alpha');
  });
  const visible=()=>page.locator('section[aria-label="Browser"]:visible');
  await visible().getByText('{ invalid manifest',{exact:false}).waitFor();
  assert.equal(await visible().locator('iframe').count(),0);
  assert.equal(await visible().getByRole('button',{name:'Сохранить версию проекта',exact:true}).isEnabled(),false);
  const first=await visible().getAttribute('data-browser-tab-id');
  await page.evaluate(()=>RNAssistantPlugins.select('beta'));
  await visible().getByText('{ invalid manifest',{exact:false}).waitFor();
  const second=await visible().getAttribute('data-browser-tab-id');assert.notEqual(first,second);
  await page.evaluate(()=>RNAssistantPlugins.select('alpha'));
  await page.waitForFunction(id=>document.querySelector('section[aria-label="Browser"]:not([hidden])[data-browser-tab-id="'+id+'"]')!==null,first);
  assert.equal(await page.evaluate(()=>pluginBrowser.calls.filter(c=>c.action==='Open').length),2,'rail reuses exact existing Browser');
  await page.evaluate(()=>{pluginBrowser.bad=false;});
  await visible().getByRole('button',{name:'Обновить предпросмотр',exact:true}).click();
  await visible().getByText('{"name":"repaired"}',{exact:false}).waitFor();
  assert.equal(await visible().getByRole('button',{name:'Сохранить версию проекта',exact:true}).isEnabled(),true);
  await page.setViewportSize({width:420,height:760});
  assert.ok(await visible().getByRole('button',{name:'Сохранить версию проекта',exact:true}).isVisible());
  await page.evaluate(id=>RNAssistantDockWorkspace.close('browser:'+id),first);
  await page.waitForFunction(id=>pluginBrowser.calls.some(c=>c.action==='Close'&&c.browserTabId===id),first);
  assert.equal(await page.evaluate(id=>pluginBrowser.calls.filter(c=>c.action==='Close'&&c.browserTabId===id).length,second),0,'closing one tab preserves sibling');
  assert.deepEqual(errors,[]);console.log('PASS plugin Browser: rail reuse, independent owners, invalid source/recovery, source-only view and narrow Save');
});})().catch(error=>{console.error(error);process.exitCode=1;});
