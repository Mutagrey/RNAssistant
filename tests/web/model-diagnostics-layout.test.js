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
      const catalog = window.labCatalog = { scenarioRevision: 4, endpoint: "http://synthetic.invalid/v1", model: "scripted",
        scenarios: Object.keys(modelDiagnosticTitles).map(id => ({ id, capability: id, assertion: "HistoryJson",
          controls: controls[id] || [], isControl: ["A2-system", "C3-valid", "C4-control"].includes(id),
          responseFormat: "json_object", streaming: id === "C4" })) };
      function exchange(id) {
        return { result: { ScenarioId: id, Model: "scripted", Endpoint: catalog.endpoint, ExecutionKind: "Synthetic",
          Verdict: id === "B5" || id === "C4" ? "Fail" : "Pass", Reason: id === "B5" ? "call_id_mismatch" :
            id === "C4" ? "v8_parser_rejected" : id === "C3" ? "invalid_schema_rejected" : "exact_json_answer",
          HttpStatusCode: id === "C3" ? 400 : 200, HttpAccepted: id !== "C3", MarkerMatches: id === "B5" ? true : null,
          CallIdMatches: id === "B5" ? false : null, TargetMatches: id === "B5" ? true : null,
          ParserVerdict: id === "C4" ? "Fail" : id === "C4-control" ? "Pass" : "NotRun", ParityVerdict: id === "C4" ? "Inconclusive" : "NotRun",
          StreamingRequested: id === "C4", StreamingObserved: id === "C4", ResponseBodyComplete: false,
          DurationMs: 1270, PromptTokens: 120, CompletionTokens: 12,
          ExpectedJson: id === "B5" ? '{"target":"ModuleB","marker":"exact","tool_call_id":"T2"}' : '{"marker":"exact"}',
          ActualContent: id === "C4" ? '<img src=x onerror="window.injected=true">' :
            id === "B5" ? '{"target":"ModuleB","marker":"exact","tool_call_id":"T1"}' : '{"marker":"exact"}', RequestSha256: "a".repeat(64) },
          request: { text: '{"messages":[{"role":"developer","content":"Test"},{"role":"user","content":"Read marker"}]}', truncated: false, redacted: false },
          response: { text: "data: [REDACTED]", truncated: true, redacted: true }, resultRedacted: true, resultTruncated: false };
      }
      window.send = function (type, payload) {
        labCalls.push({ type, payload });
        if (type === "getModelDiagnosticScenarios") return Promise.resolve(JSON.parse(JSON.stringify(catalog)));
        if (type !== "runModelDiagnostic") return Promise.reject(new Error("Synthetic bridge: " + type));
        if (window.labFailure) return Promise.reject(new Error(window.labFailure));
        if (payload.endpoint !== catalog.endpoint) return Promise.reject(new Error("Сохранённое подключение изменилось."));
        if (window.labHold) {
          const waiting = new Promise((resolve, reject) => { window.labReject = reject; });
          waiting.requestId = "held-probe"; return waiting;
        }
        return Promise.resolve({ scenarioRevision: 4, seed: payload.seed,
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
    assert.equal(await page.locator('#modelLabScenarioList [data-tree-key^="scope:"]').count(), 7);
    assert.equal(await page.locator('.diagnostics-tabs.rna-editor-tabs').count(), 1, "shared product navigation");
    assert.equal(await page.locator('#modelLabEndpoint').count(), 0, "saved connection is not duplicated as a field");
    assert.equal(await page.locator('#modelLabTimeout').isVisible(), false, "advanced options are collapsed");
    assert.equal(await page.locator('#modelLabScenarioList.navigation-tree').count(), 1, "shared navigation tree");
    await page.locator('#modelLabModel').fill('manual-model');
    await page.locator('[data-diagnostics-tab="runtime-log"]').click();
    await page.locator('[data-diagnostics-tab="model-lab"]').click();
    await page.waitForFunction(() => !modelDiagnosticsLab.loading);
    assert.equal(await page.locator('#modelLabModel').inputValue(), 'manual-model', "catalog refresh preserves a deliberate model override");
    await page.locator('#modelLabRefresh').click();
    await page.waitForFunction(() => !modelDiagnosticsLab.loading);
    assert.equal(await page.locator('#modelLabModel').inputValue(), 'scripted', "explicit refresh restores the saved model");
    assert.equal(await page.evaluate(() => labCalls.filter(c => c.type === "runModelDiagnostic").length), 0, "opening never probes");
    await page.locator('#modelLabScenarioList [data-tree-select="scope:all"]').click();
    if (process.env.RNA_DIAGNOSTICS_SCREENSHOT)
      await page.screenshot({ path: process.env.RNA_DIAGNOSTICS_SCREENSHOT.replace(/\.png$/, "-selection.png"), animations: "disabled" });
    await page.locator('#modelLabOptions > summary').click();
    await page.locator('#modelLabSeed').fill("ui-seed");
    await page.locator('#modelLabOptions > summary').click();
    await page.locator('#modelLabRun').click();
    await page.waitForFunction(() => !modelDiagnosticsLab.running && modelDiagnosticsLab.rows.length === 23);
    assert.equal(await page.evaluate(() => labCalls.filter(c => c.type === "runModelDiagnostic").length), 20, "controls are host-owned");
    assert.equal(await page.locator('#modelLabResults table.rna-table').count(), 1, "shared result table");
    assert.match(await page.locator('#modelLabResults').innerText(), /Не пройдено/);
    assert.match(await page.locator('#modelLabResults').innerText(), /C4-control/);
    assert.equal(await page.locator('#modelLabCases').getAttribute('open'), null, "results take priority after launch");
    await page.locator('#modelLabResults [data-row-key="C4/1"] td').last().click();
    assert.equal(await page.locator('#modelLabResults [data-row-key="C4/1"]').getAttribute('aria-selected'), 'true', "whole row selects");
    assert.ok(await page.locator('#modelLabInspector').evaluate(node => node.getBoundingClientRect().top <
      node.closest('.model-diagnostics-lab').getBoundingClientRect().bottom - 100), "selection reveals the detail heading");
    assert.match(await page.locator('#modelLabInspector').innerText(), /полнота не подтверждена/);
    assert.equal(await page.locator('#modelLabInspector img').count(), 0, "provider content is inert text");
    assert.match(await page.locator('#modelLabInspector').innerText(), /<img src=x/);
    await page.locator('#modelLabInspector summary').filter({ hasText: "HTTP-ответ" }).click();
    assert.match(await page.locator('#modelLabInspector').innerText(), /сокращённый просмотр.*секреты скрыты/);
    await page.locator('#modelLabResults [data-row-key="B5/1"] button').focus();
    await page.keyboard.press('Enter');
    assert.equal(await page.locator('#modelLabResults [data-row-key="B5/1"]').getAttribute('aria-selected'), 'true', "keyboard selects");
    assert.match(await page.locator('#modelLabInspector').innerText(), /Ответ связан с другим вызовом/);
    await page.locator('#modelLabResults [data-row-key="C3/1"] td').last().click();
    assert.match(await page.locator('#modelLabInspector').innerText(), /отказ HTTP 400\/422/);
    await page.locator('#modelLabResults [data-row-key="B8/1"] td').last().click();
    assert.match(await page.locator('#modelLabInspector').innerText(), /не определяет поддержку native-истории/);
    const downloadWait = page.waitForEvent("download");
    await page.locator('#modelLabExport').click();
    const download = await downloadWait;
    const fs = require("node:fs");
    const report = JSON.parse(fs.readFileSync(await download.path(), "utf8"));
    assert.equal(report.rows.length, 23);
    assert.equal(report.rows[0].seed, "ui-seed/1");
    assert.equal(report.rows.find(row => row.id === "C4-control").seed, "ui-seed/1", "controls retain reproducibility metadata");
    assert.ok(report.rows.find(row => row.id === "C4").exchange.response.truncated, "export preserves preview limitations");
    await page.locator('#modelLabResults [data-row-key="B5/1"] td').last().click();
    for (const [width, theme] of [[1100, "light"], [740, "dark"], [390, "light"], [320, "dark"]]) {
      await page.setViewportSize({ width, height: 900 });
      await page.evaluate(value => document.documentElement.setAttribute("data-theme", value), theme);
      const fits = await page.locator('.model-diagnostics-lab').evaluate(node => node.scrollWidth <= node.clientWidth + 1);
      assert.ok(fits, "diagnostics has no outer horizontal overflow at " + width);
      const tabsFit = await page.locator('.diagnostics-tabs').evaluate(node =>
        node.scrollWidth <= node.clientWidth + 1 && Array.from(node.children).every(button => button.scrollWidth <= button.clientWidth + 1));
      assert.ok(tabsFit, "navigation labels wrap without clipping at " + width);
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
      await page.locator('#modelLabInspector').evaluate(node => node.scrollIntoView({ block: 'start' }));
      await page.screenshot({ path: process.env.RNA_DIAGNOSTICS_SCREENSHOT.replace(/\.png$/, "-details.png"), animations: "disabled" });
    }
    await page.setViewportSize({ width: 1100, height: 900 });
    // Save through the real form; only the persistence boundary is synthetic.
    await page.evaluate(() => {
      state.settings = { BaseUrl: labCatalog.endpoint, Model: labCatalog.model };
      renderSettings();
      window.saveSettingsWithPromptChanges = async settings => {
        labCatalog.endpoint = settings.BaseUrl;
        return { settings, prompts: state.prompts };
      };
    });
    await page.locator('#settingsNavTree [data-settings-page="connection"]').first().click();
    await page.locator('#baseUrlInput').fill('http://updated.synthetic.invalid/v1');
    await page.locator('#saveSettingsButton').click();
    await page.waitForFunction(() => modelDiagnosticsLab.catalog && modelDiagnosticsLab.catalog.endpoint.includes('updated.synthetic'));
    await page.locator('#settingsNavTree [data-settings-page="service"]').first().click();
    assert.match(await page.locator('#modelLabConnection').innerText(), /updated.synthetic/);
    await page.evaluate(() => { window.labHold = true; });
    await page.locator('#modelLabCases > summary').click();
    await page.locator('#modelLabScenarioList [data-tree-select="scope:instruction"]').click();
    await page.locator('#modelLabRun').click();
    await page.waitForFunction(() => modelDiagnosticsLab.running);
    assert.ok(await page.locator('#modelLabModel').isDisabled());
    await page.locator('#modelLabStop').click();
    await page.waitForFunction(() => !modelDiagnosticsLab.running);
    assert.deepEqual(await page.evaluate(() => modelDiagnosticsLab.rows.map(r => r.phase)), ["Cancelled"]);
    assert.equal(await page.evaluate(() => labCalls.filter(c => c.type === "runModelDiagnostic").length), 21, "stop prevents next request");
    assert.match(await page.locator('#modelLabStatus').innerText(), /отменена/);
    assert.match(await page.locator('#modelLabInspector').innerText(), /Ошибка запуска не является результатом/);
    await page.evaluate(() => { window.labHold = false; window.labFailure = 'Synthetic transport failure'; });
    await page.locator('#modelLabRun').click();
    await page.waitForFunction(() => !modelDiagnosticsLab.running);
    await page.locator('#modelLabResults tbody td').last().click();
    assert.match(await page.locator('#modelLabInspector').innerText(), /Synthetic transport failure/);
    assert.equal(await page.locator('#modelLabResults [data-verdict="Inconclusive"]').count(), 1, "bridge failure is not a failed model assertion");
    assert.ok(await page.locator('#modelLabRun').isEnabled(), "a transport failure does not lock the lab");
    await page.setViewportSize({ width: 320, height: 900 });
    assert.ok(await page.locator('#modelLabResults').evaluate(node => node.scrollWidth <= node.clientWidth + 1),
      "long verdicts fit the narrow result table");
    assert.deepEqual(errors, []);
    console.log("PASS model diagnostics UI: explicit runs, controls, assertions, raw text, preview/export, responsive layout and cancellation");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exit(1); });
