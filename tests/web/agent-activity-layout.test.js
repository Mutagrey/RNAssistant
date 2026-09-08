"use strict";
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
    const page = await browser.newPage({ viewport: { width: 740, height: 920 } });
    const css = [...read("index.html").matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)]
      .map(match => read(match[1])).join("\n");
    await page.setContent(`<style>${css}</style><main style="padding:24px;max-width:680px;margin:auto">
      <h3>Действия</h3><div id="actions"></div><button id="contextMeter" style="margin-top:20px">Контекст модели</button></main>`);
    await page.evaluate(() => {
      window.state = { activeChatId: "chat-a", messages: [] };
      window.$ = id => document.getElementById(id);
      window.setPromptContextInspectorOpen = open => { window.inspectorOpened = open; };
    });
    for (const file of ["app-utils.js", "app-agent-model.js", "app-viewer-registry.js", "app-json-viewer.js", "app-agent-data.js", "app-agent-activity.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(() => {
      const fixtures = [
        { ToolId: "common.capabilities_read", Subtitle: "excel.write_range", ResultMessage: "Loaded catalogRevision=private-id", DataJson: '{"complete":true}' },
        { ToolId: "common.resources_find", Subtitle: "Таблицы с продажами · Документ", DataJson: '{"items":[{},{},{},{}],"complete":true}' },
        { ToolId: "common.resources_read", Subtitle: "Продажи!A1:D120 · Таблица", DataJson: JSON.stringify({ kind: "resource-read", table: { rows: Array.from({ length: 120 }, () => ({})) }, complete: true }) },
        { ToolId: "excel.write_range", Subtitle: "Продажи!B2:D121", ExecutionEvidence: { Dispatch: "MayHaveDispatched", Effect: "VerifiedChange" } },
        { ToolId: "excel.add_sheet", Subtitle: "Отчёт", Status: "failed", ErrorCode: "excel_sheet_already_exists", ExecutionEvidence: { Dispatch: "NotDispatched", Effect: "None" }, ResultMessage: "Sheet already exists: raw error" },
        { ToolId: "common.office_run_macro", Subtitle: "ОбновитьОтчёт", Status: "failed", ExecutionEvidence: { Dispatch: "MayHaveDispatched", Effect: "Unknown" } },
        { ToolId: "common.resources_find", Subtitle: "Архив отчётов", DataJson: '{"items":[],"complete":false,"partial":true}' },
        { ToolId: "common.resources_read", Subtitle: "Папка документа / Очень длинное название раздела с аналитикой продаж по регионам / Итоговые данные за последний квартал!A1:F180", Status: "running" }
      ];
      for (const item of fixtures) document.getElementById("actions").appendChild(renderActivityNode(
        Object.assign({ Kind: "tool", Status: "completed", ArgumentsJson: '{"target":"exact model argument"}' }, item), false, false, null));
    });
    for (const width of [740, 360]) {
      await page.setViewportSize({ width, height: 920 });
      const bounds = await page.locator(".agent-activity-row").evaluateAll(rows => rows.map(row => ({
        width: row.clientWidth, scroll: row.scrollWidth, text: row.innerText,
        targetOverflow: getComputedStyle(row.querySelector(".agent-activity-target")).textOverflow
      })));
      assert.ok(bounds.every(row => row.scroll <= row.width + 1), "action rows fit the narrow chat");
      assert.ok(bounds.every(row => row.targetOverflow !== "ellipsis"), "semantic targets remain visible");
      assert.ok(bounds[0].text.includes("Загружено") && !bounds[0].text.includes("private-id"));
      assert.ok(bounds[2].text.includes("Получено строк: 120"));
      assert.ok(bounds[7].text.includes("квартал!A1:F180"));
    }
    await page.setViewportSize({ width: 740, height: 920 });
    if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT });
    await page.locator(".agent-activity-row").first().click();
    await page.getByText("Что войдёт в следующий запрос модели", { exact: true }).first().click();
    assert.equal(await page.evaluate(() => window.inspectorOpened), true);
    assert.ok(await page.locator(".agent-activity-context-note").first().isVisible());
    await page.evaluate(() => { window.inspectorOpened = false; state.activeChatId = "chat-b"; });
    await page.getByText("Что войдёт в следующий запрос модели", { exact: true }).first().click();
    assert.equal(await page.evaluate(() => window.inspectorOpened), false, "stale detail cannot open another chat's context");
    console.log("PASS activity layout: action/target/result, narrow wrapping, exact technical disclosure and addressed context link");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
