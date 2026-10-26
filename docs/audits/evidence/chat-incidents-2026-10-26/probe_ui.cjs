'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {spawn,execFileSync}=require('node:child_process');const {chromium}=require('playwright');
const root=path.resolve(__dirname,'../../../..');
const output=process.argv[2]||fs.mkdtempSync(path.join(require('node:os').tmpdir(),'rna-incident-ui-'));fs.mkdirSync(output,{recursive:true});
(async()=>{
 const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd:root});
 let browser;
 try {
  const address=await new Promise((resolve,reject)=>{server.stdout.on('data',chunk=>{const m=/RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(chunk));if(m)resolve(m[1]);});server.once('exit',code=>reject(Error('Server exited '+code)));});
  browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
  const report={head:execFileSync('git',['rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).trim(),qualification:'Production Web JS with synthetic preview bridge and snapshots, Chrome/macOS. No Office qualification.',observations:[]};
  async function page(){const p=await browser.newPage();await p.goto(address+'preview/web/?scenario=ready');await p.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);await p.evaluate(()=>RNAssistantProjectChats.setScope('projects'));await p.waitForFunction(()=>!!RNAssistantProjectChats.snapshot());return p;}
  let p=await page();
  await p.evaluate(()=>{const original=send;window.auditSend=null;window.send=async function(type,payload){if(type!=='submitProjectChat')return original(type,payload);window.auditSend={payload};const result=await original(type,payload);window.auditSend.result=result;await new Promise(resolve=>{window.auditSend.release=resolve;});return result;};});
  await p.locator('#chatInput').fill('AUDIT: accepted input');await p.locator('#chatInput').press('Enter');
  await p.waitForFunction(()=>{const s=RNAssistantProjectChats.snapshot();return s&&s.messages.some(m=>(m.Content||m.content)==='AUDIT: accepted input');});
  report.observations.push(await p.evaluate(()=>({case:'message-visible-before-submit-reply',input:document.querySelector('#chatInput').value,sendDisabled:document.querySelector('#sendButton').disabled,status:RNAssistantProjectChats.snapshot().state.status,messageInSnapshot:true})));
  await p.waitForFunction(()=>auditSend&&auditSend.release);await p.evaluate(()=>auditSend.release());await p.waitForFunction(()=>!document.querySelector('#chatInput').value);
  await p.close();
  p=await page();
  await p.evaluate(()=>{const original=send;window.send=async function(type,payload){const r=await original(type,payload);if(type==='getProjectChat'&&r&&r.state){r.state.status='running';r.state.runId='orphan-run';r.state.modelProgress=null;r.state.modelProgressUnavailable=true;r.state.runViewState=null;}return r;};});
  await p.evaluate(()=>RNAssistantProjectChats.refresh());await p.waitForFunction(()=>RNAssistantProjectChats.snapshot()?.state.modelProgressUnavailable===true);
  await p.locator('#chatInput').fill('Try after owner disappeared');
  report.observations.push(await p.evaluate(()=>({case:'saved-running-owner-unavailable',notice:document.querySelector('#workspaceChatNotice').textContent,sendDisabled:document.querySelector('#sendButton').disabled,ownerUnavailable:RNAssistantProjectChats.snapshot().state.modelProgressUnavailable,status:RNAssistantProjectChats.snapshot().state.status})));
  report.observations.push(await p.evaluate(()=>{const article=document.createElement('article');const message={Id:'usage-fixture',Role:'assistant',Content:'Read completed.',RunId:'audit-usage',PromptTokens:100,CompletionTokens:20,TotalTokens:120};appendAgentRunFooter(article,[{message,index:0}],null,true);return {case:'grouped-run-usage',footer:article.textContent,availableUsage:messageUsageText(message)};}));
  report.observations.push(await p.evaluate(()=>{const list=document.createElement('div');list.className='messages';const chat={project:{workspaceId:'audit'},sessionId:'audit',messages:[],messageStartIndex:0,messageTotalCount:0,state:{status:'running',runId:'run',modelProgress:{runId:'run',stepId:'step',attemptId:'attempt',message:'Created script.js.',sequence:1}}};RNAssistantWorkspaceChat.renderMessages(list,chat);const before=list.textContent;chat.state={status:'failed',runId:'run'};RNAssistantWorkspaceChat.renderMessages(list,chat);return {case:'unaccepted-model-text',before,after:list.textContent,wasVisible:before.includes('Created script.js.'),remainsAfterRejection:list.textContent.includes('Created script.js.')};}));
  report.observations.push(await p.evaluate(()=>{const original=RNAssistantNavigationTree.render;let renders=0;RNAssistantNavigationTree.render=function(...args){renders++;return original.apply(this,args);};const snapshot=RNAssistantProjectChats.snapshot();const first=document.querySelector('#projectChatTree').firstChild;for(let sequence=1;sequence<=30;sequence++)RNAssistantWorkspaceChat.acceptProgress({workspaceId:snapshot.project.workspaceId,sessionId:snapshot.sessionId,modelProgress:{runId:snapshot.state.runId,stepId:'step',attemptId:'attempt',sequence,message:'',reasoning:''}});RNAssistantNavigationTree.render=original;return {case:'empty-model-progress-render-work',progressEvents:30,treeRenders:renders,treeNodeReplaced:first!==document.querySelector('#projectChatTree').firstChild};}));
  await p.close();
  assert.equal(report.observations[0].input,'AUDIT: accepted input');assert.equal(report.observations[1].sendDisabled,true);assert.equal(report.observations[3].wasVisible,true);assert.equal(report.observations[3].remainsAfterRejection,false);
  fs.writeFileSync(path.join(output,'ui-report.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report,null,2));
 }finally{if(browser)await browser.close();server.kill('SIGTERM');}
})().catch(e=>{console.error(e);process.exitCode=1;});
