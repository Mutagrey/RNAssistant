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
      list.remove();
      const newer = { project: { workspaceId: 'workspace' }, sessionId: 'progress-test', messages: [],
        state: { status: 'running', runId: 'run', revision: 5,
          modelProgress: { RunId: 'run', Sequence: 3, Message: 'Latest event.' } } };
      const older = { project: { workspaceId: 'workspace' }, sessionId: 'progress-test', messages: [],
        state: { status: 'running', runId: 'run', revision: 5,
          modelProgress: { RunId: 'run', Sequence: 2, Message: 'Older poll.' } } };
      const ordered = RNAssistantWorkspaceChat.latestSnapshot(newer, older).state.modelProgress.Message === 'Latest event.';
      const settings = state.settings; settings.MaxConsecutiveReflections = 0; renderSettings();
      const zero = document.getElementById('maxConsecutiveReflectionsInput').value === '0';
      return { retained, replaced, updated, reset, finished, scoped, ordered, zero };
    });
    for (const [name, passed] of Object.entries(checks)) assert.equal(passed, true, name);
    for (const owner of ['project', 'plugin']) {
      if (owner === 'project') await page.evaluate(() => RNAssistantProjectChats.setScope('projects'));
      else {
        await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
        await page.locator('#pluginDevelop').click();
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
