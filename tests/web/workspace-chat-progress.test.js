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
      chat.state.modelProgress.Message = 'Longer preview.';
      RNAssistantWorkspaceChat.renderMessages(list, chat);
      const updated = live === list.querySelector('.agent-stream-step') && live.textContent.includes('Longer preview.');
      chat.state.modelProgress = { RunId: 'run', StepId: 'step-2', AttemptId: 'b', Message: '', Reasoning: '', Retrying: true };
      RNAssistantWorkspaceChat.renderMessages(list, chat);
      const reset = !live.textContent.includes('New reasoning.') && !live.textContent.includes('Longer preview.') && live.textContent.includes('Повторная попытка');
      chat.messages.push({ Id: 'finish', RunId: 'run', Role: 'assistant', Content: 'Final answer.', ReasoningContent: 'Accepted final reasoning.' });
      chat.state = { runId: 'run', status: 'completed' };
      RNAssistantWorkspaceChat.renderMessages(list, chat);
      const finished = !list.querySelector('.agent-stream-step') && list.textContent.includes('Accepted final reasoning.') && saved.isConnected;
      chat.state = { runId: 'new-run', status: 'running', modelProgress: { RunId: 'run', Message: 'Stale preview.' } };
      RNAssistantWorkspaceChat.renderMessages(list, chat);
      const scoped = !list.textContent.includes('Stale preview.');
      list.remove();
      const settings = state.settings; settings.MaxConsecutiveReflections = 0; renderSettings();
      const zero = document.getElementById('maxConsecutiveReflectionsInput').value === '0';
      return { retained, updated, reset, finished, scoped, zero };
    });
    for (const [name, passed] of Object.entries(checks)) assert.equal(passed, true, name);
    assert.deepEqual(errors, []);
    console.log('PASS workspace progress: scoped live preview, retry reset, accepted reasoning, stable focus/DOM and reflection depth zero');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
