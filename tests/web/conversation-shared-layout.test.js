'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {withPreviewPage} = require('./preview-session');
const captures=process.env.RNA_CHAT_SCREENSHOTS || path.join(os.tmpdir(),'rnassistant-shared-chat');
fs.mkdirSync(captures,{recursive:true});
withPreviewPage(async ({page,errors}) => {
  page.setDefaultTimeout(7000); await page.setViewportSize({width:1360,height:900});
  await page.waitForFunction(() => state.activeChatId==='preview-chat' && !state.initializePromise);
  await page.evaluate(async () => {
    window.officeTarget={source:'office',sessionId:'preview-chat'};
    window.workspaceTarget={source:'workspace',workspaceId:'preview-data-workspace',sessionId:'preview-project-review'};
    const office=RNAssistantConversationPanel.find(officeTarget);
    office.node('chatInput').value='Черновик Office';office.node('chatInput').dispatchEvent(new Event('input'));
    await RNAssistantProjectChats.open(workspaceTarget.workspaceId,workspaceTarget.sessionId);
  });
  await page.waitForFunction(() => RNAssistantConversationPanel.find(workspaceTarget)?.hasSnapshot);
  const dimensions=target=>page.evaluate(target=>{
    RNAssistantConversationPanel.open(target);
    const panel=RNAssistantConversationPanel.find(target);
    return ['chatForm','chatInput','messages'].map(id=>{
      const css=getComputedStyle(panel.node(id));
      return ['fontSize','lineHeight','paddingTop','paddingBottom','borderRadius'].map(key=>css[key]);
    });
  },target);
  const targets=await page.evaluate(()=>[officeTarget,workspaceTarget]);
  assert.deepEqual(await dimensions(targets[0]),await dimensions(targets[1]),'sources share composer/transcript metrics');
  assert.deepEqual(await page.evaluate(()=>{
    const a=RNAssistantConversationPanel.find(officeTarget),b=RNAssistantConversationPanel.find(workspaceTarget);
    b.node('chatInput').value='Черновик Workspace';b.node('chatInput').dispatchEvent(new Event('input'));
    return [a.node('chatInput').value,b.node('chatInput').value,a.view!==b.view,a.composer!==b.composer];
  }),['Черновик Office','Черновик Workspace',true,true]);
  assert.deepEqual(await page.evaluate(async ()=>{
    const panel=RNAssistantConversationPanel.find(workspaceTarget),list=panel.node('messages'),button=panel.node('chatScrollBottomButton');
    const previous=panel.snapshot(),revision=(previous.state.revision||0)+1;
    const chat={...previous,messages:Array.from({length:40},(_,i)=>({Id:'scroll-'+i,Role:'user',Content:'Сообщение '+i+' — '+ 'Текст для прокрутки. '.repeat(12)})),messageStartIndex:0,messageTotalCount:40,
      state:{...previous.state,revision,status:'running',runId:'scroll-run',runViewState:null,modelProgress:{RunId:'scroll-run',AttemptId:'attempt',Message:'Начало ответа'}}};
    panel.accept(chat,{forceScroll:true});
    list.scrollTop=Math.floor((list.scrollHeight-list.clientHeight)/3);list.dispatchEvent(new Event('scroll'));
    const top=list.scrollTop,first=Array.from(list.children).find(node=>node.getBoundingClientRect().bottom>list.getBoundingClientRect().top);
    const offset=first.getBoundingClientRect().top-list.getBoundingClientRect().top;
    const visible=button.classList.contains('is-visible');
    const next={...chat,messages:chat.messages.concat({Id:'new-message',Role:'user',Content:'Новое сообщение'}),messageTotalCount:41,
      state:{...chat.state,revision:revision+1,modelProgress:{...chat.state.modelProgress,Message:'Продолжение ответа. '.repeat(80)}}};
    panel.accept(next); panel.stream();
    await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
    const anchored=Math.abs(list.scrollTop-top)<3 && Math.abs(first.getBoundingClientRect().top-list.getBoundingClientRect().top-offset)<3;
    button.click(); await new Promise(requestAnimationFrame);
    const jumped=Math.max(0,list.scrollHeight-list.scrollTop-list.clientHeight)<=2 && !button.classList.contains('is-visible');
    return {visible,anchored,jumped};
  }),{visible:true,anchored:true,jumped:true},'accepted history and live output preserve the reading position');
  await page.screenshot({path:path.join(captures,'chat-workspace.png'),animations:'disabled'});
  await page.evaluate(()=>RNAssistantConversationPanel.open(officeTarget));
  await page.screenshot({path:path.join(captures,'chat-office.png'),animations:'disabled'});
  await page.setViewportSize({width:420,height:800});
  await page.evaluate(()=>{document.documentElement.style.zoom='1.25';RNAssistantConversationPanel.open(workspaceTarget);});
  assert.equal(await page.evaluate(()=>{
    const panel=RNAssistantConversationPanel.find(workspaceTarget),box=panel.node('chatForm').getBoundingClientRect();
    return box.left>=-1 && box.right<=innerWidth+1 && panel.node('chatForm').contains(panel.node('contextMeter'));
  }),true,'narrow/zoom retains the context meter at composer without overflow');
  await page.screenshot({path:path.join(captures,'chat-workspace-narrow.png'),animations:'disabled'});
  assert.deepEqual(errors,[]);
  console.log('PASS shared layout: equal metrics, independent drafts, anchored streaming, jump to latest, narrow/125%');
}).catch(error=>{console.error(error);process.exitCode=1;});
