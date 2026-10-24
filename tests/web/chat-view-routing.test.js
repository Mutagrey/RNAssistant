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
      window.chatViewTest = {calls:[], pendingWorkspace:'preview-data-workspace', questionWorkspace:null};
      window.send = async function (type, payload) {
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

    await page.evaluate(() => {
      chatViewTest.pendingWorkspace = 'preview-plugin-workspace';
      return RNAssistantPlugins.select('data-templates');
    });
    await page.locator('#workspaceChatReject').waitFor({state:'visible'});
    await page.locator('#workspaceChatReject').click();
    await page.waitForFunction(() => chatViewTest.calls.length === 3);
    assert.deepEqual(await page.evaluate(() => {
      const call = chatViewTest.calls[2];
      return {type:call.type, workspaceId:call.payload.workspaceId, approve:call.payload.approve};
    }), {type:'resolveProjectChat', workspaceId:'preview-plugin-workspace', approve:false});
    assert.deepEqual(errors, []);
    console.log('PASS chat view routing: project approval/question and plugin rejection use the mounted owner');
  } finally {
    await browser.close();
    server.kill();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
