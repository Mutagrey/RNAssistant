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
    const demo = await browser.newPage();
    await demo.goto("file://" + path.resolve(__dirname, "../../prototypes/ui/index.html") + "#/chat/main");
    const disclosureStyle = node => { const css = getComputedStyle(node); return { font: css.fontSize, line: css.lineHeight,
      height: css.minHeight, padding: css.padding, background: css.backgroundColor, color: css.color }; };
    const referenceStyle = await demo.locator(".timeline-group > summary").first().evaluate(disclosureStyle);
    await demo.close();
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    const errors = []; page.on("pageerror", error => errors.push(error.message));
    await page.goto(address + "preview/web/?scenario=ready");
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    await page.evaluate(() => {
      window.historyQueries = [];
      window.copyTextResult = text => { window.historyCopied = text; return Promise.resolve(); };
      const row = (i, kind, status, extra = {}) => Object.assign({ id: "event-" + i, kind, status, runId: "history-run",
        stepId: i === 1 || i === 10 ? "" : i <= 5 ? "step-1" : "step-2", modelAttemptId: [2, 3].includes(i) ? "attempt-1" : [7, 8].includes(i) ? "attempt-2" : "", firstSequence: i, lastSequence: i, createdUtc: "2026-10-25T10:15:" + String(i).padStart(2, "0") + "Z",
        sourceEventSeqs: [i], sourceEventIds: ["preview-journal-event"],
        dataJson: '{"document":"Отчёт о продажах.xlsx","request":{"messages":[{"role":"user","content":"Собери сводку по кварталам"}],"limits":{"tokens":32000}},"amount":9007199254740993123456789}', resourceRefs: [] }, extra);
      const rows = [row(1, "turn.started", "running"), row(2, "llm.request", "prepared", { sourceEventIds: ["preview-request"] }), row(3, "llm.response", "received"),
        row(4, "tool.call.recorded", "accepted", { toolId: "excel.read_cells", toolCallId: "read-1", target: "Продажи · A1:F120", summary: "Чтение исходных данных для сводки" }),
        row(5, "tool.execution.finished", "completed", { toolId: "excel.read_cells", toolCallId: "read-1", summary: "Прочитано 120 строк" }),
        row(6, "tool.execution.finished", "failed", { toolId: "excel.create_sheet", toolCallId: "write-1", target: "Итоги", errorCode: "excel_sheet_already_exists", executionEvidence: { Dispatch: "NotDispatched", Effect: "None" } }),
        row(7, "model.request.prepared", "prepared"), row(8, "llm.response", "received"),
        row(9, "domain.effect.verified", "unknown", { toolId: "excel.write_cells", toolCallId: "write-2", target: "Итоги · A1:D8", summary: "Проверка записанных значений недоступна. Результат изменения не подтверждён.", executionEvidence: { Effect: "Unknown" } }),
        row(10, "turn.ended", "completed_with_errors", { summary: "Сводка подготовлена. Требуется проверить значения на листе «Итоги»." })];
      rows[1].dataJson = rows[1].dataJson.slice(0, -1) + ',\n"notes":' + JSON.stringify(Array.from({ length: 35 }, (_, i) =>
        "Строка " + (i + 1) + ": подробности запроса должны оставаться доступны целиком в общей прокрутке истории."), null, 2) + "\n}";
      rows[3].dataJson = '{"arguments":{"sheet":"Продажи","range":"A1:F120"}}';
      rows[4].dataJson = '{"resultData":{"columns":["Квартал","Выручка"],"rows":[["I",12800000],["II",15155200]],"readRows":120,"complete":true}}';
      const aggregate = (id, kind, title, status, data, extra = {}) => Object.assign(row(20, kind, status),
        { id, title, stepId: "step-1", dataJson: JSON.stringify(data, null, 2) }, extra);
      window.historyViews = {
        "model-replay": [aggregate("model-1", "model.step", "Model step-1", "completed", {
          eventTypes: ["llm.request", "llm.response"], requestEventIds: ["preview-request"], responseEventIds: ["preview-response"],
          formatRepairCount: 0, chunkBatchCount: 0, failureKinds: [] }, { promptTokens: 6240, completionTokens: 480, totalTokens: 6720, attemptCount: 1, durationMs: 1260 })],
        "tool-execution": [aggregate("tool-1", "tool", "excel.read_cells", "completed", { operationTypes: ["tool.call.recorded", "tool.execution.finished"],
          waitedForConfirmation: false, resultMessage: "Прочитано 120 строк", errorCode: null }, { toolId: "excel.read_cells", toolCallId: "read-1", durationMs: 284 }),
          aggregate("tool-2", "tool", "excel.write_cells", "unknown", { resultMessage: "Не удалось проверить записанные значения", errorCode: "readback_unavailable" },
            { toolId: "excel.write_cells", toolCallId: "write-2", failureCount: 1 })],
        "artifact-lineage": [aggregate("artifact-1", "artifact", "Сводка по кварталам.html", "created", { artifactType: "html", operationTypes: ["artifact.upsert"], revisions: 1 },
          { artifactId: "report-v1", parentArtifactId: "", lastSequence: 20 }),
          aggregate("artifact-2", "artifact", "Сводка по кварталам · уточнение.html", "created", { artifactType: "html", operationTypes: ["artifact.upsert"], revisions: 1 },
            { artifactId: "report-v2", parentArtifactId: "report-v1", firstSequence: 21, lastSequence: 21 })],
        "confirmation-pauses": [aggregate("confirm-1", "confirmation", "Confirmation: excel.write_cells", "cancelled", {
          waitedForConfirmation: true, confirmationStartedUtc: "2026-10-25T10:15:05Z", confirmationEndedUtc: "2026-10-25T10:15:14Z",
          resultMessage: "Пользователь отменил запись", pendingId: "confirm-write" }, { toolId: "excel.write_cells", toolCallId: "write-3", durationMs: 9000 })],
        "failure-retries": [aggregate("failure-1", "tool.failure", "Создание листа", "failed", { errorCode: "excel_sheet_already_exists", resultMessage: "Лист «Итоги» уже существует" },
          { toolId: "excel.create_sheet", toolCallId: "write-1", failureCount: 1 }),
          aggregate("failure-2", "model.failure", "Отклонённая попытка модели", "rejected", { failureKinds: ["response_format"], formatRepairCount: 1 }, { attemptCount: 2 })],
        "turn-usage": [aggregate("usage-1", "turn", "Turn history-run", "completed_with_errors", { modelStepCount: 2, toolCallCount: 3, confirmationPauseCount: 1, providerCostAvailable: true },
          { promptTokens: 12480, completionTokens: 960, totalTokens: 13440, costUsd: 0.00672, attemptCount: 2, durationMs: 5240 })],
        "vba-mutations": [{ mutationId: "mutation-1", sessionId: "preview-chat", journalRunId: "vba-run-1", kind: "module", operation: "replace", moduleName: "SalesSummary", status: "committed",
          componentCount: 1, firstSequence: 1, lastSequence: 3, createdUtc: "2026-10-25T10:15:20Z", errorCode: null }]
      };
      window.historyContextQueries = [];
      const original = send;
      send = async (type, payload) => {
        if (type === "getModelContext") {
          historyContextQueries.push(payload);
          const response = await original(type, payload);
          response.events.forEach(event => { if (event.type === "llm.request") event.trace = { ...event.trace, RunId: "history-run", StepId: "step-1" }; });
          return response;
        }
        if (type === "getChatEventPayload" && payload.eventId === "preview-request") {
          const response = await original(type, { ...payload, eventId: "preview-journal-event" });
          return { ...response, eventId: payload.eventId };
        }
        if (type === "getVbaMutations") return { view: "vba-mutations", rows: historyViews["vba-mutations"], totalRows: 1, totalMatches: 1 };
        if (type !== "getChatTrajectory") return original(type, payload);
        historyQueries.push(payload);
        if (window.historyFailure) throw Error("Не удалось прочитать историю");
        const selected = window.historyEmpty ? [] : (historyViews[payload.view] || rows).filter(row => !payload.minSequence || row.firstSequence >= payload.minSequence && row.firstSequence <= payload.maxSequence);
        return { chatId: payload.chatId, workspaceId: payload.workspaceId, view: payload.view, totalEvents: selected.length, totalRows: selected.length, totalMatches: selected.length,
          rows: selected, events: selected.map(row => ({ eventId: "preview-journal-event", sequence: row.firstSequence, type: row.kind,
            runId: row.runId, stepId: row.stepId, dataJson: row.dataJson, createdUtc: row.createdUtc, payloadByteLength: 104 })) };
      };
      openRunJournal({ chatId: state.activeChatId, runId: "history-run" });
    });
    await page.locator(".branch-run").first().waitFor();
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
    assert.ok((await page.locator(".branch-run > summary").first().boundingBox()).y < 285, "compact header leaves room for the tree");
    assert.equal(await page.locator(".branch-step").count(), 2);
    assert.match(await page.locator("#trajectoryViewInput").evaluate(node => getComputedStyle(node).backgroundImage), /svg/, "view picker keeps the common dropdown arrow");
    assert.deepEqual(await page.locator(".branch-step > summary").first().evaluate(disclosureStyle), referenceStyle, "history uses the demo disclosure style");
    assert.equal(await page.locator(".rn-run-journal-row:visible").count(), 0, "secondary events start collapsed");
    assert.equal(await page.locator(".branch-step > summary:visible").count(), 2, "main steps are visible immediately");
    assert.match(await page.locator(".branch-run > summary").textContent(), /Завершено с ошибками/);
    assert.match(await page.locator('[data-step-id="step-2"] > summary').textContent(), /Эффект не подтверждён/);
    await capture("history-wide");
    await page.getByLabel("Фильтр истории", { exact: true }).selectOption("problems");
    assert.equal(await page.locator(".rn-run-journal-row").count(), 3);
    await page.getByLabel("Фильтр истории", { exact: true }).selectOption("all");
    assert.equal(await page.locator(".branch-action[open]").count(), 0, "filter defaults do not overwrite user disclosure state");
    const firstStep = page.locator('[data-step-id="step-1"]');
    await firstStep.locator(":scope > summary").focus();
    await firstStep.locator(":scope > summary").press("Enter");
    assert.equal(await firstStep.locator(".branch-action:visible").count(), 1, "call and result share one action");
    assert.equal(await firstStep.locator(".rn-run-journal-row:visible").count(), 0);
    await page.getByLabel("Действия с деревом", { exact: true }).click();
    await page.getByRole("button", { name: "Развернуть шаги", exact: true }).click();
    await capture("history-steps");
    const readAction = page.locator('[data-call-id="read-1"]');
    await readAction.locator(":scope > summary").click();
    await readAction.locator(".rn-run-journal-part .rn-json-viewer").waitFor();
    assert.match(await readAction.locator(".rn-run-journal-part").textContent(), /readRows/);
    assert.equal(await readAction.locator(".rn-run-journal-row:visible").count(), 0, "direct result hides source-event noise");
    await capture("action-result");
    await readAction.getByLabel("Содержимое действия", { exact: true }).selectOption("arguments");
    assert.match(await readAction.locator(".rn-run-journal-part").textContent(), /A1:F120/);
    await capture("action-arguments");
    await readAction.getByLabel("Содержимое действия", { exact: true }).selectOption("events");
    assert.equal(await readAction.locator(".rn-run-journal-row:visible").count(), 2);
    await readAction.getByLabel("Содержимое действия", { exact: true }).selectOption("result");
    await readAction.locator(":scope > summary").click();
    await firstStep.locator(".branch-models > summary").click();
    await firstStep.getByRole("button", { name: "Состав запроса и ответа", exact: true }).click();
    await page.locator("#modelContextBody section > .model-context-message").first().waitFor({ timeout: 5000 }).catch(async error => {
      console.error(await page.locator("#modelContextStatus").textContent(), await page.locator("#modelContextBody").textContent(), errors); throw error;
    });
    assert.equal((await page.evaluate(() => historyContextQueries.at(-1))).requestEventId, "preview-request", "history opens the exact recorded exchange");
    await page.locator("#modelContextLayout").selectOption("focus");
    await capture("request-composition");
    await page.locator("#modelContextResponseTab").click();
    await page.waitForFunction(() => document.getElementById("modelContextBody").textContent.includes("18,4%"));
    await capture("model-response");
    await page.locator("#modelContextJsonTab").click();
    await page.locator("#modelContextBody .rn-json-viewer").waitFor();
    await capture("model-json");
    await page.getByLabel("Вернуться к истории", { exact: true }).click();
    await firstStep.locator(".branch-models > summary").waitFor();
    assert.equal(await page.locator("#tab-settings").evaluate(node => node.classList.contains("active")), true);
    assert.equal(await firstStep.evaluate(node => node.open), true, "return keeps the history branch open");
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
    await page.locator(".branch-run").first().waitFor();
    assert.equal((await page.evaluate(() => historyQueries.at(-1))).runId, "history-run");
    assert.equal(await request.locator(".rn-run-journal-technical").evaluate(node => node.open), true, "back restores the event disclosure");
    assert.equal(await request.locator(".rn-json-viewer").isVisible(), true, "back restores every ancestor too");
    await firstStep.locator(":scope > summary").click();
    await page.waitForFunction(() => !document.querySelector('[data-row-id="event-2"] .rn-json-viewer'));
    await page.locator("#refreshTrajectoryButton").click();
    await page.waitForFunction(() => document.querySelector('[data-step-id="step-1"]') && !document.querySelector('[data-step-id="step-1"]').open);
    assert.equal(await request.locator(".rn-json-viewer").count(), 0, "refresh retains explicit collapse without mounting hidden data");
    await firstStep.locator(":scope > summary").click();
    await request.locator(".rn-json-viewer").waitFor();
    await page.locator("#trajectoryViewInput").selectOption("raw");
    await page.locator(".trajectory-event").first().waitFor();
    assert.equal((await page.evaluate(() => historyQueries.at(-1))).runId, "history-run", "changing view retains source selection");
    assert.equal(await page.locator("#trajectoryEventData .rn-json-viewer").count(), 1);
    assert.match(await page.locator("#trajectoryDetailPart").evaluate(node => getComputedStyle(node).backgroundImage), /svg/);
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
    await page.waitForFunction(() => historyQueries.at(-1).visibility === "current");
    assert.equal(await page.locator("#trajectorySearchInput").count(), 0, "history no longer has a search field");
    assert.equal(await page.evaluate(() => settingsDirty), false, "history controls do not dirty saved settings");
    await page.locator("#trajectoryAdvancedOptions > summary").click();
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
      await page.locator(".branch-run").first().waitFor();
      await fits();
      await page.getByLabel("Действия с деревом", { exact: true }).click();
      await page.getByRole("button", { name: "Развернуть шаги", exact: true }).click();
      await capture("history-" + theme + "-narrow");
      // Long descriptions wrap and the expanded reader grows in the single pane scroll.
      const uncertain = page.locator('[data-call-id="write-2"]');
      await uncertain.locator(":scope > summary").click();
      const reason = uncertain.locator(".rn-run-journal-result-summary");
      assert.equal(await reason.evaluate(node => node.scrollHeight > node.clientHeight + 1), false);
      await uncertain.locator(":scope > summary").click();
      await firstStep.locator(".branch-models > summary").evaluate(node => { node.parentElement.open = true; });
      await request.locator(":scope > summary").evaluate(node => { node.parentElement.open = true; });
      await request.locator(".rn-run-journal-technical > summary").evaluate(node => { node.parentElement.open = true; });
      await request.locator(".rn-json-viewer").waitFor();
      assert.equal(await request.locator(".rn-json-viewer").evaluate(node => getComputedStyle(node).maxHeight), "none");
      assert.equal(await request.locator(".rn-json-body").evaluate(node => getComputedStyle(node).overflowY), "visible");
      await request.locator(".rn-json-mode-select").selectOption("raw");
      assert.ok((await request.locator(".rn-json-viewer").boundingBox()).height > 480, "long content grows beyond the old clipping height");
      assert.equal(await request.locator(".rn-json-body").evaluate(node => node.scrollHeight > node.clientHeight + 1), false, "no nested vertical scroll");
      assert.match(await request.locator(".rn-json-text").textContent(), /Строка 35:/);
      await fits();
      await request.locator(".rn-json-mode-select").selectOption("tree");
      await capture("history-expanded-" + theme + "-narrow");
      await page.locator("#trajectoryViewInput").selectOption("raw");
      await page.locator(".trajectory-event").first().waitFor();
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.evaluate(() => applyUiTheme({ UiTheme: "light" }, false));
    for (const view of ["raw", "model-replay", "tool-execution", "artifact-lineage", "confirmation-pauses", "failure-retries", "turn-usage", "vba-mutations"]) {
      await page.locator("#trajectoryViewInput").selectOption(view);
      await page.waitForFunction(() => !document.getElementById("refreshTrajectoryButton").disabled);
      await page.locator(".trajectory-event").first().waitFor();
      await fits(); await capture("view-" + view);
      if (view === "tool-execution") {
        await page.locator(".trajectory-event").first().focus();
        await page.locator(".trajectory-event").first().press("ArrowDown");
        assert.equal(await page.locator(".trajectory-event").nth(1).getAttribute("aria-selected"), "true");
      }
      if (view === "turn-usage") assert.match(await page.locator("#trajectoryOverview").textContent(), /Вход.*12.*480.*Выход.*960/);
    }
    await page.locator("#trajectoryViewInput").selectOption("raw");
    await page.locator(".trajectory-event").first().waitFor();
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
    console.log("PASS chat history: run/step/action tree, collapsed secondary events, unclipped details, exact routes/back, filters and both themes");
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
