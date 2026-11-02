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
    const officeSource = await page.evaluate(async () => {
      const original = send, messages = state.messages;
      send = async function (type, payload) {
        const result = await original(type, payload);
        return type === 'getChatState' ? Object.assign({}, result, { activeChatId: 'foreign-chat' }) : result;
      };
      try { await loadChatState('preview-chat'); return false; }
      catch (error) { return /другого чата/.test(error.message) && state.activeChatId === 'preview-chat' && state.messages === messages; }
      finally { send = original; }
    });
    assert.equal(officeSource, true, 'Office full read rejects another chat without changing its history');
    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    await page.locator('#chatInput').waitFor({state:'visible'});
    const viewIdentity = await page.evaluate(() => {
      const view = document.querySelector('#workspaceChatView');
      view.dataset.testIdentity = crypto.randomUUID();
      return view.dataset.testIdentity;
    });
    assert.equal(await page.locator('#composerAddPicker .composer-add-menu').getByRole('button', {name:'Файл с компьютера'}).isVisible(), false,
      'plugin hides project file upload');
    const workspaceId = 'preview-plugin-workspace';
    await page.waitForFunction(id => !!RNAssistantWorkspaceChat.selectedSession(id), workspaceId);
    const sessionId = await page.evaluate(id => RNAssistantWorkspaceChat.selectedSession(id), workspaceId);
    assert.ok(sessionId, 'plugin selects an existing workspace session');

    await page.locator('#chatInput').fill('Черновик из плагина');
    await page.locator('#composerAddPicker .composer-add-trigger').click();
    await page.locator('#chatPlanButton').click();
    await page.locator('#chatReasoningToggle').click();
    await page.evaluate(([workspace, session]) => RNAssistantProjectChats.open(workspace, session), [workspaceId, sessionId]);
    await page.locator('[data-tab-panel-id=conversation]').waitFor({state:'visible'});
    assert.deepEqual(await page.evaluate(() => [document.querySelectorAll('#workspaceChatView').length,
      document.querySelectorAll('#chatForm').length, document.querySelector('#workspaceChatView').dataset.testIdentity,
      document.querySelector('#workspaceChatView').parentElement.id]), [1, 1, viewIdentity, 'conversationView'],
      'project and plugin mount the same view and composer');
    assert.deepEqual(await page.evaluate(() => ['composerAddPicker', 'chatModelPicker', 'chatPlanButton',
      'chatReasoningToggle'].map(id => document.querySelectorAll('#' + id).length)), [1, 1, 1, 1],
      'one set of composer controls serves Office, project and plugin');
    assert.equal(await page.locator('#chatInput').inputValue(), 'Черновик из плагина');
    assert.equal(await page.locator('#chatPlanButton').getAttribute('aria-pressed'), 'true');
    assert.equal(await page.locator('#chatReasoningToggle').getAttribute('aria-pressed'), 'true');

    const workspaceSource = await page.evaluate(async target => {
      const original = send, before = RNAssistantWorkspaceChat.snapshot();
      send = async function (type, payload) {
        const result = await original(type, payload);
        return type === 'getProjectChat' ? Object.assign({}, result,
          { project: Object.assign({}, result.project, { workspaceId: 'foreign-workspace' }) }) : result;
      };
      try {
        await RNAssistantProjectChats.open(target.workspaceId, target.sessionId);
        return RNAssistantWorkspaceChat.snapshot() === before &&
          document.querySelector('#workspaceChatNotice').textContent.includes('другого чата');
      } finally { send = original; }
    }, {workspaceId, sessionId});
    assert.equal(workspaceSource, true, 'Workspace rejects another workspace even when its session id matches');
    await page.evaluate(target => RNAssistantProjectChats.open(target.workspaceId, target.sessionId), {workspaceId, sessionId});

    await page.locator('#chatInput').fill('Черновик из проекта');
    await page.locator('#composerAddPicker .composer-add-trigger').click();
    const [chooser] = await Promise.all([
      page.waitForEvent('filechooser', {timeout:5000}),
      page.locator('#composerAddPicker .composer-add-menu').getByRole('button', {name:'Файл с компьютера'}).click()
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
    await page.locator('#composerAddPicker .composer-add-trigger').click();
    const [otherChooser] = await Promise.all([
      page.waitForEvent('filechooser', {timeout:5000}),
      page.locator('#composerAddPicker .composer-add-menu').getByRole('button', {name:'Файл с компьютера'}).click()
    ]);
    await otherChooser.setFiles({name:'older.txt', mimeType:'text/plain', buffer:Buffer.from('older')});
    await page.waitForFunction(() => !!uploadRace.release);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-clean'));
    await page.locator('#chatInput').fill('Текст в другой сессии');
    assert.equal(await page.locator('#sendButton').isEnabled(), true,
      'an upload in a previous session must not block text in the selected chat');
    await page.locator('#composerAddPicker .composer-add-trigger').click();
    assert.equal(await page.locator('#composerAddPicker .composer-add-menu').getByRole('button', {name:'Файл с компьютера'}).isDisabled(), true,
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
      await page.waitForFunction(() => RNAssistantProjectChats.snapshot().state.status !== 'running' && !RNAssistantProjectChats.isBusy());
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
      await page.waitForFunction(() => !document.querySelector('#chatInput').value);
      const retried = await page.evaluate(() => retryTest.requests);
      assert.equal(retried.length, 1, 'cross-surface reconnect reads the receipt without resubmission: ' + destination);
      const count = await page.evaluate(async ({workspaceId, sessionId, message}) => {
        const snapshot = await draftTest.original('getProjectChat', {workspaceId, sessionId});
        return snapshot.messages.filter(item => item.Role === 'user' && item.Content === message).length;
      }, {workspaceId, sessionId, message});
      assert.equal(count, 1, 'lost acknowledgement must not execute the input twice');
      await page.waitForFunction(() => !document.querySelector('#chatInput').value);
    }
    await page.waitForFunction(() => !document.querySelector('#chatModelPicker').classList.contains('is-disabled'));
    await page.locator('#chatModelPicker summary').click();
    await page.waitForFunction(() => document.querySelectorAll('#chatModelMenu .composer-model-item').length > 1);
    assert.equal(await page.locator('#chatModelMenu [data-value=""]').count(), 1,
      'workspace uses the same explicit default-model option');
    assert.ok(await page.locator('#chatModelMenu .composer-model-item').count() > 1, 'workspace can select catalog models');
    const lateCancel = await page.evaluate(async () => {
      const original = send, before = RNAssistantProjectChats.snapshot();
      const target = { workspaceId: before.project.workspaceId, sessionId: before.sessionId };
      let rejectCancel;
      send = function (type, payload, progress) {
        return type === 'cancelProjectChat' ? new Promise((resolve, reject) => { rejectCancel = reject; }) :
          original(type, payload, progress);
      };
      try {
        const cancelling = chatComposer().dispatch('stop');
        await RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-clean');
        await RNAssistantProjectChats.open(target.workspaceId, target.sessionId);
        const reopened = RNAssistantProjectChats.snapshot();
        rejectCancel(Error('Cancellation from the old visit'));
        await cancelling;
        return RNAssistantProjectChats.snapshot() === reopened &&
          !document.querySelector('#workspaceChatNotice').textContent.includes('Cancellation from the old visit');
      } finally { send = original; }
    });
    assert.equal(lateCancel, true, 'a cancellation error from the old A visit cannot update a reopened A view');
    await page.evaluate(() => RNAssistantProjectChats.setScope('office'));
    await page.locator('#chatModelPicker summary').click();
    await page.waitForFunction(() => !document.querySelector('#chatModelMenu').textContent.includes('Настройки модели'));
    assert.equal(await page.locator('#chatModelMenu').getByText('Настройки модели').count(), 0,
      'the shared picker restores Office model choices when its owner changes');
    assert.deepEqual(errors, [], 'no browser errors');
    console.log('PASS workspace shared draft: addressed snapshot reads, project/plugin session, preferences, file link, failed retry, double submit, late ack, cross-surface receipt reconnect');
  } finally { await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
