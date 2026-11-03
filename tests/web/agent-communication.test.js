'use strict';
const assert = require('node:assert/strict'), path = require('node:path'), {spawn} = require('node:child_process'), {chromium} = require('playwright');
(async () => {
 const server = spawn('python3', ['-B','prototypes/serve_ui.py','--port','0'], {cwd:path.resolve(__dirname,'../..')});
 const url = await new Promise(resolve => server.stdout.on('data', data => {const match=data.toString().match(/http:\/\/127\.0\.0\.1:\d+\//); if(match)resolve(match[0]);}));
 let browser;
 try {
 browser = await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
  const page = await browser.newPage({viewport:{width:1440,height:900}}), errors=[];
  page.on('pageerror', error=>errors.push(error.message));
  await page.goto(url+'preview/web/?scenario=ready');
  await page.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise&&RNAssistantConversationPanel.office('preview-chat'));
  assert.deepEqual(await page.evaluate(()=>{
   renderSettings({committed:true});
   const initial=readSettings().MaxAgentMessages;
   document.getElementById('maxAgentMessagesInput').value='7';
   const changed=readSettings().MaxAgentMessages;
   state.settings.MaxAgentMessages=0; renderSettings({committed:true});
   const disabled=readSettings().MaxAgentMessages;
   state.settings.MaxAgentMessages=initial;
   resetAgentSettingsToDefaults();
   return [initial,changed,disabled,readSettings().MaxAgentMessages,document.getElementById('maxAgentMessagesInput').min];
  }),[20,7,0,20,'0'],'agent budget defaults, zero, editing and reset use common settings');
  await page.evaluate(async()=>{
   const original=send; window.agentCalls=[]; window.holdPresence=false;
   window.send=async(type,payload,progress)=>{
    if(type==='setAgentChatPresence') {
     agentCalls.push({type,payload});
     if(payload.open&&holdPresence){holdPresence=false;await new Promise(resolve=>window.releaseAgentPresence=resolve);}
     return {registered:!!payload.open};
    }
    if(type==='getAgentCommunicationState'){
     if(window.holdAgentState){holdAgentState=false;await new Promise(resolve=>window.releaseAgentState=resolve);}
     return {enabled:true,chainId:'chain-ui',limit:20,sent:20,remaining:0,limitReached:true};
    }
    if(type==='continueAgentCommunication'){agentCalls.push({type,payload});return {chainId:'chain-ui',limit:20,sent:20,remaining:20,limitReached:false};}
    return original(type,payload,progress);
   };
   const component=RNAssistantConversationPanel.office('preview-chat'); component.communication.rebind();
   const plugin=await send('getPluginChat',{pluginId:'data-templates'});
   window.agentTarget={source:'workspace',workspaceId:plugin.project.workspaceId,sessionId:plugin.sessionId};
   const incoming={Id:'agent-incoming',Role:'user',Content:'Проверь расчёт и сообщи результат.',AgentSender:{Source:'workspace',WorkspaceId:agentTarget.workspaceId,ChatId:agentTarget.sessionId,Title:'Проверка расчётов'}};
   const node=renderMessageArticle(incoming,0,{workspace:false}); node.id='agent-test-bubble'; component.node('messages').append(node);
   const normal=renderMessageArticle({Id:'human-incoming',Role:'user',Content:'Обычный запрос'},1,{}); normal.id='human-test-bubble';component.node('messages').append(normal);
   window.agentTestMessage=incoming;
  });
  await page.waitForSelector('.agent-communication-status:not([hidden])');
  assert.equal(await page.locator('#agent-test-bubble .agent-message-origin').textContent(),'Агент · Проверка расчётов');
  const styles=await page.evaluate(()=>['agent-test-bubble','human-test-bubble'].map(id=>{const s=getComputedStyle(document.querySelector('#'+id+' .markdown'));return {bg:s.backgroundColor,padding:s.padding,border:s.borderRadius};}));
  assert.notEqual(styles[0].bg,styles[1].bg);assert.equal(styles[0].padding,styles[1].padding);assert.equal(styles[0].border,styles[1].border);
  await page.evaluate(()=>{window.holdAgentState=true;RNAssistantAgentCommunication.settingsChanged();});
  await page.waitForFunction(()=>!!window.releaseAgentState);
  await page.evaluate(async()=>{state.settings.MaxAgentMessages=0;RNAssistantAgentCommunication.settingsChanged();releaseAgentState();await new Promise(requestAnimationFrame);});
  assert.equal(await page.locator('.agent-communication-status:not([hidden])').count(),0,'zero hides continuation even after stale state response');
  await page.evaluate(()=>{state.settings.MaxAgentMessages=20;RNAssistantAgentCommunication.settingsChanged();});
  await page.waitForSelector('.agent-communication-status:not([hidden])');
  await page.locator('.agent-communication-status:not([hidden]) button').first().click();
  assert.equal(await page.evaluate(()=>agentCalls.filter(c=>c.type==='continueAgentCommunication').length),1);
  await page.evaluate(()=>{window.presenceBeforeReload=agentCalls.filter(c=>c.type==='setAgentChatPresence'&&c.payload.open&&c.payload.sessionId==='preview-chat').length;holdPresence=true;RNAssistantConversationPanel.office('preview-chat').communication.rebind();});
  await page.waitForFunction(()=>!!window.releaseAgentPresence);
  await page.evaluate(()=>{RNAssistantConversationPanel.office('preview-chat').communication.rebind();releaseAgentPresence();window.releaseAgentPresence=null;});
  await page.waitForFunction(()=>agentCalls.filter(c=>c.type==='setAgentChatPresence'&&c.payload.open&&c.payload.sessionId==='preview-chat').length===presenceBeforeReload+2);
  await page.evaluate(()=>{holdPresence=true;RNAssistantConversationPanel.open(agentTarget,{direction:'right'});});
  await page.waitForFunction(()=>!!window.releaseAgentPresence);
  await page.evaluate(()=>RNAssistantConversationPanel.closeDeleted(agentTarget));
  await page.evaluate(()=>releaseAgentPresence());
  await page.waitForFunction(()=>agentCalls.some(c=>c.type==='setAgentChatPresence'&&!c.payload.open&&c.payload.viewId===agentCalls.filter(c=>c.payload.open&&c.payload.sessionId===agentTarget.sessionId).slice(-1)[0].payload.viewId));
  await page.evaluate(()=>{const c=RNAssistantConversationPanel.office('preview-chat');const article=renderMessageArticle(agentTestMessage,0,{});article.id='agent-narrow-bubble';c.node('messages').append(article);document.documentElement.style.zoom='1.25';});
  await page.setViewportSize({width:640,height:800});
  const sizes=await page.evaluate(()=>{const n=document.querySelector('#agent-narrow-bubble');return {width:n.clientWidth,scroll:n.scrollWidth};});
  assert.ok(sizes.scroll<=sizes.width+1,'sender and bubble fit narrow zoomed view');
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  await page.locator('#agent-narrow-bubble').scrollIntoViewIfNeeded();
  await page.screenshot({path:'/tmp/rnassistant-agent-communication-narrow.png'});
  await page.evaluate(()=>document.documentElement.style.zoom='1');
  await page.setViewportSize({width:1440,height:900});
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
  await page.screenshot({path:'/tmp/rnassistant-agent-communication.png'});
  assert.deepEqual(errors,[]);
  console.log('PASS agent communication: shared bubble, settings, exact continuation, reload race, late mount cleanup, narrow zoom');
 } finally {if(browser)await browser.close();server.kill();}
})().catch(error=>{console.error(error);process.exitCode=1;});
