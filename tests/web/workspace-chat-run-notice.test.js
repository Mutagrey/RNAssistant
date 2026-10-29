'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { chromium } = require('playwright');
const root = path.resolve(__dirname, '../..');

(async () => {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'], { cwd: root });
  const address = await new Promise((resolve, reject) => {
    server.stdout.on('data', chunk => {
      const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(chunk));
      if (match) resolve(match[1]);
    });
    server.once('exit', code => reject(Error('Preview server exited: ' + code)));
  });
  let browser;
  try {
    browser = await chromium.launch({ headless: true,
      ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    for (const owner of ['project', 'plugin']) {
      const page = await browser.newPage();
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.goto(address + 'preview/web/?scenario=ready');
      await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
      await page.evaluate(() => {
        const original = send;
        window.send = async function (type, payload) {
          if (type === 'getPreviousProjectChatMessages') return {
            chatId: payload.sessionId, sessionRevision: 5, startIndex: 0, totalCount: 8,
            messages: [
              { Id: 'old-user', Role: 'user', Content: 'Earlier question.' },
              { Id: 'old-answer', Role: 'assistant', RunId: 'old-run', Content: 'Earlier answer.',
                RunViewState: { RunId: 'old-run', TurnId: 'old-turn', Narrative: 'Earlier answer.',
                  Lifecycle: 'completed', ExecutionHealth: 'unknown', GoalVerification: 'not_assessed',
                  ModelResults: [], SuccessfulReads: 0, VerifiedWrites: 0, NoChangeWrites: 0,
                  UnverifiedWrites: 1, FailedCalls: 0, UnknownEffects: 1,
                  Reason: 'model_done', CurrentAction: 'Earlier answer.', StartedUtc: '2026-10-22T00:00:00Z' } }
            ]
          };
          const result = await original(type, payload);
          if ((type === 'getProjectChat' || type === 'getPluginChat') && result && result.state) {
            // A stale shell status/preview must not revive a terminal kernel fact.
            result.state.status = 'running';
            result.state.runId = 'failed-run';
            result.state.canSteer = false;
            result.state.modelProgress = { runId: 'failed-run', sequence: 8,
              message: 'Unaccepted draft after failure.' };
            result.state.revision = 5;
            result.messageStartIndex = 2;
            result.messageTotalCount = 8;
            result.messages = [
              { Id: 'user', Role: 'user', Content: 'Check the chart.' },
              { Id: 'reflection', Role: 'assistant', Content: '',
                ReflectionDecision: 'Inspect the exact source first.' },
              { Id: 'activity', Role: 'assistant', Content: '', RunId: 'failed-run', Activity: {
                RunId: 'failed-run', Kind: 'tool', Title: 'core.read',
                ToolId: 'core.read', ToolCallId: 'call-1', Status: 'completed',
                ResultMessage: 'Source read.' } },
              { Id: 'finish', Role: 'assistant', Content: 'Chart remains unfinished.', Results: [
                { Summary: 'Chart absent', Assessment: 'partially_satisfied', Evidence: ['call-1'] }
              ] },
              { Id: 'diagnostic', Role: 'assistant', RunId: 'failed-run', Content: '', Activity: {
                RunId: 'failed-run', Kind: 'diagnostic', Title: 'Выполнение остановлено',
                Status: 'failed', ResultMessage: 'Provider failed.' },
                RunViewState: { RunId: 'failed-run', TurnId: 'failed-turn', Narrative: 'Provider failed.',
                  Lifecycle: 'failed', ExecutionHealth: 'clean', GoalVerification: 'not_assessed',
                  ModelResults: [], SuccessfulReads: 0, VerifiedWrites: 0, NoChangeWrites: 0,
                  UnverifiedWrites: 0, FailedCalls: 0, UnknownEffects: 0,
                  Reason: 'provider', CurrentAction: 'Provider failed.', StartedUtc: '2026-10-23T00:00:00Z' } },
              { Id: 'protocol', Role: 'assistant', ProtocolMessage: true, Content: 'private v8 JSON' }
            ];
            result.state.runViewState = {
              RunId: 'failed-run', TurnId: 'failed-turn', Narrative: 'The model claimed success.',
              Lifecycle: 'failed', ExecutionHealth: 'clean', GoalVerification: 'not_assessed',
              ModelResults: [], SuccessfulReads: 0, VerifiedWrites: 0, NoChangeWrites: 0,
              UnverifiedWrites: 0, FailedCalls: 0, UnknownEffects: 0, Reason: 'provider',
              CurrentAction: '', StartedUtc: '2026-10-23T00:00:00Z'
            };
          }
          return result;
        };
      });
      if (owner === 'project') await page.evaluate(() => RNAssistantProjectChats.setScope('projects'));
      else {
        await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
      }
      const notice = page.locator('#workspaceChatNotice');
      await page.waitForFunction(() => document.querySelector('#workspaceChatNotice').textContent ===
        'Работа остановлена. Не удалось получить ответ от сервиса модели.');
      assert.equal(await notice.getAttribute('data-error'), 'true', owner + ': terminal failure is an alert');
      assert.equal(await page.locator('#stopButton').isVisible(), false,
        owner + ': terminal kernel state hides Stop despite stale shell status');
      await page.locator('#chatInput').fill('Новый запрос');
      assert.equal(await page.locator('#sendButton').isEnabled(), true,
        owner + ': terminal kernel state unlocks a new request');
      assert.equal((await page.locator('#messages').textContent()).includes('Unaccepted draft after failure.'), false,
        owner + ': terminal kernel state suppresses stale model preview');
      await page.locator('#messages .agent-reflection-step').getByText('Inspect the exact source first.').waitFor();
      await page.locator('#messages .agent-model-results').getByText('Chart absent', { exact: false }).waitFor();
      assert.equal(await page.locator('#messages').getByText('core.read').count() > 0, true,
        owner + ': saved tool activity uses the common message renderer');
      await page.locator('#messages .agent-run-notice').getByText('Не удалось получить ответ от сервиса модели.', { exact: false }).waitFor();
      assert.equal((await page.locator('#messages').textContent()).includes('private v8 JSON'), false,
        owner + ': protocol body stays hidden');
      await page.locator('#messages .chat-history-more').click();
      await page.locator('#messages').getByText('Earlier answer.').waitFor();
      await page.locator('#messages .agent-run-notice').getByText('Есть действия с неподтверждённым результатом', { exact: false }).waitFor();
      assert.equal(await page.locator('#messages .agent-details-link').count(), 0,
        owner + ': historical workspace outcome cannot open an Office-only journal');
      assert.equal(await page.locator('#messages .chat-history-more').count(), 0,
        owner + ': oldest page removes the history control');
      const grouped = await page.evaluate(() => {
        const list = document.createElement('section'); document.body.appendChild(list);
        const runId = 'grouped-workspace-run';
        const activity = (id, stepId) => ({ Id: id, Role: 'assistant', RunId: runId, Content: '',
          Activity: { RunId: runId, StepId: stepId, Kind: 'tool', Title: 'Чтение',
            ToolId: 'core.read', ToolCallId: id, Status: 'completed', ResultMessage: id } });
        RNAssistantWorkspaceChat.renderMessages(list, { sessionId: 'grouped-session',
          project: { workspaceId: 'grouped-workspace' }, messages: [activity('first', 'step-1'),
            activity('second', 'step-2'), { Id: 'final', Role: 'assistant', RunId: runId,
              Content: 'Готово.', RunViewState: { RunId: runId, TurnId: 'turn', Lifecycle: 'completed',
                ExecutionHealth: 'clean', ModelResults: [], FailedCalls: 0, UnknownEffects: 0 } }] });
        const article = list.querySelector('article.agent-run');
        const history = article && article.querySelector('.agent-run-overview');
        if (history) history.ensureDisclosureContent();
        const result = !!article && list.querySelectorAll('article.message').length === 1 &&
          article.querySelectorAll('.agent-activity-row').length === 2 &&
          article.textContent.includes('Готово.') &&
          !article.querySelector('[title="Ответвить чат отсюда"], [title="Удалить сообщения запуска"]');
        list.remove(); return result;
      });
      assert.equal(grouped, true, owner + ': saved actions and final answer share the Office run renderer');
      assert.deepEqual(errors, [], owner + ': no page errors');
      await page.close();
    }
    console.log('PASS shared chat: project and plugin display the Office run failure');
  } finally {
    if (browser) await browser.close();
    server.kill();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
