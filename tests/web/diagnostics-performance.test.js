"use strict";
// Identical synthetic saved request before/after. No model, Office or persistent data.
const assert = require("node:assert/strict");
const { chromium } = require("playwright");
const { spawn } = require("node:child_process");
const path = require("node:path");
const fs = require("node:fs");
(async () => {
  const server = spawn("python3", ["-B", "prototypes/serve_ui.py", "--port", "0"], { cwd: process.env.DIAGNOSTICS_SOURCE_ROOT || path.resolve(__dirname, "../..") });
  server.stderr.resume(); // Drain the preview access log; an unread pipe can stall asset delivery.
  let browser;
  try {
    const address = await new Promise((resolve, reject) => {
      server.once("error", reject); server.once("exit", code => reject(new Error("preview exited " + code)));
      server.stdout.on("data", data => { const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(data)); if (match) resolve(match[1]); });
    });
    browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    await page.goto(address + "preview/web/?scenario=ready");
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    assert.equal(await page.locator("#modelContextBody").evaluate(node => getComputedStyle(node).overflowY), "auto", "request stylesheet loaded");
    await page.evaluate(() => {
      const original = send;
      const text = JSON.stringify({ model: "synthetic", messages: Array.from({ length: 2000 }, (_, index) => ({ role: index % 2 ? "assistant" : "user", content: index + ": " + "синтетические данные ".repeat(55) })) });
      window.perfFixtureBytes = new TextEncoder().encode(text).length;
      window.perfReads = { metadata: 0, bodies: 0 };
      send = async (type, args) => {
        if (type === "getModelContext") perfReads.metadata++;
        if (type === "getModelContextPayload") {
          perfReads.bodies++;
          return { chatId: "preview-chat", eventId: args.eventId, data: { leaseId: "perf", payload: { contentType: "application/json" } } };
        }
        if (type === "resourceDataClose") return {};
        return original(type, args);
      };
      RNAssistantResourceDownload.read = async () => new TextEncoder().encode(text);
    });
    const docked = await page.locator("#modelContextStructureTab").count() > 0;
    async function message() {
      if (docked) {
        const first = page.locator('#modelContextTree [data-tree-select="message:0"]');
        if (!await first.count()) await page.locator('#modelContextTree [data-tree-select="messages"]').click();
        else await first.click();
        await page.locator('#modelContextBody .rn-markdown-viewer').waitFor();
      } else {
        await page.locator("#modelContextMessagesTab").click();
        const first = page.locator("#modelContextBody .is-message").first(); await first.waitFor();
        if (!await first.evaluate(node => node.open)) await first.locator(":scope > summary").click();
        await first.locator('.rn-markdown-viewer').waitFor();
      }
    }
    async function exact() {
      if (docked) { await page.locator("#modelContextActions").click(); await page.getByRole("menuitem", { name: "Точный запрос JSON", exact: true }).click(); }
      else await page.locator("#modelContextJsonTab").click();
    }
    const samples = [];
    for (let iteration = 0; iteration < 5; iteration++) {
      await page.evaluate(() => { RNAssistantModelContext.close(false); switchTab("chat"); perfReads = { metadata: 0, bodies: 0 }; });
      const start = performance.now();
      await page.evaluate(() => RNAssistantModelContext.open({ requestEventId: "preview-request" }));
      await page.waitForFunction(() => !document.getElementById("modelContextBody").textContent.includes("Загрузка") && document.getElementById("modelContextAttempt").value === "preview-request");
      const opening = performance.now() - start;
      const initialReads = await page.evaluate(() => ({ ...perfReads }));
      await message();
      const inputMs = performance.now() - start;
      const tabStart = performance.now();
      for (let i = 0; i < 3; i++) {
        await exact(); await message();
      }
      samples.push({ openingMs: Math.round(opening), inputMs: Math.round(inputMs), tabRoundTripsMs: Math.round(performance.now() - tabStart), initialReads,
        ...await page.evaluate(() => ({ reads: perfReads, messageRows: document.querySelectorAll('#modelContextTree [data-tree-key^="message:"], #modelContextBody .is-message').length, domNodes: document.querySelectorAll("#modelContextBody *, #modelContextTree *").length })) });
    }
    const protocol = await page.context().newCDPSession(page);
    await protocol.send("HeapProfiler.collectGarbage");
    const retainedHeap = await protocol.send("Runtime.getHeapUsage");
    await page.evaluate(() => RNAssistantModelContext.close(false));
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    await protocol.send("HeapProfiler.collectGarbage");
    const closedHeap = await protocol.send("Runtime.getHeapUsage");
    const cleanup = [];
    for (let repeat = 0; repeat < 20; repeat++) {
      await page.evaluate(() => RNAssistantModelContext.open({ requestEventId: "preview-request" }));
      await page.waitForFunction(() => document.getElementById("modelContextAttempt").value === "preview-request");
      await message(); await page.evaluate(() => RNAssistantModelContext.close(false));
      if (repeat === 0 || repeat === 19) { await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); await protocol.send("HeapProfiler.collectGarbage"); cleanup.push((await protocol.send("Runtime.getHeapUsage")).usedSize); }
    }
    const report = { cleanupTwentyCycles: cleanup, route: docked ? "Dock request / selected message / exact request via menu" : "Diagnostics request / expanded first message / exact request tab", browser: browser.version(), stylesheetVerified: true, heapAfterFiveRuns: { open: retainedHeap.usedSize, closed: closedHeap.usedSize }, fixture: "2000 messages, 55 repeated text fragments each", bytes: await page.evaluate(() => perfFixtureBytes), samples };
    console.log(JSON.stringify(report, null, 2));
    if (process.env.DIAGNOSTICS_PERF_OUTPUT) fs.writeFileSync(process.env.DIAGNOSTICS_PERF_OUTPUT, JSON.stringify(report, null, 2));
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
