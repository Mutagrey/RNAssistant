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
          const result = await original(type, payload);
          if ((type === 'getProjectChat' || type === 'getPluginChat') && result && result.state) {
            result.state.status = 'failed';
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
        await page.locator('#pluginDevelop').click();
      }
      const notice = page.locator('#workspaceChatNotice');
      await page.waitForFunction(() => document.querySelector('#workspaceChatNotice').textContent ===
        'Работа остановлена. Не удалось получить ответ от сервиса модели.');
      assert.equal(await notice.getAttribute('data-error'), 'true', owner + ': terminal failure is an alert');
      assert.deepEqual(errors, [], owner + ': no page errors');
      await page.close();
    }
    console.log('PASS shared chat: project and plugin display the Office run failure');
  } finally {
    if (browser) await browser.close();
    server.kill();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
