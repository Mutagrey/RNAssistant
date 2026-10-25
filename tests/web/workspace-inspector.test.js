"use strict";
// Real shared dock, selected workspace snapshots and preview frame; no Office or model calls.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");

(async () => {
  const server = spawn("python3", ["-B", "prototypes/serve_ui.py", "--port", "0"], { cwd: path.resolve(__dirname, "../..") });
  let browser;
  try {
    const address = await new Promise((resolve, reject) => {
      server.once("error", reject); server.once("exit", code => reject(Error("preview exited " + code)));
      server.stdout.on("data", bytes => { const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(bytes)); if (match) resolve(match[1]); });
    });
    browser = await chromium.launch({ headless: true,
      ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    const errors = []; page.on("pageerror", error => errors.push(error.message));
    await page.goto(address + "preview/web/?scenario=ready");
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    await page.evaluate(() => {
      window.inspectorNode = document.getElementById("chatInspector");
      window.workspaceInspectorRequests = [];
      window.workspaceModelRequests = [];
      const original = send;
      send = async (type, payload) => {
        workspaceInspectorRequests.push(type);
        const modelRead = ["getModelContext", "getModelContextPayload"].includes(type) && payload.workspaceId;
        const result = await original(type, payload);
        if (modelRead) {
          workspaceModelRequests.push({ type, ...payload });
          (result.events || []).forEach(event => { event.trace.RunId = "current-run"; event.trace.StepId = "two"; });
          if (window.holdWorkspaceModel) await window.holdWorkspaceModel;
          return result;
        }
        if (!["getProjectChat", "getPluginChat"].includes(type)) return result;
        const run = id => ({ RunId: id, TurnId: "turn", Lifecycle: "completed", ExecutionHealth: "clean",
          SuccessfulReads: 2, VerifiedWrites: 0, NoChangeWrites: 0, UnverifiedWrites: 0, FailedCalls: 0, UnknownEffects: 0 });
        const step = (runId, id, text) => ({ Id: id, Role: "assistant", RunId: runId, Activity: {
          RunId: runId, StepId: id, Kind: "tool", Title: text, StepMessage: text, Status: "completed",
          ToolId: "common.resources_read", ToolCallId: "call-" + id, ResultMessage: "Содержимое получено" } });
        result.messages = [step("old-run", "old", "Предыдущий запуск"),
          { Id: "old-final", Role: "assistant", RunId: "old-run", Content: "Старая сводка", RunViewState: run("old-run") },
          step("current-run", "one", "Проверка исходников"), step("current-run", "two", "Чтение отчёта"),
          { Id: "final", Role: "assistant", RunId: "current-run", Content: "Проверка завершена", RunViewState: run("current-run") }];
        result.state.runId = "current-run"; result.state.runViewState = run("current-run");
        result.state.office = { Host: "Excel", DocumentTitle: "Документ выбранного запуска.xlsx" };
        result.state.contextReceipt = result.sessionId === "preview-plan-chat" ? null : { Arsenal: {
          RunId: "current-run", StepId: "two",
          Selected: [
            { Id: "reports", Name: "Подготовка отчёта", Description: "Сравнивает данные и составляет сводку по материалам проекта.", Status: "selected" },
            { Id: "archive", Name: "Работа с архивом", Description: "Анализирует архивные материалы проекта.", Status: "unavailable", Reason: "Выбранная версия навыка больше недоступна." }
          ], Tools: [
            { Id: "common.resources_read", Name: "Чтение материалов", Description: "Читает выбранный источник. Поддерживает таблицы, документы и большие файлы с постраничной загрузкой." },
            { Id: "common.resources_find", Name: "Поиск материалов", Description: "Находит доступные источники в проекте." },
            { Id: "common.office_run_macro", Name: "Выполнение макроса", Description: "Запускает выбранный макрос документа.", RequiresConfirmation: true }
          ], Available: [{ Id: "not-selected", Name: "Навык вне запроса" }] } };
        if (result.project.workspaceId === "preview-plugin-workspace") {
          result.state.office = null;
          result.state.contextReceipt.Arsenal.Tools.pop();
        }
        result.messageStartIndex = 0; result.messageTotalCount = result.messages.length;
        return result;
      };
    });
    const view = async name => {
      await page.locator("#chatInspectorSwitcher summary").click();
      await page.locator('[data-chat-inspector-view="' + name + '"]').click();
    };
    const capture = async name => {
      if (!process.env.INSPECTOR_SCREENSHOTS) return;
      fs.mkdirSync(process.env.INSPECTOR_SCREENSHOTS, { recursive: true });
      await page.waitForFunction(() => !document.querySelector(".inspector-transition"));
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      await page.screenshot({ path: path.join(process.env.INSPECTOR_SCREENSHOTS, name + ".png"), animations: "disabled" });
    };
    await page.locator("#chatInput").fill("Черновик Office");
    await page.locator("#toggleChatInspectorButton").click();
    await page.evaluate(() => { RNAssistantModelContext.open(); RNAssistantChatInspector.setRequestLayout("focus"); });
    await page.evaluate(() => RNAssistantProjectChats.open("preview-data-workspace", "preview-project-review"));
    assert.equal(await page.locator("#modelContextDialog").isHidden(), true, "moving the dock closes the Office request and releases focus isolation");
    await page.evaluate(() => { workspaceInspectorRequests.length = 0; RNAssistantModelContext.open(); });
    assert.equal(await page.locator("#modelContextDialog").isHidden(), true, "an Office request route cannot open against a workspace chat");
    await page.locator("#workspaceInspectorToggle").click();
    assert.equal(await page.evaluate(() => document.querySelector("#projectChatLayout > #chatInspector") === inspectorNode), true);
    assert.match(await page.locator("#chatInspectorBody").textContent(), /Документ выбранного запуска.xlsx/);
    assert.doesNotMatch(await page.locator("#chatInspectorBody").textContent(), /Черновик Office/);
    await view("abilities");
    const abilitiesBody = page.locator("#chatInspectorBody");
    assert.match(await abilitiesBody.textContent(), /Навыки · 2/);
    assert.match(await abilitiesBody.textContent(), /Инструменты · 3/);
    assert.doesNotMatch(await abilitiesBody.textContent(), /Навык вне запроса/);
    await page.locator('[data-inspector-key="skill:archive"] > summary').click();
    assert.match(await page.locator('[data-inspector-key="skill:archive"] > summary').textContent(), /Недоступен/);
    assert.equal(await page.getByText("Выбранная версия навыка больше недоступна.", { exact: true }).isVisible(), true,
      "an unavailable skill keeps its reason even when a description exists");
    await page.locator('[data-inspector-key="tools:common"] > summary').click();
    assert.match(await page.locator('[data-inspector-key="tool:common.office_run_macro"] > summary').textContent(), /С разрешения/);
    await capture("project-abilities");
    await page.evaluate(() => {
      window.savedWorkspaceArsenal = structuredClone(RNAssistantWorkspaceChat.snapshot().state.contextReceipt.Arsenal);
      RNAssistantWorkspaceChat.snapshot().state.contextReceipt.Arsenal.RunId = "older-run";
      RNAssistantChatInspector.render();
    });
    assert.match(await abilitiesBody.textContent(), /Состав предыдущего запуска/);
    assert.match(await abilitiesBody.textContent(), /Для текущего запуска состав ещё не сохранён/);
    await page.evaluate(() => { delete RNAssistantWorkspaceChat.snapshot().state.contextReceipt.Arsenal.Tools; RNAssistantChatInspector.render(); });
    assert.match(await abilitiesBody.textContent(), /Сохранённый состав возможностей недоступен/);
    assert.doesNotMatch(await abilitiesBody.textContent(), /Инструменты · 0/);
    await page.evaluate(() => { RNAssistantWorkspaceChat.snapshot().state.contextReceipt = null; RNAssistantChatInspector.render(); });
    assert.match(await abilitiesBody.textContent(), /Состав ещё не сохранён/);
    assert.doesNotMatch(await abilitiesBody.textContent(), /Подключённых навыков нет/);
    await page.evaluate(() => {
      RNAssistantWorkspaceChat.snapshot().state.contextReceipt = { Arsenal: savedWorkspaceArsenal };
      RNAssistantChatInspector.render();
    });
    await view("activity");
    assert.equal(await page.locator(".chat-inspector-step").count(), 2, "latest run excludes historical actions");
    assert.doesNotMatch(await page.locator("#chatInspectorBody").textContent(), /Предыдущий запуск/);
    assert.equal(await page.locator('[data-chat-inspector-view="abilities"]').evaluate(node => node.hidden), false);
    assert.equal(await page.locator('[data-chat-inspector-view="usage"]').isHidden(), true);
    await page.locator('#messages [aria-label="Открыть шаг"]').first().click();
    assert.match(await page.locator(".chat-inspector-step.is-selected").textContent(), /Предыдущий запуск/);
    assert.equal(await page.locator(".chat-inspector-step").count(), 1);
    await page.locator("#chatInspector").press("Escape");
    assert.equal(await page.evaluate(() => document.activeElement.getAttribute("aria-label")), "Открыть шаг");
    await page.locator('#messages [aria-label="Открыть шаг"]').nth(2).click();
    assert.match(await page.locator(".chat-inspector-step.is-selected").textContent(), /Чтение отчёта/);
    await page.evaluate(() => { window.oldInspectorTarget = RNAssistantChatInspector.target(); });
    await capture("project-activity");
    const openRequest = page.locator('.chat-inspector-step.is-selected').getByRole("button", { name: "Запрос / ответ модели", exact: true });
    await openRequest.click();
    await page.locator("#modelContextBody .is-message").first().waitFor();
    assert.ok((await page.locator("#chatInspector").boundingBox()).width >= 280,
      "the project dock retains its own grid track after fixed-width summary content is hidden");
    assert.equal(await page.locator("#modelContextMode").isDisabled(), true, "workspace cannot use the Office preview compiler");
    assert.equal(await page.locator("#modelContextAttempt").inputValue(), "preview-request");
    const readCount = await page.evaluate(() => workspaceModelRequests.filter(item => item.type === "getModelContextPayload").length);
    await page.locator("#modelContextLayout").selectOption("wide");
    await page.locator("#modelContextBody .is-message").first().locator(":scope > summary").click();
    await capture("project-request-wide");
    await page.locator("#modelContextLayout").selectOption("focus");
    assert.equal(await page.locator("#workspaceChatView").evaluate(node => node.inert), true);
    await page.locator("#modelContextDialog").press("Escape");
    assert.equal(await page.locator("#modelContextLayout").inputValue(), "wide");
    assert.equal(await page.evaluate(() => workspaceModelRequests.filter(item => item.type === "getModelContextPayload").length), readCount,
      "resizing retains the selected payload");
    await page.locator("#modelContextResponseTab").click();
    await page.locator("#modelContextBody .rn-markdown-viewer-rendered").first().waitFor();
    await capture("project-response-wide");
    await page.locator("#closeModelContextButton").click();
    assert.equal(await page.evaluate(() => document.activeElement.textContent), "Запрос / ответ модели");
    await page.evaluate(() => { window.holdWorkspaceModel = new Promise(resolve => { window.releaseWorkspaceModel = resolve; }); });
    await openRequest.click();
    await page.evaluate(() => RNAssistantProjectChats.open("preview-plan-workspace", "preview-plan-chat"));
    await page.evaluate(() => { releaseWorkspaceModel(); holdWorkspaceModel = null; });
    assert.equal(await page.locator("#modelContextDialog").isHidden(), true, "switching sessions closes a pending read");
    await page.evaluate(() => RNAssistantProjectChats.open("preview-data-workspace", "preview-project-review"));
    await page.locator('#messages [aria-label="Открыть шаг"]').nth(2).click();
    await page.setViewportSize({ width: 390, height: 844 });
    await capture("project-activity-narrow");
    assert.ok(await page.locator("#closeChatInspectorButton").isVisible());
    assert.ok(await page.locator("#chatInspector").evaluate(node => node.getBoundingClientRect().right <= innerWidth));
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.evaluate(() => RNAssistantProjectChats.open("preview-plan-workspace", "preview-plan-chat"));
    assert.equal(await page.locator(".chat-inspector-step.is-selected").count(), 0, "selection resets across exact session identity");
    assert.equal(await page.evaluate(() => RNAssistantChatInspector.openActivity({ ...oldInspectorTarget, runId: "old-run", stepId: "old" })), false);
    assert.equal(await page.evaluate(() => RNAssistantChatInspector.openActivity({ ...RNAssistantChatInspector.target(), workspaceId: "another-workspace", runId: "current-run" })), false);
    await view("abilities");
    assert.match(await abilitiesBody.textContent(), /Состав ещё не сохранён/);
    assert.doesNotMatch(await abilitiesBody.textContent(), /Подготовка отчёта/);
    await page.evaluate(() => RNAssistantProjectChats.setScope("office"));
    assert.equal(await page.evaluate(() => document.querySelector("#chatLayout > #chatInspector") === inspectorNode), true);
    assert.equal(await page.locator("#chatInput").inputValue(), "Черновик Office");
    assert.equal(await page.locator("#chatInspectorViewTitle").textContent(), "Контекст");
    await page.evaluate(() => RNAssistantPlugins.select("data-templates"));
    await page.locator("#pluginPreviewMount iframe").waitFor();
    await page.evaluate(() => { window.previewFrame = document.querySelector("#pluginPreviewMount iframe"); window.previewDocument = previewFrame.contentDocument; });
    await page.locator("#chatInput").fill("Черновик плагина");
    await view("abilities");
    assert.match(await abilitiesBody.textContent(), /Навыки · 2/);
    assert.match(await abilitiesBody.textContent(), /Инструменты · 2/);
    assert.doesNotMatch(await abilitiesBody.textContent(), /Выполнение макроса/);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.evaluate(() => document.documentElement.dataset.theme = "dark");
    await capture("plugin-abilities-dark-narrow");
    assert.equal(await abilitiesBody.evaluate(node => node.scrollWidth > node.clientWidth + 1), false);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.evaluate(() => document.documentElement.dataset.theme = "light");
    await view("activity");
    assert.equal(await page.evaluate(() => document.querySelector("#projectChatLayout > #chatInspector") === inspectorNode), true);
    assert.equal(await page.locator(".chat-inspector-step").count(), 2);
    assert.equal(await page.locator("#pluginPreviewMount").isHidden(), true);
    await page.locator('#messages [aria-label="Открыть шаг"]').nth(2).click();
    assert.match(await page.locator(".chat-inspector-step.is-selected").textContent(), /Чтение отчёта/);
    await capture("plugin-activity");
    await openRequest.click();
    await page.locator("#modelContextBody .is-message").first().waitFor();
    await page.locator("#modelContextLayout").selectOption("wide");
    const pluginRequestWidth = (await page.locator("#chatInspector").boundingBox()).width;
    await page.locator("#chatInspectorSplitter").focus();
    await page.keyboard.press("ArrowRight");
    assert.ok((await page.locator("#chatInspector").boundingBox()).width < pluginRequestWidth,
      "the plugin reader uses the shared resize divider");
    await page.locator("#closeModelContextButton").click();
    await page.evaluate(() => document.documentElement.dataset.theme = "dark");
    await page.setViewportSize({ width: 390, height: 844 });
    await capture("plugin-activity-dark-narrow");
    assert.ok(await page.locator("#chatInspector").evaluate(node => node.getBoundingClientRect().right <= innerWidth));
    await openRequest.click();
    await page.locator("#modelContextBody .is-message").first().waitFor();
    await page.locator("#modelContextResponseTab").click();
    await page.locator("#modelContextBody .rn-markdown-viewer-rendered").first().waitFor();
    await capture("plugin-response-dark-narrow");
    assert.equal(await page.locator("#modelContextDialog").evaluate(node => node.scrollWidth > node.clientWidth + 1), false);
    await page.locator("#closeModelContextButton").click();
    await view("preview");
    assert.equal(await page.evaluate(() => document.querySelector("#pluginPreviewMount iframe") === previewFrame && previewFrame.contentDocument === previewDocument), true, "diagnostics never reloads the plugin frame");
    assert.equal(await page.locator("#chatInput").inputValue(), "Черновик плагина");
    const pluginTarget = await page.evaluate(() => RNAssistantChatInspector.target());
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.evaluate(target => RNAssistantProjectChats.open(target.workspaceId, target.sessionId), pluginTarget);
    await page.locator('#messages [aria-label="Открыть шаг"]').nth(2).click();
    assert.match(await page.locator(".chat-inspector-step.is-selected").textContent(), /Чтение отчёта/,
      "the same session gets current owner routes when moved between plugin and project");
    assert.equal(await page.locator("#chatInput").inputValue(), "Черновик плагина");
    assert.equal(await page.evaluate(target => RNAssistantChatInspector.openActivity({ ...target, runId: "current-run", stepId: "two" }), pluginTarget), false);
    await page.evaluate(() => RNAssistantPlugins.select("data-templates"));
    assert.equal(await page.locator("#pluginPreviewMount").isVisible(), true, "explicit plugin selection opens its preview even after diagnostics");
    assert.equal(await page.evaluate(() => document.querySelector("#pluginPreviewMount iframe") === previewFrame), true);
    assert.equal(await page.evaluate(() => workspaceInspectorRequests.some(type => ["getAgentArsenal", "inspectPromptContext", "inspectModelContextPreview"].includes(type))), false, "workspace diagnostics never uses the Office compiler");
    assert.equal(await page.evaluate(() => workspaceModelRequests.every(item => item.workspaceId && item.chatId !== "preview-chat")), true,
      "saved model requests carry the exact workspace session");
    assert.deepEqual(errors, []);
    console.log("PASS workspace inspector: one dock, exact session/run/step, old route rejection, drafts, preview preservation, narrow/light/dark");
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
