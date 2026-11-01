"use strict";
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
      server.once("error", reject); server.once("exit", code => reject(new Error("preview exited " + code)));
      server.stdout.on("data", data => { const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(data)); if (match) resolve(match[1]); });
    });
    browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    page.setDefaultTimeout(10000);
    const errors = []; page.on("pageerror", error => errors.push(error.message));
    await page.goto(address + "preview/web/?scenario=ready");
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    assert.equal(await page.locator("#modelContextBody").evaluate(node => getComputedStyle(node).overflowY), "auto", "request stylesheet loaded");
    await page.evaluate(() => {
      const original = send, originalDownload = RNAssistantResourceDownload.read;
      window.diagnosticCalls = []; window.holdRequest = null;
      state.activeRunViewState = { runId: "preview-run", lifecycle: "failed" };
      const settings = { ResponseFormat: "json_object", ToolResultRole: "tool", InstructionRole: "developer", MaxOutputTokens: 2048, Temperature: 0, TopP: 1 };
      const make = (id, sequence, type, trace = {}) => ({ eventId: id, sequence, type, alias: "Q" + sequence, createdUtc: "2026-10-30T10:00:00Z", hasPayload: true,
        trace: { RunId: "preview-run", StepId: "preview-step", ModelAttemptId: "attempt", RequestId: id, Model: "synthetic-" + "long-model-name-".repeat(12), Streaming: false, ResponseFormat: "json_object", Settings: settings, ...trace } });
      const requests = [make("q1", 1, "llm.request"), make("q2", 4, "llm.request", { StepId: "retry-step" })];
      const rejection = "invalid_tool_call: tool_calls[0].arguments must be an object; received string.";
      window.diagnosticBodies = {
        q1: JSON.stringify({ model: "synthetic", messages: [{ role: "developer", content: "Rules" }, { role: "user", content: "Тест " + "long name ".repeat(70) },
          { role: "assistant", content: '{"action":"execute","message":"historical","tool_calls":[]}' }] }),
        q2: JSON.stringify({ model: "synthetic", messages: [{ role: "developer", content: "Rules" }, { role: "user", content: "Тест" }, { role: "developer", content: rejection }] }),
        reject: '{"action":"execute","reason":"read","tool_calls":[{"name":"core.read","arguments":"{\\\"target\\\":\\\"file\\\"}"}],"message":""}',
        response: '{"choices":[{"message":{"content":"{\\\"action\\\":\\\"finish\\\",\\\"message\\\":\\\"done\\\"}"}}]}'
      };
      send = function (type, payload) {
        diagnosticCalls.push({ type, payload });
        if (type === "getModelContext" && payload.requestEventId === window.holdRequest) {
          const pending = new Promise(resolve => window.releaseDiagnostic = resolve); pending.requestId = "held"; return pending;
        }
        if (type === "getModelContext") {
          if (window.diagnosticsError) return Promise.reject(new Error("Synthetic read failure"));
          const request = requests.find(item => item.eventId === payload.requestEventId);
          if (window.diagnosticsEmpty) return Promise.resolve({ chatId: payload.chatId, workspaceId: payload.workspaceId, revision: 99, events: [] });
          const rejected = request && request.eventId === "q1";
          return Promise.resolve({ chatId: payload.chatId, workspaceId: payload.workspaceId, revision: 99,
            events: request ? [request, rejected ? make("reject", 2, "agent.response.rejected", { Error: rejection }) : make("response", 5, "llm.response", { Completed: false, CaptureTruncated: true })] : requests.slice().reverse(),
            summary: request ? { parser: rejected ? "Rejected" : "Accepted", runtime: "NotRecorded", reason: rejected ? rejection : null,
              previousRequestEventId: rejected ? null : "q1", requestBytes: 512, captureTruncated: !rejected, responseComplete: false } : null,
            resources: [], evidenceReport: payload.includeEvidence ? { hasContextTrace: true, tools: [{ callId: "call", operationAlias: "T1", evidenceAlias: "E1", toolName: "core.patch", outcome: "Unknown", eligibility: "Eligible", delivery: "Excluded", messageIndices: [], runId: "preview-run", stepId: "preview-step" }], finishes: [] } : null,
            comparison: payload.compareRequestEventId ? { beforeEventId: "q1", afterEventId: "q2", available: true,
              reason: "Сохранённые запросы", beforeBytes: 512, afterBytes: 600, beforeMessages: 2, afterMessages: 3,
              unchangedPrefix: 2, unchangedSuffix: 0, changes: [{ kind: "added", afterIndex: 2, afterRole: "developer", fields: ["content"] }], settingsChanged: [] } : null });
        }
        if (type === "getModelContextPayload") return Promise.resolve({ chatId: payload.chatId, workspaceId: payload.workspaceId, eventId: payload.eventId,
          data: { leaseId: payload.eventId, payload: { contentType: "application/json" } } });
        if (type === "resourceDataClose") return Promise.resolve({});
        if (type === "getChatTrajectory" && window.holdTrajectory) {
          const pending = new Promise(resolve => window.releaseTrajectory = resolve); pending.requestId = "held-journal"; return pending;
        }
        if (type === "getChatTrajectory") return Promise.resolve({ chatId: payload.chatId, workspaceId: payload.workspaceId, view: "run-causal", rows: [
          { id: "request-row", kind: "model.request.prepared", runId: "preview-run", stepId: "preview-step", modelAttemptId: "attempt", title: "Запрос модели", status: "prepared", firstSequence: 1, lastSequence: 1, sourceEventIds: ["q1"], sourceEventSeqs: [1], dataJson: "{}" },
          { id: "reject-row", kind: "model.response.rejected", runId: "preview-run", stepId: "preview-step", modelAttemptId: "attempt", title: rejection, status: "rejected", firstSequence: 2, lastSequence: 2, sourceEventIds: ["reject"], sourceEventSeqs: [2], dataJson: "{}" }
        ], hasMore: false, totalRows: 2, totalMatches: 2 });
        return original(type, payload);
      };
      RNAssistantResourceDownload.read = (data, options) => diagnosticBodies[data.leaseId] ? Promise.resolve(new TextEncoder().encode(diagnosticBodies[data.leaseId])) : originalDownload(data, options);
      cancelBridgeRequest = async id => { diagnosticCalls.push({ type: "cancelRequest", id }); };
    });
    const screenshot = async name => {
      if (!process.env.DIAGNOSTICS_SCREENSHOTS) return;
      fs.mkdirSync(process.env.DIAGNOSTICS_SCREENSHOTS, { recursive: true });
      await page.mouse.move(0, 0); await page.screenshot({ path: path.join(process.env.DIAGNOSTICS_SCREENSHOTS, name + ".png"), animations: "disabled" });
    };
    await page.locator("#contextMeter").focus(); await page.keyboard.press("Enter");
    assert.equal(await page.locator("#chatInspectorViewTitle").textContent(), "Токены и прогноз");
    assert.equal(await page.evaluate(() => diagnosticCalls.filter(call => call.type === "inspectPromptContext").length), 0, "estimate stays lazy");
    await page.locator('[data-inspector-key="estimate"] > summary').click();
    await page.getByText(/Окно:.*резерв ответа/).waitFor();
    assert.equal(await page.evaluate(() => diagnosticCalls.filter(call => call.type === "inspectPromptContext").length), 1);
    await page.locator("#chatInspector").press("Escape");
    assert.equal(await page.evaluate(() => document.activeElement.id), "contextMeter");
    assert.equal(await page.locator("#contextMeter").getAttribute("aria-expanded"), "false");
    await page.getByRole("button", { name: "Диагностика", exact: true }).click();
    await page.locator("#trajectoryEvents .rn-run-journal-tree").waitFor();
    assert.equal(await page.evaluate(() => diagnosticCalls.find(call => call.type === "getChatTrajectory").payload.runId), "preview-run", "current run takes precedence over older messages");
    assert.equal(await page.locator('[data-settings-page="service"]').count(), 0);
    assert.equal(await page.locator("#promptContextInspector").count(), 0);
    assert.equal(await page.evaluate(() => diagnosticCalls.filter(call => /runModel|testModel/.test(call.type)).length), 0);
    await screenshot("run-wide-light");
    await page.evaluate(() => {
      window.savedJournal = document.getElementById("trajectoryEvents").firstElementChild;
      RNAssistantModelContext.open({ runId: "preview-run", stepId: "preview-step", requestEventId: "q1" });
    });
    await page.getByRole("heading", { name: "Ответ модели отклонён", exact: true }).waitFor();
    assert.match(await page.locator("#modelContextBody").innerText(), /tool_calls\[0\]\.arguments/);
    await screenshot("before-settings");
    await page.getByText("Настройки этого запроса", { exact: true }).click();
    assert.match(await page.locator("#modelContextBody").innerText(), /Native tool history/);
    await page.getByText("Настройки этого запроса", { exact: true }).click();
    assert.equal(await page.evaluate(() => diagnosticCalls.filter(call => call.type === "getModelContextPayload").length), 0, "summary is metadata only");
    await screenshot("rejected-wide-light");
    await page.locator("#modelContextResponseTab").click();
    await page.getByText("Точный отклонённый ответ", { exact: true }).click();
    await page.locator("#modelContextBody .rn-json-viewer").waitFor();
    assert.match(await page.locator("#modelContextBody").innerText(), /string/);
    await page.locator("#modelContextMessagesTab").click();
    await page.locator("#modelContextBody .is-message").nth(2).locator(":scope > summary").click();
    await page.getByText(/Историческая проекция · content является строкой/).waitFor();
    const expanded = page.locator('#modelContextBody .is-message[data-message-index="2"]');
    await page.evaluate(() => window.savedMessageNode = document.querySelector('#modelContextBody .is-message[data-message-index="2"]'));
    await page.locator("#modelContextJsonTab").click(); await page.locator("#modelContextMessagesTab").click();
    assert.equal(await expanded.evaluate(node => node === window.savedMessageNode && node.open), true, "preserves DOM, expansion and selected request");
    await page.locator("#closeModelContextButton").click();
    assert.equal(await page.evaluate(() => savedJournal === document.getElementById("trajectoryEvents").firstElementChild), true, "return preserves journal");
    await page.evaluate(() => RNAssistantModelContext.open({ runId: "preview-run", stepId: "retry-step", requestEventId: "q2" }));
    await page.getByRole("heading", { name: "Форма ответа принята", exact: true }).waitFor();
    await page.locator("#modelContextCompareTab").click();
    await page.getByRole("heading", { name: "Предыдущий → выбранный запрос" }).waitFor();
    await page.getByRole("button", { name: "Открыть предыдущий запрос", exact: true }).click();
    await page.getByRole("heading", { name: "Ответ модели отклонён", exact: true }).waitFor();
    await page.locator("#modelContextAttempt").selectOption("q2");
    await page.getByRole("heading", { name: "Форма ответа принята", exact: true }).waitFor();
    await page.locator("#modelContextCompareTab").click();
    await page.getByRole("button", { name: /Добавлено · сообщение/ }).click();
    await page.locator('#modelContextBody .is-message[data-message-index="2"][open]').waitFor();
    await page.locator("#modelContextEvidenceTab").click();
    await page.locator('#modelContextBody [data-evidence-call="call"] > summary').click();
    await page.getByText("E исключён из этого запроса.", { exact: true }).waitFor();
    assert.match(await page.locator("#modelContextBody").innerText(), /Unknown|неизвест|не подтвержд/i);
    await page.evaluate(() => window.savedEvidence = document.querySelector('[data-evidence-call="call"]'));
    await page.getByRole("button", { name: "Ход выполнения", exact: true }).click();
    await page.locator("#trajectoryEvents .rn-run-journal-tree").waitFor();
    await page.locator("#diagnosticsReturnRequest").click();
    assert.equal(await page.locator('[data-evidence-call="call"]').evaluate(node => node === savedEvidence && node.open), true, "return from linked events preserves the request");
    for (const theme of ["light", "dark"]) {
      await page.evaluate(theme => document.documentElement.dataset.theme = theme, theme);
      for (const width of [320, 420, 1440]) {
        await page.setViewportSize({ width, height: 900 });
        await page.locator("#modelContextSummaryTab").click();
        await screenshot("partial-" + width + "-" + theme);
        assert.equal(await page.locator("#tab-diagnostics").evaluate(node => node.scrollWidth > node.clientWidth + 1), false, "no horizontal overflow " + width);
        assert.equal(await page.locator("#modelContextBody").evaluate(node => node.scrollWidth > node.clientWidth + 1), false, "request content fits " + width);
        await screenshot("partial-" + width + "-" + theme);
      }
    }
    await page.locator("#modelContextSummaryTab").focus(); await page.keyboard.press("ArrowRight");
    assert.equal(await page.locator("#modelContextMessagesTab").getAttribute("aria-selected"), "true");
    const bodyReads = await page.evaluate(() => diagnosticCalls.filter(call => call.type === "getModelContextPayload").length);
    await page.locator('[data-diagnostics-tab="application"]').click();
    await page.locator('[data-diagnostics-application="log"]').waitFor();
    await screenshot("application-log-wide");
    await page.locator("#diagnosticsAppView").selectOption("health");
    await page.getByRole("heading", { name: "Задержки текущей панели" }).waitFor();
    await page.setViewportSize({ width: 320, height: 900 });
    assert.equal(await page.locator("#tab-diagnostics").evaluate(node => node.scrollWidth > node.clientWidth + 1), false);
    await screenshot("application-health-narrow");
    assert.equal(await page.evaluate(() => diagnosticCalls.filter(call => call.type === "getModelContextPayload").length), bodyReads);
    assert.equal(await page.evaluate(() => diagnosticCalls.filter(call => /runModel|testModel|auditCas|collectCas/.test(call.type)).length), 0, "opening application screens runs no checks");
    await page.locator('[data-diagnostics-tab="runs"]').click();
    await page.locator("#modelContextMessagesTab[aria-selected=true]").waitFor();
    await page.locator("#modelContextMessagesTab").focus();
    await page.keyboard.press("Escape");
    await page.evaluate(() => { holdTrajectory = true; });
    await page.locator("#refreshTrajectoryButton").click();
    await page.waitForFunction(() => !!window.releaseTrajectory);
    await page.locator('[data-diagnostics-tab="application"]').click();
    assert.ok(await page.evaluate(() => diagnosticCalls.some(call => call.id === "held-journal")), "hidden journal query is cancelled");
    await page.evaluate(() => { holdTrajectory = false; });
    await page.locator('[data-diagnostics-tab="runs"]').click();
    await page.locator("#trajectoryEvents .rn-run-journal-tree").waitFor();
    await page.evaluate(() => releaseTrajectory({ chatId: "preview-chat", rows: [], hasMore: false, totalRows: 0 }));
    assert.equal(await page.locator("#trajectoryEvents .rn-run-journal-tree").count(), 1, "late cancelled journal cannot replace resumed data");
    await page.evaluate(() => { holdRequest = "q1"; RNAssistantModelContext.open({ requestEventId: "q1" }); });
    await page.getByText("Загрузка запроса…", { exact: true }).waitFor();
    await page.evaluate(() => { state.activeChatId = "other-chat"; RNAssistantModelContext.sync(); releaseDiagnostic({ chatId: "preview-chat", events: [] }); });
    assert.equal(await page.locator("#modelContextDialog").isVisible(), false, "late response cannot bind to another chat");
    assert.ok(await page.evaluate(() => diagnosticCalls.some(call => call.type === "cancelRequest")), "selection cancels pending bridge request");
    await page.evaluate(() => { state.activeChatId = "preview-chat"; holdRequest = null; diagnosticsEmpty = true; RNAssistantModelContext.open(); });
    await page.getByRole("heading", { name: "Нет сохранённых запросов", exact: true }).waitFor();
    await screenshot("empty");
    await page.evaluate(() => { RNAssistantModelContext.close(false); diagnosticsEmpty = false; diagnosticsError = true; RNAssistantModelContext.open(); });
    await page.getByRole("heading", { name: "Не удалось загрузить историю", exact: true }).waitFor();
    await screenshot("error");
    assert.deepEqual(errors, []);
    console.log("diagnostics navigation: rejection, retry, comparison, types, lazy bodies, state, scope, cancellation, empty/error, keyboard and themes passed");
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
