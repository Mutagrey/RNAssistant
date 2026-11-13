'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const { withPreviewPage } = require('./preview-session');
(async () => withPreviewPage(async ({page, errors}) => {
  page.setDefaultTimeout(6000);
  await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
  await page.evaluate(() => {
    const original = send; window.nativeLayout = {calls:[], value:null};
    send = (type, p, ...rest) => {
      if (type === 'readBrowserSource') return Promise.resolve({files:['index.html'],path:'index.html',text:'<button>Локальная страница</button>',offset:0,totalCharacters:33,requestGeneration:p.requestGeneration});
      if (type !== 'browserCommand') return original(type,p,...rest);
      nativeLayout.calls.push(structuredClone(p));
      let value = nativeLayout.value;
      if (p.action === 'Open') value = nativeLayout.value = {browserTabId:p.browserTabId,operationId:p.operationId,sessionId:'native-session',ownerScope:p.ownerScope,
        url:p.locator,title:'Локальная страница',revision:1,documentGeneration:1,status:'Starting',attached:false,controlRevision:1,
        target:{kind:'WorkspaceHtmlProject',state:'Ready',workspaceId:'project',ownerScope:p.ownerScope,capabilities:{source:true}}};
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
  await root.getByRole('button',{name:'Исходник',exact:true}).click();
  await page.waitForFunction(() => nativeLayout.calls.filter(c=>c.action==='Layout').at(-1)?.bounds.visible === false);
  await root.getByRole('button',{name:'Страница',exact:true}).click();
  await page.waitForFunction(() => nativeLayout.calls.filter(c=>c.action==='Layout').at(-1)?.bounds.visible === true);
  await page.evaluate(() => {const dialog=document.createElement('dialog');dialog.id='native-test-dialog';document.body.append(dialog);dialog.showModal();});
  await page.waitForFunction(() => nativeLayout.calls.filter(c=>c.action==='Layout').at(-1)?.bounds.visible === false);
  await page.evaluate(() => document.getElementById('native-test-dialog').remove());
  await page.waitForFunction(() => nativeLayout.calls.filter(c=>c.action==='Layout').at(-1)?.bounds.visible === true);
  await page.evaluate(() => {const value=nativeLayout.value;value.manualControl=true;value.controlRevision=2;value.revision++;RNAssistantBrowser.apply(structuredClone(value));});
  await root.getByRole('button',{name:'Продолжить агентом'}).waitFor();
  // The fixture deliberately leaves the native viewport blank; no Shell iframe impersonates it.
  await fs.mkdir('docs/ui/review', {recursive:true});
  await page.screenshot({path:'docs/ui/review/browser-interactive-wide.png'});
  await page.setViewportSize({width:420,height:800});await page.evaluate(()=>document.documentElement.style.zoom='1.25');
  await page.waitForTimeout(150);
  assert.ok(await root.evaluate(n=>n.scrollWidth<=n.clientWidth+1));
  const latest = await page.evaluate(()=>nativeLayout.calls.filter(c=>c.action==='Layout').at(-1).bounds);
  assert.ok(latest.width > 0 && latest.x + latest.width <= latest.viewportWidth + 1);
  await page.screenshot({path:'docs/ui/review/browser-interactive-narrow.png'});
  await root.getByRole('button',{name:'Продолжить агентом'}).click();
  assert.equal(await page.evaluate(()=>nativeLayout.assertRevision),2);
  await page.evaluate(()=>RNAssistantDockWorkspace.close('browser:native-layout'));
  assert.ok(await page.evaluate(()=>nativeLayout.calls.some(c=>c.action==='Close')));
  assert.deepEqual(errors,[]);
  console.log('PASS native Browser layout: content bounds, source/preview, modal airspace, manual status, narrow zoom, close');
}))().catch(error=>{console.error(error);process.exitCode=1;});
