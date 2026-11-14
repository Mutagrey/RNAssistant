'use strict';
const assert = require('node:assert/strict');
const {withPreviewPage} = require('./preview-session');

withPreviewPage(async ({page, errors}) => {
  page.setDefaultTimeout(7000);
  await page.setViewportSize({width:1100, height:500});
  await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
  const targets = await page.evaluate(async () => {
    const catalog = await send('getProjectChats', {}), project = catalog.projects.find(p => p.available && p.chats.length);
    return [{source:'office', sessionId:'preview-chat'}, {source:'workspace', workspaceId:project.workspaceId, sessionId:project.chats[0].Id}];
  });
  for (const target of targets) {
    await page.evaluate(target => {
      window.refreshTarget = target;
      RNAssistantDockWorkspace.close(RNAssistantConversationPanel.key(target));
    }, target);
    await page.waitForFunction(() => !RNAssistantConversationPanel.find(refreshTarget));
    await page.evaluate(target => {
      window.originalReadSend = send;
      window.send = (name, payload, progress) => {
        if ((name === 'getChatState' && payload.chatId === target.sessionId) ||
            (name === 'getProjectChat' && payload.workspaceId === target.workspaceId && payload.sessionId === target.sessionId))
          return Promise.reject(Error('История временно недоступна'));
        return originalReadSend(name, payload, progress);
      };
      RNAssistantConversationPanel.open(target);
    }, target);
    await page.waitForFunction(() => RNAssistantConversationPanel.find(refreshTarget)?.loadError);
    assert.deepEqual(await page.evaluate(() => {
      const panel = RNAssistantConversationPanel.find(refreshTarget);
      return [panel.node('chatLoadStatusText').textContent, panel.node('chatLoadRetry').hidden,
        panel.node('messages').querySelectorAll('.chat-empty').length, panel.hasSnapshot];
    }), ['История временно недоступна', false, 0, false]);
    await page.evaluate(() => { window.send = originalReadSend; RNAssistantConversationPanel.find(refreshTarget).node('chatLoadRetry').click(); });
    await page.waitForFunction(() => RNAssistantConversationPanel.find(refreshTarget)?.hasSnapshot);
    assert.deepEqual(await page.evaluate(async () => {
      const panel = RNAssistantConversationPanel.find(refreshTarget), box = panel.node('messages');
      panel.node('chatInput').value = 'Не терять мой черновик'; panel.node('chatInput').dispatchEvent(new Event('input'));
      // A real retained disclosure/reading anchor in the borrowed snapshot.
      const disclosure = box.querySelector('details'); if (disclosure) disclosure.open = true;
      box.dispatchEvent(new WheelEvent('wheel', {deltaY:-100}));
      box.scrollTop = Math.min(80, box.scrollHeight - box.clientHeight); box.dispatchEvent(new Event('scroll'));
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
      const nodes = Array.from(box.childNodes), snapshot = panel.snapshot(), draft = panel.draft(), top = box.scrollTop;
      const original = send;
      window.send = (name, payload, progress) => /^(getChatState|getProjectChat)$/.test(name)
        ? Promise.reject(Error('Ошибка refresh')) : original(name, payload, progress);
      try { await panel.refresh(); } finally { window.send = original; }
      return [panel.snapshot() === snapshot, panel.draft() === draft, panel.node('chatInput').value,
        nodes.every((node, index) => box.childNodes[index] === node), Math.abs(box.scrollTop - top) < 2,
        !disclosure || disclosure.open, panel.node('chatLoadStatusText').textContent, panel.node('chatLoadRetry').hidden];
    }), [true, true, 'Не терять мой черновик', true, true, true, 'Ошибка refresh', false], target.source);
    await page.setViewportSize({width:420, height:700});
    await page.evaluate(() => document.documentElement.style.zoom = '1.25');
    assert.equal(await page.evaluate(() => {
      const panel = RNAssistantConversationPanel.find(refreshTarget), status = panel.node('chatLoadStatus'), button = panel.node('chatLoadRetry');
      const bounds = panel.element.getBoundingClientRect(), rect = button.getBoundingClientRect();
      return status.scrollWidth <= status.clientWidth + 1 && rect.left >= bounds.left && rect.right <= bounds.right;
    }), true, 'retry stays inside narrow panel at 125%');
    if (process.env.RNA_CHAT_REVIEW) await page.screenshot({path:process.env.RNA_CHAT_REVIEW + '-' + target.source + '.png'});
    await page.evaluate(() => document.documentElement.style.zoom = '1');
    await page.setViewportSize({width:1100, height:500});
    await page.evaluate(() => RNAssistantConversationPanel.find(refreshTarget).node('chatLoadRetry').click());
    await page.waitForFunction(() => !RNAssistantConversationPanel.find(refreshTarget).loadError);
  }
  assert.deepEqual(errors, []);
  console.log('PASS initial/refresh failures: Office + Workspace retry, snapshot/draft identity, zero history nodes replaced, scroll/disclosure preserved');
}).catch(error => { console.error(error); process.exitCode = 1; });
