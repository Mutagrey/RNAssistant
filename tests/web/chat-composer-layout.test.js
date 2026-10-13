"use strict";

// Production markup/styles and composer actions, with a controlled bridge boundary.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require("playwright");
const root = path.resolve(__dirname, "../../web");
const read = file => fs.readFileSync(path.join(root, file), "utf8");

(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1100, height: 760 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    const html = read("index.html");
    await page.setContent(html.replace(/<script\b[^>]*>[\s\S]*?<\/script>/g, "")
      .replace(/<meta http-equiv="Content-Security-Policy"[^>]*>/, "").replace(/<link\b[^>]*>/g, ""));
    for (const match of html.matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g))
      await page.addStyleTag({ content: read(match[1]) });
    await page.evaluate(() => {
      window.$ = id => document.getElementById(id);
      window.state = { activeChatId: "chat", activeChatMode: "agent", draftAttachments: [], settings: {},
        contextUsage: { lastPromptTokens: 118000, lastCompletionTokens: 500,
          lastTotalTokens: 118500, contextWindowTokens: 258000 } };
      window.currentActiveSend = () => state.sending ? {} : null;
      window.isPendingChatSubmit = () => !!state.preparing;
      window.hasActiveMessageEdit = () => !!state.editing;
      window.runningMessageDelivery = () => "Steer";
      window.saveChatMode = mode => { state.activeChatMode = mode; renderSendControls(); };
      window.findEditingMessage = () => null;
    });
    for (const file of ["app-accent.js", "app-utils.js", "app-model-picker.js", "app-chat-composer.js", "app-chat-state.js", "app-context-inspector.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(() => {
      RNAssistantAccents.apply("blue", false);
      window.activeChatUsesCurrentDocument = () => !state.foreign;
      window.renderChatModelPicker = () => {};
      window.loadPromptContextInspector = () => { window.inspections = (window.inspections || 0) + 1; };
      $("chatModelButtonLabel").textContent = "Модель с длинным названием";
      $("compactContextButton").hidden = false;
      bindComposerPickers(); bindContextInspectorActions(); renderSendControls(); renderContextMeter();
    });

    for (const width of [1100, 740, 360]) {
      await page.setViewportSize({ width, height: 760 });
      await page.evaluate(width => document.querySelector(".chat-workspace").classList.toggle("is-sidebar-hidden", width < 800), width);
      for (const theme of ["light", "dark"]) {
        await page.evaluate(theme => { document.documentElement.dataset.theme = theme; RNAssistantAccents.apply("blue", false); }, theme);
        await page.locator("#composerAddButton").click();
        const menu = await page.locator("#composerAddMenu").boundingBox();
        const trigger = await page.locator("#composerAddButton").boundingBox();
        assert.ok(menu.x >= 0 && menu.x + menu.width <= width && menu.y >= 0 && menu.y + menu.height <= trigger.y,
          `menu overlays above the composer inside ${width}px / ${theme}`);
        for (const id of ["attachFileButton", "addSelectionContextButton", "compactContextButton"])
          assert.equal(await page.locator("#" + id).isVisible(), true);
        assert.equal(await page.locator('#chatModeMenu [aria-pressed="true"]').count(), 1);
        await page.keyboard.press("Escape");
        assert.equal(await page.locator("#composerAddPicker").getAttribute("open"), null);
        assert.equal(await page.evaluate(() => document.activeElement.id), "composerAddButton");
        await page.locator("#contextMeter").hover();
        const tooltip = await page.locator("#contextMeterTooltip").boundingBox();
        assert.ok(tooltip && tooltip.x >= 0 && tooltip.x + tooltip.width <= width && tooltip.y >= 0);
        assert.match(await page.locator("#contextMeterSummary").textContent(), /Осталось 54%/);
        assert.equal(await page.locator("#contextMeter").textContent().then(t => t.trim()), "");
        await page.keyboard.press("Escape");
        assert.equal(await page.locator("#contextMeterTooltip").isVisible(), false);
      }
    }
    console.log("PASS composer: menu and ring tooltip fit 1100/740/360px in both themes");

    await page.locator("#composerAddButton").click();
    await page.locator('#chatModeMenu button[data-mode="plan"]').click();
    assert.equal(await page.evaluate(() => state.activeChatMode), "plan");
    assert.equal(await page.locator("#composerAddPicker").getAttribute("open"), null);
    await page.locator("#composerAddButton").press("Enter");
    await page.locator("#addSelectionContextButton").click();
    assert.equal(await page.locator("#composerAddPicker").getAttribute("open"), null);
    await page.locator("#composerAddButton").click();
    await page.locator(".chatbar").click({ position: { x: 10, y: 10 } });
    assert.equal(await page.locator("#composerAddPicker").getAttribute("open"), null);

    await page.evaluate(() => { state.sending = true; renderSendControls(); });
    await page.locator("#composerAddButton").click();
    assert.equal(await page.locator("#chatModeMenu button:disabled").count(), 3);
    assert.equal(await page.locator("#attachFileButton").isEnabled(), true);
    assert.equal(await page.locator("#addSelectionContextButton").isEnabled(), true);
    await page.evaluate(() => { state.foreign = true; renderSendControls(); });
    assert.equal(await page.locator("#addSelectionContextButton").isDisabled(), true);
    await page.evaluate(() => { state.sending = false; });
    for (const flag of ["preparing", "editing", "bridgeUnavailable", "chatNavigationPending"]) {
      await page.evaluate(flag => { state[flag] = true; renderSendControls(); }, flag);
      assert.equal(await page.locator("#composerAddButton").getAttribute("aria-disabled"), "true", flag);
      assert.equal(await page.locator("#composerAddPicker").getAttribute("open"), null);
      await page.evaluate(flag => { state[flag] = false; }, flag);
    }
    await page.evaluate(() => { state.sending = false; state.foreign = false; renderSendControls(); });
    console.log("PASS composer: mode/actions close the menu, keyboard dismissal and runtime availability remain intact");

    await page.locator("#contextMeter").focus();
    assert.equal(await page.locator("#contextMeterTooltip").isVisible(), true);
    await page.locator("#contextMeter").press("Enter");
    assert.equal(await page.locator("#promptContextInspector").isVisible(), true);
    assert.equal(await page.locator("#contextMeterTooltip").isVisible(), false);
    assert.equal(await page.evaluate(() => window.inspections), 1);
    assert.equal(await page.locator("#openModelContextFromTokens").isVisible(), true);
    await page.locator("#closePromptContextInspectorButton").click();
    assert.equal(await page.locator("#promptContextInspector").isVisible(), false);
    assert.equal(await page.evaluate(() => document.activeElement.id), "contextMeter");
    assert.deepEqual(errors, []);
    console.log("PASS composer: ring opens the existing usage panel and restores keyboard focus on close");
    if (process.env.COMPOSER_SCREENSHOT) {
      await page.locator("#contextMeter").blur();
      await page.setViewportSize({ width: 740, height: 760 });
      await page.locator("#composerAddButton").click();
      await page.screenshot({ path: process.env.COMPOSER_SCREENSHOT });
      await page.keyboard.press("Escape");
      await page.setViewportSize({ width: 360, height: 760 });
      await page.evaluate(() => { document.documentElement.dataset.theme = "light"; RNAssistantAccents.apply("blue", false); });
      await page.locator("#contextMeter").hover();
      await page.screenshot({ path: process.env.COMPOSER_SCREENSHOT.replace(/\.png$/, "-tooltip.png") });
    }
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
