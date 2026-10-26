'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { chromium } = require('playwright');
(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], { cwd: path.resolve(__dirname, '../..') });
  let browser;
  try {
    const address = await new Promise((resolve, reject) => {
      server.stdout.on('data', value => { const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(value)); if (match) resolve(match[1]); });
      server.once('exit', code => reject(Error('Preview exited: ' + code)));
    });
    browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const page = await browser.newPage(); const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(address + 'preview/web/?scenario=ready');
    await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
    const checks = await page.evaluate(() => {
      const list = document.createElement('section'); document.body.appendChild(list);
      const first = { Id: 'reflection', RunId: 'run', Role: 'assistant', Content: '', ReflectionDecision: 'Read the procedure.', ReasoningContent: 'Accepted reasoning.' };
      const chat = { sessionId: 'progress-test', project: { workspaceId: 'workspace' }, messages: [first], state: { status: 'running', runId: 'run' } };
      RNAssistantWorkspaceChat.renderMessages(list, chat);
      const saved = list.querySelector('.reasoning-block'), step = list.querySelector('.workspace-reflection-step');
      const summary = saved.querySelector('summary'); saved.open = true; summary.focus();
      chat.state.modelProgress = { RunId: 'run', StepId: 'step-2', AttemptId: 'a', Message: 'Preview text.', Reasoning: 'New reasoning.' };
      RNAssistantWorkspaceChat.renderMessages(list, chat);
      const live = list.querySelector('.agent-stream-step');
      const retained = saved === list.querySelector('.reasoning-block') && step === list.querySelector('.workspace-reflection-step') && document.activeElement === summary && saved.open;
      first.ReasoningContent = 'Revised accepted reasoning.';
      RNAssistantWorkspaceChat.renderMessages(list, chat);
      const revised = list.querySelector('.reasoning-block');
      const replaced = revised !== saved && revised.open &&
        document.activeElement === revised.querySelector('summary') && revised.textContent.includes('Revised accepted reasoning.');
      const liveReasoning = live.querySelector('.reasoning-block');
      liveReasoning.open = true; liveReasoning.querySelector('summary').focus();
      chat.state.modelProgress.Message = 'Longer preview.';
      chat.state.modelProgress.Reasoning = 'Longer reasoning.';
      RNAssistantWorkspaceChat.renderMessages(list, chat);
      const updated = live === list.querySelector('.agent-stream-step') && live.textContent.includes('Longer preview.') &&
        liveReasoning === live.querySelector('.reasoning-block') && liveReasoning.open &&
        document.activeElement === liveReasoning.querySelector('summary') && live.textContent.includes('Longer reasoning.');
      chat.state.modelProgress = { RunId: 'run', StepId: 'step-2', AttemptId: 'b', Message: '', Reasoning: '', Retrying: true };
      RNAssistantWorkspaceChat.renderMessages(list, chat);
      const reset = !live.textContent.includes('Longer reasoning.') && !live.textContent.includes('Longer preview.') && live.textContent.includes('Повторная попытка');
      chat.messages.push({ Id: 'finish', RunId: 'run', Role: 'assistant', Content: 'Final answer.', ReasoningContent: 'Accepted final reasoning.' });
      chat.state = { runId: 'run', status: 'completed' };
      RNAssistantWorkspaceChat.renderMessages(list, chat);
      const finished = !list.querySelector('.agent-stream-step') && list.textContent.includes('Accepted final reasoning.') && revised.isConnected;
      chat.state = { runId: 'new-run', status: 'running', modelProgress: { RunId: 'run', Message: 'Stale preview.' } };
      RNAssistantWorkspaceChat.renderMessages(list, chat);
      const scoped = !list.textContent.includes('Stale preview.');
      const activeList = document.createElement('section');
      const activeChat = { project: { workspaceId: 'workspace' }, sessionId: 'active-run',
        messages: [{ Id: 'saved-call', RunId: 'active', Role: 'assistant', Content: '', Activity: {
          RunId: 'active', Kind: 'tool', ToolId: 'common.resources_read', ToolCallId: 'call',
          Title: 'Чтение', Status: 'completed', ResultMessage: 'Источник прочитан.' } }],
        state: { status: 'running', runId: 'active', modelProgress: { RunId: 'active', Message: 'Формирую ответ.' } } };
      RNAssistantWorkspaceChat.renderMessages(activeList, activeChat);
      const attachedLive = activeList.querySelectorAll('article.agent-run').length === 1 &&
        !!activeList.querySelector('article.agent-run.live .agent-stream-step') &&
        activeList.textContent.includes('Формирую ответ.');
      activeChat.messages.push({ Id: 'accepted', RunId: 'active', Role: 'assistant', Content: 'Ответ готов.',
        RunViewState: { RunId: 'active', TurnId: 'turn', Lifecycle: 'completed', ExecutionHealth: 'clean',
          ModelResults: [], FailedCalls: 0, UnknownEffects: 0 } });
      activeChat.state = { status: 'completed', runId: 'active' };
      RNAssistantWorkspaceChat.renderMessages(activeList, activeChat);
      const attachedFinal = activeList.querySelectorAll('article.agent-run').length === 1 &&
        !activeList.querySelector('.agent-stream-step') && activeList.textContent.includes('Ответ готов.');
      const reflectionList = document.createElement('section');
      RNAssistantWorkspaceChat.renderMessages(reflectionList, {
        project: { workspaceId: 'workspace' }, sessionId: 'reflection-message',
        messages: [{ Id: 'reflection-with-message', Role: 'assistant', Content: 'Проверю источник.',
          ReflectionDecision: 'Проверю источник.' },
          { Id: 'reflection-with-commentary', Role: 'assistant', Content: 'Отвечу после чтения.',
            ReflectionDecision: 'Сначала нужен точный источник.' },
          { Id: 'finish-clean', Role: 'assistant', Content: 'Готово.', Results: [
            { Summary: 'Отчёт подготовлен', Assessment: 'satisfied', Evidence: ['call-1'] }] },
          { Id: 'finish-unclean', Role: 'assistant', Content: 'Часть данных недоступна.', Results: [
            { Summary: 'Сверка не завершена', Assessment: 'partially_satisfied', Evidence: ['call-2'] }],
            RunViewState: { RunId: 'run-2', TurnId: 'turn-2', Narrative: 'Часть данных недоступна.',
              Lifecycle: 'completed', ExecutionHealth: 'errors', GoalVerification: 'not_assessed',
              ModelResults: [{ Summary: 'Сверка не завершена', Assessment: 'partially_satisfied', Evidence: ['call-2'] }],
              SuccessfulReads: 0, VerifiedWrites: 0, NoChangeWrites: 0, UnverifiedWrites: 0,
              FailedCalls: 1, UnknownEffects: 0, Reason: 'model_finish', CurrentAction: '',
              StartedUtc: '2026-10-25T00:00:00Z' } }],
        state: { status: 'completed' }
      });
      const reflectionSteps = reflectionList.querySelectorAll('.workspace-reflection-step');
      const reflectionArticles = Array.from(reflectionList.querySelectorAll('article.message'))
        .filter(article => article.querySelector('.workspace-reflection-step'));
      const oneReflection = reflectionSteps.length === 2 &&
        reflectionArticles.every(article => !article.querySelector('.markdown') &&
          !!article.querySelector('.message-footer')) &&
        reflectionSteps[0].textContent.split('Проверю источник.').length === 2 &&
        reflectionSteps[1].textContent.includes('Сначала нужен точный источник.') &&
        reflectionSteps[1].textContent.includes('Отвечу после чтения.');
      const cleanFinish = Array.from(reflectionList.querySelectorAll('article.message'))
        .find(article => article.textContent.includes('Готово.'));
      const cleanClaims = !!cleanFinish && !cleanFinish.querySelector('.message-outcome') &&
        !!cleanFinish.querySelector('.agent-model-results:not([open])') &&
        cleanFinish.querySelector('.agent-model-results').textContent.includes('call-1');
      const uncleanFinish = Array.from(reflectionList.querySelectorAll('article.message'))
        .find(article => article.textContent.includes('Часть данных недоступна.'));
      const uncleanClaims = !!uncleanFinish &&
        uncleanFinish.querySelectorAll('.message-outcome').length === 1 &&
        uncleanFinish.querySelectorAll('.agent-model-results').length === 1 &&
        uncleanFinish.textContent.includes('call-2');
      list.remove();
      const newer = { project: { workspaceId: 'workspace' }, sessionId: 'progress-test', messages: [],
        state: { status: 'running', runId: 'run', revision: 5,
          modelProgress: { RunId: 'run', Sequence: 3, Message: 'Latest event.' } } };
      const older = { project: { workspaceId: 'workspace' }, sessionId: 'progress-test', messages: [],
        state: { status: 'running', runId: 'run', revision: 5,
          modelProgress: { RunId: 'run', Sequence: 2, Message: 'Older poll.' } } };
      const ordered = RNAssistantWorkspaceChat.latestSnapshot(newer, older).state.modelProgress.Message === 'Latest event.';
      const lostOwner = RNAssistantWorkspaceChat.latestSnapshot(newer, { project: { workspaceId: 'workspace' },
        sessionId: 'progress-test', messages: [], state: { status: 'running', runId: 'run', revision: 5,
          modelProgressUnavailable: true } }).state.modelProgress == null;
      const settings = state.settings; settings.MaxConsecutiveReflections = 0; renderSettings();
      const zero = document.getElementById('maxConsecutiveReflectionsInput').value === '0';
      return { retained, replaced, updated, reset, finished, scoped, attachedLive, attachedFinal,
        oneReflection, cleanClaims, uncleanClaims,
        ordered, lostOwner, zero };
    });
    for (const [name, passed] of Object.entries(checks)) assert.equal(passed, true, name);
    for (const owner of ['project', 'plugin']) {
      if (owner === 'project') await page.evaluate(() => RNAssistantProjectChats.setScope('projects'));
      else {
        await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
      }
      await page.waitForFunction(expected => RNAssistantWorkspaceChat.owner() === expected &&
        RNAssistantWorkspaceChat.selectedSession(expected === 'project' ? 'preview-data-workspace' : 'preview-plugin-workspace'), owner);
      await page.evaluate(() => {
        window.__workspaceProgress = [];
        chrome.webview.addEventListener('message', event => {
          if (event.data.type === 'progress' && event.data.payload.workspaceId)
            window.__workspaceProgress.push(event.data.payload);
        });
      });
      await page.locator('#chatInput').fill('Проверка живого ответа');
      await page.locator('#chatInput').press('Enter');
      await page.waitForFunction(() => document.querySelector('#messages .agent-stream-step')?.textContent.includes('Готовлю ответ проекта'));
      if (owner === 'project' && process.env.RNA_CHAT_STREAM_SCREENSHOT)
        await page.screenshot({ path: process.env.RNA_CHAT_STREAM_SCREENSHOT, animations: 'disabled' });
      const delivered = await page.evaluate(() => {
        const events = window.__workspaceProgress;
        const last = events[events.length - 1];
        const preview = document.querySelector('#messages .agent-stream-step');
        const before = preview.textContent;
        RNAssistantWorkspaceChat.acceptProgress({ workspaceId: last.workspaceId, sessionId: last.sessionId,
          modelProgress: Object.assign({}, last.modelProgress, { Sequence: 1, Message: 'Stale event.' }) });
        RNAssistantWorkspaceChat.acceptProgress({ workspaceId: 'foreign-workspace', sessionId: last.sessionId,
          modelProgress: Object.assign({}, last.modelProgress, { Sequence: 999, Message: 'Foreign event.' }) });
        return { count: events.length, unchanged: preview.textContent === before,
          scope: last.workspaceId === (RNAssistantWorkspaceChat.owner() === 'project'
            ? 'preview-data-workspace' : 'preview-plugin-workspace') };
      });
      assert.ok(delivered.count >= 2, owner + ': native-shaped progress events reached the browser');
      assert.equal(delivered.scope, true, owner + ': event has exact workspace scope');
      assert.equal(delivered.unchanged, true, owner + ': stale and foreign events do not replace preview');
      await page.waitForFunction(() => !document.querySelector('#messages .agent-stream-step'));
    }
    assert.deepEqual(errors, []);
    console.log('PASS workspace progress: project/plugin bridge events, scoped live preview, retry reset, accepted reasoning and stable DOM');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
