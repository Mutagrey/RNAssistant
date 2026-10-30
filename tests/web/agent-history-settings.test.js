"use strict";
// Real settings form, no provider or native bridge.
const assert = require("node:assert/strict");
const path = require("node:path");
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
    assert.equal(await page.locator('#toolResultRoleField').evaluate(node => node.classList.contains('hidden')), true);
    for (const response of ['json_object', 'json_schema']) {
      await page.locator('#agentResponseModeInput').selectOption(response);
      let saved = await page.evaluate(() => readSettings());
      assert.equal(saved.AgentResponseMode, response);
      assert.equal(saved.ToolResultRole, 'tool', 'response format cannot change native history');
      await page.locator('#agentHistoryFormatInput').selectOption('json_content');
      await page.locator('#toolResultRoleInput').selectOption('developer');
      saved = await page.evaluate(() => readSettings());
      assert.equal(saved.AgentResponseMode, response);
      assert.equal(saved.ToolResultRole, 'developer');
      await page.locator('#agentHistoryFormatInput').selectOption('native_tools');
      saved = await page.evaluate(() => readSettings());
      assert.equal(saved.ToolResultRole, 'tool');
      await page.evaluate(settings => { state.settings = settings; renderSettings(); }, saved);
      assert.equal(await page.locator('#agentHistoryFormatInput').inputValue(), 'native_tools', 'save/reopen restores history choice');
    }
    await page.evaluate(() => resetAgentSettingsToDefaults());
    assert.equal(await page.locator('#agentHistoryFormatInput').inputValue(), 'json_content');
    assert.equal(await page.evaluate(() => readSettings().ToolResultRole), 'user');
    assert.deepEqual(errors, []);
    console.log('PASS settings: response format and history representation, roles and persistence');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
