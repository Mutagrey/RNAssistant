'use strict';
const assert=require('node:assert/strict');
const {withPreviewPage}=require('./preview-session');
(async()=>withPreviewPage(async({page,errors})=>{
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  await page.evaluate(()=>{
    const original=send, scope={kind:'Document',scopeId:'document',chatId:'document-chat'};
    window.docProbe={calls:[],head:1,dirty:true,fail:false,page:null,saves:0};const p=docProbe;
    function target(){return {kind:'HtmlWorkspace',state:'Ready',documentId:'document',chatId:'document-chat',logicalId:'html_ws_fixture',
      artifactId:'snapshot-'+p.head,ownerScope:scope,resourceUri:'rna://fixture/'+p.head,displayLocator:'/resources/html_ws_fixture',title:'Document HTML',
      capabilities:{source:true,versions:true,saveVersion:true}};}
    function state(){return {target:target(),stateRevision:'catalog',contentRevision:'rna://fixture/'+p.head,activeStateRevision:'rna://fixture/'+p.head,capabilities:target().capabilities};}
    RNAssistantHtmlWorkspaceSurface.flushForCheckpoint=async value=>{
      p.calls.push({type:'bufferSave',chatId:value.chatId,logicalId:value.logicalId});
      if(p.fail)throw new Error('Правки редактора не сохранены');
      if(!p.dirty)return false;p.head++;p.dirty=false;return true;
    };
    window.send=(type,payload,...rest)=>{
      if(!['browserCommand','readBrowserSource','getBrowserVersionState','getBrowserVersions','browserVersionCommand'].includes(type))return original(type,payload,...rest);
      p.calls.push({type,...structuredClone(payload)});
      if(type==='getBrowserVersionState')return Promise.resolve(state());
      if(type==='getBrowserVersions')return Promise.resolve({items:[],stateRevision:'catalog'});
      if(type==='browserVersionCommand'){p.saves++;return Promise.resolve({outcome:'Saved',version:{versionId:'saved',ordinal:1},state:state()});}
      if(type==='readBrowserSource')return Promise.resolve({requestGeneration:payload.requestGeneration,path:'index.html',files:['index.html'],revision:String(p.head),text:'<h1>Saved document source</h1>',offset:0,totalCharacters:30});
      if(payload.action==='Open')p.page={browserTabId:payload.browserTabId,sessionId:'doc-page',ownerScope:scope,url:payload.locator,title:'Document HTML',status:'Unavailable',revision:0,documentGeneration:0};
      if(payload.action==='Navigate')p.page.url=payload.locator;
      p.page.target=target();p.page.revision++;p.page.documentGeneration++;return Promise.resolve(structuredClone(p.page));
    };
    RNAssistantBrowser.open('/resources/html_ws_fixture',scope,{browserTabId:'document-checkpoints'});
  });
  const root=page.locator('section[aria-label="Browser"]:visible'),versions=root.getByRole('button',{name:'Версии проекта',exact:true});
  async function openVersions(){if(!await page.locator('.rna-action-menu-popup:visible').count())await versions.click();}
  async function saveVersion(){await openVersions();const item=page.getByRole('menuitem',{name:'Сохранить версию проекта',exact:true});await item.waitFor();await item.click();}
  await openVersions();assert.equal(await page.getByRole('menuitem',{name:'Сохранить версию проекта',exact:true}).isEnabled(),true);await page.keyboard.press('Escape');
  await saveVersion();await page.waitForFunction(()=>docProbe.saves===1);
  let probe=await page.evaluate(()=>docProbe);const index=probe.calls.findIndex(c=>c.type==='browserVersionCommand');
  assert.equal(probe.calls[index].expectedContentRevision,'rna://fixture/2');
  assert.deepEqual(probe.calls.find(c=>c.type==='bufferSave'),{type:'bufferSave',chatId:'document-chat',logicalId:'html_ws_fixture'});
  assert.ok(probe.calls.slice(0,index).some(c=>c.action==='Reload'),'buffer publication precedes checkpoint state refresh');
  await page.evaluate(()=>{docProbe.dirty=true;docProbe.fail=true;});
  await saveVersion();await root.locator('.material-surface-viewer-status').getByText('Правки редактора не сохранены',{exact:true}).waitFor();
  assert.equal(await page.evaluate(()=>docProbe.saves),1,'failed file Save never creates a checkpoint');
  await page.setViewportSize({width:420,height:800});await page.evaluate(()=>document.documentElement.style.zoom='1.25');
  await page.screenshot({path:'/tmp/rna-document-checkpoints-narrow.png'});
  assert.equal(await versions.isVisible(),true);assert.deepEqual(errors,[]);
  await page.evaluate(()=>RNAssistantDockWorkspace.close('browser:document-checkpoints'));
  console.log('PASS document checkpoint flushes its exact editor owner and refuses unsaved buffers');
}))().catch(error=>{console.error(error);process.exitCode=1;});
