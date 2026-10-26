'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');
const root = path.resolve(__dirname, '../..');

(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], {cwd:root});
  const address = await new Promise((resolve, reject) => {
    server.stdout.on('data', chunk => {
      const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(chunk));
      if (match) resolve(match[1]);
    });
    server.once('exit', code => reject(Error('Preview server exited: ' + code)));
  });
  const browser = await chromium.launch({headless:true,
    ...(process.env.BROWSER_EXECUTABLE ? {executablePath:process.env.BROWSER_EXECUTABLE} : {})});
  try {
    const page = await browser.newPage(), errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(address + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(() => {
      const original = send;
      window.chatViewTest = {calls:[], editRequests:[], pendingWorkspace:'preview-data-workspace', questionWorkspace:null};
      window.send = async function (type, payload) {
        if (type === 'submitProjectChat' && payload.input.EditMessageId)
          chatViewTest.editRequests.push(payload.input);
        if (type === 'resolveProjectChat') {
          chatViewTest.calls.push({type, payload});
          chatViewTest.pendingWorkspace = null;
          return original('getProjectChat', {workspaceId:payload.workspaceId, sessionId:payload.sessionId});
        }
        if (type === 'submitProjectChat' && payload.input.answers) {
          chatViewTest.calls.push({type, payload});
          chatViewTest.questionWorkspace = null;
          return original('getProjectChat', {workspaceId:payload.workspaceId, sessionId:payload.sessionId});
        }
        if (type === 'forkProjectChat') chatViewTest.calls.push({type, payload});
        const result = await original(type, payload);
        if ((type === 'getProjectChat' || type === 'getPluginChat') && result.project) {
          if (result.project.workspaceId === chatViewTest.pendingWorkspace) {
            result.pendingId = 'approval-' + result.sessionId;
            result.pendingTool = 'common.test';
            result.pendingArguments = '{}';
          }
          if (result.project.workspaceId === chatViewTest.questionWorkspace) {
            result.state.userInput = {runId:'preview-run', questionSet:{questionSetId:'preview-questions', questions:[
              {id:'confirm', prompt:'Продолжить?', selection:'single', allowFreeText:false,
                options:[{id:'yes', label:'Да'}]}
            ]}};
          }
        }
        return result;
      };
    });
    if (process.env.RNA_OFFICE_STREAM_SCREENSHOT) {
      await page.locator('#chatInput').fill('Проверь текущий документ');
      await page.locator('#chatInput').press('Enter');
      await page.locator('#messages .agent-stream-step').waitFor({state:'visible'});
      await page.screenshot({path:process.env.RNA_OFFICE_STREAM_SCREENSHOT, animations:'disabled'});
      await page.locator('#messages .agent-stream-step').waitFor({state:'detached'});
    }
    await page.evaluate(() => RNAssistantProjectChats.setScope('projects'));
    await page.locator('#workspaceChatApprove').waitFor({state:'visible'});
    await page.locator('#workspaceChatApprove').click();
    await page.waitForFunction(() => chatViewTest.calls.length === 1);
    assert.deepEqual(await page.evaluate(() => chatViewTest.calls[0].payload),
      {workspaceId:'preview-data-workspace', sessionId:'preview-project-review',
        pendingId:'approval-preview-project-review', approve:true});

    await page.evaluate(() => {
      chatViewTest.questionWorkspace = 'preview-data-workspace';
      return RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review');
    });
    await page.locator('#workspaceChatQuestions').waitFor({state:'visible'});
    await page.locator('#workspaceChatQuestions input[value="yes"]').click();
    await page.waitForFunction(() => chatViewTest.calls.length === 2);
    assert.deepEqual(await page.evaluate(() => chatViewTest.calls[1].payload.input.answers),
      {runId:'preview-run', questionSetId:'preview-questions',
        answers:[{questionId:'confirm', optionIds:['yes'], freeText:''}]});

    await page.waitForFunction(() => !RNAssistantProjectChats.isBusy());
    await page.locator('#chatInput').fill('Проверить ответвление');
    await page.waitForFunction(() => !document.querySelector('#sendButton').disabled);
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => document.querySelector('#messages .message-actions button[title="Ответвить чат отсюда"]'));
    const sourceSession = await page.evaluate(() => RNAssistantWorkspaceChat.selectedSession('preview-data-workspace'));
    await page.locator('#messages .message-actions button[title="Ответвить чат отсюда"]').first().click();
    await page.waitForFunction(previous => RNAssistantWorkspaceChat.selectedSession('preview-data-workspace') !== previous,
      sourceSession);
    assert.equal(await page.evaluate(() => chatViewTest.calls[2].type), 'forkProjectChat');
    assert.equal(await page.evaluate(() => chatViewTest.calls[2].payload.sessionId), sourceSession);
    assert.ok(await page.locator('#messages .message').count() >= 1,
      'fork opens its retained transcript in the same mounted ChatView');
    await page.locator('#messages .message-actions button[title="Удалить сообщение"]').first().click();
    await page.waitForFunction(() => RNAssistantWorkspaceChat.snapshot().messages.length === 0);
    assert.equal(await page.locator('#messages .message').count(), 0,
      'delete updates the same workspace transcript');

    await page.locator('#chatInput').fill('Проверить редактирование');
    await page.locator('#chatInput').press('Enter');
    await page.locator('#messages .message-actions button[title="Изменить сообщение"]').waitFor();
    await page.locator('#messages .message-actions button[title="Изменить сообщение"]').click();
    await page.locator('#messageEditBar').waitFor({state:'visible'});
    if (process.env.RNA_CHAT_EDIT_SCREENSHOT)
      await page.screenshot({path:process.env.RNA_CHAT_EDIT_SCREENSHOT, animations:'disabled'});
    await page.locator('#chatInput').fill('Черновик правки');
    await page.locator('#cancelMessageEditButton').click();
    assert.equal(await page.locator('#chatInput').inputValue(), '', 'cancel restores the workspace draft');
    assert.equal(await page.locator('#sendButton').getAttribute('title'), 'Отправить');
    await page.locator('#messages .message-actions button[title="Изменить сообщение"]').click();
    await page.locator('#chatInput').fill('Исправленный запрос');
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => RNAssistantWorkspaceChat.snapshot().messages.some(
      message => message.Role === 'user' && message.Content === 'Исправленный запрос'));
    await page.locator('#messageEditBar').waitFor({state:'hidden'});
    const editRequest = await page.evaluate(() => ({count:chatViewTest.editRequests.length,
      messageId:chatViewTest.editRequests[0] && chatViewTest.editRequests[0].EditMessageId,
      revision:chatViewTest.editRequests[0] && chatViewTest.editRequests[0].ExpectedRevision,
      resources:chatViewTest.editRequests[0] && chatViewTest.editRequests[0].ResourceDraftIds}));
    assert.equal(editRequest.count, 1);
    assert.ok(editRequest.messageId);
    assert.ok(Number.isSafeInteger(editRequest.revision));
    assert.deepEqual(editRequest.resources, []);

    await page.evaluate(() => {
      chatViewTest.pendingWorkspace = 'preview-plugin-workspace';
      return RNAssistantPlugins.select('data-templates');
    });
    await page.locator('#workspaceChatReject').waitFor({state:'visible'});
    await page.locator('#workspaceChatReject').click();
    await page.waitForFunction(() => chatViewTest.calls.length === 4);
    assert.deepEqual(await page.evaluate(() => {
      const call = chatViewTest.calls[3];
      return {type:call.type, workspaceId:call.payload.workspaceId, approve:call.payload.approve};
    }), {type:'resolveProjectChat', workspaceId:'preview-plugin-workspace', approve:false});
    assert.deepEqual(errors, []);
    console.log('PASS chat view routing: project approval/question and plugin rejection use the mounted owner');
  } finally {
    await browser.close();
    server.kill();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
