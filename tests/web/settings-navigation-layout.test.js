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
    assert.equal(await page.locator("#appVersion").count(), 0, "version no longer takes space in the service page");
    await page.evaluate(() => { state.appVersion = "16.1.0-dev+g2a2f69c38d90"; });

    await page.locator("#settingsNavSearchToggle").click();
    await page.locator("#settingsNavSearch").fill("мод");
    assert.deepEqual(await page.locator("#settingsNavTree .navigation-tree-label:visible").allTextContents(), ["Модель"]);
    assert.equal(await page.locator("#saveSettingsButton").isVisible(), false, "navigation search never dirties settings");
    await page.locator('#settingsNavTree .navigation-tree-label[data-settings-page="model"]').click();
    assert.equal(await page.locator('#settingsNavTree .navigation-tree-label[aria-current="page"]').getAttribute("data-settings-page"), "model");
    assert.ok(await page.locator('.settings-page[data-settings-page="model"]').isVisible());
    assert.equal(await page.locator("#modelSelect").count(), 0, "model selection has one visible field");
    assert.equal(await page.locator(".product-model-disclosure").count(), 1, "generation details remain collapsible");
    await page.evaluate(() => {
      state.modelCatalog = { models: [{ value: "model-a", title: "Модель A", maxTokens: 512, supportsImages: true,
        maxContextTokens: 1000000, maxOutputTokens: 32000, supportsReasoning: true, supportsAudio: false },
        { value: "model-b", title: "Модель B", maxContextTokens: 128000, supportsAudio: true }],
        loaded: true, loading: false, error: "" };
      renderModelControls();
    });
    assert.equal(await page.locator("#modelOptions option").count(), 2);
    await page.evaluate(() => { window.firstModelOption = document.querySelector("#modelOptions option"); });
    await page.locator("#modelInput").fill("model-a");
    assert.equal(await page.evaluate(() => document.querySelector("#modelOptions option") === window.firstModelOption), true,
      "typing keeps the catalog options stable");
    await page.locator("#modelInput").press("Tab");
    assert.equal(await page.locator("#maxTokensInput").inputValue(), "512", "catalog defaults follow the chosen model");
    assert.deepEqual(await page.locator("#modelCapabilityBadges span").allTextContents(),
      ["Текст", "Изображения", "Рассуждения", "Аудио нет"]);
    assert.equal(await page.locator(".model-capability-item").count(), 2, "the full catalog is visible");
    assert.equal(await page.locator("#modelCapabilityList input, #modelCapabilityList select").count(), 0,
      "collapsed rows create editors only when opened");
    const dirtyBeforeSearch = await page.evaluate(() => settingsDirty);
    await page.locator("#modelCapabilitySearch").fill("Модель B");
    assert.equal(await page.locator(".model-capability-item").count(), 1, "catalog search filters by title");
    assert.equal(await page.evaluate(() => settingsDirty), dirtyBeforeSearch, "catalog search does not change settings");
    await page.locator("#modelCapabilitySearch").fill("");
    const first = page.locator('.model-capability-item[data-model-value="model-a"]');
    await first.locator("summary").click();
    await first.locator(".model-capability-body").waitFor();
    assert.equal(await first.locator(".model-capability-flag").count(), 7, "each model exposes all editable capabilities");
    await first.locator('.model-capability-flag').filter({ hasText: "Аудио" }).locator("select").selectOption("true");
    assert.equal(await page.evaluate(() => state.settings.ModelAudioSupportOverrides["model-a"]), true);
    await first.locator('.model-capability-flag').filter({ hasText: "Передача reasoning" }).locator("select").selectOption("reasoning_effort");
    assert.equal(await page.evaluate(() => state.settings.ModelCapabilities["model-a"].ReasoningRequestMode), "reasoning_effort");
    await first.locator('.model-capability-flag').filter({ hasText: "Окно модели" }).locator("input").fill("500000");
    await first.locator('.model-capability-flag').filter({ hasText: "Окно модели" }).locator("input").press("Tab");
    assert.equal(await page.evaluate(() => readSettings().ModelCapabilities["model-a"].MaxContextTokens), 500000);
    assert.equal(await page.locator(".model-capability-item").count(), 2, "editing keeps the other catalog rows");
    assert.deepEqual(await page.evaluate(() => {
      const original = state.settings.ModelCapabilities;
      state.settings.ModelCapabilities = { "MODEL-A": { MaxContextTokens: 700000 } };
      const saved = modelCapabilitiesForSettings();
      state.settings.ModelCapabilities = original;
      return Object.keys(saved).filter(key => key.toLowerCase() === "model-a").map(key => [key, saved[key].MaxContextTokens]);
    }), [["MODEL-A", 700000]], "catalog updates preserve a saved model ID's casing and value");
    assert.equal(await page.locator("#settingsNavSearch").isVisible(), false, "blur collapses the search field without losing its filter");
    await page.locator("#settingsNavSearchToggle").click();
    await page.locator("#settingsNavSearch").press("Escape");
    assert.equal(await page.locator("#settingsNavTree .navigation-tree-item:visible").count(), 7, "Escape clears the filter");
    assert.equal(await page.locator("#settingsNavSearchToggle").getAttribute("aria-expanded"), "false");
    await page.locator('.section-rail [data-section="settings"]').hover();
    assert.match(await page.locator('.rail-hover-card[data-kind="settings-menu"] .rail-hover-title').textContent(),
      /Настройки.*v16\.1\.0-dev · 2a2f69c/, "settings hover title includes the current version");
    assert.equal(await page.locator('.rail-hover-version').evaluate(node => getComputedStyle(node).fontWeight), "400");
    assert.equal(await page.locator('.rail-hover-card[data-kind="settings-menu"] button svg').count(), 7,
      "hover menu offers an icon for each settings page");
    await page.locator('.settings-pages').evaluate(node => { node.scrollTop = 200; });
    await page.locator('.rail-hover-card').getByRole('button', { name: 'Контекст и вложения' }).click();
    assert.ok(await page.locator('.settings-page[data-settings-page="context"]').isVisible());
    assert.equal(await page.locator('.settings-pages').evaluate(node => node.scrollTop), 0,
      "switching settings pages starts at the heading");
    await page.locator("#contextLimitInput").fill("32768");
    assert.match(await page.locator("#contextWindowSummary").textContent(), /500\s?000.*32\s?768/,
      "the application limit caps the chosen model window");
    await page.evaluate(() => {
      window.catalogRequests = [];
      state.bridgeUnavailable = false;
      state.modelCatalog.loaded = false;
      loadModelCatalog = useForm => { window.catalogRequests.push(useForm); return Promise.resolve(true); };
    });
    await page.locator('#settingsNavTree .navigation-tree-label[data-settings-page="model"]').click();
    assert.deepEqual(await page.evaluate(() => window.catalogRequests), [true],
      "opening Model loads the catalog with current form settings");
    await page.evaluate(() => { state.bridgeUnavailable = true; });
    for (const name of ["connection", "model", "context", "agent", "interface", "protection", "service"]) {
      await page.evaluate(pageName => {
        document.querySelectorAll('.settings-page').forEach(node =>
          node.classList.toggle('active', node.dataset.settingsPage === pageName));
      }, name);
      assert.equal(await page.locator('.settings-pages').evaluate(node => node.scrollWidth <= node.clientWidth + 1), true,
        name + " fits the settings pane without horizontal overflow");
    }
    await page.locator('.section-rail [data-section="chat"]').click();
    await page.locator('.section-rail [data-section="settings"]').focus();
    assert.equal(await page.locator('.rail-hover-card[data-kind="settings-menu"] button').count(), 7,
      "keyboard focus opens the same settings menu");
    await page.keyboard.press('Tab');
    assert.equal(await page.locator('.rail-hover-card button').first().evaluate(node => node === document.activeElement), true);
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('.section-rail [data-section="settings"]').evaluate(node => node === document.activeElement), true);
    await page.setViewportSize({ width: 560, height: 760 });
    await page.mouse.move(300, 300);
    await page.evaluate(() => RNAssistantRailPopovers.hide());
    if (await page.locator('#productMobileMenuButton').getAttribute('aria-expanded') !== 'true')
      await page.locator('#productMobileMenuButton').click();
    await page.locator('.section-rail [data-section="settings"]').hover();
    await page.locator('.rail-hover-card').getByRole('button', { name: 'Помощь и обслуживание' }).click();
    assert.equal(await page.locator('.settings-page.active .settings-heading h2').evaluate(node =>
      node.getBoundingClientRect().top >= 0), true, "narrow service heading is not clipped");
    assert.equal(await page.locator('.settings-pages').evaluate(node => node.scrollWidth <= node.clientWidth + 1), true,
      "narrow settings fit without horizontal overflow");
    assert.deepEqual(errors, [], "settings screen has no page errors");
    if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT });
    console.log("PASS settings: demo navigation icons/search, active page and version location");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
