"use strict";
// Shipped UI with a synthetic bridge only. Never calls a model or Office.
const assert = require("node:assert/strict");
const path = require("node:path");
const { chromium } = require("playwright");

(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1100, height: 900 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto("file://" + path.resolve(__dirname, "../../web/index.html"));
    await page.evaluate(() => {
      window.labCalls = []; window.labHold = false;
      const controls = { A2: ["A2-system"], C3: ["C3-valid"], C4: ["C4-control"] };
      const catalog = { scenarioRevision: 2, endpoint: "http://synthetic.invalid/v1", model: "scripted",
        scenarios: Object.keys(modelDiagnosticTitles).map(id => ({ id, capability: id, assertion: "HistoryJson",
          controls: controls[id] || [], isControl: ["A2-system", "C3-valid", "C4-control"].includes(id),
          responseFormat: "json_object", streaming: id === "C4" })) };
      function exchange(id) {
        return { result: { ScenarioId: id, Model: "scripted", Endpoint: catalog.endpoint, ExecutionKind: "Synthetic",
          Verdict: id === "B5" ? "Fail" : "Pass", Reason: id === "B5" ? "call_id_mismatch" : "exact_v8_response",
          HttpStatusCode: 200, HttpAccepted: true, MarkerMatches: id === "B5" ? true : null,
          CallIdMatches: id === "B5" ? false : null, TargetMatches: id === "B5" ? true : null,
          ParserVerdict: id.startsWith("C4") ? "Pass" : "NotRun", ParityVerdict: id === "C4" ? "Pass" : "NotRun",
          StreamingRequested: id === "C4", StreamingObserved: id === "C4", ResponseBodyComplete: false,
          DurationMs: 1270, PromptTokens: 120, CompletionTokens: 12, ExpectedJson: '{"marker":"exact"}',
          ActualContent: '<img src=x onerror="window.injected=true">', RequestSha256: "a".repeat(64) },
          request: { text: '{"messages":[{"role":"developer","content":"Test"},{"role":"user","content":"Read marker"}]}', truncated: false, redacted: false },
          response: { text: "data: [REDACTED]", truncated: true, redacted: true }, resultRedacted: true, resultTruncated: false };
      }
      window.send = function (type, payload) {
        labCalls.push({ type, payload });
        if (type === "getModelDiagnosticScenarios") return Promise.resolve(catalog);
        if (type !== "runModelDiagnostic") return Promise.reject(new Error("Synthetic bridge: " + type));
        if (window.labHold) {
          const waiting = new Promise((resolve, reject) => { window.labReject = reject; });
          waiting.requestId = "held-probe"; return waiting;
        }
        return Promise.resolve({ scenarioRevision: 2, seed: payload.seed,
          exchanges: [...(controls[payload.scenarioId] || []), payload.scenarioId].map(exchange) });
      };
      window.cancelBridgeRequest = function (id) {
        labCalls.push({ type: "cancelRequest", payload: { requestId: id } });
        window.labReject(new Error("Cancelled")); return Promise.resolve({ cancelled: true });
      };
    });
    await page.locator('.rail-button[data-tab="settings"]').click();
    await page.locator('#settingsNavTree [data-settings-page="service"]').first().click();
    await page.locator('[data-diagnostics-tab="model-lab"]').click();
    await page.waitForFunction(() => modelDiagnosticsLab.catalog !== null);
    assert.equal(await page.locator('#modelLabScenarioList [data-tree-key^="case:"]').count(), 20);
    assert.equal(await page.locator('#modelLabEndpoint').count(), 0, "saved connection is not duplicated as a field");
    assert.equal(await page.locator('#modelLabTimeout').isVisible(), false, "advanced options are collapsed");
    assert.equal(await page.locator('#modelLabScenarioList.navigation-tree').count(), 1, "shared navigation tree");
    assert.equal(await page.evaluate(() => labCalls.filter(c => c.type === "runModelDiagnostic").length), 0, "opening never probes");
    await page.locator('#modelLabScenarioList [data-tree-select="scope:all"]').click();
    await page.locator('#modelLabOptions > summary').click();
    await page.locator('#modelLabSeed').fill("ui-seed");
    await page.locator('#modelLabOptions > summary').click();
    await page.locator('#modelLabRun').click();
    await page.waitForFunction(() => !modelDiagnosticsLab.running && modelDiagnosticsLab.rows.length === 23);
    assert.equal(await page.evaluate(() => labCalls.filter(c => c.type === "runModelDiagnostic").length), 20, "controls are host-owned");
    assert.equal(await page.locator('#modelLabResults table.rna-table').count(), 1, "shared result table");
    assert.match(await page.locator('#modelLabResults').innerText(), /Не пройдено/);
    assert.match(await page.locator('#modelLabResults').innerText(), /C4-control/);
    await page.locator('#modelLabResults button').filter({ hasText: /^C4$/ }).click();
    assert.match(await page.locator('#modelLabInspector').innerText(), /полнота не подтверждена/);
    assert.equal(await page.locator('#modelLabInspector img').count(), 0, "provider content is inert text");
    await page.locator('#modelLabInspector summary').filter({ hasText: "Фактический ответ" }).click();
    assert.match(await page.locator('#modelLabInspector').innerText(), /<img src=x/);
    await page.locator('#modelLabInspector summary').filter({ hasText: "HTTP response" }).click();
    assert.match(await page.locator('#modelLabInspector').innerText(), /сокращённый просмотр.*секреты скрыты/);
    const downloadWait = page.waitForEvent("download");
    await page.locator('#modelLabExport').click();
    const download = await downloadWait;
    const fs = require("node:fs");
    const report = JSON.parse(fs.readFileSync(await download.path(), "utf8"));
    assert.equal(report.rows.length, 23);
    assert.equal(report.rows[0].seed, "ui-seed/1");
    assert.ok(report.rows.find(row => row.id === "C4").exchange.response.truncated, "export preserves preview limitations");
    for (const [width, theme] of [[1100, "light"], [740, "dark"], [390, "light"]]) {
      await page.setViewportSize({ width, height: 900 });
      await page.evaluate(value => document.documentElement.setAttribute("data-theme", value), theme);
      const fits = await page.locator('.model-diagnostics-lab').evaluate(node => node.scrollWidth <= node.clientWidth + 1);
      assert.ok(fits, "diagnostics has no outer horizontal overflow at " + width);
      if (process.env.RNA_DIAGNOSTICS_SCREENSHOT && width === 390) {
        await page.locator('.model-diagnostics-lab').evaluate(node => { node.scrollTop = 0; });
        await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
        await page.screenshot({ path: process.env.RNA_DIAGNOSTICS_SCREENSHOT.replace(/\.png$/, "-narrow.png"), animations: "disabled" });
      }
    }
    if (process.env.RNA_DIAGNOSTICS_SCREENSHOT) {
      await page.setViewportSize({ width: 1100, height: 1000 });
      await page.locator('.model-diagnostics-lab').evaluate(node => { node.scrollTop = 0; });
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      await page.screenshot({ path: process.env.RNA_DIAGNOSTICS_SCREENSHOT, animations: "disabled" });
    }
    await page.evaluate(() => { window.labHold = true; });
    await page.locator('#modelLabScenarioList [data-tree-select="scope:A"]').click();
    await page.locator('#modelLabRun').click();
    await page.waitForFunction(() => modelDiagnosticsLab.running);
    assert.ok(await page.locator('#modelLabModel').isDisabled());
    await page.locator('#modelLabStop').click();
    await page.waitForFunction(() => !modelDiagnosticsLab.running);
    assert.deepEqual(await page.evaluate(() => modelDiagnosticsLab.rows.map(r => r.phase)), ["Cancelled", "NotRun", "NotRun", "NotRun"]);
    assert.equal(await page.evaluate(() => labCalls.filter(c => c.type === "runModelDiagnostic").length), 21, "stop prevents next request");
    assert.match(await page.locator('#modelLabStatus').innerText(), /отменена/);
    assert.deepEqual(errors, []);
    console.log("PASS model diagnostics UI: explicit runs, controls, assertions, raw text, preview/export, responsive layout and cancellation");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exit(1); });
