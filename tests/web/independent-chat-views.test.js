'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
const root = path.resolve(__dirname, '../..');
(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], {cwd: root});
  const address = await new Promise(resolve => server.stdout.on('data', data => {
    const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(data)); if (match) resolve(match[1]);
  }));
  const browser = await chromium.launch({headless: true, ...(process.env.BROWSER_EXECUTABLE ? {executablePath: process.env.BROWSER_EXECUTABLE} : {})});
  try {
    const page = await browser.newPage({viewport: {width: 1440, height: 900}}), errors = [];
    page.setDefaultTimeout(7000);
    page.on('pageerror', error => { errors.push(error.message); console.error(error.message); });
    async function verifyBounds() {
      try { await page.waitForFunction(() => {
        let shown = 0, valid = true;
        RNAssistantConversationPanel.each(view => {
          if (!view.element.isConnected || view.element.inert) return;
          shown++;
          const body = view.element.closest('.dv-content-container');
          if (!body || view.element.closest('.dv-render-overlay')) { valid = false; return; }
          const limit = body.getBoundingClientRect(), group = body.closest('.dv-groupview').getBoundingClientRect();
          if (limit.right > group.right + 1 || limit.bottom > group.bottom + 1 || group.right > innerWidth + 1) valid = false;
          [view.element, view.node('messages'), view.node('chatForm')].forEach(node => {
            const rect = node.getBoundingClientRect();
            if (rect.left < limit.left - 1 || rect.right > limit.right + 1 || rect.top < limit.top - 1 || rect.bottom > limit.bottom + 1) valid = false;
          });
        });
        return shown > 0 && valid;
      }); } catch (error) {
        console.error('Chat bounds', await page.evaluate(() => {
          const values=[]; RNAssistantConversationPanel.each(view=>{
            if (!view.element.isConnected || view.element.inert) return;
            const nodes=[view.element.closest('.dv-content-container'),view.element,view.node('messages'),view.node('chatForm')];
            values.push({target:view.target,nodes:nodes.map(node=>node?{name:node.className,...node.getBoundingClientRect().toJSON()}:null)});
          }); return values;
        }));
        await page.screenshot({path:'/tmp/rnassistant-chat-bounds-failure.png'}); throw error;
      }
    }
    await page.goto(address + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    const first = page.locator('.conversation-panel').filter({has: page.locator('[data-chat-node="messages"] .message')}).first();
    await first.locator('[data-chat-node="chatInput"]').fill('Первый отдельный черновик');
    await page.evaluate(() => RNAssistantConversationPanel.open({source:'office',sessionId:'preview-summary'}, {direction:'right'}));
    await page.waitForFunction(() => { const p = RNAssistantConversationPanel.office('preview-summary'); return p && !p.loading; });
    const second = page.locator('.conversation-panel').filter({has: page.locator('[data-chat-node="messages"] .chat-empty')}).last();
    await second.locator('[data-chat-node="chatInput"]').fill('Второй отдельный черновик');
    await verifyBounds();
    await page.evaluate(() => document.documentElement.style.zoom = '1.25');
    await verifyBounds();
    await page.evaluate(() => document.documentElement.style.zoom = '1');
    await verifyBounds();
    const views = await page.evaluate(() => {
      const a = RNAssistantConversationPanel.office('preview-chat'), b = RNAssistantConversationPanel.office('preview-summary');
      return {drafts: [a.node('chatInput').value,b.node('chatInput').value], sources:[a.snapshot().activeChatId,b.snapshot().activeChatId],
        visible: [a,b].map(v => v.node('messages').getBoundingClientRect().height > 0 && !v.element.closest('[inert]')),
        different: a.view !== b.view && a.composer !== b.composer && a.projection() !== b.projection()};
    });
    assert.deepEqual(views.drafts, ['Первый отдельный черновик','Второй отдельный черновик']);
    assert.deepEqual(views.sources, ['preview-chat','preview-summary']); assert.ok(views.different); assert.deepEqual(views.visible,[true,true]);
    await page.evaluate(async () => {
      const a = RNAssistantConversationPanel.office('preview-chat');
      await a.composer.setPreference('model','model-a');
      await a.composer.setPreference('reasoning',true);
    });
    assert.deepEqual(await page.evaluate(() => ['preview-chat','preview-summary'].map(id => {
      const p = RNAssistantConversationPanel.office(id); return [p.snapshot().activeChatModel,p.snapshot().activeChatReasoning,p.node('chatInput').value];
    })), [['model-a',true,'Первый отдельный черновик'],['preview-model',false,'Второй отдельный черновик']]);
    await page.evaluate(async () => {
      const a = RNAssistantConversationPanel.office('preview-chat'), b = RNAssistantConversationPanel.office('preview-summary');
      window.firstHistoryNode = a.node('messages').firstChild;
      await Promise.all([a.composer.dispatch('submit'),b.composer.dispatch('submit')]);
    });
    await page.waitForFunction(() => ['preview-chat','preview-summary'].every(id => {
      const p = RNAssistantConversationPanel.office(id);
      return p.snapshot().messages.some(m => m.Id && m.Id.startsWith('preview-reply-')) && !chatInbox(id).running;
    }));
    assert.deepEqual(await page.evaluate(() => ['preview-chat','preview-summary'].map(id => {
      const p = RNAssistantConversationPanel.office(id); return [p.node('chatInput').value,p.snapshot().messages.filter(m=>m.Role==='user').at(-1).Content];
    })), [['','Первый отдельный черновик'],['','Второй отдельный черновик']]);
    assert.equal(await page.evaluate(() => RNAssistantConversationPanel.office('preview-chat').node('messages').firstChild === firstHistoryNode),true,
      'background stream and terminal delivery retain attached history');
    await page.evaluate(() => {
      const view=RNAssistantConversationPanel.office('preview-chat'); RNAssistantConversationPanel.open(view.target);
      const box=view.node('messages'); box.scrollTop=Math.min(120,box.scrollHeight-box.clientHeight);
      box.dispatchEvent(new Event('scroll')); window.retainedChatScroll=box.scrollTop;
    });
    assert.ok(await page.evaluate(()=>retainedChatScroll>0),'fixture has a scrolled transcript');
    await page.evaluate(async () => {
      const catalog = await send('getProjectChats',{}), entry = catalog.projects.find(p => p.available && p.chats.length);
      window.chatPairTarget = {source:'workspace',workspaceId:entry.workspaceId,sessionId:entry.chats[0].Id};
      RNAssistantConversationPanel.open(chatPairTarget);
    });
    await page.waitForFunction(() => { const p = RNAssistantConversationPanel.find(chatPairTarget); return p && !p.loading && p.snapshot().messages; });
    await page.evaluate(()=>RNAssistantConversationPanel.open({source:'office',sessionId:'preview-chat'}));
    await page.waitForFunction(()=>Math.abs(RNAssistantConversationPanel.office('preview-chat').node('messages').scrollTop-retainedChatScroll)<2);
    await page.evaluate(()=>RNAssistantConversationPanel.open(chatPairTarget));
    await page.evaluate(() => {
      const p = RNAssistantConversationPanel.find(chatPairTarget); p.node('chatInput').value = 'Рабочий проект'; p.node('chatInput').dispatchEvent(new Event('input'));
    });
    assert.equal(await page.evaluate(() => RNAssistantConversationPanel.office('preview-chat').node('chatInput').value),'');
    await page.evaluate(async () => {
      const original = send;
      window.send = function(type,payload,progress) {
        if(type==='setProjectChatModel') return Promise.reject(Error('Ошибка только этого чата'));
        return original(type,payload,progress);
      };
      try { await RNAssistantConversationPanel.find(chatPairTarget).composer.setPreference('model','unavailable-model'); }
      finally { window.send=original; }
    });
    assert.deepEqual(await page.evaluate(() => {
      const own = RNAssistantConversationPanel.find(chatPairTarget).node('inboxStatus');
      return [own.textContent,getComputedStyle(own).display!=='none',RNAssistantConversationPanel.office('preview-chat').error];
    }), ['Ошибка только этого чата',true,''], 'errors are visible and stay in the addressed view');
    // Workspace uses the same composer, including receipt-based draft clearing.
    await page.evaluate(async () => {
      const p = RNAssistantConversationPanel.find(chatPairTarget);
      await p.composer.dispatch('submit');
      window.workspaceRead = p.snapshot(); window.cancelCalls = [];
      const original = send; window.originalChatPairSend = original;
      window.send = function(type,payload,progress) { if (/cancel|stop/i.test(type)) cancelCalls.push(type); return original(type,payload,progress); };
      RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key(chatPairTarget));
    });
    await page.waitForFunction(() => !RNAssistantConversationPanel.find(chatPairTarget));
    await page.evaluate(() => RNAssistantConversationPanel.open(chatPairTarget));
    await page.waitForFunction(() => {
      const p = RNAssistantConversationPanel.find(chatPairTarget);
      return p && !p.loading && p.snapshot().state.status === 'completed';
    });
    assert.deepEqual(await page.evaluate(() => {
      const p = RNAssistantConversationPanel.find(chatPairTarget);
      return [p.node('chatInput').value, p.snapshot().messages.filter(m=>m.Role==='user').at(-1).Content, cancelCalls];
    }), ['', 'Рабочий проект', []], 'closing an admitted run releases only its UI');
    await page.evaluate(async () => {
      window.send = originalChatPairSend;
      const snapshot = await send('getPluginChat',{pluginId:'data-templates'});
      window.pluginPairTarget = {source:'workspace',workspaceId:snapshot.project.workspaceId,sessionId:snapshot.sessionId};
      RNAssistantConversationPanel.open(pluginPairTarget);
    });
    await page.waitForFunction(() => { const p = RNAssistantConversationPanel.find(pluginPairTarget); return p && !p.loading; });
    assert.equal(await page.evaluate(() => RNAssistantConversationPanel.find(pluginPairTarget).contextOwner),'plugin');
    await page.evaluate(async () => {
      const p = RNAssistantConversationPanel.find(pluginPairTarget);
      p.node('chatInput').value='Проверка общего чата плагина'; p.node('chatInput').dispatchEvent(new Event('input'));
      await p.composer.setPreference('model','plugin-model'); await p.composer.dispatch('submit');
    });
    await page.waitForFunction(() => RNAssistantConversationPanel.find(pluginPairTarget).snapshot().state.status==='completed');
    assert.equal(await page.evaluate(() => RNAssistantConversationPanel.find(pluginPairTarget).snapshot().messages.filter(m=>m.Role==='user').at(-1).Content),'Проверка общего чата плагина');
    await page.evaluate(() => {
      const p = RNAssistantConversationPanel.office('preview-chat'), message = p.snapshot().messages.find(m=>m.Role==='user');
      RNAssistantConversationPanel.open(p.target);
      p.node('chatInput').value='Черновик вне редактора'; p.node('chatInput').dispatchEvent(new Event('input'));
      p.composer.dispatch('messageAction',{name:'edit',message,index:0});
    });
    assert.equal(await page.evaluate(() => !!RNAssistantConversationPanel.office('preview-chat').composer.edit({source:'office',sessionId:'preview-chat'})),true);
    await page.evaluate(() => RNAssistantConversationPanel.office('preview-chat').composer.dispatch('cancelEdit'));
    assert.equal(await page.evaluate(() => RNAssistantConversationPanel.office('preview-chat').node('chatInput').value),'Черновик вне редактора');
    await page.evaluate(() => {
      const current = RNAssistantConversationPanel.office('preview-summary');
      RNAssistantConversationPanel.open(current.target);
      window.closedChat = current; window.closedSnapshot = current.snapshot();
      const original = send; window.originalChatPairSend = original;
      window.send = function(type,payload,progress) {
        if(type==='getChatState'&&payload.chatId==='preview-summary') return new Promise(resolve => window.releaseClosedRead=resolve);
        if(type==='setChatModel'&&payload.chatId==='preview-summary') return new Promise(resolve => window.releaseClosedPreference=resolve);
        return original(type,payload,progress);
      };
      window.closedRead = current.refresh(); window.closedPreference = current.composer.setPreference('model','late-model');
    });
    await page.waitForFunction(() => !!window.releaseClosedRead && !!window.releaseClosedPreference);
    await page.evaluate(() => {
      RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key(closedChat.target)); window.send=originalChatPairSend;
    });
    await page.waitForFunction(() => !closedChat.isCurrent());
    await page.evaluate(() => RNAssistantConversationPanel.open({source:'office',sessionId:'preview-summary'}));
    await page.waitForFunction(() => { const p = RNAssistantConversationPanel.office('preview-summary'); return p && !p.loading; });
    await page.evaluate(async () => {
      releaseClosedRead(Object.assign({},closedSnapshot,{activeChatModel:'late-read',sessionRevision:999}));
      releaseClosedPreference({chatId:'preview-summary',model:'late-model',sessionRevision:999});
      await Promise.all([closedRead,closedPreference]);
    });
    assert.equal(await page.evaluate(() => RNAssistantConversationPanel.office('preview-summary').snapshot().activeChatModel),'preview-model',
      'late read and preference ACK from a closed instance cannot change its replacement');
    assert.equal(await page.evaluate(() => closedChat.accept(closedSnapshot)),false);
    await page.evaluate(async () => {
      const b = RNAssistantConversationPanel.office('preview-summary'), w = RNAssistantConversationPanel.find(chatPairTarget);
      b.node('chatInput').value = 'Восстановить второй Office'; b.node('chatInput').dispatchEvent(new Event('input'));
      w.node('chatInput').value = 'Восстановить проект'; w.node('chatInput').dispatchEvent(new Event('input'));
      await RNAssistantChatDrafts.flushAll();
    });
    await page.evaluate(() => document.documentElement.style.zoom = '1.25');
    await page.setViewportSize({width:640,height:800});
    await page.evaluate(()=>RNAssistantConversationPanel.open(chatPairTarget));
    await verifyBounds();
    await page.screenshot({path:'/tmp/rnassistant-chat-tab-bounds-narrow.png'});
    await page.setViewportSize({width:1440,height:900});
    await verifyBounds();
    await page.evaluate(() => document.documentElement.style.zoom = '1');
    await verifyBounds();
    await page.waitForFunction(() => {
      const layout=JSON.parse(localStorage.getItem('rnassistant.ui.dock.s1.v1'));
      return layout && Object.values(layout.layout.panels).filter(p=>p.contentComponent==='conversation').length===4;
    });
    const workspaceTarget = await page.evaluate(() => chatPairTarget);
    await page.evaluate(() => {
      const key = 'rnassistant.ui.dock.s1.v1', saved = JSON.parse(localStorage.getItem(key));
      Object.values(saved.layout.panels).filter(panel => panel.contentComponent === 'conversation').forEach(panel => panel.renderer = 'always');
      localStorage.setItem(key, JSON.stringify(saved));
    });
    await page.reload();
    await page.waitForFunction(() => state.activeChatId && !state.initializePromise && RNAssistantConversationPanel.office('preview-summary'));
    await verifyBounds();
    assert.equal(await page.evaluate(() => {
      const roots=[];RNAssistantConversationPanel.each(view=>roots.push(view.element));
      return new Set(roots).size;
    }),4,'all exact target views survive reload; inactive DOM remains owned without overlay mounting');
    assert.deepEqual(await page.evaluate(workspaceTarget => [RNAssistantConversationPanel.office('preview-chat').node('chatInput').value,
      RNAssistantConversationPanel.office('preview-summary').node('chatInput').value,
      RNAssistantConversationPanel.find(workspaceTarget).node('chatInput').value], workspaceTarget),
      ['Черновик вне редактора','Восстановить второй Office','Восстановить проект'], 'hydration restores every mounted draft, including inactive targets');
    await page.evaluate(target => RNAssistantConversationPanel.open(target), workspaceTarget);
    await verifyBounds();
    await page.screenshot({path:'/tmp/rnassistant-chat-tab-bounds.png'});
    await page.evaluate(() => RNAssistantConversationPanel.each(p => RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key(p.target))));
    await page.waitForFunction(() => !document.querySelector('.conversation-panel'));
    await page.reload();
    await page.waitForFunction(() => !state.initializePromise && RNAssistantConversationPanel.office(state.activeChatId));
    assert.deepEqual(errors,[]);
    console.log('PASS independent Chat Views: Office pair, parallel delivery, history identity, Workspace/plugin, edit, close-run/late reply, tab bounds at 125%, old layout restore/all-closed');
  } finally { await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
