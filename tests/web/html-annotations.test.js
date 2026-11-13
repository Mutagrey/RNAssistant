'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const { withPreviewPage } = require('./preview-session');
(async () => withPreviewPage(async ({page,errors}) => {
  page.setDefaultTimeout(6000);
  await page.waitForFunction(()=>state.activeChatId==='preview-chat' && !state.initializePromise);
  await page.evaluate(()=>{
    const original=send;window.annotationCalls=[];window.annotationNative={value:null,sequence:0,deferred:null};
    function visual(type,extra={}) { const value=annotationNative.value;value.visual={type:'rnassistant-visual-inspector-'+type,generation:value.documentGeneration,sequence:++annotationNative.sequence,...extra}; }
    window.selectionEvent=()=>{const value=annotationNative.value;visual('selected',{selection:{tag:'button',stableUiId:'save',attributes:{'data-ui-id':'save'},selector:'#save',ancestry:[],textExcerpt:'Сохранить',rect:{x:25,y:25,width:100,height:28},viewport:{width:500,height:300,dpr:1}}});value.revision++;RNAssistantBrowser.apply(structuredClone(value));};
    send=async(type,p,...rest)=>{
      if(type==='stageBrowserVisualSelection'){annotationCalls.push({type,p});if(annotationNative.deferred)await annotationNative.deferred;return {resource:{id:'visual-'+annotationCalls.filter(c=>c.type===type).length,kind:'text',fileName:'selection.json',size:500}};}
      if(type==='discardChatResourceDraft'){annotationCalls.push({type,p});return {};}
      if(type==='readBrowserSource')return {files:['index.html'],path:'index.html',text:'<button>Сохранить</button>',requestGeneration:p.requestGeneration,offset:0,totalCharacters:26};
      if(type!=='browserCommand')return original(type,p,...rest);
      annotationCalls.push({type,p});let value=annotationNative.value;
      if(p.action==='Open')value=annotationNative.value={browserTabId:p.browserTabId,operationId:p.operationId,sessionId:'annotation-page',ownerScope:p.ownerScope,url:p.locator,title:'HTML с аннотациями',revision:1,documentGeneration:1,status:'Starting',attached:false,controlRevision:1,
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
  const root=page.locator('.browser-object-view:visible'), toggle=root.getByRole('button',{name:'Аннотировать: выбрать элемент'});
  await toggle.waitFor();await toggle.click();await page.evaluate(()=>selectionEvent());
  const editor=page.getByRole('dialog',{name:'Аннотация 1',exact:true});await editor.waitFor();await editor.getByRole('textbox').fill('Сделать кнопку зелёной');
  await page.waitForFunction(()=>annotationCalls.filter(c=>c.type==='browserCommand' && c.p.action==='Layout').at(-1)?.p.bounds.visible===false);
  await fs.mkdir('docs/ui/review',{recursive:true});await page.screenshot({path:'docs/ui/review/browser-annotations-wide.png'});
  await editor.getByRole('button',{name:'Готово',exact:true}).click();await editor.waitFor({state:'detached'});
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').attachments[0].visualAnnotation.comment),'Сделать кнопку зелёной');
  assert.equal(await page.evaluate(()=>annotationCalls.find(c=>c.type==='stageBrowserVisualSelection').p.sessionId),'annotation-page');
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').pendingVisualAnnotation),null);
  await page.evaluate(()=>{const box=document.createElement('div');document.body.appendChild(box);
    const controller=RNAssistantBrowser.annotations({source:'office',sessionId:'preview-chat'});controller.renderDrafts(box,controller.visibleDrafts());
    [...box.querySelectorAll('button')].find(button=>button.textContent==='Изменить').click();box.remove();});
  await editor.waitFor();
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').pendingVisualAnnotation.PreviewGeneration),1);
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').pendingVisualAnnotation.BrowserSessionId),'annotation-page');
  await editor.getByRole('button',{name:'Отмена',exact:true}).click();
  await page.evaluate(()=>selectionEvent());const pending=page.getByRole('dialog',{name:'Аннотация 2',exact:true});await pending.getByRole('textbox').fill('Комментарий до перехода');
  await page.evaluate(()=>{const value=annotationNative.value;value.documentGeneration++;value.visual=null;value.revision++;RNAssistantBrowser.apply(structuredClone(value));});
  await pending.waitFor({state:'detached'});
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').pendingVisualAnnotation.Comment),'Комментарий до перехода');
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').pendingVisualAnnotation.PreviewGeneration),1,'generation is never rewritten onto a new page');
  assert.equal(await page.evaluate(()=>annotationCalls.filter(c=>c.type==='stageBrowserVisualSelection').length),1);
  await page.evaluate(()=>RNAssistantChatDrafts.setPendingOfficeAnnotation('preview-chat',null));
  await root.getByRole('button',{name:'Аннотировать: выбрать элемент'}).click();await page.evaluate(()=>selectionEvent());
  const late=page.getByRole('dialog',{name:'Аннотация 3',exact:true});await late.getByRole('textbox').fill('Поздний ответ');
  await page.evaluate(()=>{annotationNative.deferred=new Promise(resolve=>annotationNative.release=resolve);});
  await late.getByRole('button',{name:'Готово',exact:true}).click();
  await page.waitForFunction(()=>annotationCalls.filter(c=>c.type==='stageBrowserVisualSelection').length===2);
  await page.evaluate(()=>{const value=annotationNative.value;value.documentGeneration++;value.visual=null;value.revision++;RNAssistantBrowser.apply(structuredClone(value));annotationNative.release();});
  await page.waitForFunction(()=>annotationCalls.some(c=>c.type==='discardChatResourceDraft' && c.p.id==='visual-2'));
  assert.equal(await page.evaluate(()=>RNAssistantChatDrafts.readOffice('preview-chat').attachments.length),1,'late prepared annotation is cleaned, never moved to current page');
  await page.setViewportSize({width:420,height:800});await page.evaluate(()=>document.documentElement.style.zoom='1.25');
  await page.evaluate(()=>RNAssistantChatDrafts.setPendingOfficeAnnotation('preview-chat',null));
  await root.getByRole('button',{name:'Аннотировать: выбрать элемент'}).click();await page.evaluate(()=>selectionEvent());
  const narrow=page.getByRole('dialog',{name:'Аннотация 4',exact:true});await narrow.getByRole('textbox').fill('Узкое окно');
  const box=await narrow.boundingBox();assert.ok(box.x>=0 && box.x+box.width<=421,'common annotation editor fits narrow 125% layout');
  await page.screenshot({path:'docs/ui/review/browser-annotations-narrow.png'});
  await narrow.getByRole('button',{name:'Отмена',exact:true}).click();
  await root.locator('.preview-surface-address').focus();await page.keyboard.press('Control+f');
  const find=root.getByRole('searchbox',{name:'Найти на странице'});await find.fill('Сохранить');
  await root.getByText('1 из 2 в доступном тексте',{exact:true}).waitFor();await root.getByRole('button',{name:'Следующее совпадение'}).click();
  await root.getByText('2 из 2 в доступном тексте',{exact:true}).waitFor();
  await page.evaluate(()=>{const value=annotationNative.value;value.visual={...value.visual,sequence:++annotationNative.sequence,find:{...value.visual.find,requestId:value.visual.find.requestId-1,index:1}};value.revision++;RNAssistantBrowser.apply(structuredClone(value));});
  await root.getByText('2 из 2 в доступном тексте',{exact:true}).waitFor();
  const findBox=await find.boundingBox();assert.ok(findBox.x>=0 && findBox.x+findBox.width<=421);
  await page.screenshot({path:'docs/ui/review/browser-find-narrow.png'});
  await page.evaluate(()=>{const value=annotationNative.value;value.documentGeneration++;value.visual=null;value.revision++;RNAssistantBrowser.apply(structuredClone(value));});
  await find.waitFor({state:'hidden'});
  assert.deepEqual(errors,[]);
  console.log('PASS Browser annotations: common native control, exact source chat, draft/comment, overlay placement, generation refusal and late cleanup');
}))().catch(error=>{console.error(error);process.exitCode=1;});
