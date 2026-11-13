'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { pathToFileURL } = require('node:url');
const { withPreviewPage } = require('./preview-session');
(async()=>withPreviewPage(async({page,errors})=>{
  const folder=await fs.mkdtemp(path.join(os.tmpdir(),'rna-browser-export-'));
  try {
    page.setDefaultTimeout(8000);
    await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
    const workspace={ActiveFileId:'index',Files:[{Id:'index',Path:'index.html',Kind:'html',Content:'<!doctype html><html><head><title>Автономный проект</title></head><body><label>Имя<input id="name"></label><button id="load">Показать</button><output id="out"></output></body></html>'},
      {Id:'js',Path:'app.js',Kind:'js',Content:"document.getElementById('load').onclick=async()=>{var source=await RN.resources.open('sales');var data=await source.read({limit:10});document.getElementById('out').textContent=document.getElementById('name').value+': '+data.rows[0].total;await source.close();};"}],DataSources:[{Name:'sales'}]};
    const bytes=Buffer.from(JSON.stringify(workspace));
    const payload={sha256:createHash('sha256').update(bytes).digest('hex'),byteLength:bytes.length,contentType:'application/json; charset=utf-8'};
    await page.evaluate(({workspace,payload})=>{
      window.showSaveFilePicker=undefined;
      const original=send, originalFetch=fetch;
      const id='1'.repeat(64), dataId='2'.repeat(64), base='https://rnassistant.local-resource/v1/';
      window.offlineExport={calls:[],pages:new Map(),late:false,pending:null};
      const probe=offlineExport;
      window.fetch=async function(url,...rest){
        if(String(url).startsWith(base+'download/'+id)){
          const query=new URL(url).searchParams, bytes=new TextEncoder().encode(JSON.stringify(workspace));
          return new Response(bytes.slice(Number(query.get('offset')),Number(query.get('offset'))+Number(query.get('count'))),{headers:{'Content-Type':payload.contentType}});
        }
        if(String(url).startsWith(base+dataId)) return new Response(JSON.stringify({resource:{uri:'rna://test/data',revision:'r1'},view:'table',offset:0,nextOffset:1,done:true,rows:[{total:42}],columns:[{key:'total',type:'number'}],coverage:{kind:'record-range',start:0,end:1,fields:['total']}}));
        return originalFetch(url,...rest);
      };
      window.send=(type,p,...rest)=>{
        probe.calls.push({type,p:structuredClone(p)});
        if(type==='readBrowserSource')return Promise.resolve({files:['index.html'],path:'index.html',text:workspace.Files[0].Content,offset:0,totalCharacters:workspace.Files[0].Content.length,requestGeneration:p.requestGeneration});
        if(type==='prepareBrowserHtmlExport'){
          const value={chatId:'source-chat',target:{...p.target,entryPath:'index.html'},source:{leaseId:id,url:base+'download/'+id,payload,maxChunkBytes:256*1024},
            resourceExport:{generations:{'document:source':3},bindings:[{name:'sales',lease:{leaseId:dataId,url:base+dataId,descriptor:{reference:{uri:'rna://test/data',revision:'r1'}},view:'table',maxBatchItems:100,maxBatchBytes:32000}}]}};
          return probe.late?new Promise(resolve=>{probe.pending=()=>resolve(value)}):Promise.resolve(value);
        }
        if(type==='resourceDataClose')return Promise.resolve({closed:true});
        if(type!=='browserCommand')return original(type,p,...rest);
        let value=probe.pages.get(p.browserTabId);
        if(p.action==='Open'){value={browserTabId:p.browserTabId,operationId:p.operationId,sessionId:'session-'+p.browserTabId,revision:1,documentGeneration:1,ownerScope:p.ownerScope,url:p.locator,title:'HTML export',status:'Unavailable',
          target:{kind:'HtmlArtifact',state:'Ready',ownerScope:p.ownerScope,chatId:'source-chat',documentId:'source',resourceUri:'rna://chat/source-chat/artifact/html_ws_saved/revision/2',displayLocator:p.locator,immutable:true,capabilities:{source:true,exportProject:true}}};probe.pages.set(p.browserTabId,value);}
        if(p.action==='Attach')value.attached=true;
        if(p.action==='Close')value.status='Closed';
        value.revision++;return Promise.resolve(structuredClone(value));
      };
      RNAssistantBrowser.open('/resources/html_ws_saved~2',{kind:'Document',scopeId:'source',chatId:'source-chat'},{browserTabId:'offline-html'});
    },{workspace,payload});
    const root=page.locator('.browser-object-view:visible');
    await root.getByRole('button',{name:'Действия страницы',exact:true}).click();
    const downloadPromise=page.waitForEvent('download');
    await page.getByRole('menuitem',{name:'Скачать автономный HTML',exact:true}).click();
    const download=await downloadPromise; const file=path.join(folder,'project.html');await download.saveAs(file);
    await page.waitForFunction(()=>offlineExport.calls.filter(c=>c.type==='resourceDataClose').length===2);
    assert.ok(await page.evaluate(()=>offlineExport.calls.filter(c=>c.type==='resourceDataClose').every(c=>c.p.chatId==='source-chat'&&c.p.workspaceId==='browser')));
    const offline=await page.context().browser().newPage(), requests=[];
    offline.on('request',request=>{if(/^https?:/.test(request.url()))requests.push(request.url());});
    await offline.goto(pathToFileURL(file).href);await offline.getByLabel('Имя').fill('Файл');await offline.getByRole('button',{name:'Показать'}).click();
    await offline.waitForFunction(()=>document.getElementById('out').textContent==='Файл: 42');assert.deepEqual(requests,[]);await offline.close();
    await page.evaluate(()=>{offlineExport.late=true;});
    await root.getByRole('button',{name:'Действия страницы',exact:true}).click();await page.getByRole('menuitem',{name:'Скачать автономный HTML',exact:true}).click();
    await page.waitForFunction(()=>!!offlineExport.pending);
    await page.evaluate(()=>{RNAssistantDockWorkspace.close('browser:offline-html');offlineExport.pending();});
    await page.waitForFunction(()=>offlineExport.calls.filter(c=>c.type==='resourceDataClose').length===4);
    assert.deepEqual(errors,[]);
    console.log('PASS Browser standalone export: exact common action, real file:// JS/bindings, no HTTP, source/data lease cleanup and late close');
  } finally {await fs.rm(folder,{recursive:true,force:true});}
}))().catch(error=>{console.error(error);process.exitCode=1;});
