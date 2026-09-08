"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require("playwright");
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage();
    await page.setContent('<details id="step"><summary>Шаг</summary><div id="preview"></div><details id="raw"><summary>Результат JSON</summary>raw source</details></details>');
    await page.evaluate(() => {
      window.state = { activeChatId: "chat", chatProjectionRevisions: { chat: 1 } };
      window.activityValue = (a, upper, lower, fallback) => a[upper] || a[lower] || fallback;
      window.activityToolCallId = a => a.ToolCallId;
      window.messageRunId = m => m.RunId;
      window.requests = []; window.cancelled = [];
      window.send = (type, payload) => {
        let resolve; const promise = new Promise(done => { resolve = done; }); promise.requestId = String(requests.length);
        requests.push({ type, payload, resolve }); return promise;
      };
      window.cancelBridgeRequest = id => { cancelled.push(id); return Promise.resolve(); };
    });
    await page.addScriptTag({ content: fs.readFileSync(path.join(__dirname, "../../web/js/app-tool-result-preview.js"), "utf8") });
    await page.evaluate(() => {
      appendToolResultPreview(document.getElementById("preview"), { ToolCallId: "call", RunId: "run" }, null, document.getElementById("step"));
    });
    assert.equal(await page.evaluate(() => requests.length), 0, "collapsed card performs no fetch");
    await page.locator("#step > summary").click();
    await page.waitForFunction(() => requests.length === 1);
    assert.deepEqual(await page.evaluate(() => requests[0].payload), { chatId: "chat", runId: "run", toolCallId: "call" });
    await page.locator("#step > summary").click();
    await page.waitForFunction(() => cancelled.includes("0"));
    await page.evaluate(() => requests[0].resolve({ chatId: "chat", runId: "run", toolCallId: "call", blocks: [{ kind: "text", title: "Содержимое", text: "stale", complete: true }] }));
    assert.equal(await page.locator("#preview").textContent(), "", "collapsed late response is discarded");
    await page.locator("#step > summary").click();
    await page.waitForFunction(() => requests.length === 2);
    await page.evaluate(() => requests[1].resolve({ chatId: "wrong", runId: "run", toolCallId: "call", blocks: [] }));
    await page.getByText("Не удалось загрузить представление · Повторить").waitFor();
    await page.getByText("Не удалось загрузить представление · Повторить").click();
    await page.waitForFunction(() => requests.length === 3);
    await page.evaluate(() => {
      state.chatProjectionRevisions.chat++;
      requests[2].resolve({ chatId: "chat", runId: "run", toolCallId: "call", blocks: [] });
    });
    await page.waitForFunction(() => requests.length === 4);
    await page.evaluate(() => requests[3].resolve({ chatId: "chat", runId: "run", toolCallId: "call", blocks: [
      { kind: "text", title: "Содержимое", text: "<img src=x onerror=alert(1)>", complete: true },
      { kind: "future_kind", title: "Unknown" }
    ] }));
    await page.locator(".tool-result-preview").waitFor();
    assert.equal(await page.locator("#preview img").count(), 0);
    assert.match(await page.locator("#preview").textContent(), /Этот вид представления недоступен/);
    assert.equal(await page.locator("#raw").textContent(), "Результат JSONraw source", "presentation never rewrites original results");
    await page.locator("#step > summary").click();
    await page.locator("#step > summary").click();
    await page.waitForFunction(() => requests.length === 5);
    await page.evaluate(() => { state.activeChatId = "other"; document.getElementById("step").remove(); });
    await page.waitForFunction(() => cancelled.includes("4"));
    await page.evaluate(() => requests[4].resolve({ chatId: "chat", runId: "run", toolCallId: "call", blocks: [{ kind: "text", title: "Late", text: "foreign", complete: true }] }));
    assert.equal(await page.locator(".tool-result-preview").count(), 0, "removed card never receives foreign chat delivery");
    console.log("PASS tool presentation: exact addressing, lazy reads, cancellation, retry, revision race, inert typed blocks, unknown kinds");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
