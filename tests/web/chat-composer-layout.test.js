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
    for (const file of ["app-icons.js", "app-run-view-state.js", "app-accent.js", "app-utils.js", "app-model-picker.js", "app-chat-composer.js", "app-chat-state.js", "app-context-inspector.js", "app-diagnostics.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(() => {
      RNAssistantAccents.apply("blue", false);
      window.activeChatUsesCurrentDocument = () => !state.foreign;
      window.renderChatModelPicker = () => {};
      window.loadPromptContextInspector = () => { window.inspections = (window.inspections || 0) + 1; };
      $("chatModelButtonLabel").textContent = "Модель с длинным названием";
      $("compactContextButton").hidden = false;
      bindComposerPickers(); bindContextInspectorActions(); bindDiagnosticsActions(); renderSendControls(); renderContextMeter();
    });

    for (const width of [1800, 1100, 740, 360]) {
      await page.setViewportSize({ width, height: 760 });
      await page.evaluate(width => document.querySelector(".shell").classList.toggle("navigation-closed", width < 800), width);
      if (width === 1800) {
        const widths = await page.evaluate(() => {
          const main = document.querySelector('#tab-chat .chat-main');
          const messages = main.querySelector('.messages'), composer = main.querySelector('.composer');
          const style = getComputedStyle(messages);
          return { main: main.clientWidth, composer: composer.getBoundingClientRect().width,
            content: messages.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight) };
        });
        assert.ok(widths.content > widths.main - 60, 'transcript fills the central pane');
        assert.ok(widths.composer > widths.main - 60, 'composer fills the central pane');
      }
      for (const theme of ["light", "dark"]) {
        await page.evaluate(theme => { document.documentElement.dataset.theme = theme; RNAssistantAccents.apply("blue", false); }, theme);
        await page.locator("#composerAddButton").click();
        const menu = await page.locator("#composerAddMenu").boundingBox();
        const trigger = await page.locator("#composerAddButton").boundingBox();
        assert.ok(menu.x >= 0 && menu.x + menu.width <= width && menu.y >= 0 && menu.y + menu.height <= trigger.y,
          `menu overlays above the composer inside ${width}px / ${theme}`);
        assert.equal(await page.locator("#toggleChatResourcesButton").count(), 0);
        for (const id of ["attachFileButton", "addSelectionContextButton", "compactContextButton"])
          assert.equal(await page.locator("#" + id).isVisible(), true);
        assert.equal(await page.locator('#chatModeMenu button').count(), 1);
        assert.equal(await page.locator('#chatModeMenu [aria-pressed="false"]').count(), 1);
        const rows = await page.locator("#composerAddMenu .composer-menu-action").evaluateAll(buttons =>
          buttons.map(button => {
            const row = button.getBoundingClientRect();
            const icon = button.querySelector(".composer-menu-icon").getBoundingClientRect();
            const label = button.querySelector(".composer-menu-label").getBoundingClientRect();
            return { height: row.height, iconX: icon.x, labelX: label.x,
              aligned: Math.abs(icon.y + icon.height / 2 - label.y - label.height / 2) < 1,
              fits: button.scrollWidth <= button.clientWidth };
          }));
        assert.ok(rows.every(row => row.height === rows[0].height && row.iconX === rows[0].iconX &&
          row.labelX === rows[0].labelX && row.aligned && row.fits), "menu rows and icons stay aligned");
        await page.keyboard.press("Escape");
        assert.equal(await page.locator("#composerAddPicker").getAttribute("open"), null);
        assert.equal(await page.evaluate(() => document.activeElement.id), "composerAddButton");
        await page.locator("#contextMeter").hover();
        const tooltip = await page.locator("#contextMeterTooltip").boundingBox();
        assert.ok(tooltip && tooltip.x >= 0 && tooltip.x + tooltip.width <= width && tooltip.y >= 0);

        assert.match(await page.locator("#contextMeterSummary").textContent(), /Осталось 54%/);
        assert.equal(await page.locator("#contextMeter").textContent().then(t => t.trim()), "");
        const ring = await page.locator("#contextMeter").boundingBox();
        const model = await page.locator("#chatModelButton").boundingBox();
        assert.ok(ring && model && ring.x + ring.width <= model.x && Math.abs(ring.y - model.y) < 2,
          `context ring precedes the model on one row at ${width}px / ${theme}`);
        assert.equal(await page.locator("#chatModelButtonLabel").textContent(), "Модель с длинным названием");
        assert.equal(await page.locator("#chatModelButton .composer-picker-trigger-icon").count(), 0);
        assert.equal(await page.locator("#chatModelButton").innerText(), "Модель с длинным названием");
        assert.equal(await page.locator("#modelConnectionWrap").count(), 0);
        await page.keyboard.press("Escape");
        assert.equal(await page.locator("#contextMeterTooltip").isVisible(), false);
      }
    }
    await page.evaluate(() => {
      state.contextUsage = {}; renderContextMeter();
      document.getElementById("messages").innerHTML = "<div style='min-height:2400px'>Длинный диалог</div>";
    });
    assert.equal(await page.locator("#contextMeterWrap").isVisible(), false, "unknown context occupies no toolbar space");
    for (const height of [760, 560]) {
      await page.setViewportSize({ width: 360, height });
      const contained = await page.locator("#chatForm").evaluate(form => {
        const rect = form.getBoundingClientRect();
        return Array.from(form.querySelectorAll(".composer-toolbar button, .composer-toolbar summary"))
          .filter(node => node.getClientRects().length && !node.closest(".composer-picker-menu"))
          .every(node => { const b = node.getBoundingClientRect(); return b.top >= rect.top && b.bottom <= rect.bottom && b.left >= rect.left && b.right <= rect.right; });
      });
      assert.equal(contained, true, "long transcript cannot shrink composer around its controls");
    }
    await page.setViewportSize({ width: 360, height: 760 });
    await page.evaluate(() => {
      state.contextUsage = { lastPromptTokens: 100, contextWindowTokens: 1000 }; renderContextMeter();
    });
    assert.equal(await page.locator("#contextMeterWrap").isVisible(), true, "usage arrival restores the indicator");
    await page.evaluate(() => {
      state.activeRunViewState = { runId: "uncertain", lifecycle: "failed", executionHealth: "unknown", unknownEffects: 1 };
      renderSendControls();
    });
    assert.equal(await page.locator("#chatRunNotice").isVisible(), true);
    assert.match(await page.locator("#chatRunNotice").innerText(), /не подтверждён/);
    assert.equal(await page.locator("#chatRunNotice button").count(), 0, "unknown effects offer no automatic retry");
    await page.evaluate(() => { state.activeRunViewState = null; renderSendControls(); });
    assert.equal(await page.locator("#chatRunNotice").isVisible(), false, "next chat/run clears the notice");
    console.log("PASS composer: menu and context tooltip fit 1800/1100/740/360px; ring precedes plain model label, ping is absent");
    assert.equal(await page.locator("#testModelConnectionButton").count(), 1,
      "manual model connection check remains in settings");

    await page.evaluate(() => {
      state.settings.AutoConfirmToolActions = true; state.mutationPolicy = "Deny"; renderSendControls();
    });
    assert.equal(await page.locator("#chatMutationPolicy, #chatPolicyStatus").count(), 0);
    await page.locator("#composerAddButton").click();
    await page.locator('#chatModeMenu button[data-mode="plan"]').click();
    assert.equal(await page.evaluate(() => state.activeChatMode), "plan");
    assert.equal(await page.locator("#chatPlanButton").getAttribute("aria-pressed"), "true");
    assert.equal(await page.locator("#composerAddPicker").getAttribute("open"), null);
    await page.locator("#composerAddButton").click();
    await page.locator('#chatModeMenu button[data-mode="plan"]').click();
    assert.equal(await page.evaluate(() => state.activeChatMode), "agent");
    assert.equal(await page.evaluate(() => state.mutationPolicy), "Deny", "planning toggle preserves permissions");
    assert.equal(await page.evaluate(() => state.settings.AutoConfirmToolActions), true);
    await page.evaluate(() => { state.settings.AutoConfirmToolActions = false; state.activeChatMode = "agent"; renderChatModePicker(); });
    await page.keyboard.press("Escape");
    await page.locator("#composerAddButton").press("Enter");
    await page.locator("#addSelectionContextButton").click();
    assert.equal(await page.locator("#composerAddPicker").getAttribute("open"), null);
    await page.locator("#composerAddButton").click();
    await page.locator("#chatDropZone .chatbar").click({ position: { x: 55, y: 10 } });
    assert.equal(await page.locator("#composerAddPicker").getAttribute("open"), null);

    await page.evaluate(() => { state.sending = true; renderSendControls(); });
    await page.evaluate(() => { state.chatReadOnly = true; renderSendControls(); });
    assert.match(await page.locator("#chatInput").getAttribute("placeholder"), /История чата · создайте новый чат/);
    assert.equal(await page.locator("#chatPlanButton").isDisabled(), true);
    await page.evaluate(() => { state.chatReadOnly = false; renderSendControls(); });
    await page.locator("#composerAddButton").click();
    assert.equal(await page.locator("#chatModeMenu button:disabled").count(), 1);
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
    await page.addScriptTag({ content: read("js/app-settings.js") });
    await page.evaluate(() => {
      // Keep production form read/save/reset; stub unrelated model/prompt controls.
      for (const name of ["renderUiAccentChoices", "renderTokenEstimateCalibrationStatus", "renderModelControls", "renderContextMeter"])
        window[name] = () => {};
      window.formatAppVersionLabel = value => value;
      window.headersToText = () => ""; window.textToHeaders = () => ({});
      for (const name of ["modelImageSupportOverrides", "modelAudioSupportOverrides", "modelCapabilitiesForSettings"])
        window[name] = () => ({});
      window.attachmentModelPriorityForSettings = () => [];
      window.settingsSaves = [];
      window.saveSettingsWithPromptChanges = async settings => {
        settingsSaves.push(JSON.parse(JSON.stringify(settings)));
        return { settings: JSON.parse(JSON.stringify(settings)), prompts: {} };
      };
      state.settings = {}; renderSettings();
    });
    assert.equal(await page.locator("#autoConfirmToolActionsInput").isChecked(), false);
    assert.match(await page.locator("#autoConfirmToolActionsNote").textContent(), /новых запусков во всех чатах/);
    await page.evaluate(() => { $("autoConfirmToolActionsInput").checked = true; });
    assert.equal(await page.evaluate(() => settingsSaves.length), 0, "editing the flag alone never dispatches");
    await page.evaluate(() => persistSettingsFromForm());
    assert.equal(await page.evaluate(() => settingsSaves[0].AutoConfirmToolActions), true);
    assert.equal(await page.locator("#autoConfirmToolActionsInput").isChecked(), true);
    assert.equal(await page.locator("#chatMutationPolicy, #chatPolicyStatus").count(), 0);
    await page.evaluate(() => resetAgentSettingsToDefaults());
    await page.evaluate(() => persistSettingsFromForm());
    assert.equal(await page.evaluate(() => settingsSaves[1].AutoConfirmToolActions), false);
    assert.equal(await page.locator("#autoConfirmToolActionsInput").isChecked(), false);
    assert.equal(await page.locator("#chatMutationPolicy, #chatPolicyStatus").count(), 0);
    console.log("PASS settings: auto-confirm defaults off, saves/reloads both values and resets to Ask");
    assert.deepEqual(errors, []);
    console.log("PASS composer: ring opens the existing usage panel and restores keyboard focus on close");
    if (process.env.COMPOSER_SCREENSHOT) {
      await page.locator("#contextMeter").blur();
      await page.setViewportSize({ width: 740, height: 760 });
      for (const theme of ["light", "dark"]) {
        await page.evaluate(theme => { document.documentElement.dataset.theme = theme; RNAssistantAccents.apply("blue", false); }, theme);
        await page.locator("#composerAddButton").click();
        await page.screenshot({ path: process.env.COMPOSER_SCREENSHOT.replace(/\.png$/, "-" + theme + ".png"), animations: "disabled" });
        await page.keyboard.press("Escape");
      }
      await page.setViewportSize({ width: 360, height: 760 });
      await page.evaluate(() => { document.documentElement.dataset.theme = "light"; RNAssistantAccents.apply("blue", false); });
      await page.locator("#contextMeter").hover();
      await page.screenshot({ path: process.env.COMPOSER_SCREENSHOT.replace(/\.png$/, "-tooltip.png"), animations: "disabled" });
    }
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
