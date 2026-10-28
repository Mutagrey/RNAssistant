"use strict";
// Real product DOM + preview bridge. No Office/VSTO or model calls.
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
      server.once("error", reject); server.once("exit", code => reject(new Error("preview exited " + code)));
      server.stdout.on("data", bytes => { const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(bytes)); if (match) resolve(match[1]); });
    });
    browser = await chromium.launch({ headless: true,
      ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    const errors = []; page.on("pageerror", error => errors.push(error.message));
    await page.goto(address + "preview/web/?scenario=ready");
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    await page.locator("#toggleChatInspectorButton").click();
    const view = async name => {
      await page.locator("#chatInspectorSwitcher summary").click();
      await page.locator('[data-chat-inspector-view="' + name + '"]').click();
    };
    const screenshot = async name => {
      if (!process.env.INSPECTOR_SCREENSHOTS) return;
      fs.mkdirSync(process.env.INSPECTOR_SCREENSHOTS, { recursive: true });
      await page.waitForFunction(() => !document.getElementById("chatLayout").classList.contains("inspector-transition"));
      await page.locator("#chatInspectorBody").evaluate(node => Promise.all(node.getAnimations().map(animation => animation.finished.catch(() => {}))));
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      await page.screenshot({ path: path.join(process.env.INSPECTOR_SCREENSHOTS, name + ".png"), animations: "disabled" });
    };
    assert.deepEqual(await page.locator("[data-chat-inspector-view]").evaluateAll(buttons => buttons.map(b => b.querySelectorAll("svg").length)), [1, 1, 1, 1, 1]);
    assert.equal(await page.locator('[data-chat-inspector-view="preview"]').isVisible(), false,
      "preview is offered only while its exact browser source is available");
    await page.locator("#chatInspectorSwitcher summary").click(); await screenshot("menu");
    await page.locator('[data-chat-inspector-view="usage"]').click();
    await page.locator(".chat-inspector-composition-chart").waitFor();
    assert.equal(await page.locator(".chat-inspector-composition-chart > span").count(), 5);
    assert.match(await page.locator("#chatInspectorBody").textContent(), /следующего запроса/);
    assert.match(await page.locator("#chatInspectorBody .chat-inspector-info").textContent(), /6.200/);
    const sum = await page.locator(".chat-inspector-composition-chart > span").evaluateAll(nodes => nodes.reduce((n, node) => n + Number(node.style.flexGrow), 0));
    assert.equal(sum, 6000, "composition never rescales estimates to provider usage");
    await screenshot("usage");
    await page.locator('[data-inspector-key="composition:0"] > summary').click();
    await page.locator('[data-inspector-key="composition:0"] .prompt-context-item > summary').first().click();
    await screenshot("usage-details");
    await page.evaluate(() => RNAssistantChatInspector.render());
    assert.equal(await page.locator('[data-inspector-key="composition:0"]').evaluate(n => n.open), true);
    await view("abilities");
    await page.locator(".chat-inspector-tool-group").first().waitFor();
    assert.equal(await page.locator(".chat-inspector-tool-group").count(), 3, "dot segments form nested groups");
    await page.locator(".chat-inspector-tool-group > summary").first().click();
    const tool = page.locator('[data-inspector-key="tool:core.find"]');
    assert.equal(await tool.evaluate(n => n.open), false);
    await screenshot("abilities");
    await tool.locator("summary").click();
    assert.match(await tool.locator("code").textContent(), /core.find/);
    await view("usage");
    assert.equal(await page.locator('[data-inspector-key="composition:0"]').evaluate(n => n.open), true, "view switching preserves disclosure");
    assert.equal(await page.locator('[data-inspector-key="context-item:history:history-item"]').evaluate(n => n.open), true, "nested source disclosure survives redraw");
    await page.evaluate(() => {
      const original = send;
      send = (type, args) => {
        if (type !== "inspectPromptContext") return original(type, args);
        return Promise.resolve({ chatId: state.activeChatId, sections: [
          { id: "history", title: "История", tokens: 90, included: true, items: [] },
          { id: "document_context", title: "Неизвестно", tokens: null, included: true, items: [] },
          { id: "format_repair_reserve", title: "Резерв", tokens: 2000, included: true, items: [] },
          { id: "excluded", title: "Исключено", tokens: 4000, included: false, items: [] }
        ] });
      };
      window.restoreInspectorPreview = () => { send = original; };
    });
    await page.getByRole("button", { name: "Обновить состав", exact: true }).click();
    await page.waitForFunction(() => document.querySelector('[data-inspector-key="excluded"]'));
    assert.equal(await page.locator(".chat-inspector-composition-chart > span").evaluateAll(nodes => nodes.reduce((n, node) => n + Number(node.style.flexGrow), 0)), 90, "reserves and excluded bodies never enter the shares");
    assert.match(await page.locator('[data-inspector-key="composition:3"] > summary').textContent(), /неполная оценка/);
    assert.doesNotMatch(await page.locator('[data-inspector-key="composition:3"] > summary').textContent(), /0%/);
    await page.evaluate(() => restoreInspectorPreview());
    await page.evaluate(() => {
      window.savedInspectorSend = send;
      window.inspectorCancelled = [];
      window.savedInspectorCancel = cancelBridgeRequest;
      cancelBridgeRequest = id => { inspectorCancelled.push(id); return Promise.resolve(); };
      send = (type, args) => {
        if (type !== "inspectPromptContext") return savedInspectorSend(type, args);
        const promise = new Promise(resolve => { window.resolveInspectorPreview = resolve; });
        promise.requestId = "pending-inspector"; return promise;
      };
    });
    await page.getByRole("button", { name: "Обновить состав", exact: true }).click();
    await view("context");
    await page.evaluate(() => {
      state.activeChatId = "another-chat"; RNAssistantChatInspector.render();
      resolveInspectorPreview({ chatId: "preview-chat", sections: [{ id: "history", included: true, tokens: 999999, title: "STALE BODY", items: [] }] });
    });
    assert.ok((await page.evaluate(() => inspectorCancelled)).includes("pending-inspector"));
    assert.doesNotMatch(await page.locator("#chatInspectorBody").textContent(), /STALE BODY/);
    await page.evaluate(() => {
      send = savedInspectorSend; cancelBridgeRequest = savedInspectorCancel; state.activeChatId = "preview-chat";
      state.activeRunViewState = { lifecycle: "completed", executionHealth: "unknown", runId: "inspector-run", currentAction: "Проверка модулей завершена" };
      state.messages = [];
      state.liveAgentRun = [1, 2].map(i => ({ Id: "activity-" + i, Kind: "tool", RunId: "inspector-run", StepId: "step-" + i,
        StepMessage: "Проверю содержимое модуля", ToolId: "core.read", ToolCallId: "call-" + i,
        Status: i === 1 ? "completed" : "unknown", ResultMessage: "Прочитан фрагмент " + i }));
      window.inspectorRoutes = [];
      openRunJournal = options => inspectorRoutes.push(options);
      RNAssistantModelContext.open = options => inspectorRoutes.push(options);
    });
    await view("activity");
    assert.equal(await page.locator(".chat-inspector-step").count(), 2, "same intention never merges separate runtime steps");
    await page.locator(".chat-inspector-step-detail > summary").last().click();
    await screenshot("activity");
    await page.getByRole("button", { name: "События действия", exact: true }).click();
    await page.getByRole("button", { name: "Запрос / ответ модели", exact: true }).click();
    const routes = await page.evaluate(() => inspectorRoutes);
    assert.deepEqual(routes, [
      { chatId: "preview-chat", runId: "inspector-run", stepId: "step-2", toolCallId: "call-2" },
      { chatId: "preview-chat", runId: "inspector-run", stepId: "step-2" }
    ]);
    for (const lifecycle of ["cancelled", "failed", "completed", "running"]) {
      await page.evaluate(value => {
        state.activeRunViewState.lifecycle = value;
        state.liveAgentRun[0].Status = "running"; state.liveAgentRun[1].Status = "waiting";
        RNAssistantChatInspector.render();
      }, lifecycle);
      const expected = lifecycle === "running" ? ["Выполняю", "Нужно подтверждение"] : ["Отменено", "Отменено"];
      assert.deepEqual(await page.locator(".chat-inspector-step-call > small").allTextContents(), expected,
        "expanded actions follow the run lifecycle without a stale running or confirmation status");
      if (lifecycle !== "running") assert.equal(await page.locator(".chat-inspector-step.is-cancelled").count(), 2);
      assert.deepEqual(await page.evaluate(() => state.liveAgentRun.map(item => item.Status)), ["running", "waiting"],
        "presentation does not rewrite saved activity statuses");
    }
    await page.evaluate(() => {
      state.activeRunViewState.lifecycle = "cancelled";
      state.liveAgentRun[1].ExecutionEvidence = { Effect: "Unknown" };
      RNAssistantChatInspector.render();
    });
    assert.equal(await page.locator(".chat-inspector-step.is-unknown").count(), 1);
    assert.equal(await page.locator(".chat-inspector-step.is-unknown .chat-inspector-step-call > small").textContent(),
      "Результат требует проверки", "terminal presentation preserves unknown-effect evidence");
    await screenshot("activity-terminal");
    await page.evaluate(() => {
      state.activeRunViewState.lifecycle = "completed";
      state.liveAgentRun[0].Status = "completed"; state.liveAgentRun[1].Status = "unknown";
      delete state.liveAgentRun[1].ExecutionEvidence;
      RNAssistantChatInspector.render();
    });
    await page.evaluate(() => document.documentElement.setAttribute("data-theme", "dark"));
    await page.setViewportSize({ width: 420, height: 780 }); await screenshot("activity-narrow-dark");
    assert.equal(await page.locator("#chatInspectorBody").evaluate(n => n.scrollWidth > n.clientWidth + 1), false);
    await page.locator(".chat-inspector-step-detail > summary").last().focus();
    await page.keyboard.press("Enter");
    assert.equal(await page.locator(".chat-inspector-step-detail").last().evaluate(n => n.open), false);
    await page.keyboard.press("Escape");
    assert.equal(await page.locator("#toggleChatInspectorButton").getAttribute("aria-expanded"), "false");
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.evaluate(() => {
      document.documentElement.setAttribute("data-theme", "light");
      const oldActivity = { ...state.liveAgentRun[0], RunId: "older-run", StepId: "step-2", ToolCallId: "old-call",
        StepMessage: "Источник из предыдущего запуска" };
      const oldMessage = { Id: "old-message", RunId: "older-run", Role: "assistant", Content: "", Activity: oldActivity };
      const oldFinal = { Id: "old-final", RunId: "older-run", Role: "assistant", Content: "Предыдущий ответ" };
      state.messages = [oldMessage, oldFinal];
      const article = renderMessageArticle(oldMessage, 0); article.id = "inspector-source";
      document.getElementById("messages").appendChild(article);
      const grouped = document.createElement("article"); grouped.id = "inspector-run-source";
      appendAgentRunFooter(grouped, [{ message: oldMessage, index: 0 }], { message: oldFinal, index: 1 });
      document.getElementById("messages").appendChild(grouped);
    });
    assert.equal(await page.locator("#inspector-source .message-inspect-action").count(), 0,
      "a standalone tool row has no work-history button");
    const source = page.locator("#inspector-run-source .message-footer .message-inspect-action");
    const actionLayout = await source.evaluate(button => {
      const row = button.closest(".message-actions"), glyph = button.querySelector("svg");
      const copy = row.firstElementChild;
      const box = node => node.getBoundingClientRect();
      const center = node => box(node).top + box(node).height / 2;
      return { glyph: box(glyph).width, copyOffset: Math.abs(center(copy) - center(button)),
        order: row.children[1] === button, height: box(row).height };
    });
    assert.equal(actionLayout.glyph, 13, "the work-history icon keeps its compact size");
    assert.ok(actionLayout.copyOffset <= 1 && actionLayout.order && actionLayout.height <= 30,
      "one work-history button sits beside copy in the run footer");
    await source.click();
    assert.equal(await page.locator(".chat-inspector-step").count(), 1, "the selected older run never uses the latest run's steps");
    assert.equal(await page.locator(".chat-inspector-step.is-selected").count(), 0,
      "the footer opens the run without selecting a tool call");
    await page.evaluate(() => RNAssistantChatInspector.openActivity({ chatId: "preview-chat", runId: "older-run", stepId: "step-2" },
      document.querySelector("#inspector-run-source .message-inspect-action")));
    assert.equal(await page.locator(".chat-inspector-step.is-selected details").evaluate(n => n.open), true);
    assert.equal(await page.locator(".chat-inspector-step.is-selected summary").first().getAttribute("aria-current"), "step");
    assert.match(await page.locator("#chatInspectorBody").textContent(), /Источник из предыдущего запуска/);
    await page.evaluate(() => RNAssistantChatInspector.render());
    assert.equal(await page.locator(".chat-inspector-step.is-selected details").evaluate(n => n.open), true);
    await screenshot("activity-selected");
    await page.evaluate(() => {
      // Reproduce Escape between responsive CSS changing and the queued resize handler.
      window.holdInspectorResize = event => event.stopImmediatePropagation();
      window.addEventListener("resize", holdInspectorResize, { capture: true });
    });
    await page.setViewportSize({ width: 420, height: 780 });
    await screenshot("activity-selected-narrow");
    assert.equal(await page.locator("#chatInspectorBody").evaluate(n => n.scrollWidth > n.clientWidth + 1), false);
    await page.keyboard.press("Escape");
    assert.equal(await source.evaluate(n => n === document.activeElement), true, "Escape restores the source even before a pending resize handler");
    await page.evaluate(() => {
      window.removeEventListener("resize", holdInspectorResize, { capture: true });
      window.dispatchEvent(new Event("resize"));
    });
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.locator("#inspector-run-source .message-inspect-action").click();
    assert.match(await page.locator("#chatInspectorBody").textContent(), /Выбранный запуск/);
    assert.equal(await page.locator(".chat-inspector-step.is-selected").count(), 0, "a run footer does not invent a final step identity");
    await page.getByRole("button", { name: "К последнему запуску", exact: true }).click();
    assert.equal(await page.locator(".chat-inspector-step").count(), 2);
    await page.evaluate(() => RNAssistantChatInspector.openActivity({ chatId: "preview-chat", runId: "older-run", stepId: "missing" }));
    assert.match(await page.locator("#chatInspectorBody").textContent(), /Шаг не найден в загруженной части истории/);
    assert.equal(await page.locator(".chat-inspector-step.is-selected").count(), 0);
    await page.getByRole("button", { name: "История и диагностика", exact: true }).click();
    assert.deepEqual(await page.evaluate(() => inspectorRoutes.at(-1)), { chatId: "preview-chat", runId: "older-run", stepId: "missing" });
    assert.equal(await page.evaluate(() => RNAssistantChatInspector.openActivity({ chatId: "foreign", runId: "older-run", stepId: "step-2" })), false);
    await page.evaluate(() => { state.activeChatId = "another-chat"; state.activeRunViewState = null; RNAssistantChatInspector.render(); });
    await source.dispatchEvent("click");
    assert.doesNotMatch(await page.locator("#chatInspectorBody").textContent(), /Выбранный запуск|Источник из предыдущего запуска/);
    await page.evaluate(() => {
      state.activeChatId = "preview-chat";
      const items = Array.from({ length: 6 }, (_, index) => {
        const activity = { Kind: "tool", RunId: "grouped-run", StepId: "grouped-step",
          StepMessage: "Читаю модули", ToolId: "core.read", ToolCallId: "read-" + index,
          Status: "completed", ResultMessage: "Прочитан модуль " + index };
        return { message: { Id: "grouped-message-" + index, Role: "assistant",
          RunId: "grouped-run", Activity: activity }, activity, index };
      });
      const article = renderAgentRunArticle({ items, live: false });
      article.id = "grouped-step-probe";
      document.getElementById("messages").appendChild(article);
    });
    assert.equal(await page.locator("#grouped-step-probe .agent-activity-row").count(), 6);
    assert.equal(await page.locator("#grouped-step-probe .agent-activity-row .message-inspect-action").count(), 0,
      "individual calls have no duplicate step buttons");
    assert.equal(await page.locator("#grouped-step-probe .agent-step-actions .message-inspect-action").count(), 0,
      "the step body has no second work-history button");
    assert.equal(await page.locator("#grouped-step-probe > .message-footer .message-inspect-action").count(), 1,
      "six calls expose one work-history button beside copy and fork");
    assert.deepEqual(errors, []);
    console.log("PASS chat inspector: composition, cancellation, exact step/call routes, message entry, pinned run, missing/stale refusal, focus, light/dark and narrow layout");
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
