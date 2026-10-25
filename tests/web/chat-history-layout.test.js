"use strict";
// Shared product history with fixture events; no Office or model calls.
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
    browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    const errors = []; page.on("pageerror", error => errors.push(error.message));
    await page.goto(address + "preview/web/?scenario=ready");
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    await page.evaluate(() => {
      window.historyQueries = [];
      window.copyTextResult = text => { window.historyCopied = text; return Promise.resolve(); };
      const row = (i, kind, status, extra = {}) => Object.assign({ id: "event-" + i, kind, status, runId: "history-run",
        stepId: "step-" + Math.ceil(i / 3), firstSequence: i, lastSequence: i, createdUtc: "2026-10-25T10:15:" + String(i).padStart(2, "0") + "Z",
        sourceEventSeqs: [i], sourceEventIds: ["preview-journal-event"],
        dataJson: '{"document":"Отчёт о продажах.xlsx","request":{"messages":[{"role":"user","content":"Собери сводку по кварталам"}],"limits":{"tokens":32000}},"amount":9007199254740993123456789}', resourceRefs: [] }, extra);
      const rows = [row(1, "turn.started", "running"), row(2, "model.request.prepared", "prepared"), row(3, "llm.response", "received"),
        row(4, "tool.call.recorded", "accepted", { toolId: "excel.read_cells", toolCallId: "read-1", target: "Продажи · A1:F120", summary: "Чтение исходных данных для сводки" }),
        row(5, "tool.execution.finished", "completed", { toolId: "excel.read_cells", toolCallId: "read-1", summary: "Прочитано 120 строк" }),
        row(6, "tool.execution.finished", "failed", { toolId: "excel.create_sheet", toolCallId: "write-1", target: "Итоги", errorCode: "excel_sheet_already_exists", executionEvidence: { Dispatch: "NotDispatched", Effect: "None" } }),
        row(7, "model.request.prepared", "prepared"), row(8, "llm.response", "received"),
        row(9, "domain.effect.verified", "unknown", { toolId: "excel.write_cells", toolCallId: "write-2", target: "Итоги · A1:D8", summary: "Проверка записанных значений недоступна. Результат изменения не подтверждён.", executionEvidence: { Effect: "Unknown" } }),
        row(10, "turn.ended", "completed_with_errors", { summary: "Сводка подготовлена. Требуется проверить значения на листе «Итоги»." })];
      const original = send;
      send = async (type, payload) => {
        if (type !== "getChatTrajectory") return original(type, payload);
        historyQueries.push(payload);
        if (window.historyFailure) throw Error("Не удалось прочитать историю");
        const selected = window.historyEmpty ? [] : rows.filter(row => !payload.minSequence || row.firstSequence >= payload.minSequence && row.firstSequence <= payload.maxSequence);
        return { chatId: payload.chatId, workspaceId: payload.workspaceId, view: payload.view, totalEvents: selected.length, totalRows: selected.length, totalMatches: selected.length,
          rows: selected, events: selected.map(row => ({ eventId: "preview-journal-event", sequence: row.firstSequence, type: row.kind,
            runId: row.runId, stepId: row.stepId, dataJson: row.dataJson, createdUtc: row.createdUtc, payloadByteLength: 104 })) };
      };
      openRunJournal({ chatId: state.activeChatId, runId: "history-run" });
    });
    await page.locator(".rn-run-journal-row").last().waitFor();
    const capture = async name => {
      if (!process.env.HISTORY_SCREENSHOTS) return;
      fs.mkdirSync(process.env.HISTORY_SCREENSHOTS, { recursive: true });
      await page.screenshot({ path: path.join(process.env.HISTORY_SCREENSHOTS, name + ".png"), animations: "disabled" });
    };
    const fits = async () => {
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1);
      assert.equal(overflow, false, "history fits the viewport: " + JSON.stringify(await page.evaluate(() => [...document.querySelectorAll("body *")].map(node => ({ tag: node.id || node.className, right: node.getBoundingClientRect().right, width: node.getBoundingClientRect().width })).filter(item => item.right > innerWidth + 1 && item.width > 0).slice(-12))));
      const content = await page.locator("#trajectoryWorkspace").boundingBox();
      assert.ok(content.height >= 200, "events retain useful reading space");
    };
    await capture("history-wide");
    await page.evaluate(() => document.fonts.ready);
    await fits();
    assert.ok((await page.locator(".rn-run-journal-row-summary").first().boundingBox()).y < 285, "compact header leaves room for events");
    assert.ok((await page.locator(".rn-run-journal-row-summary").first().boundingBox()).height <= 55);
    assert.equal(await page.locator(".rn-run-journal-issues").getAttribute("open"), null);
    await capture("history-wide");
    await page.getByLabel("Фильтр истории", { exact: true }).selectOption("problems");
    assert.equal(await page.locator(".rn-run-journal-row").count(), 3);
    await page.getByLabel("Фильтр истории", { exact: true }).selectOption("all");
    const request = page.locator('[data-row-id="event-2"]');
    await request.locator(":scope > summary").click();
    await request.locator(".rn-run-journal-technical > summary").click();
    await request.locator(".rn-json-viewer").waitFor();
    assert.equal(await request.locator(".rn-json-viewer").count(), 1, "one technical tree at a time");
    await request.getByLabel("Данные события или связи").selectOption("evidence");
    assert.match(await request.textContent(), /sourceEventIds/);
    await request.getByLabel("Данные события или связи").selectOption("data");
    assert.match(await request.textContent(), /9007199254740993123456789/);
    await capture("history-expanded");
    await request.getByRole("button", { name: "Показать запрос к модели", exact: true }).click();
    await request.locator(".rn-run-journal-payload .rn-json-viewer").waitFor();
    assert.equal(await request.locator(".rn-json-viewer").count(), 1, "loading the body replaces the technical tree");
    assert.equal(await request.locator(".rn-run-journal-technical").evaluate(node => node.open), false);
    await request.locator(".rn-run-journal-technical > summary").click();
    await request.locator(".rn-run-journal-data .rn-json-viewer").waitFor();
    assert.equal(await request.locator(".rn-run-journal-payload").isHidden(), true);
    await request.getByLabel("Переходы события").click();
    await request.getByRole("button", { name: "Диапазон событий #2", exact: true }).click();
    await page.locator(".trajectory-event").first().waitFor();
    assert.equal(await page.locator("#trajectoryViewInput").inputValue(), "raw");
    assert.equal((await page.evaluate(() => historyQueries.at(-1))).minSequence, 2);
    await page.locator("#trajectoryBackButton").click();
    await page.locator(".rn-run-journal-row").last().waitFor();
    assert.equal((await page.evaluate(() => historyQueries.at(-1))).runId, "history-run");
    assert.equal(await request.locator(".rn-run-journal-technical").evaluate(node => node.open), true, "back restores the event disclosure");
    await page.locator("#trajectoryViewInput").selectOption("raw");
    await page.locator(".trajectory-event").first().waitFor();
    assert.equal((await page.evaluate(() => historyQueries.at(-1))).runId, "history-run", "changing view retains source selection");
    assert.equal(await page.locator("#trajectoryEventData .rn-json-viewer").count(), 1);
    assert.equal(await page.locator("#trajectoryEventData .rn-json-mode").count(), 0, "one view selector replaces repeated mode buttons");
    const nodeActions = page.locator("#trajectoryEventData .rn-json-scalar-row").first().locator(".rn-json-node-actions");
    assert.equal(await nodeActions.evaluate(node => getComputedStyle(node).opacity), "0", "node buttons stay quiet until needed");
    await nodeActions.locator("summary").focus();
    assert.equal(await nodeActions.evaluate(node => getComputedStyle(node).opacity), "1", "keyboard focus reveals node actions");
    await nodeActions.locator("summary").press("Enter");
    await nodeActions.getByRole("button", { name: "Узел", exact: true }).click();
    assert.equal(await page.evaluate(() => historyCopied), '\"Отчёт о продажах.xlsx\"');
    assert.equal(await nodeActions.evaluate(node => node.open), false, "copy dismisses its menu");
    await nodeActions.locator("summary").press("Enter");
    await nodeActions.locator("summary").press("Escape");
    assert.equal(await nodeActions.evaluate(node => node.open), false);
    await capture("history-tree-wide");
    await page.getByLabel("Действия с историей", { exact: true }).click();
    await page.locator("#trajectoryFiltersButton").click();
    await page.locator("#trajectoryVisibilityInput").selectOption("current");
    await page.locator("#trajectorySearchInput").fill("квартал");
    await page.locator("#trajectorySearchInput").press("Enter");
    await page.waitForFunction(() => historyQueries.at(-1).search === "квартал");
    assert.equal(await page.evaluate(() => settingsDirty), false, "history controls do not dirty saved settings");
    await page.locator("#trajectoryAdvancedOptions > summary").click();
    await page.locator("#trajectorySearchInput").fill("");
    for (const theme of ["light", "dark"]) {
      await page.evaluate(theme => applyUiTheme({ UiTheme: theme }, false), theme);
      await page.setViewportSize({ width: 390, height: 844 });
      await capture("history-tree-" + theme + "-narrow");
      await fits();
      const mode = page.locator("#trajectoryEventData .rn-json-mode-select");
      await mode.selectOption("raw");
      assert.match(await page.locator("#trajectoryEventData").textContent(), /9007199254740993123456789/);
      await mode.selectOption("tree");
      await page.locator("#trajectoryViewInput").selectOption("run-causal");
      await page.locator(".rn-run-journal-row").last().waitFor();
      await fits();
      await capture("history-" + theme + "-narrow");
      await page.locator("#trajectoryViewInput").selectOption("raw");
      await page.locator(".trajectory-event").first().waitFor();
    }
    await page.getByLabel("Действия с историей", { exact: true }).click();
    await page.locator("#clearTrajectoryCorrelationButton").click();
    await page.waitForFunction(() => !historyQueries.at(-1).runId);
    assert.equal((await page.evaluate(() => historyQueries.at(-1))).chatId, "preview-chat");
    await page.getByLabel("Действия с историей", { exact: true }).click();
    await page.locator("#trajectoryExportOptionsButton").click();
    assert.equal(await page.locator("#exportTrajectoryButton").isVisible(), true);
    await page.locator("#trajectoryExportOptions > summary").click();
    await page.evaluate(() => { window.historyEmpty = true; });
    await page.locator("#trajectoryViewInput").selectOption("run-causal");
    await page.getByText("Для выбранного фильтра строк нет.", { exact: true }).waitFor();
    await page.evaluate(() => { window.historyFailure = true; });
    await page.locator("#refreshTrajectoryButton").click();
    await page.waitForFunction(() => document.getElementById("trajectoryStatus").textContent.includes("Не удалось"));
    assert.equal(await page.locator("#trajectoryWorkspace").isHidden(), true);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.locator('[data-settings-page="model"]').first().click();
    await page.locator("#modelInput").fill("test-model-change");
    assert.equal(await page.evaluate(() => settingsDirty), true, "real settings still become dirty");
    assert.deepEqual(errors, []);
    console.log("PASS chat history: compact layouts, one tree, exact routes/back, filters, settings isolation, empty/error and both themes");
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
