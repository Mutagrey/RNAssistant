'use strict';
// Same ready fixture and operations on either side of S1b. Not an Office benchmark.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {spawn}=require('node:child_process'),{chromium}=require('playwright');
(async()=>{
  const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd:path.resolve(__dirname,'../..')});server.stderr.resume();
  let browser;
  try{
    const address=await new Promise((resolve,reject)=>{server.once('error',reject);server.stdout.on('data',bytes=>{const match=/RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(bytes));if(match)resolve(match[1]);});});
    browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
    const runs=[];
    for(let i=0;i<3;i++){
      const page=await browser.newPage({viewport:{width:1280,height:900}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
      await page.addInitScript(()=>{
        if(window.top!==window)return;
        // S1b's saved single-view preset matches the pre-Dockview initial Inspector state.
        localStorage.setItem('rnassistant.ui.dock.s1.v1',JSON.stringify({version:1,layout:{grid:{root:{type:'branch',data:[{type:'leaf',data:{views:['conversation'],activeView:'conversation',id:'1'},size:1005}],size:832},width:1005,height:832,orientation:'HORIZONTAL'},panels:{conversation:{id:'conversation',contentComponent:'conversation',title:'Чат',renderer:'always',minimumHeight:120,minimumWidth:150}},activeGroup:'1'}}));
      });
      await page.goto(address+'preview/web/?scenario=ready');await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
      const cold=await page.evaluate(()=>performance.now());
      await page.evaluate(()=>{window.measureCalls={};const original=send;send=function(type,...args){measureCalls[type]=(measureCalls[type]||0)+1;return original(type,...args)};window.measureNodes=[document.getElementById('messages'),document.getElementById('chatForm')];});
      const cycles=await page.evaluate(()=>{const before=document.querySelectorAll('*').length,start=performance.now();for(let j=0;j<20;j++){RNAssistantChatInspector.setOpen(true);RNAssistantChatInspector.setOpen(false)}return{ms:performance.now()-start,before,after:document.querySelectorAll('*').length};});
      await page.locator('#chatInput').fill('Проверка черновика');
      await page.evaluate(()=>RNAssistantProjectChats.open('preview-data-workspace','preview-project-review'));
      await page.waitForFunction(()=>document.getElementById('chatForm').dataset.owner==='project');
      const sample=await page.evaluate(()=>({calls:measureCalls,nodes:document.querySelectorAll('*').length,identity:measureNodes[0]===document.getElementById('messages')&&measureNodes[1]===document.getElementById('chatForm'),heap:performance.memory?.usedJSHeapSize}));
      assert.equal(sample.identity,true);assert.equal(sample.calls.getProjectChats,1);assert.equal(sample.calls.getProjectChat,1);assert.deepEqual(errors,[]);
      runs.push({cold,cycles,...sample,errors});await page.close();
    }
    if(process.env.DOCK_PERF_JSON)fs.writeFileSync(process.env.DOCK_PERF_JSON,JSON.stringify(runs,null,2)+'\n');
    console.log(JSON.stringify(runs,null,2));
  }finally{if(browser)await browser.close();server.kill();}
})().catch(e=>{console.error(e);process.exitCode=1;});
