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
    for (const owner of ['project', 'plugin']) {
      const page = await browser.newPage(), errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.goto(address + 'preview/web/?scenario=ready');
      await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
      if (owner === 'project') await page.evaluate(() => RNAssistantProjectChats.setScope('projects'));
      else {
        await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
      }
      await page.evaluate(() => {
        const original = send;
        window.snapshotRace = {release:null, delivered:false};
        window.send = async function (type, payload) {
          const result = await original(type, payload);
          if (result && result.state && result.sessionId) {
            result.state.revision = result.state.status === 'running' ? 10 : 20;
            if (type === 'getProjectChat' && result.state.status === 'running' && !snapshotRace.release) {
              await new Promise(resolve => { snapshotRace.release = resolve; });
              snapshotRace.delivered = true;
            }
          }
          return result;
        };
      });
      await page.locator('#chatInput').fill('Проверка задержанного опроса');
      await page.locator('#chatInput').press('Enter');
      await page.waitForFunction(() => !!snapshotRace.release);
      await page.waitForFunction(() => !document.querySelector('#chatInput').value &&
        !document.querySelector('#projectChatNew').disabled);
      await page.evaluate(async () => {
        snapshotRace.release();
        await new Promise(resolve => setTimeout(resolve, 0));
      });
      assert.equal(await page.locator('#workspaceChatNotice').textContent(), '',
        owner + ': delayed running snapshot must not overwrite completed state');
      await page.locator('#chatInput').fill('Следующий запрос');
      assert.equal(await page.locator('#sendButton').isEnabled(), true,
        owner + ': completed chat must remain available for input');
      assert.deepEqual(errors, []);
      await page.close();
    }
    for (const owner of ['project', 'plugin']) {
      const page = await browser.newPage();
      await page.goto(address + 'preview/web/?scenario=ready');
      await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
      if (owner === 'project') await page.evaluate(() => RNAssistantProjectChats.setScope('projects'));
      else {
        await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
      }
      await page.evaluate(owner => {
        const workspaceId = owner === 'project' ? 'preview-data-workspace' : 'preview-plugin-workspace';
        const sessionId = RNAssistantWorkspaceChat.selectedSession(workspaceId);
        window.backgroundRun = send('submitProjectChat', {workspaceId, sessionId,
          input:{sessionId, requestId:crypto.randomUUID(), message:'Уже запущено вне этой панели'}});
      }, owner);
      if (owner === 'project') await page.evaluate(() => RNAssistantProjectChats.setScope('projects'));
      else await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
      await page.evaluate(() => backgroundRun);
      await page.waitForFunction(() => !document.querySelector('#workspaceChatNotice').textContent, null, {timeout:5000});
      await page.locator('#chatInput').fill('После завершения фонового запуска');
      assert.equal(await page.locator('#sendButton').isEnabled(), true,
        owner + ': reopening a running chat follows completion without resubmitting');
      await page.close();
    }
    const page = await browser.newPage();
    await page.goto(address + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    await page.evaluate(() => {
      const original = send;
      window.sendBeforeSelectionRace = original;
      window.selectionRace = {release:null, delivered:false};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'submitProjectChat') {
          await new Promise(resolve => { selectionRace.release = resolve; });
          selectionRace.delivered = true;
        }
        return result;
      };
    });
    await page.locator('#chatInput').fill('Завершение в другом экране');
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => !!selectionRace.release);
    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    await page.evaluate(async () => {
      selectionRace.release();
      await new Promise(resolve => setTimeout(resolve, 0));
    });
    await page.locator('#chatInput').fill('Новый запрос после возврата');
    assert.equal(await page.locator('#projectChatNew').isEnabled(), true,
      'reopening the plugin must not retain the previous view busy flag');
    assert.equal(await page.locator('#sendButton').isEnabled(), true);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.previousSessionReply = {release:null};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'submitProjectChat' && payload.sessionId === 'preview-project-review')
          await new Promise(resolve => { previousSessionReply.release = resolve; });
        return result;
      };
    });
    await page.locator('#chatInput').fill('Ответ в прежней сессии');
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => !!previousSessionReply.release);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-clean'));
    await page.locator('#chatInput').fill('Новый запрос в другой сессии');
    assert.equal(await page.locator('#sendButton').isEnabled(), true,
      'pending reply from another session must not lock the selected chat');
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => RNAssistantProjectChats.snapshot().sessionId === 'preview-project-clean' &&
      RNAssistantProjectChats.snapshot().messages.some(message => message.Content === 'Новый запрос в другой сессии'));
    await page.evaluate(() => previousSessionReply.release());
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.snapshot().sessionId), 'preview-project-clean',
      'late reply from the previous session must not replace the selected chat');
    await page.waitForFunction(() => !RNAssistantProjectChats.isBusy());
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.createReply = {release:null};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'createProjectChat') await new Promise(resolve => { createReply.release = resolve; });
        return result;
      };
    });
    await page.locator('#projectChatNew').click();
    await page.waitForFunction(() => !!createReply.release);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    await page.evaluate(() => createReply.release());
    await page.waitForFunction(() => !RNAssistantProjectChats.isBusy());
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.snapshot().sessionId), 'preview-project-review',
      'late create response must not replace the chat selected while creation was pending');
    assert.ok(await page.locator('#projectChatTree').getByText('Новый чат', {exact:true}).count(),
      'a late create response must still refresh the project tree');
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.cancelReply = {release:null};
      window.send = function (type, payload) {
        if (type === 'cancelProjectChat') return new Promise((resolve, reject) => {
          cancelReply.release = () => reject(new Error('Previous chat cancellation failed'));
        });
        return original(type, payload);
      };
      const snapshot = RNAssistantProjectChats.snapshot();
      snapshot.state.status = 'running';
      snapshot.state.runId = 'previous-run';
      snapshot.state.cursor = 'previous-cursor';
      RNAssistantProjectChats.render();
    });
    await page.locator('#stopButton').click();
    await page.waitForFunction(() => !!cancelReply.release);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-clean'));
    await page.evaluate(() => {
      const snapshot = RNAssistantProjectChats.snapshot();
      snapshot.state.status = 'running';
      snapshot.state.runId = 'selected-run';
      snapshot.state.cursor = 'selected-cursor';
      RNAssistantProjectChats.render();
    });
    assert.equal(await page.locator('#stopButton').isEnabled(), true,
      'a pending cancel from another session must not disable this chat stop button');
    await page.evaluate(async () => {
      cancelReply.release();
      await new Promise(resolve => setTimeout(resolve, 0));
    });
    assert.doesNotMatch(await page.locator('#workspaceChatNotice').textContent(), /Previous chat cancellation failed/,
      'a late cancel error must remain with its original session');
    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    const sourceSession = await page.evaluate(() => RNAssistantWorkspaceChat.selectedSession('preview-plugin-workspace'));
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.deleteReply = {release:null, pluginSelections:0};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'deleteProjectChat') await new Promise(resolve => { deleteReply.release = resolve; });
        return result;
      };
      const select = RNAssistantPlugins.select;
      RNAssistantPlugins.select = function (...args) {
        deleteReply.pluginSelections++;
        return select.apply(this, args);
      };
    });
    const sourceRow = page.locator('[data-tree-key="project-chat:preview-plugin-workspace:' + sourceSession + '"]');
    page.once('dialog', dialog => dialog.accept());
    await sourceRow.hover();
    await sourceRow.getByRole('button', {name:/Удалить чат/}).click();
    await page.waitForFunction(() => !!deleteReply.release);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    await page.evaluate(async () => {
      deleteReply.release();
      await new Promise(resolve => setTimeout(resolve, 0));
    });
    assert.equal(await page.evaluate(() => deleteReply.pluginSelections), 0,
      'late deletion of a plugin chat must not reopen the plugin after another project was selected');
    assert.equal(await page.evaluate(() => RNAssistantWorkspaceChat.owner()), 'project');
    await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
    const expectedPluginSelections = await page.evaluate(() => deleteReply.pluginSelections);
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.historyReply = {release:null};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'deleteProjectHistory') await new Promise(resolve => { historyReply.release = resolve; });
        return result;
      };
    });
    const sourceProject = page.locator('[data-tree-key="project:preview-plugin-workspace"]');
    page.once('dialog', dialog => dialog.accept());
    await sourceProject.locator(':scope > .navigation-tree-row').hover();
    await sourceProject.locator('button[title^="Удалить историю"]').click();
    await page.waitForFunction(() => !!historyReply.release);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    await page.evaluate(async () => {
      historyReply.release();
      await new Promise(resolve => setTimeout(resolve, 0));
    });
    assert.equal(await page.evaluate(() => deleteReply.pluginSelections), expectedPluginSelections,
      'late history deletion must not reopen the plugin after another project was selected');
    assert.equal(await page.evaluate(() => RNAssistantWorkspaceChat.owner()), 'project');
    console.log('PASS workspace snapshot ordering, running-chat reconnect and plugin selection during a pending reply');
  } finally {
    await browser.close();
    server.kill();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
