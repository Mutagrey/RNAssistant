'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
const root = path.resolve(__dirname, '../..');

(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], {cwd:root});
  const address = await new Promise((resolve,reject) => {
    server.stdout.on('data', chunk => {
      const match=/RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(chunk));
      if(match)resolve(match[1]);
    });
    server.once('exit', code => reject(Error('Preview server exited: '+code)));
  });
  const browser = await chromium.launch({headless:true, ...(process.env.BROWSER_EXECUTABLE ? {executablePath:process.env.BROWSER_EXECUTABLE} : {})});
  try {
    const page=await browser.newPage({viewport:{width:1200,height:850}}), errors=[];
    page.on('pageerror', error => errors.push(error.stack || error.message));
    await page.goto(address+'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId==='preview-chat' && !state.initializePromise);
    await page.evaluate(() => {
      const original=send, revision=state.chatProjectionRevisions[state.activeChatId];
      const rows=(start,count)=>Array.from({length:count},(_,i)=>({Id:'history-'+(start+i),Role:'user',
        Content:'Сообщение '+(start+i)+' — проверка ограниченного окна истории.'}));
      const latest=()=>({messages:rows(920,80),messageStartIndex:920,messageTotalCount:1000});
      const previous=(id,rev,before)=>({chatId:id,sessionRevision:rev,startIndex:Math.max(0,before-80),
        totalCount:1000,messages:rows(Math.max(0,before-80),Math.min(80,before))});
      window.windowTest={requests:[]};
      window.send=async function(type,payload){
        if(type==='getPreviousChatMessages')return previous(payload.chatId,revision,payload.beforeIndex);
        if(type==='getChatState')return {activeChatId:payload.chatId,sessionRevision:revision,...latest()};
        if(type==='getPreviousProjectChatMessages'){
          windowTest.requests.push({...payload});
          return previous(payload.sessionId,77,payload.beforeIndex);
        }
        const result=await original(type,payload);
        if(type==='getProjectChat'){
          Object.assign(result,latest()); result.state={...result.state,revision:77};
          if(windowTest.holdLatest)await new Promise(resolve=>{windowTest.release=()=>resolve();});
        }
        return result;
      };
      Object.assign(state,latest()); renderMessages({fullReset:true,forceScroll:true});
    });
    async function inspectWindow(owner) {
      return page.evaluate(owner=>{
        const value=owner==='office'?state:RNAssistantProjectChats.snapshot();
        return {start:value.messageStartIndex,count:value.messages.length,
          rendered:document.querySelectorAll('#messages > .message').length};
      },owner);
    }
    for(const owner of ['office','project']) {
      if(owner==='project') {
        await page.evaluate(()=>RNAssistantProjectChats.setScope('projects'));
        await page.waitForFunction(()=>RNAssistantProjectChats.snapshot()?.messageStartIndex===920);
      }
      for(let pageIndex=0;pageIndex<5;pageIndex++) {
        await page.getByRole('button',{name:'Показать предыдущие сообщения',exact:true}).click();
        const expected=920-(pageIndex+1)*80;
        await page.waitForFunction(({owner,expected})=>
          (owner==='office'?state:RNAssistantProjectChats.snapshot()).messageStartIndex===expected,{owner,expected});
        const window=await inspectWindow(owner);
        assert.ok(window.count<=240 && window.rendered<=240,owner+' bounds data and rendered messages');
      }
      await page.getByRole('button',{name:'Вернуться к последним сообщениям',exact:true}).click();
      await page.waitForFunction(owner=>(owner==='office'?state:RNAssistantProjectChats.snapshot()).messageStartIndex===920,owner);
      assert.deepEqual(await inspectWindow(owner),{start:920,count:80,rendered:80},owner+' explicitly reloads latest');
      assert.equal(await page.getByRole('button',{name:'Вернуться к последним сообщениям',exact:true}).count(),0);
    }
    // A delayed latest-page reply must not restore Workspace after switching to Office.
    await page.getByRole('button',{name:'Показать предыдущие сообщения',exact:true}).click();
    await page.waitForFunction(()=>RNAssistantProjectChats.snapshot().messageStartIndex===840);
    await page.evaluate(()=>{windowTest.holdLatest=true;});
    await page.getByRole('button',{name:'Показать предыдущие сообщения',exact:true}).click();
    await page.waitForFunction(()=>RNAssistantProjectChats.snapshot().messageStartIndex===760);
    // The bounded tail is still complete after two pages; load a third to expose the jump.
    await page.getByRole('button',{name:'Показать предыдущие сообщения',exact:true}).click();
    await page.waitForFunction(()=>RNAssistantProjectChats.snapshot().messageStartIndex===680);
    await page.getByRole('button',{name:'Вернуться к последним сообщениям',exact:true}).click();
    await page.waitForFunction(()=>!!windowTest.release);
    await page.evaluate(async()=>{
      RNAssistantProjectChats.setScope('office');
      windowTest.holdLatest=false; windowTest.release();
      await new Promise(resolve=>setTimeout(resolve,50));
    });
    assert.equal(await page.evaluate(()=>RNAssistantWorkspaceChat.owner()),'office','late workspace history cannot remount its owner');
    assert.equal(await page.evaluate(()=>RNAssistantProjectChats.snapshot().messageStartIndex),680,'late latest reply is discarded after navigation');
    assert.deepEqual(errors,[]);
    console.log('PASS shared history: Office/Workspace data + DOM <= 240, latest returns 80, stale reply preserves owner');
  } finally { await browser.close(); server.kill(); }
})().catch(error=>{console.error(error);process.exitCode=1;});
