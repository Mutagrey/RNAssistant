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
    await page.goto(address + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    await page.locator('#chatInput').waitFor({state:'visible'});
    const viewIdentity = await page.evaluate(() => {
      const view = document.querySelector('#workspaceChatView');
      view.dataset.testIdentity = crypto.randomUUID();
      return view.dataset.testIdentity;
    });
    assert.equal(await page.locator('#workspaceComposerPicker .composer-add-menu').getByRole('button', {name:'Файл с компьютера'}).count(), 0,
      'plugin hides project file upload');
    const workspaceId = 'preview-plugin-workspace';
    await page.waitForFunction(id => !!RNAssistantWorkspaceChat.selectedSession(id), workspaceId);
    const sessionId = await page.evaluate(id => RNAssistantWorkspaceChat.selectedSession(id), workspaceId);
    assert.ok(sessionId, 'plugin selects an existing workspace session');

    await page.locator('#chatInput').fill('Черновик из плагина');
    await page.locator('#workspaceComposerPicker .composer-add-trigger').click();
    await page.locator('#workspacePlanButton').click();
    await page.locator('#workspaceReasoningToggle').click();
    await page.evaluate(([workspace, session]) => RNAssistantProjectChats.open(workspace, session), [workspaceId, sessionId]);
    await page.locator('#projectChatTitle').waitFor({state:'visible'});
    assert.deepEqual(await page.evaluate(() => [document.querySelectorAll('#workspaceChatView').length,
      document.querySelectorAll('#chatForm').length, document.querySelector('#workspaceChatView').dataset.testIdentity,
      document.querySelector('#workspaceChatView').parentElement.id]), [1, 1, viewIdentity, 'projectChatLayout'],
      'project and plugin mount the same view and composer');
    assert.equal(await page.locator('#chatInput').inputValue(), 'Черновик из плагина');
    assert.equal(await page.locator('#workspacePlanButton').getAttribute('aria-pressed'), 'true');
    assert.equal(await page.locator('#workspaceReasoningToggle').getAttribute('aria-pressed'), 'true');

    await page.locator('#chatInput').fill('Черновик из проекта');
    await page.locator('#workspaceComposerPicker .composer-add-trigger').click();
    const [chooser] = await Promise.all([
      page.waitForEvent('filechooser', {timeout:5000}),
      page.locator('#workspaceComposerPicker .composer-add-menu').getByRole('button', {name:'Файл с компьютера'}).click()
    ]);
    await chooser.setFiles({name:'draft.txt', mimeType:'text/plain', buffer:Buffer.from('hello')});
    await page.waitForFunction(() => document.querySelector('#chatInput').value.includes('Файл проекта: chat-files/'));
    const projectDraft = await page.locator('#chatInput').inputValue();
    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    await page.locator('#chatInput').waitFor({state:'visible'});
    assert.equal(await page.locator('#chatInput').inputValue(), projectDraft,
      'file reference and text share the source chat draft');

    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    await page.evaluate(() => {
      const original = send;
      window.uploadRace = {original, release:null};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'beginProjectChatFileUpload' && payload.sessionId === 'preview-project-review')
          await new Promise(resolve => { uploadRace.release = resolve; });
        return result;
      };
    });
    await page.locator('#workspaceComposerPicker .composer-add-trigger').click();
    const [otherChooser] = await Promise.all([
      page.waitForEvent('filechooser', {timeout:5000}),
      page.locator('#workspaceComposerPicker .composer-add-menu').getByRole('button', {name:'Файл с компьютера'}).click()
    ]);
    await otherChooser.setFiles({name:'older.txt', mimeType:'text/plain', buffer:Buffer.from('older')});
    await page.waitForFunction(() => !!uploadRace.release);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-clean'));
    await page.locator('#chatInput').fill('Текст в другой сессии');
    assert.equal(await page.locator('#sendButton').isEnabled(), true,
      'an upload in a previous session must not block text in the selected chat');
    await page.locator('#workspaceComposerPicker .composer-add-trigger').click();
    assert.equal(await page.locator('#workspaceComposerPicker .composer-add-menu').getByRole('button', {name:'Файл с компьютера'}).isDisabled(), true,
      'the file picker remains unavailable while the single upload slot is occupied');
    await page.evaluate(() => uploadRace.release());
    await page.waitForFunction(() => ((RNAssistantChatDrafts.readWorkspace({workspaceId:'preview-data-workspace',
      sessionId:'preview-project-review'}) || {}).text || '').includes('Файл проекта: chat-files/'));
    assert.equal(await page.locator('#chatInput').inputValue(), 'Текст в другой сессии',
      'completed upload updates only its original draft');
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    assert.match(await page.locator('#projectChatFileStatus').textContent(), /Файл добавлен в проект:.*older\.txt/,
      'the source chat retains the completed upload status after switching back');
    await page.evaluate(() => { send = uploadRace.original; return RNAssistantPlugins.select('data-templates'); });

    await page.evaluate(() => {
      const original = send;
      window.draftTest = {original, requests:[], release:null, failOnce:true};
      window.send = async function (type, payload) {
        if (type !== 'submitProjectChat') return original(type, payload);
        draftTest.requests.push(payload);
        if (draftTest.failOnce) { draftTest.failOnce = false; throw Error('Expected transport failure'); }
        const result = await original(type, payload);
        await new Promise(resolve => { draftTest.release = resolve; });
        return result;
      };
    });
    await page.locator('#chatInput').fill('Тот же текст');
    await page.locator('#chatInput').press('Enter');
    await page.locator('#workspaceChatNotice').getByText('Expected transport failure').waitFor();
    assert.equal(await page.locator('#chatInput').inputValue(), 'Тот же текст',
      'failed delivery keeps the draft');
    await page.evaluate(() => {
      document.querySelector('#chatForm').requestSubmit();
      document.querySelector('#chatForm').requestSubmit();
    });
    await page.waitForFunction(() => typeof draftTest.release === 'function');
    const requests = await page.evaluate(() => draftTest.requests);
    assert.equal(requests.length, 2, 'two simultaneous submits add only one retry request');
    assert.equal(requests[0].input.requestId, requests[1].input.requestId,
      'the same intent keeps its request ID on retry');
    await page.locator('#chatInput').fill('Новый ввод');
    await page.locator('#chatInput').fill('Тот же текст');
    await page.evaluate(() => draftTest.release());
    await page.waitForFunction(() => !document.querySelector('#projectChatNew').disabled);
    assert.equal(await page.locator('#chatInput').inputValue(), 'Тот же текст',
      'late acknowledgement does not erase a later draft with identical text');
    await page.evaluate(([workspace, session]) => RNAssistantProjectChats.open(workspace, session), [workspaceId, sessionId]);
    assert.equal(await page.locator('#chatInput').inputValue(), 'Тот же текст',
      'project and plugin still read the same later draft');

    for (const destination of ['plugin', 'project']) {
      const message = 'Повтор после потери ответа: ' + destination;
      await page.evaluate(() => {
        const original = draftTest.original;
        window.retryTest = {requests:[], failOnce:true};
        window.send = async function (type, payload) {
          const result = await original(type, payload);
          if (type === 'submitProjectChat') {
            retryTest.requests.push(payload);
            if (retryTest.failOnce) { retryTest.failOnce = false; throw Error('Accepted reply lost'); }
          }
          return result;
        };
      });
      await page.locator('#chatInput').fill(message);
      await page.locator('#chatInput').press('Enter');
      await page.waitForFunction(() => retryTest.requests.length === 1 &&
        !document.querySelector('#projectChatNew').disabled);
      if (destination === 'plugin') await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
      else await page.evaluate(([workspace, session]) => RNAssistantProjectChats.open(workspace, session), [workspaceId, sessionId]);
      assert.equal(await page.locator('#chatInput').inputValue(), message, 'unacknowledged draft survives navigation');
      await page.locator('#chatInput').press('Enter');
      await page.waitForFunction(() => retryTest.requests.length === 2);
      const retried = await page.evaluate(() => retryTest.requests);
      assert.equal(retried[0].input.requestId, retried[1].input.requestId,
        'cross-surface retry preserves accepted input identity: ' + destination);
      const count = await page.evaluate(async ({workspaceId, sessionId, message}) => {
        const snapshot = await draftTest.original('getProjectChat', {workspaceId, sessionId});
        return snapshot.messages.filter(item => item.Role === 'user' && item.Content === message).length;
      }, {workspaceId, sessionId, message});
      assert.equal(count, 1, 'lost acknowledgement must not execute the input twice');
      await page.waitForFunction(() => !document.querySelector('#chatInput').value);
    }
    assert.deepEqual(errors, [], 'no browser errors');
    console.log('PASS workspace shared draft: project/plugin session, preferences, file link, failed retry, double submit, late ack, cross-surface accepted-input retry');
  } finally { await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
