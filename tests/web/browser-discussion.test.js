'use strict';
const assert = require('node:assert/strict');
const { withPreviewPage } = require('./preview-session');

(async () => withPreviewPage(async ({ page, errors }) => {
  page.setDefaultTimeout(8000);
  await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
  await page.evaluate(async () => {
    const original = send;
    const catalog = await original('createProjectWorkspace', { rootPath: '/tmp/Browser discussion' });
    const project = catalog.projects.find(p => p.rootPath === '/tmp/Browser discussion');
    const probe = window.discussionProbe = { calls: [], pages: new Map(), projectId: project.workspaceId, late: false, pending: null };
    window.send = (type, p, ...rest) => {
      probe.calls.push({ type, p: structuredClone(p) });
      if (type === 'createProjectChat' && probe.late) return original(type, p, ...rest).then(result => new Promise(resolve => { probe.pending = () => resolve(result); }));
      if (type === 'readBrowserSource') return Promise.resolve({ files: ['index.html'], path: 'index.html', text: '<p>Page</p>', offset: 0, totalCharacters: 11, requestGeneration: p.requestGeneration });
      if (type !== 'browserCommand') return original(type, p, ...rest);
      let value = probe.pages.get(p.browserTabId);
      if (p.action === 'Open') {
        value = { browserTabId: p.browserTabId, operationId: p.operationId, sessionId: 'session-' + p.browserTabId, revision: 1, documentGeneration: 1,
          ownerScope: p.ownerScope, chatBound: !!p.ownerScope.chatId && p.bindChat !== false, url: p.locator, title: 'Страница ' + p.browserTabId, status: 'Unavailable',
          target: { kind: 'WorkspaceHtmlProject', state: 'Ready', ownerScope: p.ownerScope, workspaceId: p.ownerScope.scopeId, displayLocator: p.locator,
            capabilities: { source: true } } };
        probe.pages.set(p.browserTabId, value);
      }
      if (p.action === 'Attach') value.attached = true;
      if (p.action === 'Bind') {
        if (p.generation !== value.documentGeneration || p.binding.scopeId !== value.ownerScope.scopeId) throw new Error('Stale or foreign Bind');
        value.chatBound = true; value.ownerScope = p.binding; value.target.ownerScope = p.binding; value.documentGeneration++;
      }
      if (p.action === 'Close') value.status = 'Closed';
      value.revision++; return Promise.resolve(structuredClone(value));
    };
    RNAssistantBrowser.open('/projects/' + project.workspaceId + '/source/index.html', { kind: 'Workspace', scopeId: project.workspaceId }, { browserTabId: 'A' });
  });
  const browser = id => page.locator('[data-browser-tab-id="' + id + '"]:visible');
  async function toolbar(id, name) {
    await browser(id).locator('.preview-surface-toolbar').waitFor({ state: 'visible' });
    const button = browser(id).getByRole('button', { name, exact: true });
    if (!await button.isVisible()) await browser(id).getByRole('button', { name: 'Инструменты страницы', exact: true }).click();
    await button.click();
  }
  await browser('A').locator('.browser-document-source').waitFor({ state: 'visible' });
  assert.equal(await page.getByText(/Без привязки к чату/).count(), 0);
  await toolbar('A', 'Обсудить в чате рядом');
  await page.waitForFunction(() => discussionProbe.pages.get('A').chatBound && RNAssistantConversationPanel.selected()?.target.sessionId === discussionProbe.pages.get('A').ownerScope.chatId);
  const first = await page.evaluate(() => discussionProbe.pages.get('A').ownerScope.chatId);
  const selected = page.locator('#chatSessionList [role="treeitem"][aria-selected="true"]');
  assert.ok((await selected.getAttribute('data-tree-key')).endsWith(':' + first));
  await page.screenshot({ path: 'docs/ui/review/browser-discussion-wide.png' });
  await page.evaluate(() => RNAssistantDockWorkspace.openTarget({ view: 'browser', browserTabId: 'A', ownerScope: discussionProbe.pages.get('A').ownerScope,
    locator: discussionProbe.pages.get('A').url }));
  await toolbar('A', 'Обсудить в чате рядом');
  assert.equal(await page.evaluate(() => discussionProbe.calls.filter(c => c.type === 'createProjectChat').length), 1, 'bound click opens its existing chat');

  // The actual tree action creates another chat without changing either Browser binding.
  await page.locator('#newChatButton').click();
  await page.getByRole('menuitem', { name: 'Новый чат', exact: true }).click();
  await page.waitForFunction(first => RNAssistantConversationPanel.selected()?.target.source === 'workspace' && RNAssistantConversationPanel.selected().target.sessionId !== first, first);
  const other = await page.evaluate(() => RNAssistantConversationPanel.selected().target.sessionId);
  assert.equal(await page.evaluate(() => discussionProbe.pages.get('A').ownerScope.chatId), first);
  await page.evaluate(() => RNAssistantBrowser.open('/projects/' + discussionProbe.projectId + '/source/index.html',
    { kind: 'Workspace', scopeId: discussionProbe.projectId }, { browserTabId: 'B' }));
  await toolbar('B', 'Обсудить в чате рядом');
  await page.waitForFunction(() => discussionProbe.pages.get('B').chatBound);
  const second = await page.evaluate(() => discussionProbe.pages.get('B').ownerScope.chatId);
  assert.notEqual(second, first); assert.notEqual(second, other);
  await page.evaluate(() => RNAssistantDockWorkspace.openTarget({ view: 'browser', browserTabId: 'A', ownerScope: discussionProbe.pages.get('A').ownerScope, locator: discussionProbe.pages.get('A').url }));
  await toolbar('A', 'Действия страницы');
  await page.getByRole('menuitem', { name: 'Связать с другим чатом…', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Новый чат', exact: true }).click();
  await page.waitForFunction(other => discussionProbe.pages.get('A').ownerScope.chatId === other, other);
  assert.equal(await page.evaluate(() => discussionProbe.pages.get('B').ownerScope.chatId), second, 'rebind changes only the addressed tab');
  assert.ok((await selected.getAttribute('data-tree-key')).endsWith(':' + other));

  // Reveal must also work after collapsing an already selected node and filtering.
  await page.getByRole('button', { name: 'Свернуть Browser discussion', exact: true }).click();
  await toolbar('A', 'Обсудить в чате рядом');
  await page.waitForFunction(() => document.querySelector('[data-tree-key="project:' + discussionProbe.projectId + '"]').getAttribute('aria-expanded') === 'true');
  await page.evaluate(() => RNAssistantWorkspaceNavigation.focusSearch());
  await page.locator('#chatSearchInput').fill('no-matching-chat');
  await page.evaluate(() => RNAssistantShellLayout.setNavigationOpen(false));
  await toolbar('A', 'Обсудить в чате рядом');
  await page.waitForFunction(() => document.getElementById('chatSearchInput').value === '' && RNAssistantShellLayout.isNavigationOpen());
  assert.ok((await selected.getAttribute('data-tree-key')).endsWith(':' + other));
  await page.setViewportSize({ width: 420, height: 800 });
  await page.evaluate(() => { document.documentElement.style.zoom = '1.25'; });
  await page.evaluate(() => RNAssistantDockWorkspace.openTarget({ view: 'browser', browserTabId: 'A', ownerScope: discussionProbe.pages.get('A').ownerScope, locator: discussionProbe.pages.get('A').url }));
  await toolbar('A', 'Обсудить в чате рядом');
  await page.waitForFunction(() => RNAssistantShellLayout.isNavigationOpen());
  assert.ok(await selected.isVisible(), 'narrow discussion reveals the exact chat in the navigation drawer');
  assert.ok((await selected.getAttribute('data-tree-key')).endsWith(':' + other));
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  assert.equal(await page.evaluate(() => RNAssistantConversationPanel.selected()?.target.sessionId), other, 'late page opens cannot steal the selected chat');
  assert.ok((await selected.getAttribute('data-tree-key')).endsWith(':' + other));
  await page.screenshot({ path: 'docs/ui/review/browser-discussion-narrow.png' });

  await page.setViewportSize({ width: 1280, height: 900 }); await page.evaluate(() => { document.documentElement.style.zoom = ''; });
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await page.evaluate(() => {
    discussionProbe.late = true;
    RNAssistantBrowser.open('/projects/' + discussionProbe.projectId + '/source/index.html', { kind: 'Workspace', scopeId: discussionProbe.projectId }, { browserTabId: 'late' });
  });
  await toolbar('late', 'Обсудить в чате рядом'); await page.waitForFunction(() => !!discussionProbe.pending);
  await page.evaluate(() => document.querySelector('[data-browser-tab-id="late"] [aria-label="Обсудить в чате рядом"]').click());
  assert.equal(await page.evaluate(() => discussionProbe.calls.filter(c => c.type === 'createProjectChat').length), 4, 'pending clicks cannot create duplicate chats');
  const before = await page.evaluate(() => RNAssistantConversationPanel.selected()?.target.sessionId);
  await page.evaluate(() => { RNAssistantDockWorkspace.close('browser:late'); discussionProbe.pending(); });
  await page.waitForFunction(() => discussionProbe.calls.some(c => c.type === 'browserCommand' && c.p.action === 'Close' && c.p.browserTabId === 'late'));
  assert.equal(await page.evaluate(() => discussionProbe.calls.filter(c => c.type === 'browserCommand' && c.p.action === 'Bind' && c.p.browserTabId === 'late').length), 0);
  assert.equal(await page.evaluate(() => RNAssistantConversationPanel.selected()?.target.sessionId), before);

  await page.evaluate(() => {
    const original = send;
    window.send = async (type, p, ...rest) => {
      if (type === 'createBrowserChat') {
        discussionProbe.documentCreates = (discussionProbe.documentCreates || 0) + 1;
        discussionProbe.documentRequest = structuredClone(p);
        const response = await original('createChat', { title: 'Обсуждение документа' });
        for (const chat of response.chats || []) if (chat.Id === response.activeChatId) chat.DocumentAuthorityId = 'source-document';
        return response;
      }
      return original(type, p, ...rest);
    };
    RNAssistantDockWorkspace.openTarget({ view: 'browser', browserTabId: 'document', locator: '/resources/html-fixture~1',
      ownerScope: { kind: 'Document', scopeId: 'source-document', chatId: 'preview-chat' }, chatBound: false });
  });
  await toolbar('document', 'Обсудить в чате рядом');
  await page.waitForFunction(() => discussionProbe.pages.get('document').chatBound && RNAssistantConversationPanel.selected()?.target.source === 'office');
  assert.equal(await page.evaluate(() => discussionProbe.documentRequest.ownerScope.scopeId), 'source-document');
  const documentChat = await page.evaluate(() => discussionProbe.pages.get('document').ownerScope.chatId);
  assert.ok((await selected.getAttribute('data-tree-key')).includes(documentChat));
  await toolbar('document', 'Обсудить в чате рядом');
  assert.equal(await page.evaluate(() => discussionProbe.documentCreates), 1);
  assert.deepEqual(errors, []);
  console.log('PASS Browser discussion: Workspace/document create/reopen, real tree create/reveal, isolated tab bindings, explicit rebind, filtered/narrow reveal and late-close/double-click guards');
}))().catch(error => { console.error(error); process.exitCode = 1; });
