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
      const page = await browser.newPage(), errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.goto(address + 'preview/web/?scenario=ready');
      await page.waitForFunction(() => state.activeChatId === 'preview-chat' && !state.initializePromise);
      if (owner === 'project') await page.evaluate(() => RNAssistantProjectChats.setScope('projects'));
      else {
        await page.evaluate(() => RNAssistantPlugins.select('data-templates'));
        await page.locator('#pluginSourceChat').click();
      }
      await page.waitForFunction(() => !!RNAssistantProjectChats.snapshot());
      for (const scenario of ['drift', 'requested', 'replaced', 'terminal', 'unknown', 'late-error', 'drift-twice', 'leave']) {
        const result = await page.evaluate(async ({ scenario, owner }) => {
          const original = send, snapshot = RNAssistantProjectChats.snapshot();
          const target = { workspaceId: snapshot.project.workspaceId, sessionId: snapshot.sessionId };
          const saved = JSON.parse(JSON.stringify(snapshot.state)), calls = [];
          let reads = 0;
          const running = { ...saved, revision: saved.revision + 10, status: 'running',
            runId: 'cancel-run', cursor: 'events1.observed', runViewState: null, modelProgress: null };
          snapshot.state = running;
          const fresh = JSON.parse(JSON.stringify(snapshot));
          fresh.state = { ...running, revision: running.revision + 1, cursor: 'events1.current' };
          if (scenario === 'replaced') fresh.state.runId = 'new-run';
          if (scenario === 'terminal') fresh.state.status = 'completed';
          function refusal(code) { return Object.assign(Error(code), { code }); }
          send = async function (type, payload) {
            if (type === 'getProjectChat' && payload.workspaceId === target.workspaceId && payload.sessionId === target.sessionId) {
              reads++;
              return JSON.parse(JSON.stringify(fresh));
            }
            if (type !== 'cancelProjectChat') return original(type, payload);
            calls.push({ ...payload });
            if (scenario === 'leave') await RNAssistantProjectChats.setScope('office');
            if (scenario === 'late-error') {
              snapshot.state = { ...running, runId: 'new-run' };
              RNAssistantProjectChats.render();
              throw refusal('Old run cancellation failed');
            }
            if (scenario === 'unknown') throw refusal('project_chat_cancel_unknown');
            if (scenario === 'requested') return { ...fresh, cancelStatus: 'Requested' };
            if (calls.length === 1 || scenario === 'drift-twice' || scenario === 'leave')
              throw refusal('project_chat_stale');
            return { ...fresh, cancelStatus: 'Requested', state: { ...fresh.state,
              revision: fresh.state.revision + 1, status: 'cancelled' } };
          };
          try {
            RNAssistantProjectChats.render();
            const stopping = chatComposer().dispatch('stop');
            const busy = document.querySelector('#stopButton').disabled;
            await stopping;
            const current = RNAssistantProjectChats.snapshot();
            return { calls, reads, busy, status: current && current.state.status,
              buttonDisabled: document.querySelector('#stopButton').disabled,
              notice: document.querySelector('#workspaceChatNotice').textContent };
          } finally {
            send = original;
            // Reopen from the mock after each isolated race to reset revisions/owner.
            await RNAssistantProjectChats.setScope('office');
            if (owner === 'plugin') await RNAssistantPlugins.select('data-templates', target.sessionId);
            else await RNAssistantProjectChats.open(target.workspaceId, target.sessionId);
          }
        }, { scenario, owner });
        if (scenario === 'drift') {
          assert.equal(result.calls.length, 2, owner + ': stale cancellation refreshes and retries once');
          assert.deepEqual(result.calls.map(call => [call.runId, call.cursor]),
            [['cancel-run', 'events1.observed'], ['cancel-run', 'events1.current']]);
          assert.equal(result.status, 'cancelled');
          assert.equal(result.busy, true, owner + ': Stop acknowledges the click immediately');
        } else if (scenario === 'drift-twice') {
          assert.equal(result.calls.length, 2, owner + ': no unbounded cancellation retry');
          assert.equal(result.buttonDisabled, false, owner + ': second refusal allows an explicit retry');
        } else {
          assert.equal(result.calls.length, 1, owner + ': ' + scenario + ' must not send another cancellation');
          if (scenario === 'unknown' || scenario === 'leave' || scenario === 'requested') assert.equal(result.reads, 0);
          if (scenario === 'requested') {
            assert.equal(result.status, 'running', owner + ': signal acknowledgement is not terminal evidence');
            assert.equal(result.notice, 'Отмена запрошена…');
          }
          if (scenario === 'replaced') assert.equal(result.status, 'running');
          if (scenario === 'late-error') {
            assert.equal(result.buttonDisabled, false);
            assert.doesNotMatch(result.notice, /Old run/);
          }
          if (scenario === 'terminal') assert.equal(result.status, 'completed');
        }
      }
      assert.deepEqual(errors, [], owner + ': no page errors');
      await page.close();
    }
    console.log('PASS workspace Stop: bounded stale refresh, exact run, terminal, unknown and navigation');
  } finally {
    if (browser) await browser.close();
    server.kill();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
