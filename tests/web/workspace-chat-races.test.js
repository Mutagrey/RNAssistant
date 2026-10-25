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
      const historyGuard = await page.evaluate(() => {
        const snapshot = RNAssistantProjectChats.snapshot();
        const workspaceId = RNAssistantProjectChats.workspaceId();
        const originalStatus = snapshot.state.status;
        const historyButton = id => document.querySelector(
          '#projectChatTree [data-tree-key="project:' + id + '"] > .navigation-tree-row button[title^="Удалить историю"]');
        const unrelated = Array.from(document.querySelectorAll('#projectChatTree [data-tree-key^="project:"]'))
          .find(row => row.dataset.treeKey !== 'project:' + workspaceId);
        const results = {};
        for (const status of ['running', 'awaitingconfirmation', 'completed']) {
          snapshot.state.status = status;
          RNAssistantProjectChats.refreshTree();
          results[status] = [historyButton(workspaceId).disabled,
            document.querySelector('#projectChatTree [data-tree-key="project-chat:' + workspaceId + ':' +
              snapshot.sessionId + '"] > .navigation-tree-row button[title^="Удалить чат"]').disabled,
            historyButton(unrelated.dataset.treeKey.slice(8)).disabled];
        }
        snapshot.state.status = originalStatus;
        RNAssistantProjectChats.refreshTree();
        return results;
      });
      assert.deepEqual(historyGuard, { running: [true, true, false],
        awaitingconfirmation: [true, true, false], completed: [false, false, false] },
      owner + ': selected snapshot guards its project history without locking unrelated projects');
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
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.connectionReply = {release:null};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'connectProjectWorkspace') await new Promise(resolve => { connectionReply.release = resolve; });
        return result;
      };
    });
    await page.locator('#projectChatWorkspaceMenu summary').click();
    await page.locator('#projectChatConnect').click();
    await page.locator('#projectChatConnectionPath').fill('/tmp/rna-connected-race');
    await page.locator('#projectChatConnectionSubmit').click();
    await page.waitForFunction(() => !!connectionReply.release);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-clean'));
    await page.evaluate(async () => {
      connectionReply.release();
      await new Promise(resolve => setTimeout(resolve, 0));
    });
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.workspaceId()), 'preview-data-workspace',
      'late connection must not change a project selected while its response was pending');
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.snapshot().sessionId), 'preview-project-clean');
    assert.ok(await page.locator('#projectChatTree').getByText('rna-connected-race', {exact:true}).count(),
      'the newly connected project remains available in the tree');
    await page.evaluate(() => { send = sendBeforeSelectionRace; });
    await page.locator('#projectChatWorkspaceMenu summary').click();
    await page.locator('#projectChatConnect').click();
    await page.locator('#projectChatConnectionPath').fill('/tmp/rna-connected-current');
    await page.locator('#projectChatConnectionSubmit').click();
    await page.waitForFunction(() => document.querySelector('#projectChatContext').textContent.includes('rna-connected-current'));
    assert.ok(await page.locator('#projectChatTree').getByText('rna-connected-current', {exact:true}).count(),
      'connecting without a later selection still opens the new project');
    await page.locator('#projectChatWorkspaceMenu summary').click();
    await page.locator('#projectChatConnect').click();
    await page.evaluate(() => {
      const original = send;
      window.openAfterCancel = {release:null};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'getProjectChat' && payload.sessionId === 'preview-project-review')
          await new Promise(resolve => { openAfterCancel.release = resolve; });
        return result;
      };
      window.pendingOpenAfterCancel = RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review');
    });
    await page.waitForFunction(() => !!openAfterCancel.release);
    await page.locator('#projectChatConnectionCancel').click();
    await page.evaluate(() => openAfterCancel.release());
    await page.evaluate(() => pendingOpenAfterCancel);
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.snapshot()?.sessionId), 'preview-project-review',
      'closing an idle connection form must not discard a pending chat read');
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.cancelledConnection = {release:null};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'connectProjectWorkspace') await new Promise(resolve => { cancelledConnection.release = resolve; });
        return result;
      };
    });
    await page.locator('#projectChatWorkspaceMenu summary').click();
    await page.locator('#projectChatConnect').click();
    await page.locator('#projectChatConnectionPath').fill('/tmp/rna-connected-cancel');
    await page.locator('#projectChatConnectionSubmit').click();
    await page.waitForFunction(() => !!cancelledConnection.release);
    assert.equal(await page.locator('#projectChatConnectionCancel').textContent(), 'Скрыть',
      'an accepted connection cannot be presented as cancellable');
    await page.locator('#projectChatConnectionCancel').click();
    await page.evaluate(() => cancelledConnection.release());
    await page.waitForFunction(() => !RNAssistantProjectChats.isBusy());
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.workspaceId()), 'preview-data-workspace',
      'closing a pending connection form must not change the selected project');
    assert.ok(await page.locator('#projectChatTree').getByText('rna-connected-cancel', {exact:true}).count(),
      'the completed connection still appears in the tree');
    assert.equal(await page.locator('#projectChatConnectionCancel').textContent(), 'Отмена');
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-clean'));
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.connectionDuringChat = {release:null};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'connectProjectWorkspace') await new Promise(resolve => { connectionDuringChat.release = resolve; });
        return result;
      };
    });
    await page.locator('#projectChatWorkspaceMenu summary').click();
    await page.locator('#projectChatConnect').click();
    await page.locator('#projectChatConnectionPath').fill('/tmp/rna-connected-race');
    await page.locator('#projectChatConnectionSubmit').click();
    await page.waitForFunction(() => !!connectionDuringChat.release);
    await page.locator('#chatInput').fill('Сообщение во время подключения');
    assert.equal(await page.locator('#sendButton').isEnabled(), true,
      'a pending connection must not block sending in the selected chat');
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => RNAssistantProjectChats.snapshot()?.messages.some(message =>
      message.Content === 'Сообщение во время подключения'));
    await page.evaluate(async () => { connectionDuringChat.release(); await new Promise(resolve => setTimeout(resolve, 0)); });
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.snapshot()?.sessionId), 'preview-project-clean',
      'the connection must not replace a chat used while it was pending');
    assert.ok(await page.locator('#projectChatTree').getByText('rna-connected-race', {exact:true}).count());
    await page.waitForFunction(() => !RNAssistantProjectChats.isBusy());
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.createDuringChat = {release:null, sessionId:null};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'createProjectChat') {
          createDuringChat.sessionId = result.sessionId;
          await new Promise(resolve => { createDuringChat.release = resolve; });
        }
        return result;
      };
    });
    await page.locator('#projectChatNew').click();
    await page.waitForFunction(() => !!createDuringChat.release);
    await page.locator('#chatInput').fill('Сообщение во время создания чата');
    assert.equal(await page.locator('#sendButton').isEnabled(), true,
      'a pending chat creation must not block sending in an existing chat');
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => RNAssistantProjectChats.snapshot()?.messages.some(message =>
      message.Content === 'Сообщение во время создания чата'));
    await page.evaluate(async () => { createDuringChat.release(); await new Promise(resolve => setTimeout(resolve, 0)); });
    await page.waitForFunction(() => !RNAssistantProjectChats.isBusy());
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.snapshot()?.sessionId), 'preview-project-clean',
      'the created chat must not replace a chat used while creation was pending');
    const backgroundCreatedId = await page.evaluate(() => createDuringChat.sessionId);
    assert.equal(await page.locator('[data-tree-key="project-chat:preview-data-workspace:' + backgroundCreatedId + '"]').count(), 1);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-review'));
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.renameReply = {release:null};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'renameProjectChat') await new Promise(resolve => { renameReply.release = resolve; });
        return result;
      };
    });
    const renamedRow = page.locator('[data-tree-key="project-chat:preview-data-workspace:preview-project-review"]');
    page.once('dialog', dialog => dialog.accept('Новое имя беседы'));
    await renamedRow.hover();
    await renamedRow.getByRole('button', {name:/Переименовать чат/}).click();
    await page.waitForFunction(() => !!renameReply.release);
    await page.locator('#projectChatNew').click();
    await page.waitForFunction(() => RNAssistantProjectChats.snapshot()?.sessionId?.startsWith('preview-project-') &&
      RNAssistantProjectChats.snapshot().sessionId !== 'preview-project-review');
    const createdDuringRename = await page.evaluate(() => RNAssistantProjectChats.snapshot().sessionId);
    const createdRow = page.locator('[data-tree-key="project-chat:preview-data-workspace:' + createdDuringRename + '"]');
    assert.equal(await createdRow.count(), 1);
    await page.evaluate(async () => { renameReply.release(); await new Promise(resolve => setTimeout(resolve, 0)); });
    assert.equal(await createdRow.count(), 1,
      'a late rename catalog must not hide a chat created while the rename was pending');
    assert.match(await renamedRow.textContent(), /Новое имя беседы/,
      'the renamed chat must still show its confirmed title');
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.failedRename = {reject:null};
      window.send = function (type, payload) {
        if (type === 'renameProjectChat') return new Promise((resolve, reject) => {
          failedRename.reject = reject;
        });
        return original(type, payload);
      };
    });
    page.once('dialog', dialog => dialog.accept('Ещё одно имя'));
    await renamedRow.hover();
    await renamedRow.getByRole('button', {name:/Переименовать чат/}).click();
    await page.waitForFunction(() => !!failedRename.reject);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-clean'));
    await page.evaluate(async () => {
      failedRename.reject(new Error('Previous chat rename failed'));
      await new Promise(resolve => setTimeout(resolve, 0));
    });
    assert.doesNotMatch(await page.locator('#workspaceChatNotice').textContent(), /Previous chat rename failed/,
      'a late rename error must not appear in a newly selected chat');
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.deleteCatalogReply = {release:null};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'deleteProjectChat') await new Promise(resolve => { deleteCatalogReply.release = resolve; });
        return result;
      };
    });
    page.once('dialog', dialog => dialog.accept());
    await renamedRow.hover();
    await renamedRow.getByRole('button', {name:/Удалить чат/}).click();
    await page.waitForFunction(() => !!deleteCatalogReply.release);
    await page.locator('#projectChatNew').click();
    await page.waitForFunction(previousId => RNAssistantProjectChats.snapshot()?.sessionId?.startsWith('preview-project-') &&
      RNAssistantProjectChats.snapshot().sessionId !== previousId, createdDuringRename);
    const createdDuringDelete = await page.evaluate(() => RNAssistantProjectChats.snapshot().sessionId);
    const rowCreatedDuringDelete = page.locator('[data-tree-key="project-chat:preview-data-workspace:' + createdDuringDelete + '"]');
    assert.equal(await rowCreatedDuringDelete.count(), 1);
    await page.evaluate(async () => { deleteCatalogReply.release(); await new Promise(resolve => setTimeout(resolve, 0)); });
    assert.equal(await rowCreatedDuringDelete.count(), 1,
      'a late delete catalog must not hide a chat created while deletion was pending');
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.historyCatalogReply = {release:null};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'deleteProjectHistory') await new Promise(resolve => { historyCatalogReply.release = resolve; });
        return result;
      };
    });
    const removedHistoryRow = page.locator('[data-tree-key^="project:"]').filter({hasText:'rna-connected-current'}).first();
    page.once('dialog', dialog => dialog.accept());
    await removedHistoryRow.locator(':scope > .navigation-tree-row').hover();
    await removedHistoryRow.locator('button[title^="Удалить историю"]').click();
    await page.waitForFunction(() => !!historyCatalogReply.release);
    await page.locator('#projectChatNew').click();
    await page.waitForFunction(previousId => RNAssistantProjectChats.snapshot()?.sessionId?.startsWith('preview-project-') &&
      RNAssistantProjectChats.snapshot().sessionId !== previousId, createdDuringDelete);
    const createdDuringHistoryDelete = await page.evaluate(() => RNAssistantProjectChats.snapshot().sessionId);
    const rowCreatedDuringHistoryDelete = page.locator('[data-tree-key="project-chat:preview-data-workspace:' + createdDuringHistoryDelete + '"]');
    assert.equal(await rowCreatedDuringHistoryDelete.count(), 1);
    await page.evaluate(async () => { historyCatalogReply.release(); await new Promise(resolve => setTimeout(resolve, 0)); });
    assert.equal(await rowCreatedDuringHistoryDelete.count(), 1,
      'a late history deletion catalog must not hide a chat created while deletion was pending');
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.connectionCatalogReply = {release:null};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'connectProjectWorkspace') await new Promise(resolve => { connectionCatalogReply.release = resolve; });
        return result;
      };
    });
    await page.locator('#projectChatWorkspaceMenu summary').click();
    await page.locator('#projectChatConnect').click();
    await page.locator('#projectChatConnectionPath').fill('/tmp/rna-connected-race');
    await page.locator('#projectChatConnectionSubmit').click();
    await page.waitForFunction(() => !!connectionCatalogReply.release);
    const concurrentlyRenamed = page.locator('[data-tree-key="project-chat:preview-data-workspace:preview-project-clean"]');
    page.once('dialog', dialog => dialog.accept('Название после подключения'));
    await concurrentlyRenamed.hover();
    await concurrentlyRenamed.getByRole('button', {name:/Переименовать чат/}).click();
    await page.waitForFunction(() => document.querySelector('[data-tree-key="project-chat:preview-data-workspace:preview-project-clean"]')
      ?.textContent.includes('Название после подключения'));
    await page.evaluate(async () => { connectionCatalogReply.release(); await new Promise(resolve => setTimeout(resolve, 0)); });
    assert.match(await concurrentlyRenamed.textContent(), /Название после подключения/,
      'a late connection catalog must not revert a chat renamed while connection was pending');
    assert.ok(await page.locator('#projectChatTree').getByText('rna-connected-race', {exact:true}).count());
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-clean'));
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.staleRunCatalog = {release:null, armed:true};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'getProjectChats' && staleRunCatalog.armed) {
          staleRunCatalog.armed = false;
          await new Promise(resolve => { staleRunCatalog.release = resolve; });
        }
        return result;
      };
    });
    await page.locator('#chatInput').fill('Обновить список после ответа');
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => !!staleRunCatalog.release);
    await page.locator('#projectChatNew').click();
    await page.waitForFunction(previousId => RNAssistantProjectChats.snapshot()?.sessionId?.startsWith('preview-project-') &&
      RNAssistantProjectChats.snapshot().sessionId !== previousId, createdDuringHistoryDelete);
    const createdDuringRunCatalog = await page.evaluate(() => RNAssistantProjectChats.snapshot().sessionId);
    const rowCreatedDuringRunCatalog = page.locator('[data-tree-key="project-chat:preview-data-workspace:' + createdDuringRunCatalog + '"]');
    assert.equal(await rowCreatedDuringRunCatalog.count(), 1);
    await page.evaluate(() => RNAssistantProjectChats.open('preview-data-workspace', 'preview-project-clean'));
    await page.evaluate(async () => { staleRunCatalog.release(); await new Promise(resolve => setTimeout(resolve, 0)); });
    assert.equal(await rowCreatedDuringRunCatalog.count(), 1,
      'a late run catalog must not hide a chat created before returning to the original session');
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.staleTitleCatalog = {release:null, armed:true};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'getProjectChats' && staleTitleCatalog.armed) {
          staleTitleCatalog.armed = false;
          await new Promise(resolve => { staleTitleCatalog.release = resolve; });
        }
        return result;
      };
    });
    await page.locator('#chatInput').fill('Обновить заголовок после ответа');
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => !!staleTitleCatalog.release);
    page.once('dialog', dialog => dialog.accept('Свежее название'));
    await concurrentlyRenamed.hover();
    await concurrentlyRenamed.getByRole('button', {name:/Переименовать чат/}).click();
    await page.waitForFunction(() => document.querySelector('[data-tree-key="project-chat:preview-data-workspace:preview-project-clean"]')
      ?.textContent.includes('Свежее название'));
    await page.evaluate(async () => { staleTitleCatalog.release(); await new Promise(resolve => setTimeout(resolve, 0)); });
    assert.match(await concurrentlyRenamed.textContent(), /Свежее название/,
      'a late run catalog must not revert a title renamed while its read was pending');
    await page.evaluate(() => {
      const original = window.sendBeforeSelectionRace;
      window.staleCreateCatalog = {release:null, armed:true};
      window.send = async function (type, payload) {
        const result = await original(type, payload);
        if (type === 'getProjectChats' && staleCreateCatalog.armed) {
          staleCreateCatalog.armed = false;
          await new Promise(resolve => { staleCreateCatalog.release = resolve; });
        }
        return result;
      };
    });
    await page.locator('#projectChatNew').click();
    await page.waitForFunction(() => !!staleCreateCatalog.release);
    const newlySelected = await page.evaluate(() => RNAssistantProjectChats.snapshot()?.sessionId);
    await page.locator('#chatInput').fill('Первое сообщение нового чата');
    assert.equal(await page.locator('#sendButton').isEnabled(), true,
      'the new chat must accept input while its catalog refresh is pending');
    await page.locator('#chatInput').press('Enter');
    await page.waitForFunction(() => RNAssistantProjectChats.snapshot()?.messages.some(message =>
      message.Content === 'Первое сообщение нового чата'));
    page.once('dialog', dialog => dialog.accept('Имя во время создания'));
    await concurrentlyRenamed.hover();
    await concurrentlyRenamed.getByRole('button', {name:/Переименовать чат/}).click();
    await page.waitForFunction(() => document.querySelector('[data-tree-key="project-chat:preview-data-workspace:preview-project-clean"]')
      ?.textContent.includes('Имя во время создания'));
    await page.evaluate(async () => { staleCreateCatalog.release(); await new Promise(resolve => setTimeout(resolve, 0)); });
    await page.waitForFunction(() => !RNAssistantProjectChats.isBusy());
    assert.equal(await page.evaluate(() => RNAssistantProjectChats.snapshot()?.sessionId), newlySelected);
    assert.match(await concurrentlyRenamed.textContent(), /Имя во время создания/,
      'a late create catalog must not revert a title renamed while its read was pending');
    console.log('PASS workspace chat session and catalog races');
  } finally {
    await browser.close();
    server.kill();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
