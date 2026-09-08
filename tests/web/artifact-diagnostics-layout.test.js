"use strict";
// Run with the bundled Playwright in NODE_PATH; no Office/VSTO required.
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
    const page = await browser.newPage({ viewport: { width: 1100, height: 800 } });
    const css = [...read("index.html").matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)]
      .map(match => read(match[1])).join("\n");
    await page.setContent(`<style>${css}</style><div style="height:100vh;padding:20px;display:grid;grid-template-columns:280px 1fr;gap:16px">
      <div class="html-workspace-tree" id="tree"></div><div class="trajectory-panel">
      <div class="trajectory-workspace split-layout" id="trajectoryWorkspace" data-min-left="160" data-max-ratio="0.6">
      <div class="trajectory-events"><button class="trajectory-event"><span class="trajectory-event-line"><span class="trajectory-event-type">#103 agent.response.rejected</span></span><span class="trajectory-event-time">08.09.2026, 12:05:56</span></button></div>
      <div class="splitter"></div><div class="trajectory-detail"><strong>Технические события</strong><span>Данные события</span><div id="json" class="trajectory-json-host"></div></div></div></div></div>`);
    for (const file of ["vendor/wunderbaum.umd.min.js", "app-tree-adapter.js", "app-viewer-registry.js", "app-json-viewer.js", "app-layout.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(async () => {
      await RNAssistantTreeAdapter.mount(document.getElementById("tree"), { selectedKey: "plan", nodes: [
        { key: "html", title: "HTML · только этот чат", expanded: true, children: [
          { key: "pages", title: "Страницы", expanded: true, children: [
            { key: "index", title: "index.html", itemType: "file", itemId: "index", meta: "HTML", deletable: true }
          ] }
        ] },
        { key: "plan", title: "Новый план", itemType: "plan", itemId: "plan", meta: "v3 · Общий для чатов документа" }
      ] }).ready;
      RNAssistantViewerRegistry.mount("json", document.getElementById("json"), {
        text: JSON.stringify({ Stage: "model.attempt.rejected", SessionId: "833242ffb89c4a54b91efa9512bfffbc", payload: '{"request":{"messages":[{"role":"user","content":"Текст"}]}}' })
      });
      initializeSplitPanes();
    });
    const row = page.locator(".rn-json-scalar-row").first();
    const key = await row.locator(".rn-json-key").boundingBox();
    const value = await row.locator(".rn-json-value").boundingBox();
    assert.ok(Math.abs(key.y - value.y) < 2, "JSON key and short string share a line");
    const title = page.locator(".wb-title").filter({ hasText: "index.html" });
    const titleBox = await title.boundingBox();
    assert.ok(titleBox.width >= 100, "filename has room after indentation and action");
    assert.ok((await title.locator(".rn-tree-meta").boundingBox()).y > titleBox.y + 15, "metadata is below the name");
    await title.click();
    assert.equal(await page.locator(".wb-row.wb-active, .wb-row.wb-selected").count(), 1, "one selected artifact");
    const event = await page.locator(".trajectory-event").boundingBox();
    const eventLine = await page.locator(".trajectory-event-line").boundingBox();
    assert.ok(eventLine.x - event.x < 15, "event text is left aligned");
    const before = await page.locator(".trajectory-events").boundingBox();
    const splitter = await page.locator(".splitter").boundingBox();
    await page.mouse.move(splitter.x, splitter.y + 70);
    await page.mouse.down();
    await page.mouse.move(splitter.x + 80, splitter.y + 70);
    await page.mouse.up();
    assert.ok((await page.locator(".trajectory-events").boundingBox()).width > before.width + 40, "divider resizes event list");
    if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT });
    console.log("PASS artifact/diagnostics layout: readable tree, one selection, inline JSON, left aligned events, draggable divider");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
