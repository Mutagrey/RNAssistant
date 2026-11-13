'use strict';
const assert=require('node:assert/strict');
const {withPreviewPage}=require('./preview-session');
(async()=>withPreviewPage(async({page,errors})=>{
  await page.setViewportSize({width:1280,height:900});
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  await page.evaluate(()=>{window.startProbe={calls:[],pages:new Map(),deferred:[]}; const old=send;
    send=(type,p,...rest)=>{
      if(!['browserCommand','queryNavigationSuggestions','readBrowserSource'].includes(type)) return old(type,p,...rest);
      const probe=startProbe;probe.calls.push({type,...structuredClone(p)});
      if(type==='queryNavigationSuggestions') {
        const result={requestGeneration:p.requestGeneration,items:[{title:p.prefix||'Страница проекта',displayLocator:'/projects/demo/source/index.html',kind:'WorkspaceHtmlProject',sourceLabel:'Проект Demo',disabledReason:'None'}]};
        if(p.prefix==='old'||p.prefix==='new')return new Promise(resolve=>probe.deferred.push(()=>resolve(result)));
        return Promise.resolve(result);
      }
      if(type==='readBrowserSource')return Promise.resolve({files:['index.html'],path:'index.html',text:'<h1>Exact source</h1>',offset:0,totalCharacters:21,revision:'sha',requestGeneration:p.requestGeneration});
      let value=probe.pages.get(p.browserTabId);
      if(p.action==='Open') { value={browserTabId:p.browserTabId,sessionId:'session-'+p.browserTabId,ownerScope:p.ownerScope,visitRevision:1,revision:0,documentGeneration:1,url:p.locator,title:'Главная',status:'Home',attached:false};probe.pages.set(p.browserTabId,value); }
      else if(p.action==='Navigate') {
        if(!p.locator.startsWith('/projects/'))return Promise.reject(new Error('Локальный адрес недоступен'));
        value.visitRevision++;value.url=p.locator;value.title='Страница проекта';value.documentGeneration++;value.status='Unavailable';
        value.target={kind:'WorkspaceHtmlProject',state:'Ready',ownerScope:{kind:'Workspace',scopeId:'demo'},workspaceId:'demo',capabilities:{source:true}};
      } else if(p.action==='Home') {value.status='Home';value.url='/';value.title='Главная';value.documentGeneration++;value.target=null;}
      else if(p.action==='Reload')value.documentGeneration++;
      else if(p.action==='Attach')value.attached=true;
      else if(p.action==='Close')value.status='Closed';
      value.revision++;return Promise.resolve(structuredClone(value));
    };
    for(let i=0;i<1000;i++)state.chats.push({Id:'extra-'+i,Title:'Скрытый чат '+i});
    const key='rnassistant.ui.dock.s1.v1.local-recents.v1';
    localStorage.setItem(key,JSON.stringify({version:1,entries:[{locator:'https://secret.invalid/?token=bad',title:'External',kind:'HtmlResource',scope:{kind:'User',scopeId:'local'},visitedAt:1},
      {locator:'/resources/private',title:'Другой документ',kind:'HtmlResource',scope:{kind:'Document',scopeId:'other'},visitedAt:1}]}));
  });
  const plus=()=>page.locator('.dock-group-actions').first().getByRole('button',{name:'Выбрать экран',exact:true});
  await plus().click();
  const chooser=page.locator('.dock-screen-chooser:visible');
  assert.ok(await chooser.getByRole('button').count()<=5);
  assert.equal(await chooser.getByText(/Скрытый чат/).count(),0);
  assert.equal(await chooser.getByRole('button',{name:'Инспектор',exact:true}).count(),0);
  await chooser.getByRole('button',{name:'Открыть чат',exact:true}).click();
  assert.equal(await page.evaluate(()=>document.activeElement.id),'chatSearchInput');
  assert.equal(await page.locator('[data-tab-panel-id^="choose-"]').count(),0);
  await plus().click();await chooser.getByRole('button',{name:'Browser',exact:true}).click();
  const browser=page.locator('section[aria-label="Browser"]:visible'),home=browser.locator('.start-surface');
  await home.getByRole('button',{name:'Страница проекта',exact:true}).waitFor();
  assert.equal(await browser.locator('iframe').count(),0);
  assert.equal(await page.evaluate(()=>startProbe.calls.filter(c=>c.type==='readBrowserSource').length),0,'Home never reads source');
  assert.equal(await home.getByText('Другой документ').count(),0,'User recents do not expose document scope');
  const input=home.getByRole('searchbox');await input.fill('old');await input.fill('new');
  await page.evaluate(()=>{startProbe.deferred[1]();startProbe.deferred[0]();});
  await home.getByRole('button',{name:'new',exact:true}).waitFor();assert.equal(await home.getByRole('button',{name:'old',exact:true}).count(),0);
  await input.press('Escape');await home.getByRole('button',{name:'Страница проекта',exact:true}).click();
  await browser.locator('.browser-document-source').getByText('Exact source',{exact:false}).waitFor();
  const address=browser.getByRole('combobox',{name:'Адрес материала'});
  await address.fill('/projects/demo/source/index.html?token=SECRET#private');await address.press('Enter');
  await page.waitForFunction(()=>startProbe.calls.some(c=>c.action==='Navigate'&&c.locator.includes('SECRET')));
  await page.waitForTimeout(220);
  const persisted=await page.evaluate(()=>({recent:localStorage.getItem('rnassistant.ui.dock.s1.v1.local-recents.v1'),dock:localStorage.getItem('rnassistant.ui.dock.s1.v1')}));
  assert.ok(!persisted.recent.includes('SECRET')&&!persisted.dock.includes('SECRET'));
  assert.ok(!persisted.recent.includes('session-')&&!persisted.dock.includes('session-browser-'));
  assert.equal(JSON.parse(persisted.recent).entries.filter(e=>e.scope.kind==='Workspace').length,1);
  await browser.getByRole('button',{name:'Обновить предпросмотр',exact:true}).click();
  assert.equal(await page.evaluate(()=>localStorage.getItem('rnassistant.ui.dock.s1.v1.local-recents.v1')),persisted.recent,'Reload does not record a visit');
  await address.fill('https://external.invalid');await address.press('Enter');
  await browser.getByRole('alert').filter({hasText:'Локальный'}).waitFor();
  assert.equal(await page.evaluate(()=>localStorage.getItem('rnassistant.ui.dock.s1.v1.local-recents.v1')),persisted.recent,'Rejected address does not record a visit');
  await page.evaluate(() => {
    const value = [...startProbe.pages.values()].find(value => value.status === 'Unavailable');
    value.url = '/projects/demo/source/from-native.html?token=HIDDEN'; value.visitRevision++; value.documentGeneration++; value.revision++;
    RNAssistantBrowser.apply(structuredClone(value));
  });
  const nativeRecent = await page.evaluate(() => localStorage.getItem('rnassistant.ui.dock.s1.v1.local-recents.v1'));
  assert.ok(nativeRecent.includes('from-native.html') && !nativeRecent.includes('HIDDEN'), 'native/agent state events enter recents without a Shell command');
  await browser.getByRole('button',{name:'Главная',exact:true}).click();
  await home.getByRole('button',{name:'Очистить недавние страницы',exact:true}).waitFor();
  await page.screenshot({path:'docs/ui/review/browser-home-wide.png'});
  await page.setViewportSize({width:420,height:800});await page.evaluate(()=>document.documentElement.style.zoom='1.25');
  await page.waitForTimeout(200);const bounds=await home.evaluate(el=>({client:el.clientWidth,scroll:el.scrollWidth,right:el.getBoundingClientRect().right,viewport:innerWidth}));
  assert.ok(bounds.scroll<=bounds.client+1&&bounds.right<=bounds.viewport+1,JSON.stringify(bounds));
  await page.screenshot({path:'docs/ui/review/browser-home-narrow.png'});
  await home.getByRole('button',{name:'Очистить недавние страницы',exact:true}).click();
  assert.equal(await page.evaluate(()=>JSON.parse(localStorage.getItem('rnassistant.ui.dock.s1.v1.local-recents.v1')).entries.length),0);
  await page.evaluate(()=>{
    for(let i=0;i<205;i++)RNAssistantDockWorkspace.recordNavigation({url:'/projects/demo/source/page'+i+'.html?private=discard',title:'Page '+i,status:'Unavailable',target:{kind:'WorkspaceHtmlProject',state:'Ready',ownerScope:{kind:'Workspace',scopeId:'demo'}}});
  });
  assert.equal(await page.evaluate(()=>JSON.parse(localStorage.getItem('rnassistant.ui.dock.s1.v1.local-recents.v1')).entries.length),200);
  await page.evaluate(()=>{const old=Storage.prototype.setItem;Storage.prototype.setItem=function(key,value){if(key.endsWith('.local-recents.v1'))throw new Error('Storage disabled');return old.call(this,key,value);};});
  await home.getByRole('button',{name:'Страница проекта',exact:true}).click();
  await page.locator('#workspaceLayoutNotice').getByText('Недавние страницы доступны только до закрытия окна: хранилище недоступно.',{exact:true}).waitFor();
  await browser.getByRole('button',{name:'Главная',exact:true}).click();
  await home.getByText('Недавние страницы доступны только до закрытия окна.',{exact:true}).waitFor();
  assert.equal(await page.locator('#workspacePanels, #workspaceMaximize').count(),0);
  assert.deepEqual(errors,[]);console.log('PASS shared StartSurface, scoped bounded recents, stale search, local resolution and preference hygiene');
}))().catch(error=>{console.error(error);process.exitCode=1;});
