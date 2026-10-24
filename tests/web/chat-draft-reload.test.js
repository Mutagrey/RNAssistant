'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
const root = path.resolve(__dirname, '../..');

(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], {cwd:root});
  let serverError = '';
  server.stderr.on('data', chunk => { serverError += String(chunk).slice(0, 4000); });
  const address = await new Promise((resolve, reject) => {
    server.stdout.on('data', chunk => {
      const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(chunk));
      if (match) resolve(match[1]);
    });
    server.once('exit', code => reject(Error('Preview server exited: ' + code + ' ' + serverError)));
  });
  const browser = await chromium.launch({headless:true,
    ...(process.env.BROWSER_EXECUTABLE ? {executablePath:process.env.BROWSER_EXECUTABLE} : {})});
  try {
    const page = await browser.newPage(), errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const url = address + 'preview/web/?scenario=ready';
    await page.goto(url);
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.locator('#chatInput').fill('Черновик Office после reload');
    await page.evaluate(async () => {
      state.inboxSubmissions = state.inboxSubmissions || {};
      state.inboxSubmissions['preview-chat'] = { OperationId:'office-retry-id',
        Text:'Черновик Office после reload', Attachments:[], Delivery:'Steer', Answers:null };
      await RNAssistantChatDrafts.flushOffice('preview-chat');
    });

    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    await page.locator('#chatInput').waitFor({state:'visible'});
    const workspaceId = 'preview-plugin-workspace';
    await page.waitForFunction(id => !!RNAssistantWorkspaceChat.selectedSession(id), workspaceId);
    const sessionId = await page.evaluate(id => RNAssistantWorkspaceChat.selectedSession(id), workspaceId);
    await page.locator('#chatInput').fill('Черновик workspace после reload');
    await page.locator('#workspaceComposerPicker .composer-add-trigger').click();
    await page.locator('#workspacePlanButton').click();
    await page.locator('#workspaceReasoningToggle').click();
    const firstRequestId = await page.evaluate(async target => {
      const request = RNAssistantChatDrafts.prepareWorkspaceInput(target, 'Черновик workspace после reload',
        {Mode:'plan', ReasoningEnabled:true});
      await RNAssistantChatDrafts.flushWorkspace(target);
      return request.requestId;
    }, {workspaceId, sessionId});

    await page.reload();
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    assert.equal(await page.locator('#chatInput').inputValue(), 'Черновик Office после reload');
    assert.equal(await page.evaluate(() => state.inboxSubmissions['preview-chat'].OperationId), 'office-retry-id');
    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    await page.waitForFunction(target => RNAssistantWorkspaceChat.selectedSession(target.workspaceId) === target.sessionId,
      {workspaceId, sessionId});
    assert.equal(await page.locator('#chatInput').inputValue(), 'Черновик workspace после reload');
    assert.equal(await page.locator('#workspacePlanButton').getAttribute('aria-pressed'), 'true');
    assert.equal(await page.locator('#workspaceReasoningToggle').getAttribute('aria-pressed'), 'true');
    const restoredRequestId = await page.evaluate(target => RNAssistantChatDrafts.prepareWorkspaceInput(target,
      'Черновик workspace после reload', {Mode:'plan', ReasoningEnabled:true}).requestId,
    {workspaceId, sessionId});
    assert.equal(restoredRequestId, firstRequestId, 'uncertain send must reuse its persisted request ID');

    await page.evaluate(target => RNAssistantProjectChats.open(target.workspaceId, target.sessionId), {workspaceId, sessionId});
    await page.locator('#projectChatTitle').waitFor({state:'visible'});
    assert.equal(await page.locator('#chatInput').inputValue(), 'Черновик workspace после reload',
      'project and plugin use the same restored draft');
    await page.evaluate(async target => {
      const draft = RNAssistantChatDrafts.readWorkspace(target);
      RNAssistantChatDrafts.acknowledgeWorkspaceInput(target,
        RNAssistantChatDrafts.prepareWorkspaceInput(target, draft.text, {Mode:draft.mode, ReasoningEnabled:draft.reasoning}));
      RNAssistantChatDrafts.acceptWorkspace(target, draft.text, draft.version);
      await RNAssistantChatDrafts.flushWorkspace(target);
    }, {workspaceId, sessionId});
    await page.reload();
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    await page.waitForFunction(target => RNAssistantWorkspaceChat.selectedSession(target.workspaceId) === target.sessionId,
      {workspaceId, sessionId});
    assert.equal(await page.locator('#chatInput').inputValue(), '', 'accepted draft stays cleared after reload');
    await page.evaluate(() => sessionStorage.setItem('rnassistant-preview-chat-drafts', JSON.stringify([{
      Owner:'office', ChatId:'preview-chat', Text:'С файлом', ResourceDraftIds:['missing-stage'],
      PendingOfficeInput:{OperationId:'attachment-retry-id', Text:'С файлом',
        ResourceDraftIds:['missing-stage'], Delivery:'Steer'}
    }])));
    await page.reload();
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    assert.equal(await page.locator('#chatInput').inputValue(), 'С файлом');
    await page.locator('#attachmentDrafts').getByText('Недоступное вложение').waitFor();
    await page.locator('#attachmentStatus').getByText('проверьте их перед отправкой').waitFor();
    assert.equal(await page.evaluate(() => state.inboxSubmissions['preview-chat'].OperationId),
      'attachment-retry-id', 'unavailable staged file must not discard uncertain-send identity');
    assert.deepEqual(errors, [], 'no browser errors');
    const unavailable = await browser.newPage();
    const unavailableErrors = [];
    unavailable.on('pageerror', error => unavailableErrors.push(error.message));
    await unavailable.goto(address + 'preview/web/?scenario=draft-save-error');
    await unavailable.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await unavailable.locator('#chatInput').fill('Не отправлять без сохранённого ID');
    await unavailable.locator('#chatInput').press('Enter');
    await unavailable.locator('#inboxStatus').getByText('Не удалось подтвердить отправку').waitFor();
    assert.equal(await unavailable.evaluate(() => state.messages.filter(item => item.Role === 'user').length), 1,
      'failed protected save must prevent Office submission');
    assert.deepEqual(unavailableErrors, [], 'no browser errors on save failure');
    const unreadable = await browser.newPage();
    await unreadable.goto(address + 'preview/web/?scenario=draft-read-error');
    await unreadable.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await unreadable.locator('#chatInput').fill('Не отправлять без восстановления');
    await unreadable.locator('#chatInput').press('Enter');
    await unreadable.locator('#inboxStatus').getByText('Не удалось подтвердить отправку').waitFor();
    assert.equal(await unreadable.evaluate(() => state.messages.filter(item => item.Role === 'user').length), 1,
      'unreadable protected store must prevent a new send');
    console.log('PASS protected draft reload: Office text/retry ID, shared workspace text/options/retry ID and acknowledgement');
  } finally { await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
