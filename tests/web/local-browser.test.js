'use strict';
const assert = require('node:assert/strict');
const {withPreviewPage} = require('./preview-session');
(async () => {
  await withPreviewPage(async ({page, errors}) => {
    await page.setViewportSize({width:1280,height:900});
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(() => {
      const original = send;
      window.localProbe = {calls:[], pages:new Map(), pending:[], targets:0};
      window.send = (type, payload, ...rest) => {
        if (!['browserCommand','readBrowserSource','queryNavigationSuggestions'].includes(type)) return original(type,payload,...rest);
        const p = localProbe;p.calls.push({type,...structuredClone(payload)});
        if (type === 'queryNavigationSuggestions') return new Promise(resolve => p.pending.push(() => resolve({requestGeneration:payload.requestGeneration,
          items:[{title:payload.prefix,displayLocator:'/projects/project/source/'+payload.prefix+'.html'}]})));
        if (type === 'readBrowserSource') return Promise.resolve({target:p.pages.values().next().value.target,files:['index.html','second.html'],path:payload.path||'index.html',
          text:'<h1>Exact source '+(payload.path||'index.html')+'</h1>',offset:0,totalCharacters:40,revision:'sha256',requestGeneration:payload.requestGeneration});
        let value = p.pages.get(payload.browserTabId);
        if (payload.action === 'Open') {
          value = {browserTabId:payload.browserTabId,operationId:payload.operationId,sessionId:'session-'+(++p.targets),ownerScope:payload.ownerScope,
            revision:1,documentGeneration:1,attached:false,status:'Unavailable',reason:'IsolationUnqualified',url:payload.locator,title:'Local source',message:RNAssistantBrowser.unavailable,
            target:{ownerScope:payload.ownerScope,workspaceId:'project',capabilities:{source:true}}};p.pages.set(payload.browserTabId,value);
        } else if (payload.action === 'Close') {value.status='Closed';p.pages.delete(payload.browserTabId);}
        else if (payload.sessionId !== value.sessionId) throw new Error('Stale session');
        else if (payload.action === 'Attach') value.attached=true;
        else if (payload.action === 'Navigate') { if (!payload.locator.startsWith('/')) return Promise.reject(new Error('Укажите локальный адрес Browser.'));
          value.url=payload.locator;value.documentGeneration++;value.canGoBack=true; }
        else if (payload.action === 'Reload') value.documentGeneration++;
        else if (payload.action === 'Home') {value.url='/';value.status='Home';value.documentGeneration++;}
        value.revision++;return Promise.resolve(structuredClone(value));
      };
      RNAssistantBrowser.open('/projects/project/source/index.html',{kind:'Workspace',scopeId:'project',chatId:'source-chat'}, {browserTabId:'tab-one'});
    });
    const root=page.locator('section[aria-label="Browser"]:visible');
    await root.locator('.browser-document-source').getByText('<h1>Exact source index.html</h1>',{exact:false}).waitFor();
    assert.equal(await page.evaluate(() => localProbe.calls.filter(c=>c.action==='Open').length),1);
    assert.equal(await page.evaluate(() => localProbe.calls.find(c=>c.action==='Open').ownerScope.chatId),'source-chat');
    assert.ok(await page.evaluate(() => localProbe.calls.some(c=>c.action==='Attach')));
    assert.equal(await root.locator('iframe').count(),0,'unqualified content never executes in a Shell iframe');
    const address=root.getByRole('combobox',{name:'Адрес материала'});
    await address.fill('old');await address.fill('new');
    await page.evaluate(() => {localProbe.pending[1]();localProbe.pending[0]();});
    await root.locator('.preview-surface-completion').getByRole('option').filter({hasText:'new'}).waitFor();
    assert.equal(await root.locator('.preview-surface-completion').getByRole('option').count(),1,'stale completion cannot replace latest query');
    await address.press('ArrowDown');await address.press('Enter');
    await page.waitForFunction(() => localProbe.calls.some(c=>c.action==='Navigate'&&c.locator.endsWith('/new.html')));
    await address.fill('https://example.com');await address.press('Enter');
    await root.getByRole('alert').filter({hasText:'локальный'}).waitFor();
    assert.equal(await address.inputValue(),'/projects/project/source/new.html','refusal restores committed address');
    await page.evaluate(() => RNAssistantDockWorkspace.show('settings'));
    await page.evaluate(() => RNAssistantDockWorkspace.openTarget({view:'browser',browserTabId:'tab-one',ownerScope:{kind:'Workspace',scopeId:'project',chatId:'source-chat'},locator:'/projects/project/source/index.html'}));
    assert.equal(await page.evaluate(() => localProbe.calls.filter(c=>c.action==='Open').length),1,'hide/show preserves session');
    await root.getByRole('button',{name:'Обновить предпросмотр',exact:true}).click();
    await root.locator('.browser-document-source').getByText('Exact source index.html',{exact:false}).waitFor();
    await page.screenshot({path:'/tmp/rna-local-browser-wide.png'});
    await page.setViewportSize({width:420,height:800});await page.evaluate(()=>{document.documentElement.style.zoom='1.25';});
    await page.waitForTimeout(200);
    const sizes=await root.evaluate(el=>({width:el.clientWidth,scroll:el.scrollWidth,right:el.getBoundingClientRect().right,viewport:innerWidth}));
    assert.ok(sizes.scroll<=sizes.width+1 && sizes.right<=sizes.viewport+1,JSON.stringify(sizes));
    await page.screenshot({path:'/tmp/rna-local-browser-narrow.png'});
    await page.evaluate(()=>RNAssistantDockWorkspace.close('browser:tab-one'));
    await page.waitForFunction(()=>localProbe.calls.some(c=>c.action==='Close'));
    assert.deepEqual(errors,[]);
    console.log(JSON.stringify({status:'pass',sizes}));
  });
})().catch(error=>{console.error(error);process.exitCode=1;});
