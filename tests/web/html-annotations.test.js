'use strict';
const assert = require('node:assert/strict');
const { withPreviewPage } = require('./preview-session');
(async () => withPreviewPage(async ({page,errors}) => {
  page.setDefaultTimeout(6000);
  await page.setViewportSize({width:1600,height:900});
  await page.waitForFunction(()=>state.activeChatId==='preview-chat' && !state.initializePromise);
  await page.evaluate(()=>{
    const original=send;window.annotationCalls=[];window.annotationNative={value:null,sequence:0,deferred:null};
    function visual(type,extra={}) { const value=annotationNative.value;value.visual={type:'rnassistant-visual-inspector-'+type,generation:value.documentGeneration,sequence:++annotationNative.sequence,...extra}; }
    window.selectionEvent=()=>{const value=annotationNative.value;visual('selected',{selection:{tag:'button',stableUiId:'save',attributes:{'data-ui-id':'save'},selector:'#save',ancestry:[],textExcerpt:'Сохранить',rect:{x:25,y:25,width:100,height:28},viewport:{width:500,height:300,dpr:1}}});value.revision++;RNAssistantBrowser.apply(structuredClone(value));};
    send=async(type,p,...rest)=>{
      if(type==='stageBrowserVisualSelection'){annotationCalls.push({type,p});if(annotationNative.deferred)await annotationNative.deferred;return {resource:{id:'visual-'+annotationCalls.filter(c=>c.type===type).length,kind:'text',fileName:'selection.json',size:500}};}
      if(type==='openFolder'){annotationCalls.push({type,p});return {dispatched:true};}
      if(type==='discardChatResourceDraft'){annotationCalls.push({type,p});return {};}
      if(type==='readBrowserSource')return {files:['index.html'],path:'index.html',text:'<button>Сохранить</button>',requestGeneration:p.requestGeneration,offset:0,totalCharacters:26};
      if(type!=='browserCommand')return original(type,p,...rest);
      annotationCalls.push({type,p});let value=annotationNative.value;
      if(p.action==='Open')value=annotationNative.value={browserTabId:p.browserTabId,operationId:p.operationId,sessionId:'annotation-page',ownerScope:p.ownerScope,url:p.locator,title:'HTML с аннотациями',revision:1,documentGeneration:1,status:'Starting',attached:false,chatBound:true,controlRevision:1,
        annotationContext:{sourceId:'html-r1',sourceRevision:1},target:{kind:'HtmlWorkspace',state:'Ready',documentId:'doc',chatId:'preview-chat',artifactId:'html-r1',revision:1,resourceUri:'rna://chat/doc/artifact/html-r1/revision/1',ownerScope:p.ownerScope,capabilities:{source:true,annotations:true,interact:true,preview:true}}};
      if(p.action==='Attach'){value.attached=true;value.status='Ready';}
      if(p.action==='VisualControl'){
        if(p.visual.generation!==value.documentGeneration)throw new Error('stale page');
        if(p.visual.operation==='probe')visual('ready');
        if(p.visual.operation==='activate')visual('activated');
        if(p.visual.operation==='find')visual('find',{find:{...p.visual.find,index:p.visual.find.direction===1?2:1,count:2,truncated:false}});
        if(p.visual.operation==='watch')visual('locations',{locations:p.visual.watches.map(w=>({id:w.id,status:'visible',rect:{x:25,y:25,width:100,height:28}}))});
      }
      if(p.action==='Reload'){value.documentGeneration++;value.visual=null;}
      if(p.action==='Close')value.status='Closed';
      value.revision++;return structuredClone(value);
    };
    RNAssistantBrowser.open('/resources/html-r1',{kind:'Document',scopeId:'doc',chatId:'preview-chat'},{browserTabId:'annotation-test'});
  });
  const root=page.locator('.browser-object-view:visible'), toggle=root.locator('button.browser-annotate');
  async function pressAnnotation() {
    await page.waitForFunction(()=>document.querySelector('.browser-object-view:not([hidden]) button.browser-annotate:not(:disabled)'));
    if (await toggle.isVisible()) await toggle.click();
    else {
      await root.getByRole('button',{name:'Действия страницы',exact:true}).click();
      await page.getByRole('menuitem',{name:'Аннотировать страницу',exact:true}).click();
    }
  }
  await toggle.waitFor({state:'attached'});
  await root.getByRole('button',{name:'Показать дерево материала',exact:true}).click();
  await root.getByRole('navigation',{name:'Файлы проекта',exact:true}).getByRole('button',{name:'Открыть папку документа',exact:true}).click();
  assert.deepEqual(await page.evaluate(()=>annotationCalls.find(c=>c.type==='openFolder').p),{chatId:'preview-chat'});
  await root.getByRole('button',{name:'Скрыть дерево материала',exact:true}).click();
  await pressAnnotation();
  await page.waitForFunction(()=>document.querySelector('.browser-object-view:not([hidden]) button.browser-annotate')?.getAttribute('aria-pressed')==='true');
  await pressAnnotation();
  await page.waitForFunction(()=>document.querySelector('.browser-object-view:not([hidden]) button.browser-annotate')?.getAttribute('aria-pressed')==='false');
  assert.ok(await page.evaluate(()=>annotationCalls.some(c=>c.type==='browserCommand'&&c.p.action==='VisualControl'&&c.p.visual.operation==='deactivate')),'second annotation click exits selection mode');
  await pressAnnotation();await page.waitForFunction(()=>document.querySelector('.browser-object-view:not([hidden]) button.browser-annotate')?.getAttribute('aria-pressed')==='true');
  await page.evaluate(()=>selectionEvent());
  const editor=page.getByRole('dialog',{name:'Аннотация 1',exact:true});await editor.waitFor();await editor.getByRole('textbox').fill('Сделать кнопку зелёной');
  await page.waitForFunction(()=>annotationCalls.filter(c=>c.type==='browserCommand' && c.p.action==='Layout').at(-1)?.p.bounds.visible===false);
  if (process.env.BROWSER_SCREENSHOT) await page.screenshot({path:process.env.BROWSER_SCREENSHOT+'-wide.png'});
  await editor.getByRole('button',{name:'Готово',exact:true}).click();await editor.waitFor({state:'detached'});
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').attachments[0].visualAnnotation.comment),'Сделать кнопку зелёной');
  assert.equal(await page.evaluate(()=>annotationCalls.find(c=>c.type==='stageBrowserVisualSelection').p.sessionId),'annotation-page');
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').pendingVisualAnnotation),null);
  await pressAnnotation();
  await page.waitForFunction(()=>document.querySelector('.browser-object-view:not([hidden]) button.browser-annotate')?.getAttribute('aria-pressed')==='false');
  assert.equal(await root.locator('.visual-annotation-marker').count(),1,'staged annotation stays visible after selection mode closes');
  await page.evaluate(()=>{const box=document.createElement('div');document.body.appendChild(box);
    const controller=RNAssistantBrowser.annotations({source:'office',sessionId:'preview-chat'});controller.renderDrafts(box,controller.visibleDrafts());
    [...box.querySelectorAll('button')].find(button=>button.textContent==='Изменить').click();box.remove();});
  await editor.waitFor();
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').pendingVisualAnnotation.PreviewGeneration),1);
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').pendingVisualAnnotation.BrowserSessionId),'annotation-page');
  await editor.getByRole('button',{name:'Отмена',exact:true}).click();
  await page.evaluate(()=>{const box=document.createElement('div');box.id='annotation-delete-test';box.style.cssText='position:fixed;bottom:24px;left:100px;z-index:100';document.body.appendChild(box);
    const controller=RNAssistantBrowser.annotations({source:'office',sessionId:'preview-chat'});controller.renderDrafts(box,controller.visibleDrafts());box.querySelector('details').open=true;});
  await page.locator('#annotation-delete-test').getByRole('button',{name:'Удалить',exact:true}).click();
  await page.waitForFunction(()=>annotationCalls.some(c=>c.type==='discardChatResourceDraft'&&c.p.id==='visual-1'));
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').attachments.length),0,'deleting from the chat draft removes the annotation');
  assert.equal(await root.locator('.visual-annotation-marker').count(),0,'deleted annotation leaves the page overlay');
  await page.evaluate(()=>document.getElementById('annotation-delete-test')?.remove());
  await pressAnnotation();
  await page.waitForFunction(()=>document.querySelector('.browser-object-view:not([hidden]) button.browser-annotate')?.getAttribute('aria-pressed')==='true');
  await page.evaluate(()=>selectionEvent());const pending=page.getByRole('dialog',{name:'Аннотация 2',exact:true});await pending.getByRole('textbox').fill('Комментарий до перехода');
  await page.evaluate(()=>{const value=annotationNative.value;value.documentGeneration++;value.visual=null;value.revision++;RNAssistantBrowser.apply(structuredClone(value));});
  await pending.waitFor({state:'detached'});
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').pendingVisualAnnotation.Comment),'Комментарий до перехода');
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').pendingVisualAnnotation.PreviewGeneration),1,'generation is never rewritten onto a new page');
  assert.equal(await page.evaluate(()=>annotationCalls.filter(c=>c.type==='stageBrowserVisualSelection').length),1);
  await page.evaluate(()=>RNAssistantChatDrafts.setPendingOfficeAnnotation('preview-chat',null));
  await pressAnnotation();await page.waitForFunction(()=>document.querySelector('.browser-object-view:not([hidden]) button.browser-annotate')?.getAttribute('aria-pressed')==='true');
  await page.evaluate(()=>selectionEvent());
  const late=page.getByRole('dialog',{name:'Аннотация 3',exact:true});await late.getByRole('textbox').fill('Поздний ответ');
  await page.evaluate(()=>{annotationNative.deferred=new Promise(resolve=>annotationNative.release=resolve);});
  await late.getByRole('button',{name:'Готово',exact:true}).click();
  await page.waitForFunction(()=>annotationCalls.filter(c=>c.type==='stageBrowserVisualSelection').length===2);
  await page.evaluate(()=>{const value=annotationNative.value;value.documentGeneration++;value.visual=null;value.revision++;RNAssistantBrowser.apply(structuredClone(value));annotationNative.release();});
  await page.waitForFunction(()=>annotationCalls.some(c=>c.type==='discardChatResourceDraft' && c.p.id==='visual-2'));
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').attachments.length),0,'late prepared annotation is cleaned, never moved to current page');
  await page.setViewportSize({width:420,height:800});await page.evaluate(()=>document.documentElement.style.zoom='1.25');
  await toggle.waitFor({state:'hidden'});
  await page.evaluate(()=>RNAssistantChatDrafts.setPendingOfficeAnnotation('preview-chat',null));
  await pressAnnotation();await page.waitForFunction(()=>document.querySelector('.browser-object-view:not([hidden]) button.browser-annotate')?.getAttribute('aria-pressed')==='true');
  await page.evaluate(()=>selectionEvent());
  const narrow=page.getByRole('dialog',{name:'Аннотация 4',exact:true});await narrow.getByRole('textbox').fill('Узкое окно');
  const box=await narrow.boundingBox();assert.ok(box.x>=0 && box.x+box.width<=421,'common annotation editor fits narrow 125% layout');
  if (process.env.BROWSER_SCREENSHOT) await page.screenshot({path:process.env.BROWSER_SCREENSHOT+'-narrow.png'});
  await narrow.getByRole('button',{name:'Отмена',exact:true}).click();
  await root.locator('.preview-surface-address').focus();await page.keyboard.press('Control+f');
  const find=root.getByRole('searchbox',{name:'Найти на странице'});await find.fill('Сохранить');
  await root.getByText('1 из 2 в доступном тексте',{exact:true}).waitFor();await root.getByRole('button',{name:'Следующее совпадение'}).click();
  await root.getByText('2 из 2 в доступном тексте',{exact:true}).waitFor();
  await page.evaluate(()=>{const value=annotationNative.value;value.visual={...value.visual,sequence:++annotationNative.sequence,find:{...value.visual.find,requestId:value.visual.find.requestId-1,index:1}};value.revision++;RNAssistantBrowser.apply(structuredClone(value));});
  await root.getByText('2 из 2 в доступном тексте',{exact:true}).waitFor();
  const findBox=await find.boundingBox();assert.ok(findBox.x>=0 && findBox.x+findBox.width<=421);
  await page.evaluate(()=>{const value=annotationNative.value;value.documentGeneration++;value.visual=null;value.revision++;RNAssistantBrowser.apply(structuredClone(value));});
  await find.waitFor({state:'hidden'});
  assert.deepEqual(errors,[]);
  console.log('PASS Browser annotations: common native control, exact source chat, draft/comment, overlay placement, generation refusal and late cleanup');
}))().catch(error=>{console.error(error);process.exitCode=1;});
