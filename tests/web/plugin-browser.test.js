'use strict';
// Rail and failed source retain the same common Browser owner. No authored Shell iframe.
const assert=require('node:assert/strict');
const {withPreviewPage}=require('./preview-session');
(async()=>{await withPreviewPage(async({page,errors})=>{
  page.setDefaultTimeout(7000);
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  await page.evaluate(async()=>{
    const original=send;
    window.pluginBrowser={calls:[],pages:new Map(),bad:true,sequence:0};const p=pluginBrowser;
    const plugins=['alpha','beta'].map(pluginId=>({pluginId,name:pluginId,sourceWorkspaceId:'ws-'+pluginId,entryPath:'index.html'}));
    function visual(value,type,detail={}) { value.visual={type:'rnassistant-visual-inspector-'+type,generation:value.documentGeneration,sequence:++p.sequence,...detail}; }
    p.selectElement=()=>{
      const value=[...p.pages.values()].find(v=>v.target.pluginId==='alpha');
      visual(value,'selected',{selection:{tag:'button',selector:'#run',attributes:{id:'run'},ancestry:[],textExcerpt:'Run',rect:{x:25,y:25,width:100,height:30},viewport:{width:500,height:300,dpr:1}}});
      value.revision++;RNAssistantBrowser.apply(structuredClone(value));
    };
    window.send=(type,payload,...rest)=>{
      if(type==='getPlugins')return Promise.resolve({items:plugins});
      if(type==='getProjectChats')return Promise.resolve({projects:plugins.map(v=>({workspaceId:v.sourceWorkspaceId,name:v.name,available:true,chats:[{Id:v.pluginId+'-chat',Title:'Чат '+v.name}]}))});
      if(!['browserCommand','readBrowserSource','getBrowserVersionState','getBrowserVersions','stageBrowserVisualSelection'].includes(type))return original(type,payload,...rest);
      p.calls.push({type,...structuredClone(payload)});
      if(type==='stageBrowserVisualSelection')return Promise.resolve({resource:{id:'plugin-annotation',kind:'text',fileName:'selection.json',size:500}});
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
          revision:1,documentGeneration:1,url:payload.locator,status:'Unavailable',reason:'NativeUnavailable',title:pluginId,
          target:{kind:'PluginSource',state:'Ready',pluginId,workspaceId:scope.scopeId,ownerScope:scope,displayLocator:payload.locator,
            entryPath:'plugin.json',capabilities:{source:true,versions:true,saveVersion:false,saveReason:'InvalidContent'}}};p.pages.set(payload.browserTabId,value);
      }else if(payload.action==='Close'){value.status='Closed';}
      else if(payload.action==='Reload'){
        value.documentGeneration++;value.target.capabilities.saveVersion=!p.bad;
        if(!p.bad){value.status='Ready';value.target.capabilities.preview=true;value.target.entryPath='index.html';}
      }
      else if(payload.action==='Bind'){
        value.chatBound=true;value.ownerScope=payload.binding;value.target.ownerScope=payload.binding;
        value.target.capabilities.annotations=true;value.annotationContext={sourceId:'alpha-exact-release',sourceRevision:1};value.documentGeneration++;
      }
      else if(payload.action==='VisualControl'){
        if(payload.visual.generation!==value.documentGeneration)throw new Error('stale generation');
        if(payload.visual.operation==='probe')visual(value,'ready');
        if(payload.visual.operation==='activate')visual(value,'activated');
      }
      else if(payload.action==='Attach')value.attached=true;
      value.revision++;return Promise.resolve(structuredClone(value));
    };
    await RNAssistantPlugins.refresh();
    RNAssistantConversationPanel.selected().node('chatInput').value='draft retained';
    await RNAssistantPlugins.select('alpha');
  });
  const visible=()=>page.locator('section[aria-label="Browser"]:visible');
  async function saveEnabled(){
    const versions=visible().getByRole('button',{name:'Версии проекта',exact:true});
    if(!await page.locator('.rna-action-menu-popup:visible').count())await versions.click();
    const item=page.getByRole('menuitem',{name:'Сохранить версию проекта',exact:true});
    await item.waitFor();const enabled=await item.isEnabled();await page.keyboard.press('Escape');return enabled;
  }
  await visible().getByText('{ invalid manifest',{exact:false}).waitFor();
  assert.equal(await page.evaluate(()=>pluginBrowser.calls.filter(c=>c.type==='getBrowserVersionState').length),0,'opening a plugin does not recheck its package for the version menu');
  assert.equal(await visible().locator('iframe').count(),0);
  assert.equal(await saveEnabled(),false);
  assert.equal(await page.evaluate(()=>pluginBrowser.calls.filter(c=>c.type==='getBrowserVersionState').length),1,'the version menu reads fresh state on demand');
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
  assert.equal(await page.evaluate(()=>pluginBrowser.calls.filter(c=>c.type==='getBrowserVersionState').length),1,'switching plugins, returning to a retained tab and reloading need no version capture');
  assert.equal(await saveEnabled(),true);
  assert.equal(await page.evaluate(()=>pluginBrowser.calls.filter(c=>c.action==='Bind').length),0,'opening the rail never binds a different chat');
  await visible().getByRole('button',{name:'Аннотировать: выбрать чат',exact:true}).click();
  await page.getByRole('menu',{name:'Выбрать чат для аннотации'}).getByRole('menuitem',{name:'Чат alpha',exact:true}).click();
  await visible().getByRole('button',{name:'Выключить выбор элемента',exact:true}).waitFor();
  assert.equal(await visible().locator('.browser-native-viewport').isVisible(),true,'selection starts in Preview after explicit binding from Source');
  assert.deepEqual(await page.evaluate(()=>pluginBrowser.calls.find(c=>c.action==='Bind').binding),{kind:'Workspace',scopeId:'ws-alpha',chatId:'alpha-chat'});
  await page.evaluate(()=>pluginBrowser.selectElement());
  const annotation=page.getByRole('dialog',{name:'Аннотация 1',exact:true});
  await annotation.getByRole('textbox').fill('Изменить кнопку');
  await annotation.getByRole('button',{name:'Готово',exact:true}).click();
  await annotation.waitFor({state:'detached'});
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readWorkspace({workspaceId:'ws-alpha',sessionId:'alpha-chat'}).attachments[0].visualAnnotation.comment),'Изменить кнопку');
  assert.equal(await page.evaluate(()=>pluginBrowser.calls.find(c=>c.type==='stageBrowserVisualSelection').sourceId),'alpha-exact-release');
  await visible().getByRole('button',{name:'Исходник',exact:true}).click();
  await visible().getByRole('button',{name:'Аннотировать: выбрать элемент',exact:true}).click();
  await visible().getByRole('button',{name:'Выключить выбор элемента',exact:true}).waitFor();
  assert.equal(await page.evaluate(()=>pluginBrowser.calls.filter(c=>c.action==='Bind').length),1,'existing binding enters selection directly from Source');
  await page.setViewportSize({width:420,height:760});
  assert.ok(await visible().getByRole('button',{name:'Версии проекта',exact:true}).isVisible());
  await page.evaluate(id=>RNAssistantDockWorkspace.close('browser:'+id),first);
  await page.waitForFunction(id=>pluginBrowser.calls.some(c=>c.action==='Close'&&c.browserTabId===id),first);
  assert.equal(await page.evaluate(id=>pluginBrowser.calls.filter(c=>c.action==='Close'&&c.browserTabId===id).length,second),0,'closing one tab preserves sibling');
  assert.deepEqual(errors,[]);console.log('PASS plugin Browser: rail reuse, independent owners, invalid source/recovery, source-only view and narrow Save');
});})().catch(error=>{console.error(error);process.exitCode=1;});
