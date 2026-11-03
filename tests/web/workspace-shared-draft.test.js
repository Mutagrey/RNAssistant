'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
const root = path.resolve(__dirname, '../..');
(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], {cwd: root});
  const address = await new Promise(resolve => server.stdout.on('data', data => {
    const match = data.toString().match(/http:\/\/127\.0\.0\.1:\d+\//); if (match) resolve(match[0]);
  }));
  const browser = await chromium.launch({headless:true, ...(process.env.BROWSER_EXECUTABLE ? {executablePath:process.env.BROWSER_EXECUTABLE} : {})});
  try {
    const page = await browser.newPage({viewport:{width:1440,height:900}}), errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(address + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(async () => {
      await send('getPluginChat',{pluginId:'data-templates'});
      const catalog = await send('getProjectChats',{});
      const project = catalog.projects.find(p => !p.pluginId && p.available && p.chats.length);
      const plugin = catalog.projects.find(p => p.pluginId && p.available && p.chats.length);
      window.attachmentTargets = [project,plugin].map(p => ({source:'workspace',workspaceId:p.workspaceId,sessionId:p.chats[0].Id}));
      attachmentTargets.forEach((target,i) => RNAssistantConversationPanel.open(target, i ? {direction:'right'} : {}));
      const original = send, leases = new Map(); window.attachmentCalls = []; window.pauseAttachment = true;
      window.send = async function(type,payload,progress) {
        if (!/^(beginChatResourceUpload|completeChatResourceUpload|discardChatResourceDraft|cancelChatResourceUpload)$/.test(type))
          return original(type,payload,progress);
        attachmentCalls.push({type,payload});
        if (type === 'beginChatResourceUpload') {
          const leaseId = String(leases.size + 1).padStart(64,'0'); leases.set(leaseId,payload);
          return {leaseId,byteLength:payload.byteLength,maxChunkBytes:262144,url:'https://rnassistant.local-resource/v1/upload/'+leaseId};
        }
        if (type === 'completeChatResourceUpload') {
          const value = leases.get(payload.leaseId);
          if (pauseAttachment) { pauseAttachment=false; await new Promise(resolve => { window.finishAttachment=resolve; }); }
          return {resource:{Id:'draft-'+payload.leaseId,FileName:value.fileName,Kind:'text',Size:value.byteLength,Status:'ready'}};
        }
        return {deleted:true,closed:true};
      };
      RNAssistantResourceUpload.write = async (lease,file) => { window.attachmentBytes = await file.text(); };
    });
    await page.waitForFunction(() => attachmentTargets.every(t => { const p=RNAssistantConversationPanel.find(t); return p && !p.loading; }));
    await page.evaluate(() => {
      const p=RNAssistantConversationPanel.find(attachmentTargets[0]);
      p.node('chatInput').value='Текст проекта'; p.node('chatInput').dispatchEvent(new Event('input'));
      const transfer=new DataTransfer(); transfer.items.add(new File(['original bytes'],'source.txt',{type:'text/plain'}));
      p.element.dispatchEvent(new DragEvent('drop',{bubbles:true,dataTransfer:transfer}));
    });
    await page.waitForFunction(() => !!window.finishAttachment);
    await page.evaluate(() => {
      const p=RNAssistantConversationPanel.find(attachmentTargets[1]); p.focus();
      p.node('chatInput').value='Черновик плагина'; p.node('chatInput').dispatchEvent(new Event('input'));
      finishAttachment();
    });
    await page.waitForFunction(() => (readWorkspaceDraft(attachmentTargets[0])?.attachments.length||0) === 1);
    assert.deepEqual(await page.evaluate(() => attachmentTargets.map(t => {
      const p=RNAssistantConversationPanel.find(t); return [p.node('chatInput').value,p.node('attachmentDrafts').children.length];
    })), [['Текст проекта',1],['Черновик плагина',0]], 'late upload changes only its exact draft and shared card');
    await page.evaluate(() => {
      const p=RNAssistantConversationPanel.find(attachmentTargets[1]);
      const transfer=new DataTransfer(); transfer.items.add(new File(['plugin bytes'],'plugin.txt',{type:'text/plain'}));
      p.node('attachmentFileInput').files=transfer.files; p.node('attachmentFileInput').dispatchEvent(new Event('change'));
    });
    await page.waitForFunction(() => (readWorkspaceDraft(attachmentTargets[1])?.attachments.length||0) === 1);
    assert.deepEqual(await page.evaluate(() => attachmentCalls.filter(c=>c.type==='beginChatResourceUpload').map(c=>[c.payload.workspaceId,c.payload.chatId])),
      await page.evaluate(() => attachmentTargets.map(t=>[t.workspaceId,t.sessionId])), 'project and plugin use scoped common upload');
    assert.equal(await page.evaluate(() => attachmentTargets.every(t=>!RNAssistantConversationPanel.find(t).node('attachFileButton').hidden)),true);
    await page.evaluate(() => {
      const p=RNAssistantConversationPanel.find(attachmentTargets[0]);
      p.node('attachmentDrafts').querySelector('button').click();
    });
    await page.waitForFunction(() => readWorkspaceDraft(attachmentTargets[0]).attachments.length === 0);
    assert.equal(await page.evaluate(() => readWorkspaceDraft(attachmentTargets[1]).attachments.length),1,'removing one draft cannot clear the other');
    assert.equal(await page.evaluate(() => RNAssistantConversationPanel.find(attachmentTargets[1]).node('attachmentDrafts').getBoundingClientRect().height > 0),true,'attachment card is actually visible');
    await page.screenshot({path:'/tmp/rna-chat-attachments.png'});
    // The same mounted view renders durable queued input and uses addressed commands.
    await page.evaluate(async () => {
      const target=attachmentTargets[0], original=send;
      window.queueSnapshot=await original('getProjectChat',target); window.queueCalls=[];
      Object.assign(queueSnapshot.state,{status:'running',runId:'queue-run',canSteer:true,pendingInputs:[]});
      state.settings.RunningMessageDelivery='Queue';
      const copy=()=>JSON.parse(JSON.stringify(queueSnapshot));
      window.send=async(type,payload,progress)=>{
        if(payload.workspaceId!==target.workspaceId||payload.sessionId!==target.sessionId) return original(type,payload,progress);
        if(type==='getProjectChat') return copy();
        if(type==='submitProjectChat') {
          queueCalls.push({type,payload});
          const input=payload.input;
          queueSnapshot.state.pendingInputs=[{Id:input.requestId,Text:input.message,Revision:1,Delivery:'Queue',Status:'Pending',Attachments:[]}];
          queueSnapshot.state.revision++;
          queueSnapshot.inputReceipt={SessionId:target.sessionId,RequestId:input.requestId,InputId:input.requestId,
            Queued:true,AcceptedSequence:queueSnapshot.state.revision,AcceptedCursor:'queue-cursor'};
          return copy();
        }
        if(type==='updateProjectChatInput') {
          queueCalls.push({type,payload});
          const change=payload.inbox,item=queueSnapshot.state.pendingInputs[0];
          if(change.Action==='Edit') {item.Text=change.Text;item.Revision++;}
          if(change.Action==='Remove') queueSnapshot.state.pendingInputs=[];
          if(change.Action==='Resume') queueSnapshot.state.status='running';
          queueSnapshot.state.revision++;return copy();
        }
        return original(type,payload,progress);
      };
      const p=RNAssistantConversationPanel.find(target);p.focus();await p.refresh();
      p.node('chatInput').value='Следующее сообщение';p.node('chatInput').dispatchEvent(new Event('input'));
      await p.composer.dispatch('submit');
    });
    assert.equal(await page.evaluate(()=>queueCalls[0].payload.input.Enqueue),true,'running Queue uses durable queue admission');
    assert.equal(await page.evaluate(()=>RNAssistantConversationPanel.find(attachmentTargets[0]).node('chatInput').value),'','queued receipt clears the accepted draft without inventing a run ID');
    assert.equal(await page.evaluate(()=>RNAssistantConversationPanel.find(attachmentTargets[0]).node('messages').querySelectorAll('.inbox-message').length),1,
      JSON.stringify(await page.evaluate(()=>({state:RNAssistantConversationPanel.find(attachmentTargets[0]).snapshot().state,
        text:RNAssistantConversationPanel.find(attachmentTargets[0]).node('messages').textContent,errors:RNAssistantConversationPanel.find(attachmentTargets[0]).node('inboxStatus').textContent}))));
    const queued=page.getByRole('article',{name:'Сообщение в очереди'});
    await queued.getByLabel('Действия с сообщением в очереди').click();
    await queued.getByRole('button',{name:'Редактировать',exact:true}).click();
    await queued.getByLabel('Текст сообщения в очереди').fill('Исправленное сообщение');
    await queued.getByRole('button',{name:'Сохранить',exact:true}).click();
    await queued.getByText('Исправленное сообщение',{exact:true}).waitFor();
    assert.deepEqual(await page.evaluate(()=>queueCalls.at(-1).payload.inbox),await page.evaluate(()=>({SessionId:attachmentTargets[0].sessionId,
      Action:'Edit',InputId:queueCalls[0].payload.input.requestId,ExpectedRevision:1,Text:'Исправленное сообщение'})));
    await page.evaluate(async()=>{queueSnapshot.state.status='cancelled';queueSnapshot.state.revision++;
      await RNAssistantConversationPanel.find(attachmentTargets[0]).refresh();});
    await page.getByRole('button',{name:'Продолжить очередь',exact:true}).click();
    await page.waitForFunction(()=>queueCalls.at(-1).payload.inbox.Action==='Resume');
    assert.equal(await page.evaluate(()=>RNAssistantConversationPanel.find(attachmentTargets[1]).node('chatInput').value),'Черновик плагина','queue commands leave the neighboring draft unchanged');
    assert.deepEqual(errors,[]);
    console.log('PASS shared draft: common upload, exact late reply, cards, independent drafts; Queue receipt, shared editing and explicit resume');
  } finally { await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode=1; });
