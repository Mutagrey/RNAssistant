"use strict";
// Real production form, scripted settings acknowledgements; no model/Office.
const assert = require("node:assert/strict");
const path = require("node:path");
const fs = require("node:fs");
const { chromium } = require("playwright");
(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto("file://" + path.resolve(__dirname, "../../web/index.html"));
    await page.locator('.rail-button[data-tab="settings"]').click();
    await page.evaluate(() => {
      state.settings = { AgentResponseMode: "json_object", ProtocolMigrationNotice: "История переведена на JSON-профиль. Для старого контракта нужен новый чат." };
      renderSettings({ committed: true });
    });
    if (await page.locator('#settingsPageSelect').isVisible()) await page.locator('#settingsPageSelect').selectOption('agent');
    else await page.locator('#settingsNavTree [data-tree-key="settings:agent"] .navigation-tree-label').click();
    assert.equal(await page.locator('#agentHistoryFormatInput, #toolResultRoleInput, #fallbackToJsonObjectInput').count(), 0);
    assert.match(await page.locator('#agentProtocolMigrationNote').innerText(), /новый чат/);
    await page.locator('#agentProtocolExample > summary').click();
    await page.locator('#agentResponseExample .rn-json-viewer').waitFor();
    for (const response of ['json_object', 'json_schema']) {
      await page.locator('#agentResponseModeInput').selectOption(response);
      for (const instruction of ['developer', 'system', 'user']) {
        await page.locator('#systemPromptRoleInput').selectOption(instruction);
        const saved = await page.evaluate(() => readSettings());
        assert.equal(saved.AgentResponseMode, response);
        assert.equal(saved.SystemPromptRole, instruction);
        assert.equal(saved.ToolResultRole, undefined);
        assert.equal(saved.FallbackToJsonObject, undefined);
        const example = await page.evaluate(() => ({
          response: JSON.parse(agentProtocolExampleViewers.agentResponseExample.getDocument().source),
          request: JSON.parse(agentProtocolExampleViewers.agentHistoryExample.getDocument().source)
        }));
        const wire = JSON.parse(example.response.content);
        const decision = response === 'json_schema' ? wire.response : wire;
        assert.equal(decision.tool_calls.length, 1);
        assert.equal(decision.message, undefined);
        assert.equal(example.response.tool_calls, undefined);
        assert.equal(example.request.response_format.type, response);
        assert.equal(example.request.messages[0].role, instruction);
        assert.equal(example.request.messages[2].role, 'user');
        const history = JSON.parse(example.request.messages[1].content);
        assert.deepEqual(history, decision, 'accepted decision retains its shape');
        const result = JSON.parse(example.request.messages[2].content);
        assert.equal(result.type, 'tool_results');
        assert.equal(result.results[0].call_index, 0);
        assert.equal(result.results[0].tool_call_id, undefined);
        assert.equal(history.tool_calls[0].id, undefined);
      }
    }
    for (const theme of ['light', 'dark']) {
      await page.evaluate(theme => applyUiTheme({ UiTheme: theme }), theme);
      for (const width of [1100, 390, 320]) {
        await page.setViewportSize({ width, height: 1000 });
        await page.locator('#agentProtocolTitle').scrollIntoViewIfNeeded();
        const geometry = await page.evaluate(() => {
          const root = document.querySelector('.settings-page[data-settings-page="agent"]');
          const control = document.getElementById('agentResponseModeInput');
          const label = control.parentElement.querySelector('span').getBoundingClientRect();
          const rect = control.getBoundingClientRect(), bounds = root.getBoundingClientRect();
          return { overflow: Math.max(...Array.from(root.querySelectorAll(':scope > section')).map(s => s.scrollWidth - s.clientWidth)),
            left: rect.left - bounds.left, right: bounds.right - rect.right, gap: rect.top - label.bottom };
        });
        assert.ok(geometry.overflow <= 1, `${theme}/${width}: no horizontal overflow`);
        assert.ok(geometry.left >= -1 && geometry.right >= -1);
        assert.ok(geometry.gap >= 4 && geometry.gap <= 6);
        if (process.env.AGENT_SETTINGS_SCREENSHOTS && width !== 320) {
          fs.mkdirSync(process.env.AGENT_SETTINGS_SCREENSHOTS, { recursive: true });
          await page.screenshot({ path: path.join(process.env.AGENT_SETTINGS_SCREENSHOTS, `json-profile-${theme}-${width}.png`) });
        }
      }
    }
    await page.setViewportSize({ width: 1100, height: 1000 });
    await page.evaluate(() => {
      state.settings = { AgentResponseMode: 'json_object', SystemPromptRole: 'developer' };
      renderSettings({ committed: true });
      loadModelCatalog = () => Promise.resolve(false);
      saveSettingsWithPromptChanges = async settings => {
        await new Promise(resolve => { window.releaseSettingsSave = resolve; });
        return { settings, prompts: state.prompts };
      };
    });
    await page.locator('#agentResponseModeInput').selectOption('json_schema');
    await page.locator('#saveSettingsButton').click();
    await page.waitForFunction(() => !!window.releaseSettingsSave);
    await page.locator('#agentResponseModeInput').selectOption('json_object');
    assert.equal(await page.locator('#saveSettingsButton').isDisabled(), true);
    await page.evaluate(() => releaseSettingsSave());
    await page.waitForFunction(() => !settingsSaveInProgress);
    assert.equal(await page.evaluate(() => state.settings.AgentResponseMode), 'json_schema');
    assert.equal(await page.locator('#agentResponseModeInput').inputValue(), 'json_object', 'later edits survive acknowledgement');
    assert.match(await page.locator('#agentProtocolApplyNote').innerText(), /черновик/);
    await page.evaluate(() => { window.releaseSettingsSave = null; });
    await page.locator('#saveSettingsButton').click();
    await page.waitForFunction(() => !!window.releaseSettingsSave);
    await page.evaluate(() => releaseSettingsSave());
    await page.waitForFunction(() => !settingsSaveInProgress);
    assert.equal(await page.evaluate(() => state.settings.AgentResponseMode), 'json_object', 'next save applies without reload');
    await page.locator('#systemPromptRoleInput').selectOption('system');
    await page.evaluate(() => { saveSettingsWithPromptChanges = async () => { throw new Error('Завершите активный запуск.'); }; });
    await page.locator('#saveSettingsButton').click();
    await page.waitForFunction(() => !settingsSaveInProgress && document.getElementById('chatCopyFeedback').textContent.includes('не сохранены'));
    assert.equal(await page.evaluate(() => state.settings.SystemPromptRole), 'developer');
    assert.equal(await page.locator('#systemPromptRoleInput').inputValue(), 'system');
    await page.evaluate(() => resetAgentSettingsToDefaults());
    assert.equal(await page.locator('#agentResponseModeInput').inputValue(), 'json_object');
    assert.deepEqual(errors, []);
    console.log('PASS settings: JSON profile, six instruction/response combinations, layout and acknowledged live saves');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
