"use strict";
// Production placement/hover lifecycle with the existing synthetic bridge.
const assert = require("node:assert/strict");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");
(async () => {
  const server = spawn("python3", ["-B", "prototypes/serve_ui.py", "--port", "0"], { cwd: path.resolve(__dirname, "../..") });
  server.stderr.resume(); let browser;
  try {
    const address = await new Promise((resolve, reject) => {
      server.once("error", reject); server.once("exit", code => reject(Error("preview exit " + code)));
      server.stdout.on("data", bytes => { const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(bytes)); if (match) resolve(match[1]); });
    });
    browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, reducedMotion: "reduce" }), errors = [];
    page.setDefaultTimeout(8000); page.on("pageerror", error => errors.push(error.message));
    await page.goto(address + "preview/web/?scenario=ready");
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    const gear = page.locator(".section-rail .rail-settings"), settings = page.locator("[data-tab-panel-id=settings]");
    const layout = async name => { await page.locator("#workspaceLayout").click(); await page.getByRole("menuitem", { name, exact: true }).click(); };
    const groupAction = async name => {
      await page.locator(".dv-groupview").filter({ has: settings }).getByRole("button", { name: "Действия с вкладкой", exact: true }).click();
      await page.getByRole("menuitem", { name, exact: true }).click();
    };
    await layout("Сохранить раскладку");
    await page.evaluate(() => {
      window.settingsTest = { reads: [], nodes: ["settingsView", "baseUrlInput", "chatForm"].map(id => document.getElementById(id)) };
      const original = send; send = function (type, ...args) { settingsTest.reads.push(type); return original(type, ...args); };
    });
    for (let i = 0; i < 4; i++) {
      await gear.click(); await settings.getByRole("button").click();
    }
    assert.deepEqual(await page.evaluate(() => settingsTest.reads), [], "opening/closing the existing form performs no bridge reads");
    await gear.click(); await page.locator("#baseUrlInput").fill("https://draft.invalid/v1");
    await gear.click(); assert.equal(await settings.count(), 1, "gear is a singleton activation, not another tab");
    await page.locator("[data-tab-panel-id=conversation]").click(); await gear.click();
    assert.equal(await page.locator("#baseUrlInput").inputValue(), "https://draft.invalid/v1");
    await page.evaluate(() => renderSettings());
    assert.equal(await page.locator("#baseUrlInput").inputValue(), "https://draft.invalid/v1", "background render cannot erase an edited form");
    await page.locator(".dv-groupview").filter({ has: settings }).getByRole("button", { name: "Открыть экран справа", exact: true }).click();
    await page.locator(".dock-screen-chooser").getByRole("button", { name: "Настройки", exact: true }).click();
    assert.equal(await settings.count(), 1); assert.equal(await page.locator(".dock-group-actions").count(), 3);
    assert.equal(await page.locator("#baseUrlInput").inputValue(), "https://draft.invalid/v1");
    await groupAction("Собрать в одну группу");
    const panelCount = await page.locator(".dv-tab").count();
    page.once("dialog", dialog => dialog.dismiss()); await groupAction("Закрыть группу");
    assert.equal(await page.locator(".dv-tab").count(), panelCount, "cancel closes no part of a group");
    page.once("dialog", dialog => dialog.dismiss()); await settings.press("Delete");
    assert.equal(await settings.count(), 1, "keyboard close uses the same guard");
    page.once("dialog", dialog => dialog.dismiss()); await layout("Чат и инспектор");
    assert.equal(await settings.count(), 1, "preset cannot bypass the guard");
    page.once("dialog", dialog => dialog.dismiss()); await layout("Вернуть сохранённую");
    assert.equal(await settings.count(), 1, "restoring a layout cannot bypass the guard");
    page.once("dialog", dialog => dialog.accept()); await settings.getByRole("button").click();
    assert.equal(await settings.count(), 0); await gear.click();
    assert.equal(await page.locator("#baseUrlInput").inputValue(), "https://draft.invalid/v1", "confirmed close parks the draft until app restart");
    assert.equal(await page.evaluate(() => settingsTest.nodes.every(node => document.getElementById(node.id) === node)), true);
    assert.equal(await page.locator("#tab-settings").count(), 0, "no legacy Settings section/placement path");

    await page.evaluate(() => {
      settingsTest.catalogSend = send; state.modelCatalog.loaded = false;
      send = (type, ...args) => type === "getModelCatalog" ? new Promise(resolve => { settingsTest.catalogReply = resolve; }) : settingsTest.catalogSend(type, ...args);
    });
    await page.locator('#settingsNavTree [data-tree-key="settings:model"] .navigation-tree-label').click();
    await page.waitForFunction(() => !!settingsTest.catalogReply);
    await page.locator("[data-tab-panel-id=conversation]").click();
    await page.evaluate(() => { send = settingsTest.catalogSend; settingsTest.catalogReply({ models: [] }); });
    await page.waitForFunction(() => !state.modelCatalogRequest);
    assert.equal(await page.evaluate(() => state.modelCatalog.loaded), false, "a late form catalog cannot hydrate after deactivation");
    await gear.click();
    await page.locator('#settingsNavTree [data-tree-key="settings:connection"] .navigation-tree-label').click();
    assert.equal(await page.locator("#baseUrlInput").inputValue(), "https://draft.invalid/v1");

    // A late save applies only its submitted values; failures keep the edited form.
    await page.evaluate(() => {
      saveSettingsWithPromptChanges = settings => new Promise((resolve, reject) => {
        settingsTest.saved = settings; settingsTest.resolve = () => resolve({ settings, prompts: state.prompts }); settingsTest.reject = reject;
      });
    });
    await page.locator("#saveSettingsButton").click();
    await page.locator("#baseUrlInput").fill("https://newer.invalid/v1");
    await settings.getByRole("button").click(); assert.equal(await settings.count(), 1, "pending save remains visible");
    await page.evaluate(() => settingsTest.resolve()); await page.waitForFunction(() => !settingsSaveInProgress);
    assert.equal(await page.locator("#baseUrlInput").inputValue(), "https://newer.invalid/v1");
    assert.equal(await page.evaluate(() => settingsDirty), true);
    await page.locator("#saveSettingsButton").click(); await page.evaluate(() => settingsTest.reject(Error("Проверочная ошибка")));
    await page.waitForFunction(() => !settingsSaveInProgress);
    assert.equal(await page.evaluate(() => settingsDirty), true);
    await page.locator("#saveSettingsButton").click(); await page.evaluate(() => settingsTest.resolve());
    await page.waitForFunction(() => !settingsSaveInProgress && !settingsDirty);

    // Hover is usable across the gap; keyboard and Escape share the same menu.
    await page.mouse.move(1400, 880); await page.evaluate(() => RNAssistantRailPopovers.hide()); await gear.hover();
    const card = page.locator(".rail-hover-card:visible"); await card.waitFor();
    assert.equal(await card.getByRole("button").count(), 6);
    const rowStyle = locator => locator.evaluate(node => { const s = getComputedStyle(node); return [s.padding, s.minHeight, s.fontSize, s.gap, s.borderRadius]; });
    const hoverStyle = await rowStyle(card.getByRole("button").first());
    await card.getByRole("button", { name: "Интерфейс", exact: true }).hover();
    await card.getByRole("button", { name: "Интерфейс", exact: true }).click();
    await page.locator('[data-settings-page="interface"].active').waitFor();
    await page.locator("#workspacePanels").click();
    assert.deepEqual(await rowStyle(page.getByRole("menuitem").first()), hoverStyle, "rail and Dock menus share row geometry");
    await page.keyboard.press("Escape"); await gear.focus(); await page.keyboard.press("Tab");
    assert.equal(await page.locator(".rail-hover-body button").first().evaluate(n => n === document.activeElement), true);
    await page.keyboard.press("End"); assert.equal(await page.locator(".rail-hover-body button").last().evaluate(n => n === document.activeElement), true);
    await page.keyboard.press("Escape");
    assert.equal(await page.locator(".rail-hover-card").isVisible(), false, "Escape does not reopen the menu when focus returns");
    assert.equal(await gear.evaluate(n => n === document.activeElement), true);
    await page.locator('[data-section="vba"]').hover(); await page.locator('.rail-hover-card[role="tooltip"]:visible').waitFor();
    assert.equal(await page.locator(".rail-hover-title").textContent(), "VBA");
    await page.mouse.move(1400, 880); await page.locator(".rail-hover-card").waitFor({ state: "hidden" });

    // Stale plugin hover results must not replace a newer menu, even on the same button.
    await page.evaluate(() => { const original = send; settingsTest.pending = []; send = (type, ...args) => type === "getRemovedPlugins" ? new Promise(resolve => settingsTest.pending.push(resolve)) : original(type, ...args); });
    await page.locator("#pluginAdd").hover(); await page.waitForFunction(() => settingsTest.pending.length === 1);
    await gear.hover(); await page.waitForFunction(() => document.querySelector('.rail-hover-card').dataset.kind === "settings-menu");
    await page.locator("#pluginAdd").hover(); await page.waitForFunction(() => settingsTest.pending.length === 2);
    await page.evaluate(() => settingsTest.pending[0]({ items: [{ name: "Устаревший ответ" }] }));
    assert.doesNotMatch(await card.textContent(), /Устаревший ответ/);
    await page.evaluate(() => settingsTest.pending[1]({ items: [] }));
    await card.getByRole("button", { name: "Создать плагин", exact: true }).waitFor();
    await page.keyboard.press("Escape");

    await gear.click(); await page.setViewportSize({ width: 420, height: 900 });
    await page.locator("#settingsPageSelect").waitFor();
    await page.waitForFunction(() => {
      const tab = document.querySelector('[data-tab-panel-id=settings] .dv-default-tab-content');
      const rect = tab.getBoundingClientRect();
      return document.elementsFromPoint(rect.left + 5, rect.top + rect.height / 2).includes(tab);
    }); // The active title is scrolled into view when tabs overflow.
    for (const theme of ["light", "dark"]) {
      await page.evaluate(theme => { applyUiTheme({ UiTheme: theme }, false); applyUiFontScale({ UiFontScale: 1.25 }); }, theme);
      for (const section of ["connection", "model", "context", "agent", "interface", "protection"]) {
        await page.locator("#settingsPageSelect").selectOption(section);
        assert.equal(await page.locator(".settings-pages").evaluate(n => n.scrollWidth <= n.clientWidth + 1), true, theme + ": " + section + " fits its Dockview pane");
      }
    }
    assert.equal(await page.evaluate(() => settingsDirty), false, "navigation never edits the form");
    await page.waitForFunction(() => JSON.parse(localStorage.getItem("rnassistant.ui.dock.s1.v1"))?.layout.panels.settings);
    await page.reload(); await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    assert.equal(await settings.count(), 1, "layout restores exactly one Settings view");
    await gear.click(); await page.locator("#settingsView:visible").waitFor();
    assert.deepEqual(errors, []);
    console.log("PASS Settings Dock: singleton/DOM/draft, group/preset/keyboard guards, save race/failure, shared hover style/keyboard/stale responses, narrow/theme/zoom");
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
