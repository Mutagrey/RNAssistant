'use strict';
const assert = require('node:assert/strict');
const { withPreviewPage } = require('./preview-session');
(async () => withPreviewPage(async ({page, errors}) => {
  page.setDefaultTimeout(6000);
  await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
  await page.evaluate(() => {
    const original = send; window.nativeLayout = {calls:[], reads:[], folders:[], value:null};
    send = (type, p, ...rest) => {
      if (type === 'readBrowserSource') {
        nativeLayout.reads.push(structuredClone(p));
        const info={files:['index.html','styles/site.css'],path:p.path||'index.html',requestGeneration:p.requestGeneration};
        return Promise.resolve(p.filesOnly?info:{...info,text:'Source of '+info.path,offset:0,totalCharacters:25});
      }
      if (type === 'openFolder') { nativeLayout.folders.push(structuredClone(p));return Promise.resolve({dispatched:true}); }
      if (type === 'queryNavigationSuggestions') return Promise.resolve({requestGeneration:p.requestGeneration,
        items:['index.html','other.html'].map((path,index)=>({title:index?'Другая страница':'Локальная страница',displayLocator:'/projects/project/source/'+path,kind:'WorkspaceHtmlProject',sourceLabel:'project',disabledReason:'None'}))});
      if (type !== 'browserCommand') return original(type,p,...rest);
      nativeLayout.calls.push(structuredClone(p));
      let value = nativeLayout.value;
      if (p.action === 'Open') value = nativeLayout.value = {browserTabId:p.browserTabId,operationId:p.operationId,sessionId:'native-session',ownerScope:p.ownerScope,
        url:p.locator,title:'Локальная страница',revision:1,visitRevision:1,documentGeneration:1,status:'Starting',attached:false,controlRevision:1,chatBound:!!p.ownerScope.chatId,
        target:{kind:'WorkspaceHtmlProject',state:'Ready',workspaceId:'project',ownerScope:p.ownerScope,relativePath:'index.html',capabilities:{source:true}}};
      if (p.action === 'Attach') { value.attached=true;value.status='Ready'; }
      if (p.action === 'ResumeAgent') { nativeLayout.assertRevision=p.controlRevision;value.manualControl=false;value.controlRevision++; }
      if (p.action === 'Close') value.status='Closed';
      value.revision++;return Promise.resolve(structuredClone(value));
    };
    RNAssistantBrowser.open('/projects/project/source/index.html',{kind:'Workspace',scopeId:'project',chatId:'chat'},{browserTabId:'native-layout'});
  });
  await page.waitForFunction(() => nativeLayout.value?.status === 'Ready');
  const root = page.locator('.browser-object-view:visible');
  const bounds = await page.evaluate(() => nativeLayout.calls.find(c=>c.action==='Attach').bounds);
  const bar = await root.locator('.preview-surface-toolbar').boundingBox();
  assert.ok(bounds.visible && bounds.y > bar.y && bounds.height > 100, 'native bounds cover content only');
  async function expectCutout(selector) {
    await page.waitForFunction(selector => {
      const bounds=nativeLayout.calls.filter(c=>c.action==='Layout').at(-1)?.bounds;
      const node=document.querySelector(selector);
      if (!bounds?.visible || bounds.cutouts?.length!==1 || !node) return false;
      const box=node.getBoundingClientRect(), cutout=bounds.cutouts[0];
      return cutout.x===Math.max(bounds.x,box.left) && cutout.y===Math.max(bounds.y,box.top) &&
        Math.abs(cutout.x+cutout.width-Math.min(bounds.x+bounds.width,box.right))<0.01 &&
        Math.abs(cutout.y+cutout.height-Math.min(bounds.y+bounds.height,box.bottom))<0.01 &&
        (cutout.height<bounds.height || cutout.width<bounds.width);
    },selector);
  }
  async function expectUncovered() {
    await page.waitForFunction(()=>{
      const bounds=nativeLayout.calls.filter(c=>c.action==='Layout').at(-1)?.bounds;
      return bounds?.visible && bounds.cutouts?.length===0;
    });
  }
  const more = root.getByRole('button',{name:'Действия страницы',exact:true});
  await more.click();
  await page.getByRole('menuitem',{name:'Связать с другим чатом…',exact:true}).waitFor();
  await expectCutout('.rna-action-menu-popup');
  assert.equal(await page.evaluate(()=>nativeLayout.calls.filter(c=>c.action==='Layout').at(-1).bounds.height),bounds.height,'menu does not resize the native viewport');
  if (process.env.BROWSER_SCREENSHOT) await page.screenshot({path:process.env.BROWSER_SCREENSHOT+'-wide.png'});
  await page.keyboard.press('Escape');
  await expectUncovered();
  const tree=root.getByRole('navigation',{name:'Файлы проекта',exact:true});
  const openTree=root.getByRole('button',{name:'Показать дерево материала',exact:true});
  const closeTree=root.getByRole('button',{name:'Скрыть дерево материала',exact:true});
  assert.deepEqual(await page.evaluate(()=>nativeLayout.reads),[],'initial Preview requires no source read');
  await openTree.click();
  await tree.getByRole('treeitem',{name:'styles/site.css',exact:true}).waitFor();
  assert.equal(await root.locator('.browser-native-viewport').isVisible(),true,'opening the file tree retains Preview');
  assert.deepEqual(await page.evaluate(()=>nativeLayout.reads.map(read=>read.filesOnly)),[true],'opening the tree fetches file names only');
  await tree.getByRole('button',{name:'Открыть папку проекта',exact:true}).click();
  assert.deepEqual(await page.evaluate(()=>nativeLayout.folders),[{workspaceId:'project'}]);
  await tree.getByRole('treeitem',{name:'styles/site.css',exact:true}).click();
  await root.locator('.browser-document-source').getByText('Source of styles/site.css').waitFor();
  assert.deepEqual(await page.evaluate(()=>nativeLayout.reads.map(read=>read.path||null)),[null,'styles/site.css'],'selection opens the chosen source once');
  await root.getByRole('button',{name:'Страница',exact:true}).click();
  assert.equal(await tree.locator('[aria-selected="true"]').getAttribute('aria-label'),'index.html','Preview highlights the live HTML member');
  await closeTree.click();
  await page.evaluate(()=>{
    const trigger = document.querySelector('.browser-object-view:not([hidden]) .preview-surface-actions-overflow');
    window.resizedMenu = RNAssistantActionMenu.open(trigger,[{label:'Тестовая строка',onSelect(){}}]);
  });
  await expectCutout('.rna-action-menu-popup');
  const small = await page.evaluate(()=>nativeLayout.calls.filter(c=>c.action==='Layout').at(-1).bounds.cutouts[0].height);
  await page.evaluate(()=>resizedMenu.update(Array.from({length:4},()=>({label:'Тестовая строка',onSelect(){}}))));
  await expectCutout('.rna-action-menu-popup');
  assert.ok(await page.evaluate(()=>nativeLayout.calls.filter(c=>c.action==='Layout').at(-1).bounds.cutouts[0].height)>small,'open popup resizing updates the cutout');
  await page.keyboard.press('Escape');
  await expectUncovered();
  const reload=root.getByRole('button',{name:'Обновить предпросмотр',exact:true}), stop=root.getByRole('button',{name:'Остановить загрузку',exact:true});
  assert.equal(await reload.isVisible(),true,'ready page exposes reload');
  assert.equal(await stop.isVisible(),false,'ready page does not expose stop');
  await page.evaluate(() => {const value=nativeLayout.value;value.status='Starting';value.revision++;RNAssistantBrowser.apply(structuredClone(value));});
  await stop.waitFor({state:'visible'});assert.equal(await reload.isVisible(),false,'loading page replaces reload with stop');
  await root.getByRole('status').filter({hasText:'Открытие страницы…'}).waitFor();
  await page.evaluate(() => {const value=nativeLayout.value;value.status='Ready';value.revision++;RNAssistantBrowser.apply(structuredClone(value));});
  await reload.waitFor({state:'visible'});assert.equal(await stop.isVisible(),false,'completed load restores reload');
  assert.equal(await root.locator('.browser-view-loading').isVisible(),false,'ready page removes the loading surface');
  const rightmost=await root.locator('.preview-surface-actions button:visible').evaluateAll(nodes=>nodes.map(node=>({label:node.getAttribute('aria-label'),right:node.getBoundingClientRect().right})).sort((a,b)=>b.right-a.right)[0]);
  assert.equal(rightmost.label,'Действия страницы','overflow menu is the rightmost action');
  await root.locator('.preview-surface-address').focus();
  assert.equal(await root.locator('.preview-surface-actions').isVisible(),false,'address focus hides the lower action strip');
  assert.equal(await root.getByRole('button',{name:'Копировать адрес',exact:true}).count(),0,'address copy action is removed');
  await expectCutout('.preview-surface-completion:not([hidden])');
  await root.locator('.preview-surface-address').fill('page');
  await root.locator('.preview-surface-completion').getByRole('option').filter({hasText:'Другая страница'}).waitFor();
  await expectCutout('.preview-surface-completion:not([hidden])');
  await root.locator('.preview-surface-address').blur();
  await expectUncovered();
  await root.getByRole('button',{name:'Исходник',exact:true}).click();
  await page.waitForFunction(() => nativeLayout.calls.filter(c=>c.action==='Layout').at(-1)?.bounds.visible === false);
  await root.getByRole('button',{name:'Страница',exact:true}).click();
  await page.waitForFunction(() => nativeLayout.calls.filter(c=>c.action==='Layout').at(-1)?.bounds.visible === true);
  await page.evaluate(() => {const dialog=document.createElement('dialog');dialog.id='native-test-dialog';document.body.append(dialog);dialog.showModal();});
  await page.waitForFunction(() => nativeLayout.calls.filter(c=>c.action==='Layout').at(-1)?.bounds.visible === false);
  await page.evaluate(() => document.getElementById('native-test-dialog').remove());
  await page.waitForFunction(() => nativeLayout.calls.filter(c=>c.action==='Layout').at(-1)?.bounds.visible === true);
  const layoutsBeforeInvalidation=await page.evaluate(()=>{
    const before=nativeLayout.calls.filter(c=>c.action==='Layout').length;
    const value=nativeLayout.value;value.layoutRevision=1;value.revision++;RNAssistantBrowser.apply(structuredClone(value));
    return before;
  });
  await page.waitForFunction(count=>nativeLayout.calls.filter(c=>c.action==='Layout').length===count+1,layoutsBeforeInvalidation);
  await page.evaluate(() => {const value=nativeLayout.value;value.manualControl=true;value.controlRevision=2;value.revision++;RNAssistantBrowser.apply(structuredClone(value));});
  assert.equal(await root.getByRole('button',{name:'Продолжить агентом'}).count(),0,'manual takeover has no resume button');
  await page.setViewportSize({width:420,height:800});await page.evaluate(()=>document.documentElement.style.zoom='1.25');
  await page.waitForTimeout(150);
  assert.ok(await root.evaluate(n=>n.scrollWidth<=n.clientWidth+1));
  const header = await root.locator('.material-surface-header').evaluate(node => ({
    height: node.offsetHeight, rowHeight: parseFloat(getComputedStyle(node).minHeight)
  }));
  assert.ok(header.height <= header.rowHeight + 1, 'Browser header stays one row at narrow/125%: ' + JSON.stringify(header));
  await openTree.click();
  await expectCutout('.material-surface-sidebar:not([hidden])');
  if (process.env.BROWSER_SCREENSHOT) await page.screenshot({path:process.env.BROWSER_SCREENSHOT+'-files-narrow.png'});
  await closeTree.click();
  await expectUncovered();
  const narrowAddress=root.getByRole('combobox',{name:'Адрес материала',exact:true});
  await narrowAddress.focus();
  await expectCutout('.preview-surface-completion:not([hidden])');
  await narrowAddress.press('Escape');
  await expectUncovered();
  assert.equal(await root.getByRole('button',{name:'Обсудить в чате рядом',exact:true}).isVisible(),false,'compact header folds discuss into overflow');
  await more.click();
  assert.equal(await page.getByRole('menuitem',{name:'Обсудить в чате рядом',exact:true}).isVisible(),true,'overflow keeps compact Browser actions reachable');
  await expectCutout('.rna-action-menu-popup');
  if (process.env.BROWSER_SCREENSHOT) await page.screenshot({path:process.env.BROWSER_SCREENSHOT+'-narrow.png'});
  await page.keyboard.press('Escape');
  await expectUncovered();
  const latest = await page.evaluate(()=>nativeLayout.calls.filter(c=>c.action==='Layout').at(-1).bounds);
  assert.ok(latest.width > 0 && latest.x + latest.width <= latest.viewportWidth + 1);
  await page.locator('#productMobileMenuButton').click();
  await page.waitForFunction(()=>nativeLayout.assertRevision===2);
  assert.equal(await page.evaluate(()=>nativeLayout.assertRevision),2);
  await page.evaluate(()=>RNAssistantDockWorkspace.close('browser:native-layout'));
  assert.ok(await page.evaluate(()=>nativeLayout.calls.some(c=>c.action==='Close')));
  assert.deepEqual(errors,[]);
  console.log('PASS native Browser layout: content bounds, source/preview, modal airspace, manual status, narrow zoom, close');
}))().catch(error=>{console.error(error);process.exitCode=1;});
