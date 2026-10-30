"use strict";
// Real settings form, no provider or native bridge.
const assert = require("node:assert/strict");
const path = require("node:path");
const fs = require("node:fs");
const { chromium } = require("playwright");

(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1100, height: 900 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto("file://" + path.resolve(__dirname, "../../web/index.html"));
    await page.locator('.rail-button[data-tab="settings"]').click();
    await page.evaluate(() => {
      state.settings = { AgentResponseMode: "json_object", ToolResultRole: "tool" };
      renderSettings();
    });
    await page.locator('#settingsNavTree .navigation-tree-label[data-settings-page="agent"]').click();
    assert.equal(await page.locator('#agentHistoryFormatInput').inputValue(), 'native_tools', 'saved tool history is preserved');
    assert.equal(await page.locator('#toolResultRoleInput').isDisabled(), true);
    assert.equal(await page.locator('#toolResultRoleInput').inputValue(), 'tool', 'fixed native role is explicit');
    assert.equal(await page.locator('#toolResultRoleField').isVisible(), true);
    await page.locator('#agentProtocolExample > summary').click();
    await page.locator('#agentResponseExample .rn-json-viewer').waitFor();
    for (const response of ['json_object', 'json_schema']) {
      await page.locator('#agentResponseModeInput').selectOption(response);
      assert.equal(await page.locator('#agentJsonFallbackField').isVisible(), response === 'json_schema');
      for (const instruction of ['developer', 'system', 'user']) {
        await page.locator('#systemPromptRoleInput').selectOption(instruction);
        for (const role of ['user', 'developer', 'tool']) {
          const history = role === 'tool' ? 'native_tools' : 'json_content';
          await page.locator('#agentHistoryFormatInput').selectOption(history);
          if (role !== 'tool') await page.locator('#toolResultRoleInput').selectOption(role);
          const saved = await page.evaluate(() => readSettings());
          assert.equal(saved.AgentResponseMode, response);
          assert.equal(saved.SystemPromptRole, instruction);
          assert.equal(saved.ToolResultRole, role);
          await page.evaluate(settings => { state.settings = settings; renderSettings(); }, saved);
          assert.equal(await page.locator('#agentHistoryFormatInput').inputValue(), history, 'save/reopen restores history choice');
          assert.equal(await page.locator('#toolResultRoleInput').inputValue(), role);
          assert.equal(await page.locator('#toolResultRoleInput').isDisabled(), role === 'tool');
          const example = await page.evaluate(() => ({
            response: JSON.parse(agentProtocolExampleViewers.agentResponseExample.getDocument().source),
            request: JSON.parse(agentProtocolExampleViewers.agentHistoryExample.getDocument().source)
          }));
          const decision = JSON.parse(example.response.content);
          assert.equal((response === 'json_schema' ? decision.response : decision).tool_calls.length, 1, 'new execute always has calls in content');
          assert.equal(example.response.tool_calls, undefined, 'example never requests native generation');
          assert.equal(example.request.response_format.type, response);
          assert.equal(example.request.messages[0].role, instruction);
          assert.equal(example.request.messages[2].role, role);
          const historyDecision = JSON.parse(example.request.messages[1].content);
          assert.equal(historyDecision.action, 'execute');
          if (role === 'tool') {
            assert.equal(historyDecision.tool_calls, undefined);
            assert.equal(example.request.messages[1].tool_calls[0].id, example.request.messages[2].tool_call_id);
          } else {
            assert.equal(historyDecision.tool_calls[0].id, 'T1');
            assert.match(example.request.messages[2].content, /^TOOL_RESULT:/);
          }
        }
      }
    }
    await page.locator('#agentHistoryFormatInput').selectOption('json_content');
    await page.locator('#toolResultRoleInput').selectOption('developer');
    await page.locator('#agentHistoryFormatInput').selectOption('native_tools');
    await page.locator('#agentHistoryFormatInput').selectOption('json_content');
    assert.equal(await page.locator('#toolResultRoleInput').inputValue(), 'developer', 'switching history preserves the in-form JSON role');
    assert.equal(await page.locator('#toolResultRoleInput option[value="tool"]').evaluate(option => option.disabled && option.hidden), true,
      'JSON history cannot select an unpaired tool role');
    await page.locator('#agentProtocolExample > summary').click();
    for (const theme of ['light', 'dark']) {
      await page.evaluate(theme => applyUiTheme({ UiTheme: theme }), theme);
      for (const width of [1100, 740, 390, 320]) {
        await page.setViewportSize({ width, height: 1000 });
        for (const history of ['native_tools', 'json_content']) {
          await page.locator('#agentHistoryFormatInput').selectOption(history);
          await page.locator('#agentProtocolTitle').scrollIntoViewIfNeeded();
          const geometry = await page.evaluate(() => {
            const root = document.querySelector('.settings-page[data-settings-page="agent"]');
            const bounds = root.getBoundingClientRect();
            return {
              overflow: Math.max(...Array.from(root.querySelectorAll(':scope > section')).map(section => section.scrollWidth - section.clientWidth)),
              fields: ['agentResponseModeInput', 'agentHistoryFormatInput', 'toolResultRoleInput'].map(id => {
                const control = document.getElementById(id), rect = control.getBoundingClientRect();
                const label = control.parentElement.querySelector('span').getBoundingClientRect();
                return { id, top: rect.top, left: rect.left, right: rect.right, gap: rect.top - label.bottom };
              }),
              left: bounds.left, right: bounds.right
            };
          });
          assert.ok(geometry.overflow <= 1, `${theme}/${width}/${history}: no horizontal overflow`);
          for (const field of geometry.fields) {
            assert.ok(field.left >= geometry.left - 1 && field.right <= geometry.right + 1, `${width}: ${field.id} stays inside the form`);
            assert.ok(field.gap >= 4 && field.gap <= 6, `${width}: ${field.id} label stays next to its control`);
          }
          if (width === 1100) {
            assert.ok(Math.abs(geometry.fields[0].top - geometry.fields[1].top) <= 1, 'response/history controls share a baseline');
            assert.equal(geometry.fields[1].left, geometry.fields[2].left, 'result role belongs to the history column');
          }
          if (process.env.AGENT_SETTINGS_SCREENSHOTS && (width === 1100 || width === 390)) {
            fs.mkdirSync(process.env.AGENT_SETTINGS_SCREENSHOTS, { recursive: true });
            await page.mouse.move(0, 0);
            await page.screenshot({ path: path.join(process.env.AGENT_SETTINGS_SCREENSHOTS, `agent-${theme}-${width}-${history}.png`), animations: 'disabled' });
          }
        }
      }
    }
    for (const width of [1100, 390]) {
      await page.setViewportSize({ width, height: 1000 });
      await page.locator('#agentProtocolExample').evaluate(node => { node.open = true; });
      await page.locator('#agentProtocolExample > summary').scrollIntoViewIfNeeded();
      assert.ok(await page.locator('#agentProtocolExample').evaluate(node => node.scrollWidth <= node.clientWidth + 1), 'shared JSON examples fit ' + width);
      if (process.env.AGENT_SETTINGS_SCREENSHOTS) await page.screenshot({
        path: path.join(process.env.AGENT_SETTINGS_SCREENSHOTS, `agent-example-${width}.png`), animations: 'disabled' });
    }
    await page.setViewportSize({ width: 1100, height: 1000 });
    await page.evaluate(() => {
      state.settings = { AgentResponseMode: 'json_object', ToolResultRole: 'user', SystemPromptRole: 'developer' };
      renderSettings();
      loadModelCatalog = () => Promise.resolve(false);
      saveSettingsWithPromptChanges = async settings => {
        await new Promise(resolve => { window.releaseSettingsSave = resolve; });
        return { settings, prompts: state.prompts };
      };
    });
    await page.locator('#agentResponseModeInput').selectOption('json_schema');
    await page.locator('#saveSettingsButton').click();
    await page.waitForFunction(() => !!window.releaseSettingsSave);
    await page.locator('#agentHistoryFormatInput').selectOption('native_tools');
    assert.equal(await page.locator('#saveSettingsButton').isDisabled(), true, 'editing cannot start a competing save');
    await page.evaluate(() => releaseSettingsSave());
    await page.waitForFunction(() => !settingsSaveInProgress);
    assert.equal(await page.evaluate(() => state.settings.ToolResultRole), 'user', 'applied state is the acknowledged snapshot');
    assert.equal(await page.locator('#agentHistoryFormatInput').inputValue(), 'native_tools', 'later edits survive the save response');
    assert.match(await page.locator('#agentProtocolApplyNote').innerText(), /черновик/);
    assert.match(await page.locator('#chatCopyFeedback').innerText(), /неприменённые/);
    await page.evaluate(() => { window.releaseSettingsSave = null; });
    await page.locator('#saveSettingsButton').click();
    await page.waitForFunction(() => !!window.releaseSettingsSave);
    await page.evaluate(() => releaseSettingsSave());
    await page.waitForFunction(() => !settingsSaveInProgress);
    assert.equal(await page.evaluate(() => state.settings.ToolResultRole), 'tool', 'next save applies the retained edit without reload');
    assert.match(await page.locator('#agentProtocolApplyNote').innerText(), /Сохранённый/);
    await page.locator('#systemPromptRoleInput').selectOption('system');
    await page.locator('.rail-button[data-tab="chat"]').click();
    await page.locator('.rail-button[data-tab="settings"]').click();
    assert.equal(await page.locator('#systemPromptRoleInput').inputValue(), 'system', 'leaving the tab does not discard unapplied controls');
    await page.evaluate(() => { saveSettingsWithPromptChanges = async () => { throw new Error('Завершите активный запуск.'); }; });
    await page.locator('#saveSettingsButton').click();
    await page.waitForFunction(() => !settingsSaveInProgress && document.getElementById('chatCopyFeedback').textContent.includes('не сохранены'));
    assert.equal(await page.evaluate(() => state.settings.SystemPromptRole), 'developer', 'failed save cannot change applied settings');
    assert.equal(await page.locator('#systemPromptRoleInput').inputValue(), 'system', 'failed save retains the draft');
    await page.evaluate(() => resetAgentSettingsToDefaults());
    assert.equal(await page.locator('#agentHistoryFormatInput').inputValue(), 'json_content');
    assert.equal(await page.evaluate(() => readSettings().ToolResultRole), 'user');
    assert.deepEqual(errors, []);
    console.log('PASS settings: protocol examples and combinations, responsive layout, acknowledged saves, retained edits and visible failures');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
