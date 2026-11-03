"use strict";
// Shared product history with fixture events; no Office or model calls.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");
(async () => {
  const server = spawn("python3", ["-B", "prototypes/serve_ui.py", "--port", "0"], { cwd: path.resolve(__dirname, "../..") });
  server.stderr.resume(); // Drain the preview access log; an unread pipe can stall asset delivery.
  let browser;
  try {
    const address = await new Promise((resolve, reject) => {
      server.once("error", reject); server.once("exit", code => reject(Error("preview exited " + code)));
      server.stdout.on("data", bytes => { const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(bytes)); if (match) resolve(match[1]); });
    });
    browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    page.setDefaultTimeout(10000);
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
        const available = !payload.runId && payload.view === "run-causal" ? rows.concat([
          row(11, "turn.started", "running", { runId: "another-run", stepId: "" }),
          row(12, "turn.ended", "completed", { runId: "another-run", stepId: "", summary: "Сохранён второй запуск." })
        ]) : rows;
        const selected = window.historyEmpty ? [] : (historyViews[payload.view] || available).filter(row => !payload.minSequence || row.firstSequence >= payload.minSequence && row.firstSequence <= payload.maxSequence);
        return { chatId: payload.chatId, workspaceId: payload.workspaceId, view: payload.view, totalEvents: selected.length, totalRows: selected.length, totalMatches: selected.length,
          rows: selected, events: selected.map(row => ({ eventId: "preview-journal-event", sequence: row.firstSequence, type: row.kind,
            runId: row.runId, stepId: row.stepId, dataJson: row.dataJson, createdUtc: row.createdUtc, payloadByteLength: 104 })) };
      };
      openRunJournal({ chatId: state.activeChatId, runId: "history-run" });
    });
    const nav = page.locator(".rn-run-journal-nav"), detail = page.locator(".rn-run-journal-detail");
    await nav.locator("[data-tree-select]").first().waitFor();
    const capture = async name => {
      if (!process.env.HISTORY_SCREENSHOTS) return;
      fs.mkdirSync(process.env.HISTORY_SCREENSHOTS, { recursive: true });
      await page.screenshot({ path: path.join(process.env.HISTORY_SCREENSHOTS, name + ".png"), animations: "disabled" });
    };
    const fits = async () => {
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false, "history fits the viewport");
      assert.ok((await page.locator("#trajectoryWorkspace").boundingBox()).height >= 200, "reading space remains useful");
      assert.equal(await page.locator("#tab-diagnostics").evaluate(node => node.scrollWidth > node.clientWidth + 1), false);
    };
    const treeItem = title => nav.locator("[data-tree-select]").filter({ hasText: new RegExp("^" + title + "$") }).first().locator("..").locator("..");
    const choose = async title => treeItem(title).locator(":scope > .navigation-tree-row [data-tree-select]").click();
    const expand = async title => {
      const item = treeItem(title);
      if (await item.getAttribute("aria-expanded") === "false") await item.locator(":scope > .navigation-tree-row [data-tree-toggle]").click();
    };
    assert.equal(await page.locator('#tab-diagnostics h2').count(), 0, "Dock tab owns the screen title");
    assert.equal(await page.locator('#diagnosticsReturnChat, #diagnosticsReturnRequest').count(), 0, "Dock tabs own screen navigation");
    assert.equal(await nav.getAttribute("role"), "tree");
    assert.equal(await detail.locator("details, .navigation-tree").count(), 0, "no duplicate event tree in the reader");
    await fits(); await capture("history-wide");
    await expand("Шаг 1"); await choose("Чтение ячеек");
    await detail.locator(".rn-json-viewer").waitFor();
    assert.match(await detail.textContent(), /readRows/);
    assert.equal((await detail.textContent()).split("Прочитано 120 строк").length - 1, 1, "one result summary");
    assert.equal(await detail.locator('[data-row-id]').count(), 0, "source events live only in navigation");
    await nav.evaluate(node => Promise.allSettled(node.getAnimations({ subtree: true }).map(animation => animation.finished)));
    const styles = await page.evaluate(() => {
      const values = selector => { const style = getComputedStyle(document.querySelector(selector)); return [style.backgroundColor, style.color, style.borderRadius]; };
      return [values(".rn-run-journal-nav .navigation-tree-row.selected"), values("#chatSessionList .navigation-tree-row.selected")];
    });
    assert.deepEqual(styles[0], styles[1], "selection uses the common tree component");
    const nestedStyle = await nav.locator('[aria-level="2"] > .navigation-tree-row').first().evaluate(row => {
      const style = getComputedStyle(row), item = getComputedStyle(row.parentElement);
      return { font: style.font, color: style.color, height: row.getBoundingClientRect().height, bottom: item.marginBottom };
    });
    const chatStyle = await page.locator('#chatSessionList [aria-level="2"] > .navigation-tree-row:not(.selected)').first().evaluate(row => {
      const style = getComputedStyle(row); return { font: style.font, color: style.color, height: row.getBoundingClientRect().height, bottom: "0px" };
    });
    assert.deepEqual(nestedStyle, chatStyle, "nested groups use the same typography, color and density as ordinary navigation rows");
    await detail.getByLabel("Содержимое действия").selectOption("arguments");
    assert.match(await detail.textContent(), /A1:F120/);
    await capture("action-arguments");
    await choose("Модель");
    await detail.getByRole("button", { name: "Открыть запрос", exact: true }).click();
    await page.waitForFunction(() => document.getElementById("modelContextAttempt").value === "preview-request");
    assert.equal((await page.evaluate(() => historyContextQueries.at(-1))).requestEventId, "preview-request");
    await page.getByLabel("Вернуться к запуску", { exact: true }).click();
    assert.equal(await page.evaluate(() => RNAssistantDockWorkspace.isVisible("diagnostics")), true);
    await expand("Модель");
    await nav.locator('[data-tree-select="event-2"]').click();
    const eventChoice = detail.getByLabel("Содержимое события");
    await eventChoice.selectOption("evidence"); assert.match(await detail.textContent(), /sourceEventIds/);
    await eventChoice.selectOption("data"); assert.match(await detail.textContent(), /9007199254740993123456789/);
    await eventChoice.selectOption("payload"); await detail.locator(".rn-run-journal-payload .rn-json-viewer").waitFor();
    assert.equal(await detail.locator(".rn-json-viewer").count(), 1, "only the selected reader is mounted");
    await eventChoice.selectOption("data");
    await detail.getByLabel("Переходы события").click();
    await page.getByRole("menuitem", { name: "Диапазон событий #2", exact: true }).click();
    await page.locator(".trajectory-event").first().waitFor();
    assert.equal((await page.evaluate(() => historyQueries.at(-1))).minSequence, 2);
    await page.locator("#trajectoryBackButton").click();
    await detail.locator(".rn-json-viewer").waitFor();
    assert.equal(await detail.getAttribute("data-selection-id"), "event-2", "back restores selection");
    assert.equal(await nav.locator('[data-tree-key="event-2"]').getAttribute("aria-current"), "page");
    await page.locator('[data-tab-panel-id="diagnostics"] .dv-default-tab-action').click();
    await page.locator('#workspacePanels').click();
    await page.getByRole('menuitem', { name: 'Диагностика', exact: true }).click();
    await detail.locator(".rn-json-viewer").waitFor();
    assert.equal(await detail.getAttribute("data-selection-id"), "event-2", "close/reopen keeps presentation selection");
    for (const theme of ["light", "dark"]) {
      await page.evaluate(theme => applyUiTheme({ UiTheme: theme }, false), theme);
      await page.setViewportSize({ width: 390, height: 844 });
      await fits();
      await detail.locator(".rn-json-mode-select").selectOption("raw");
      assert.match(await detail.textContent(), /Строка 35:/);
      assert.equal(await detail.locator(".rn-json-body").evaluate(node => node.scrollHeight > node.clientHeight + 1), false, "detail pane owns vertical scrolling");
      await capture("history-" + theme + "-narrow");
      await detail.locator(".rn-json-mode-select").selectOption("tree");
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.evaluate(() => { document.documentElement.style.zoom = "1.25"; });
    await fits(); await capture("history-zoom");
    await page.evaluate(() => { document.documentElement.style.zoom = ""; applyUiTheme({ UiTheme: "light" }, false); });
    await page.locator("#trajectoryViewInput").selectOption("raw");
    await page.locator(".trajectory-event").first().waitFor();
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
    await page.getByRole("button", { name: "Все запуски", exact: true }).click();
    await page.waitForFunction(() => !historyQueries.at(-1).runId);
    assert.equal((await page.evaluate(() => historyQueries.at(-1))).chatId, "preview-chat");
    assert.equal(await nav.locator('[aria-level="1"]').count(), 2, "all runs can be selected inside diagnostics");
    await nav.locator('[aria-level="1"] > .navigation-tree-row [data-tree-select]').last().click();
    assert.match(await detail.textContent(), /Сохранён второй запуск/);
    await page.evaluate(() => {
      window.savedRunTree = document.querySelector('.rn-run-journal-nav');
      RNAssistantDiagnostics.returnToJournal({ owner: "office", chatId: "preview-chat", runId: "another-run" });
    });
    assert.equal(await nav.evaluate(node => node === window.savedRunTree), true, "request return preserves the selected run from the whole-chat tree");
    const queriesBeforeMove = await page.evaluate(() => historyQueries.length);
    await page.locator('.dock-group-actions').first().getByRole('button', { name: 'Открыть экран снизу', exact: true }).click();
    await page.locator('.dock-screen-chooser').getByRole('button', { name: 'Диагностика', exact: true }).click();
    assert.equal(await page.locator('[data-tab-panel-id="diagnostics"]').count(), 1);
    await page.locator('#workspaceLayout').click();
    await page.getByRole('menuitem', { name: 'Сохранить раскладку', exact: true }).click();
    await page.locator('#workspaceLayout').click();
    await page.getByRole('menuitem', { name: 'Одна группа вкладок', exact: true }).click();
    await page.locator('#workspaceLayout').click();
    await page.getByRole('menuitem', { name: 'Вернуть сохранённую', exact: true }).click();
    assert.equal(await nav.evaluate(node => node === window.savedRunTree), true, "move/split/restore keeps the mounted tree");
    assert.match(await detail.textContent(), /Сохранён второй запуск/);
    assert.equal(await page.evaluate(() => historyQueries.length), queriesBeforeMove, "placement does not reread the journal");
    await page.getByLabel("Действия с историей", { exact: true }).click();
    await page.getByRole("menuitem", { name: "Экспорт диагностики", exact: true }).click();
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
    await page.locator('.rail-button[data-tab="settings"]').click();
    await page.locator('[data-settings-page="model"]').first().click();
    await page.locator("#modelInput").fill("test-model-change");
    assert.equal(await page.evaluate(() => settingsDirty), true, "real settings still become dirty");
    assert.deepEqual(errors, []);
    console.log("PASS history: shared tree and selection, one reader, exact routes, close/reopen, bounds, narrow/zoom and themes");
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
