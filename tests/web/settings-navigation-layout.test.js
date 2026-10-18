"use strict";
// Real shipped Settings screen without Office/WebView2.
const assert = require("node:assert/strict");
const path = require("node:path");
const { chromium } = require("playwright");

(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1100, height: 760 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto("file://" + path.resolve(__dirname, "../../web/index.html"));
    await page.locator('.rail-button[data-tab="settings"]').click();
    assert.equal(await page.locator("#settingsNavTree .navigation-tree-icon svg").count(), 7, "demo navigation uses icons for every section");
    assert.equal(await page.locator("#settingsNavTree .navigation-tree-item:visible").count(), 7);
    assert.equal(await page.locator(".settings-nav #appVersion").count(), 0, "version is not a navigation item");
    assert.equal(await page.locator('.settings-page[data-settings-page="service"] #appVersion').count(), 1);

    await page.locator("#settingsNavSearchToggle").click();
    await page.locator("#settingsNavSearch").fill("мод");
    assert.deepEqual(await page.locator("#settingsNavTree .navigation-tree-label:visible").allTextContents(), ["Модель"]);
    assert.equal(await page.locator("#saveSettingsButton").isVisible(), false, "navigation search never dirties settings");
    await page.locator('#settingsNavTree .navigation-tree-label[data-settings-page="model"]').click();
    assert.equal(await page.locator('#settingsNavTree .navigation-tree-label[aria-current="page"]').getAttribute("data-settings-page"), "model");
    assert.ok(await page.locator('.settings-page[data-settings-page="model"]').isVisible());
    assert.equal(await page.locator("#modelSelect").count(), 0, "model selection has one visible field");
    assert.equal(await page.locator(".product-model-disclosure").count(), 2, "advanced model controls follow the demo disclosures");
    await page.evaluate(() => {
      state.modelCatalog = { models: [{ value: "model-a", title: "Модель A", maxTokens: 512, supportsImages: true,
        supportsReasoning: true, supportsAudio: false }], loaded: true, loading: false, error: "" };
      renderModelControls();
    });
    assert.equal(await page.locator("#modelOptions option").count(), 1);
    await page.evaluate(() => { window.firstModelOption = document.querySelector("#modelOptions option"); });
    await page.locator("#modelInput").fill("model-a");
    assert.equal(await page.evaluate(() => document.querySelector("#modelOptions option") === window.firstModelOption), true,
      "typing keeps the catalog options stable");
    await page.locator("#modelInput").press("Tab");
    assert.equal(await page.locator("#maxTokensInput").inputValue(), "512", "catalog defaults follow the chosen model");
    assert.deepEqual(await page.locator("#modelCapabilityBadges span").allTextContents(),
      ["Текст", "Изображения", "Рассуждения", "Аудио нет"]);
    await page.locator(".product-model-disclosure").last().locator("summary").click();
    assert.equal(await page.locator(".model-capability-flag").count(), 6, "selected model exposes editable capabilities");
    await page.locator('.model-capability-flag').filter({ hasText: "Аудио" }).locator("select").selectOption("true");
    assert.equal(await page.evaluate(() => state.settings.ModelAudioSupportOverrides["model-a"]), true);
    assert.equal(await page.locator("#settingsNavSearch").isVisible(), false, "blur collapses the search field without losing its filter");
    await page.locator("#settingsNavSearchToggle").click();
    await page.locator("#settingsNavSearch").press("Escape");
    assert.equal(await page.locator("#settingsNavTree .navigation-tree-item:visible").count(), 7, "Escape clears the filter");
    assert.equal(await page.locator("#settingsNavSearchToggle").getAttribute("aria-expanded"), "false");
    assert.deepEqual(errors, [], "settings screen has no page errors");
    if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT });
    console.log("PASS settings: demo navigation icons/search, active page and version location");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
