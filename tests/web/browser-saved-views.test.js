'use strict';
const assert = require('node:assert/strict');
const {withPreviewPage} = require('./preview-session');
(async () => {
  await withPreviewPage(async ({page, errors}) => {
    await page.setViewportSize({width:1280,height:900});
    page.on('dialog', dialog => dialog.accept());
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(() => {
      const original = send, scope = {kind:'Workspace',scopeId:'project'};
      window.versionProbe = {calls:[], pages:new Map(), active:'v3', revision:1, ordinal:3, lost:false, receipts:new Map(),
        entries:[1,2,3].map(n=>({versionId:'v'+n,ordinal:n,label:'',createdUtc:'2026-11-13T10:00:00Z',releaseId:n===2?'different':'same-bytes',removed:false}))};
      const p=versionProbe;
      function target(locator) {
        const match=/\/versions\/([^/]+)\//.exec(locator), version=match&&match[1], entry=p.entries.find(e=>e.versionId===version);
        return {kind:version?'PluginRelease':'PluginSource',state:entry&&entry.removed?'Removed':'Ready',pluginId:'plugin',workspaceId:'project',
          ownerScope:scope,displayLocator:locator,entryPath:'index.html',relativePath:'index.html',savedVersionId:version,title:'Fixture plugin',
          capabilities:{source:true,versions:true,saveVersion:!version,activateVersion:!!version,saveReason:version?'ReadOnly':'None'}};
      }
      function entry(e){return {...e,active:e.versionId===p.active,canDelete:e.versionId!==p.active&&!e.removed,deleteBlockedReason:e.versionId===p.active?'ActiveVersion':'None'};}
      function state(value){return {target:target(value.displayLocator),stateRevision:String(p.revision),activeStateRevision:'active',contentRevision:'working',
        activeSavedVersion:entry(p.entries.find(e=>e.versionId===p.active)),capabilities:{...value.capabilities,saveVersion:value.capabilities.saveVersion&&!p.invalid}};}
      window.send=(type, payload,...rest)=>{
        if(!['browserCommand','readBrowserSource','getBrowserVersionState','getBrowserVersions','browserVersionCommand'].includes(type))return original(type,payload,...rest);
        p.calls.push({type,...structuredClone(payload)});
        if(type==='getBrowserVersionState')return Promise.resolve(state(payload.target));
        if(type==='getBrowserVersions')return Promise.resolve({items:p.entries.filter(e=>!e.removed).slice().reverse().map(entry),stateRevision:String(p.revision)});
        if(type==='browserVersionCommand'){
          if(p.receipts.has(payload.operationId))return Promise.resolve(p.receipts.get(payload.operationId));
          let saved=p.entries.find(e=>e.versionId===payload.versionId),outcome;
          if(payload.action==='Save'){saved={versionId:'v'+(++p.ordinal),ordinal:p.ordinal,label:'',createdUtc:'2026-11-13T10:30:00Z',releaseId:'same-bytes'};p.entries.push(saved);outcome='Saved';}
          else if(payload.action==='Activate'){p.active=saved.versionId;outcome='Activated';}
          else {if(saved.versionId===p.active)return Promise.resolve({outcome:'Rejected',reason:'ActiveVersion'});saved.removed=true;outcome='Removed';}
          p.revision++;const result={outcome,version:entry(saved),state:state(payload.target)};p.receipts.set(payload.operationId,result);
          if(p.lost){p.lost=false;return Promise.resolve({outcome:'Unknown'});}return Promise.resolve(result);
        }
        if(type==='readBrowserSource')return Promise.resolve({requestGeneration:payload.requestGeneration,path:'index.html',files:['index.html'],revision:'exact',
          text:'Source of '+payload.locator,offset:0,totalCharacters:100});
        let value=p.pages.get(payload.browserTabId);
        if(payload.action==='Open'){value={browserTabId:payload.browserTabId,sessionId:'session-'+payload.browserTabId,ownerScope:scope,url:payload.locator,
          target:target(payload.locator),title:'Fixture plugin',status:'Unavailable',revision:0,documentGeneration:1,message:RNAssistantBrowser.unavailable};p.pages.set(payload.browserTabId,value);}
        else if(payload.action==='Navigate'){value.url=payload.locator;value.target=target(value.url);value.documentGeneration++;}
        else if(payload.action==='Reload'){value.target=target(value.url);value.documentGeneration++;}
        else if(payload.action==='Close')value.status='Closed';
        value.status=payload.action==='Close'?'Closed':value.target.state==='Removed'?'Removed':'Unavailable';value.revision++;return Promise.resolve(structuredClone(value));
      };
      RNAssistantBrowser.open('/plugins/plugin/source/index.html',scope,{browserTabId:'versions'});
    });
    const root=page.locator('section[aria-label="Browser"]:visible'),save=root.getByRole('button',{name:'Сохранить версию проекта',exact:true});
    await save.waitFor();await page.waitForFunction(()=>!document.querySelector('[aria-label="Сохранить версию проекта"]').disabled);
    await root.getByRole('button',{name:'Версии проекта',exact:true}).click();
    await page.getByRole('menuitem',{name:'Удалить версию 3',exact:true}).waitFor();
    assert.equal(await page.getByRole('menuitem',{name:'Удалить версию 3',exact:true}).isDisabled(),true);
    await page.getByRole('menuitemcheckbox',{name:'v1',exact:true}).click();
    await root.locator('.browser-document-source').getByText('Source of /plugins/plugin/versions/v1/index.html').waitFor();
    assert.equal(await save.isDisabled(),true,'saved selection cannot save a fake working copy');
    const navigationBefore=await page.evaluate(()=>versionProbe.calls.filter(c=>c.action==='Navigate').length);
    await root.getByRole('button',{name:'Версии проекта',exact:true}).click();
    await page.getByRole('menuitem',{name:'Удалить версию 1',exact:true}).click();
    await page.waitForFunction(()=>versionProbe.pages.get('versions').status==='Removed');
    assert.equal(await page.evaluate(()=>versionProbe.calls.filter(c=>c.action==='Navigate').length),navigationBefore,'trash does not open the row');
    assert.equal(await page.evaluate(()=>versionProbe.entries.find(e=>e.versionId==='v3').removed),false,'same payload does not merge saved-entry identity');
    assert.equal(await root.locator('iframe').count(),0);
    await root.getByRole('button',{name:'Версии проекта',exact:true}).click();
    await page.getByRole('menuitemcheckbox',{name:'Рабочая копия',exact:true}).click();
    await root.locator('.browser-document-source').getByText('Source of /plugins/plugin/source/index.html').waitFor();
    await page.waitForFunction(()=>!document.querySelector('[aria-label="Сохранить версию проекта"]').disabled);
    await page.evaluate(()=>{versionProbe.lost=true;});await save.click();
    await root.getByText('Результат неизвестен.',{exact:false}).waitFor();
    assert.equal(await page.evaluate(()=>versionProbe.calls.filter(c=>c.action==='Save').length),1,'lost response is not automatically retried');
    await root.getByRole('button',{name:'Версии проекта',exact:true}).click();
    await page.getByRole('menuitem',{name:'Проверить исходную операцию',exact:true}).click();
    await page.waitForFunction(()=>versionProbe.calls.filter(c=>c.action==='Save').length===2);
    const operations=await page.evaluate(()=>versionProbe.calls.filter(c=>c.action==='Save').map(c=>c.operationId));
    assert.equal(operations[0],operations[1]);assert.equal(await page.evaluate(()=>versionProbe.ordinal),4);
    assert.equal(await page.evaluate(()=>versionProbe.active),'v3','saving never activates');
    await page.evaluate(()=>{versionProbe.invalid=true;});
    await root.getByRole('button',{name:'Обновить предпросмотр',exact:true}).click();
    await page.waitForFunction(()=>document.querySelector('[aria-label="Сохранить версию проекта"]').disabled);
    assert.equal(await save.isVisible(),true,'failed structural check keeps the Save control visible');
    assert.equal(await root.getByRole('button',{name:'Версии проекта',exact:true}).isDisabled(),false,'broken working source retains saved metadata');
    await page.evaluate(()=>{versionProbe.invalid=false;});
    await root.getByRole('button',{name:'Обновить предпросмотр',exact:true}).click();
    await page.waitForFunction(()=>!document.querySelector('[aria-label="Сохранить версию проекта"]').disabled);
    await page.screenshot({path:'/tmp/rna-browser-versions-wide.png'});
    await page.setViewportSize({width:420,height:800});await page.evaluate(()=>{document.documentElement.style.zoom='1.25';});
    await page.waitForTimeout(150);assert.equal(await save.isVisible(),true,'Save remains visible at narrow/125%');
    const bounds=await save.boundingBox();assert.ok(bounds.x>=0&&bounds.x+bounds.width<=420,JSON.stringify(bounds));
    await page.screenshot({path:'/tmp/rna-browser-versions-narrow.png'});
    await root.getByRole('button',{name:'Инструменты страницы',exact:true}).click();
    await root.getByRole('button',{name:'Версии проекта',exact:true}).click();
    await page.getByRole('menuitem',{name:'Удалить версию 3',exact:true}).waitFor();
    const menuBounds=await page.locator('.rna-action-menu-popup:visible').boundingBox();
    assert.ok(menuBounds.x>=0&&menuBounds.x+menuBounds.width<=420,JSON.stringify(menuBounds));
    await page.keyboard.press('Escape');
    await page.evaluate(()=>RNAssistantDockWorkspace.close('browser:versions'));
    assert.deepEqual(errors,[]);console.log(JSON.stringify({status:'pass',operations:operations.length,bounds}));
  });
})().catch(error=>{console.error(error);process.exitCode=1;});
