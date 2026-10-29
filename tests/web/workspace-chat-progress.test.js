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
      const saved = list.querySelector('.reasoning-block'), step = list.querySelector('.agent-reflection-step');
      const summary = saved.querySelector('summary'); saved.open = true; summary.focus();
      chat.state.modelProgress = { RunId: 'run', StepId: 'step-2', AttemptId: 'a', Message: 'Preview text.', Reasoning: 'New reasoning.' };
      RNAssistantWorkspaceChat.renderMessages(list, chat);
      const live = list.querySelector('.agent-stream-step');
      const retained = saved === list.querySelector('.reasoning-block') && step === list.querySelector('.agent-reflection-step') && document.activeElement === summary && saved.open;
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
          RunId: 'active', Kind: 'tool', ToolId: 'core.read', ToolCallId: 'call',
          Title: 'Чтение', Status: 'completed', ResultMessage: 'Источник прочитан.' } }],
        state: { status: 'running', runId: 'active', modelProgress: { RunId: 'active', Message: 'Формирую ответ.' } } };
      RNAssistantWorkspaceChat.renderMessages(activeList, activeChat);
      const attachedLive = activeList.querySelectorAll('article.agent-run').length === 1 &&
        !!activeList.querySelector('article.agent-run.live .agent-stream-step') &&
        activeList.textContent.includes('Формирую ответ.');
      const stableList = document.createElement('section'); document.body.appendChild(stableList);
      const activity = (id, step) => ({ RunId: 'stable', Kind: 'tool', ToolId: 'core.read',
        ToolCallId: id, StepId: step, StepMessage: 'Читаю ' + step,
        Title: 'Чтение', Status: 'completed', ResultMessage: id + ' прочитан.' });
      const stableChat = { project: { workspaceId: 'workspace' }, sessionId: 'stable-run',
        messages: [
          { Id: 'read-1', RunId: 'stable', Role: 'assistant', Activity: activity('read-1', 'step-1') },
          { Id: 'read-2', RunId: 'stable', Role: 'assistant', Activity: activity('read-2', 'step-1') },
          { Id: 'read-3', RunId: 'stable', Role: 'assistant', Activity: activity('read-3', 'step-2') }
        ], state: { status: 'running', runId: 'stable' } };
      RNAssistantWorkspaceChat.renderMessages(stableList, stableChat);
      const stableArticle = stableList.querySelector('article.agent-run');
      const stableStep = stableArticle.querySelector('.agent-model-step');
      const stableGroup = stableStep.querySelector('.agent-action-stage');
      stableGroup.open = true;
      const stableEntry = stableStep.querySelector('.agent-transcript-entry');
      stableChat.messages.push({ Id: 'read-4', RunId: 'stable', Role: 'assistant',
        Activity: activity('read-4', 'step-3') });
      RNAssistantWorkspaceChat.renderMessages(stableList, stableChat);
      const stableParts = [stableArticle === stableList.querySelector('article.agent-run'),
        stableStep === stableArticle.querySelector('.agent-model-step'),
        stableGroup === stableArticle.querySelector('.agent-action-stage'), stableGroup.open,
        stableEntry === stableArticle.querySelector('.agent-transcript-entry')];
      const stableHistory = stableParts.every(Boolean);
      stableList.remove();
      const growingList = document.createElement('section'); document.body.appendChild(growingList);
      const growingChat = { project: { workspaceId: 'workspace' }, sessionId: 'growing-step',
        messages: stableChat.messages.slice(0, 2), state: { status: 'running', runId: 'stable' } };
      RNAssistantWorkspaceChat.renderMessages(growingList, growingChat);
      const growingGroup = growingList.querySelector('.agent-action-stage');
      const growingEntry = growingGroup.querySelector('.agent-transcript-entry');
      growingGroup.open = true;
      growingChat.messages.push({ Id: 'read-extra', RunId: 'stable', Role: 'assistant',
        Activity: activity('read-extra', 'step-1') });
      RNAssistantWorkspaceChat.renderMessages(growingList, growingChat);
      const stableGrowingGroup = growingGroup === growingList.querySelector('.agent-action-stage') &&
        growingGroup.open && growingEntry === growingGroup.querySelector('.agent-transcript-entry') &&
        growingGroup.querySelectorAll('.agent-transcript-entry').length === 3;
      growingList.remove();
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
      const reflectionSteps = reflectionList.querySelectorAll('.agent-reflection-step');
      const reflectionArticles = Array.from(reflectionList.querySelectorAll('article.message'))
        .filter(article => article.querySelector('.agent-reflection-step'));
      const oneReflection = reflectionSteps.length === 2 &&
        reflectionArticles.every(article => !!article.querySelector('.agent-reflection-body.markdown') &&
          !!article.querySelector('.agent-reflection-icon') &&
          !!article.querySelector('.message-footer')) &&
        reflectionSteps[0].textContent.split('Проверю источник.').length === 2 &&
        reflectionSteps[1].textContent.includes('Сначала нужен точный источник.') &&
        reflectionSteps[1].textContent.includes('Отвечу после чтения.');
      const runReflectionList = document.createElement('section');
      const acceptedReflection = { Id: 'accepted-reflection', RunId: 'reflection-run', Role: 'assistant',
        Content: 'Проверю **источник**.', ReflectionDecision: 'Нужно *точное* чтение.',
        Activity: { Kind: 'step', RunId: 'reflection-run', StepId: 'reflection-step',
          StepMessage: 'Проверю **источник**.', Title: 'Промежуточное решение', Status: 'completed' } };
      const runReflection = { project: { workspaceId: 'workspace' }, sessionId: 'run-reflection',
        messages: [acceptedReflection], state: { status: 'running', runId: 'reflection-run' } };
      RNAssistantWorkspaceChat.renderMessages(runReflectionList, runReflection);
      const activeReflection = runReflectionList.querySelector('.agent-reflection-step');
      const reflectionMarkdown = !!activeReflection &&
        !!activeReflection.querySelector('.agent-reflection-body strong') &&
        !!activeReflection.querySelector('.agent-reflection-body em') &&
        activeReflection.textContent.includes('Нужно точное чтение.') &&
        activeReflection.textContent.includes('Проверю источник.');
      runReflection.messages.push({ Id: 'reflection-final', RunId: 'reflection-run', Role: 'assistant',
        Content: 'Готово.', RunViewState: { RunId: 'reflection-run', TurnId: 'turn',
          Lifecycle: 'completed', ExecutionHealth: 'clean' } });
      runReflection.state = { status: 'completed', runId: 'reflection-run' };
      RNAssistantWorkspaceChat.renderMessages(runReflectionList, runReflection);
      const retainedReflection = !!runReflectionList.querySelector('.agent-reflection-step') &&
        runReflectionList.textContent.includes('Нужно точное чтение.');
      runReflectionList.remove();
      const officeActivity = { Kind: 'step', RunId: 'office-run', StepId: 'office-step',
        StepMessage: 'Сначала прочитаю **модуль**.', Title: 'Промежуточное решение', Status: 'completed' };
      const officeReflection = renderAgentRunArticle({ live: true, items: [{ index: 0,
        message: { Id: 'office-reflection', RunId: 'office-run', Role: 'assistant', Activity: officeActivity },
        activity: officeActivity }] });
      const officeReflectionMarked = !!officeReflection.querySelector('.agent-reflection-icon') &&
        officeReflection.querySelector('.agent-reflection-body strong')?.textContent === 'модуль';
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
        uncleanFinish.querySelector('.agent-model-results').open &&
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
      const terminal = { project: { workspaceId: 'workspace' }, sessionId: 'progress-test',
        messages: [{ Id: 'accepted', Role: 'assistant', Content: 'Accepted answer.' }],
        messageStartIndex: 0, messageTotalCount: 1,
        state: { status: 'running', runId: 'run', revision: 6, runViewState: {
          RunId: 'run', TurnId: 'turn', Lifecycle: 'completed', ExecutionHealth: 'clean',
          GoalVerification: 'not_assessed', ModelResults: [], SuccessfulReads: 0, VerifiedWrites: 0,
          NoChangeWrites: 0, UnverifiedWrites: 0, FailedCalls: 0, UnknownEffects: 0 } } };
      const delayed = { project: { workspaceId: 'workspace' }, sessionId: 'progress-test',
        messages: [], messageStartIndex: 0, messageTotalCount: 1,
        state: { status: 'running', runId: 'run', revision: 6 } };
      const terminalStays = RNAssistantWorkspaceChat.latestSnapshot(terminal, delayed) === terminal;
      const acceptedStays = RNAssistantWorkspaceChat.latestSnapshot({ project: terminal.project,
        sessionId: terminal.sessionId, messages: terminal.messages, messageStartIndex: 0,
        messageTotalCount: 1, state: { status: 'completed', runId: 'run', revision: 7 } },
      { project: terminal.project, sessionId: terminal.sessionId, messages: [], messageStartIndex: 0,
        messageTotalCount: 1, state: { status: 'completed', runId: 'run', revision: 7 } }).messages.length === 1;
      const acceptedNotReplaced = RNAssistantWorkspaceChat.latestSnapshot({ project: terminal.project,
        sessionId: terminal.sessionId, messages: terminal.messages, messageStartIndex: 0,
        messageTotalCount: 1, state: { status: 'completed', runId: 'run', revision: 7 } },
      { project: terminal.project, sessionId: terminal.sessionId,
        messages: [{ Id: 'other', Role: 'assistant', Content: 'Different message.' }],
        messageStartIndex: 0, messageTotalCount: 1,
        state: { status: 'completed', runId: 'run', revision: 7 } }).messages[0].Id === 'accepted';
      const settings = state.settings; settings.MaxConsecutiveReflections = 0; renderSettings();
      const zero = document.getElementById('maxConsecutiveReflectionsInput').value === '0';
      return { retained, replaced, updated, reset, finished, scoped, attachedLive, attachedFinal,
        stableHistory, stableGrowingGroup,
        oneReflection, cleanClaims, uncleanClaims, reflectionMarkdown, retainedReflection, officeReflectionMarked,
        ordered, lostOwner, terminalStays, acceptedStays, acceptedNotReplaced, zero };
    });
    for (const [name, passed] of Object.entries(checks)) assert.equal(passed, true, name);
    for (const owner of ['project', 'plugin']) {
      if (owner === 'project') await page.evaluate(() => RNAssistantProjectChats.setScope('projects'));
      else {
        await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
      }
      await page.waitForFunction(expected => RNAssistantWorkspaceChat.owner() === expected &&
        RNAssistantWorkspaceChat.selectedSession(expected === 'project' ? 'preview-data-workspace' : 'preview-plugin-workspace'), owner);
      const idleTree = await page.evaluate(() => {
        const row = document.querySelector('#projectChatTree [data-tree-key]');
        const original = RNAssistantNavigationTree.render;
        let calls = 0;
        RNAssistantNavigationTree.render = function (...args) { calls++; return original.apply(this, args); };
        try {
          for (let index = 0; index < 30; index++) RNAssistantProjectChats.render();
          return { calls, retained: row === document.querySelector('#projectChatTree [data-tree-key]') };
        } finally { RNAssistantNavigationTree.render = original; }
      });
      assert.deepEqual(idleTree, { calls: 0, retained: true },
        owner + ': unchanged chat state does not replace the project tree');
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
      const repaint = await page.evaluate(() => {
        const tree = RNAssistantNavigationTree.render, transcript = RNAssistantWorkspaceChat.renderMessages;
        const leaf = document.querySelector('#messages .agent-stream-step');
        const snapshot = RNAssistantProjectChats.snapshot();
        const current = snapshot.state.modelProgress;
        const sequence = current.Sequence || current.sequence || 0;
        let treeCalls = 0, transcriptCalls = 0;
        RNAssistantNavigationTree.render = function (...args) { treeCalls++; return tree.apply(this, args); };
        RNAssistantWorkspaceChat.renderMessages = function (...args) { transcriptCalls++; return transcript.apply(this, args); };
        try {
          for (let index = 1; index <= 30; index++)
            RNAssistantWorkspaceChat.acceptProgress({ workspaceId: snapshot.project.workspaceId,
              sessionId: snapshot.sessionId, modelProgress: Object.assign({}, current,
                { Sequence: sequence + index, Message: 'Живое обновление без перестройки ленты.' }) });
          return { treeCalls, transcriptCalls, leafRetained: leaf === document.querySelector('#messages .agent-stream-step'),
            textUpdated: leaf.textContent.includes('Живое обновление без перестройки ленты.') };
        } finally {
          RNAssistantNavigationTree.render = tree;
          RNAssistantWorkspaceChat.renderMessages = transcript;
        }
      });
      assert.deepEqual(repaint, { treeCalls: 0, transcriptCalls: 0, leafRetained: true, textUpdated: true },
        owner + ': 30 progress updates paint only the live leaf');
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
      assert.equal(await page.evaluate(() => window.__workspaceProgress.some(event =>
        event.phase === 'session_changed' && event.cursor)), true,
        owner + ': terminal state arrived as an exact session change notification');
      await page.waitForFunction(() => {
        const snapshot = RNAssistantProjectChats.snapshot();
        return snapshot && (snapshot.state.status || snapshot.state.Status) !== 'running';
      });
      const terminal = await page.evaluate(() => {
        const snapshot = RNAssistantProjectChats.snapshot();
        const row = document.querySelector('[data-tree-key="project-chat:' + snapshot.project.workspaceId + ':' + snapshot.sessionId + '"]');
        return { stopHidden: document.getElementById('stopButton').hidden,
          runningBadge: !!row?.querySelector('.tree-run') };
      });
      assert.deepEqual(terminal, { stopHidden: true, runningBadge: false },
        owner + ': terminal snapshot clears Stop and the tree running badge');
    }
    assert.deepEqual(errors, []);
    console.log('PASS workspace progress: project/plugin bridge events, scoped live preview, retry reset, accepted reasoning and stable DOM');
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
