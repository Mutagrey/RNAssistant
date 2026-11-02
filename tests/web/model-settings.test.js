"use strict";
// Production composer/Settings against the existing synthetic bridge; no model calls.
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
    const page = await browser.newPage({ viewport: { width: 1360, height: 900 }, reducedMotion: "reduce" }), errors = [];
    page.setDefaultTimeout(10000); page.on("pageerror", error => errors.push(error.message));
    await page.goto(address + "preview/web/?scenario=ready");
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    await page.evaluate(() => {
      window.modelTest = { calls: [], catalog: { models: [
        { id: "text-model", display_name: "Текстовая", supports_reasoning: false, supports_images: false },
        { id: "rich-model", display_name: "Мультимодальная", supports_reasoning: true, supports_images: true }
      ] } };
      const original = send;
      send = async (type, payload, ...args) => {
        modelTest.calls.push({ type, payload });
        if (type === "getModelCatalog") return modelTest.catalog;
        if (type === "setChatMode" && modelTest.holdMode)
          await new Promise(resolve => { modelTest.releaseMode = resolve; });
        if (type === "setChatModel") return { chatId: payload.chatId, model: payload.model, mode: "agent", sessionRevision: 100 };
        if (type === "setProjectChatModel" && modelTest.fail) throw Error("Проверочная ошибка выбора модели");
        const result = await original(type, payload, ...args);
        if (type === "setProjectChatModel" && modelTest.loseReply) {
          modelTest.loseReply = false;
          const error = Error("Ответ на выбор модели потерян"); error.resultUncertain = true; throw error;
        }
        if (type === "setProjectChatModel" && modelTest.hold) await new Promise(resolve => { modelTest.release = resolve; });
        return result;
      };
      state.settings.Model = "text-model"; state.activeChatModel = "";
      normalizeModelCatalog(modelTest.catalog); renderModelControls();
    });
    const picker = page.locator("#chatModelPicker"), menu = page.locator("#chatModelMenu");
    const pick = async value => { await picker.locator("summary").click(); await menu.locator('[data-value="' + value + '"]').click(); };
    await page.evaluate(() => { modelTest.holdMode = true; });
    await page.locator("#composerAddPicker > summary").click();
    await page.locator("#chatPlanButton").click();
    await page.waitForFunction(() => !!modelTest.releaseMode);
    assert.equal(await page.locator("#chatPlanButton").getAttribute("aria-pressed"), "false", "Office mode waits for the addressed acknowledgement");
    await page.locator("#chatInput").fill("Ввод во время сохранения режима");
    assert.equal(await page.locator("#sendButton").isDisabled(), true, "pending preference blocks submission, not typing");
    await page.evaluate(() => { modelTest.holdMode = false; modelTest.releaseMode(); });
    await page.waitForFunction(() => !chatComposer().preferenceBusy() && state.activeChatMode === "plan");
    assert.equal(await page.locator("#chatInput").inputValue(), "Ввод во время сохранения режима");
    if (!await page.locator("#chatPlanButton").isVisible()) await page.locator("#composerAddPicker > summary").click();
    await page.locator("#chatPlanButton").click();
    await page.waitForFunction(() => state.activeChatMode === "agent" && !chatComposer().preferenceBusy());
    await pick("rich-model");
    await page.waitForFunction(() => !chatComposer().preferenceBusy() && state.activeChatModel === "rich-model");
    await picker.locator("summary").click();
    await menu.locator('[data-value="rich-model"] .composer-picker-check').waitFor();
    const officeMenu = await menu.locator(".composer-model-item").allTextContents();
    await picker.locator("summary").click();
    await page.locator("#chatInput").fill("Черновик Office");
    await page.evaluate(() => RNAssistantProjectChats.open("preview-plan-workspace", "preview-plan-chat"));
    await page.waitForFunction(() => RNAssistantProjectChats.snapshot()?.sessionId === "preview-plan-chat");
    await page.locator("#chatInput").fill("Черновик проекта");
    await pick("rich-model");
    await page.waitForFunction(() => !RNAssistantProjectChats.isBusy() && chatModelSelection().selected === "rich-model");
    await picker.locator("summary").click();
    await menu.locator('[data-value="rich-model"] .composer-picker-check').waitFor();
    assert.deepEqual(await menu.locator(".composer-model-item").allTextContents(), officeMenu, "all chat types render the same model menu and badges");
    assert.equal(await page.locator("#chatReasoningToggle").isEnabled(), true);
    await picker.locator("summary").click();
    await page.evaluate(() => { modelTest.fail = true; });
    await pick("text-model"); await page.waitForFunction(() => !RNAssistantProjectChats.isBusy());
    assert.equal(await page.evaluate(() => chatModelSelection().selected), "rich-model", "failure cannot claim a new selection");
    assert.equal(await page.locator("#chatInput").inputValue(), "Черновик проекта");
    await page.evaluate(() => { modelTest.fail = false; modelTest.loseReply = true; });
    await pick("text-model"); await page.waitForFunction(() => !RNAssistantProjectChats.isBusy());
    assert.equal(await page.evaluate(() => chatModelSelection().selected), "text-model", "a lost acknowledgement re-reads the actual persisted selection before send");
    assert.equal(await page.locator("#chatInput").inputValue(), "Черновик проекта");
    await pick("rich-model"); await page.waitForFunction(() => !RNAssistantProjectChats.isBusy());
    await page.evaluate(() => { modelTest.fail = false; modelTest.hold = true; });
    await pick("text-model"); await page.waitForFunction(() => !!modelTest.release);
    assert.equal(await picker.locator("summary").getAttribute("aria-disabled"), "true");
    await page.evaluate(() => RNAssistantProjectChats.setScope("office"));
    await page.waitForFunction(() => RNAssistantWorkspaceChat.owner() === "office");
    await page.evaluate(() => { modelTest.hold = false; modelTest.release(); });
    assert.equal(await page.locator("#chatInput").inputValue(), "Черновик Office", "late preference does not overwrite another owner's draft");
    assert.equal(await page.locator("#chatModelButtonLabel").textContent(), "Мультимодальная");
    await page.evaluate(() => RNAssistantProjectChats.open("preview-plan-workspace", "preview-plan-chat"));
    await page.waitForFunction(() => chatModelSelection().selected === "text-model");
    assert.equal(await page.locator("#chatReasoningToggle").isDisabled(), true);
    await pick(""); await page.waitForFunction(() => !RNAssistantProjectChats.isBusy());
    assert.equal(await page.evaluate(() => chatModelSelection().selected), "", "default selection is explicit and retained");
    await page.evaluate(() => RNAssistantPlugins.select("data-templates"));
    await page.locator("#pluginWorkbench").getByRole("button", { name: "Чат исходников", exact: true }).click();
    await page.waitForFunction(() => RNAssistantWorkspaceChat.owner() === "plugin" && !!RNAssistantProjectChats.snapshot()?.sessionId);
    await pick("rich-model"); await page.waitForFunction(() => !RNAssistantProjectChats.isBusy());
    assert.equal(await page.evaluate(() => chatModelSelection().selected), "rich-model", "plugin source uses the same preference route");
    await picker.locator("summary").click();
    await menu.locator('[data-value="rich-model"] .composer-picker-check').waitFor();
    assert.deepEqual(await menu.locator(".composer-model-item").allTextContents(), officeMenu);
    if (process.env.RNA_MODEL_SCREENSHOT) await page.screenshot({ path: process.env.RNA_MODEL_SCREENSHOT, animations: "disabled" });
    await page.setViewportSize({ width: 420, height: 900 });
    await page.evaluate(() => { RNAssistantDockWorkspace.show("conversation"); applyUiTheme({ UiTheme: "dark" }, false); });
    await page.locator("#chatForm").waitFor();
    const bounds = await menu.boundingBox(), composerBounds = await page.locator("#chatForm").boundingBox();
    if (process.env.RNA_MODEL_SCREENSHOT) await page.screenshot({ path: process.env.RNA_MODEL_SCREENSHOT.replace(/\.png$/, "-narrow.png"), animations: "disabled" });
    assert.ok(bounds.x >= composerBounds.x && bounds.x + bounds.width <= composerBounds.x + composerBounds.width, "shared model menu fits the narrow chat: " + JSON.stringify(bounds));
    await picker.locator("summary").click();
    await page.setViewportSize({ width: 1360, height: 900 });
    await page.evaluate(() => applyUiTheme({ UiTheme: "light" }, false));

    // Capabilities, priority, calibration and preview catalogs stay in the Settings form until acknowledgement.
    await page.locator(".rail-settings").click();
    await page.evaluate(() => {
      state.settings.ModelCapabilities = { "TEXT-MODEL": { SupportsReasoning: false, MaxContextTokens: 32000 } };
      state.settings.ModelImageSupportOverrides = { "text-model": false };
      state.settings.AttachmentModelPriority = ["rich-model"];
      state.settings.TokenEstimateCalibrations = { kept: { Multiplier: 1.2 } };
      renderSettings({ committed: true });
      modelTest.applied = JSON.stringify(state.settings);
      setStoredModelCapabilityField("text-model", "MaxContextTokens", 64000);
      setModelCapabilityOverride("image", "text-model", "true");
      setAttachmentModelPriority(["text-model", "rich-model"]);
      saveSettingsWithPromptChanges = settings => new Promise((resolve, reject) => {
        modelTest.submitted = settings; modelTest.resolve = () => resolve({ settings, prompts: state.prompts }); modelTest.reject = reject;
      });
    });
    assert.equal(await page.evaluate(() => JSON.stringify(state.settings)), await page.evaluate(() => modelTest.applied));
    assert.deepEqual(await page.evaluate(() => [effectiveModelSupportsImages("text-model"), readSettings().ModelImageSupportOverrides["text-model"],
      readSettings().ModelCapabilities["TEXT-MODEL"].MaxContextTokens, readSettings().AttachmentModelPriority]), [false, true, 64000, ["text-model", "rich-model"]]);
    await page.locator("#saveSettingsButton").click();
    await page.evaluate(() => { setStoredModelCapabilityField("text-model", "SupportsReasoning", true); modelTest.resolve(); });
    await page.waitForFunction(() => !settingsSaveInProgress);
    assert.deepEqual(await page.evaluate(() => [settingsDirty, effectiveModelSupportsReasoning("text-model"),
      readSettings().ModelCapabilities["TEXT-MODEL"].SupportsReasoning]), [true, false, true], "late acknowledgement applies its snapshot and retains newer edits");
    await page.locator("#saveSettingsButton").click(); await page.evaluate(() => modelTest.reject(Error("Save failed")));
    await page.waitForFunction(() => !settingsSaveInProgress);
    assert.equal(await page.evaluate(() => settingsDirty), true);
    await page.locator("#chatCopyFeedback").getByRole("button", { name: "Закрыть уведомление" }).click();
    await page.evaluate(() => { modelTest.applied = JSON.stringify(state.settings); resetModelSettingsToDefaults(); });
    assert.equal(await page.evaluate(() => JSON.stringify(state.settings)), await page.evaluate(() => modelTest.applied), "reset is also an unapplied draft");
    assert.deepEqual(await page.evaluate(() => readSettings().TokenEstimateCalibrations), {});
    await page.evaluate(async () => {
      modelTest.catalog = { models: [{ id: "preview-only", supports_images: true }] };
      modelTest.appliedCatalog = state.modelCatalog;
      await loadModelCatalog(true);
    });
    assert.equal(await page.evaluate(() => state.modelCatalog === modelTest.appliedCatalog && !findModel("preview-only")), true);
    assert.equal(await page.evaluate(() => !!findModel("preview-only", modelSettingsFormContext())), true);
    await page.locator("#saveSettingsButton").click(); await page.evaluate(() => modelTest.resolve());
    await page.waitForFunction(() => !settingsSaveInProgress && !settingsDirty);
    assert.equal(await page.evaluate(() => Object.keys(settingsModelDraft).length), 0);
    assert.deepEqual(errors, []);
    console.log("PASS model controls: one Office/project/plugin picker, persisted selection/default, failure and late reply isolation, drafts, capability save/reset/preview boundary");
  } finally { if (browser) await browser.close(); server.kill("SIGTERM"); }
})().catch(error => { console.error(error); process.exitCode = 1; });
