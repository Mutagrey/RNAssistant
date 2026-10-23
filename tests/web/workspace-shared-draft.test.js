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
    await page.locator('#pluginDevelop').click();
    await page.locator('#pluginMessage').waitFor({state:'visible'});
    const workspaceId = 'preview-plugin-workspace';
    await page.waitForFunction(id => !!RNAssistantWorkspaceChat.selectedSession(id), workspaceId);
    const sessionId = await page.evaluate(id => RNAssistantWorkspaceChat.selectedSession(id), workspaceId);
    assert.ok(sessionId, 'plugin selects an existing workspace session');

    await page.locator('#pluginMessage').fill('Черновик из плагина');
    await page.locator('#pluginChatForm .composer-add-trigger').click();
    await page.locator('#pluginChatForm [data-mode="plan"]').click();
    await page.locator('#pluginChatForm .composer-reasoning-button').click();
    await page.evaluate(([workspace, session]) => RNAssistantProjectChats.open(workspace, session), [workspaceId, sessionId]);
    await page.locator('#projectChatTitle').waitFor({state:'visible'});
    assert.equal(await page.locator('#projectChatInput').inputValue(), 'Черновик из плагина');
    assert.equal(await page.locator('#projectChatForm [data-mode="plan"]').getAttribute('aria-pressed'), 'true');
    assert.equal(await page.locator('#projectChatForm .composer-reasoning-button').getAttribute('aria-pressed'), 'true');

    await page.locator('#projectChatInput').fill('Черновик из проекта');
    await page.locator('#projectChatForm .composer-add-trigger').click();
    const [chooser] = await Promise.all([
      page.waitForEvent('filechooser', {timeout:5000}),
      page.locator('#projectChatForm .composer-add-menu').getByRole('button', {name:'Файл с компьютера'}).click()
    ]);
    await chooser.setFiles({name:'draft.txt', mimeType:'text/plain', buffer:Buffer.from('hello')});
    await page.waitForFunction(() => document.querySelector('#projectChatInput').value.includes('Файл проекта: chat-files/'));
    const projectDraft = await page.locator('#projectChatInput').inputValue();
    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    await page.locator('#pluginMessage').waitFor({state:'visible'});
    assert.equal(await page.locator('#pluginMessage').inputValue(), projectDraft,
      'file reference and text share the source chat draft');

    await page.evaluate(() => {
      const original = send;
      window.draftTest = {requests:[], release:null, failOnce:true};
      window.send = async function (type, payload) {
        if (type !== 'submitProjectChat') return original(type, payload);
        draftTest.requests.push(payload);
        if (draftTest.failOnce) { draftTest.failOnce = false; throw Error('Expected transport failure'); }
        const result = await original(type, payload);
        await new Promise(resolve => { draftTest.release = resolve; });
        return result;
      };
    });
    await page.locator('#pluginMessage').fill('Тот же текст');
    await page.locator('#pluginMessage').press('Enter');
    await page.locator('#pluginChatStatus').getByText('Expected transport failure').waitFor();
    assert.equal(await page.locator('#pluginMessage').inputValue(), 'Тот же текст',
      'failed delivery keeps the draft');
    await page.evaluate(() => {
      document.querySelector('#pluginChatForm').requestSubmit();
      document.querySelector('#pluginChatForm').requestSubmit();
    });
    await page.waitForFunction(() => typeof draftTest.release === 'function');
    const requests = await page.evaluate(() => draftTest.requests);
    assert.equal(requests.length, 2, 'two simultaneous submits add only one retry request');
    assert.equal(requests[0].input.requestId, requests[1].input.requestId,
      'the same intent keeps its request ID on retry');
    await page.locator('#pluginMessage').fill('Новый ввод');
    await page.locator('#pluginMessage').fill('Тот же текст');
    await page.evaluate(() => draftTest.release());
    await page.waitForFunction(() => !document.querySelector('#pluginChatNew').disabled);
    assert.equal(await page.locator('#pluginMessage').inputValue(), 'Тот же текст',
      'late acknowledgement does not erase a later draft with identical text');
    await page.evaluate(([workspace, session]) => RNAssistantProjectChats.open(workspace, session), [workspaceId, sessionId]);
    assert.equal(await page.locator('#projectChatInput').inputValue(), 'Тот же текст',
      'project and plugin still read the same later draft');
    assert.deepEqual(errors, [], 'no browser errors');
    console.log('PASS workspace shared draft: project/plugin session, preferences, file link, failed retry, double submit, late ack');
  } finally { await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
