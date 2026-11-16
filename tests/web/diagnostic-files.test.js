"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");

(async () => {
  const root = path.resolve(__dirname, "../..");
  const server = spawn("python3", ["-B", "prototypes/serve_ui.py", "--port", "0"], { cwd: root });
  server.stderr.resume();
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
    await page.evaluate(() => {
      const original = send;
      window.fileLogCalls = []; window.heldLog = null; window.partialLogFailure = false;
      window.logInventory = { files: [
        { id: "rnassistant.log", name: "rnassistant.log", source: "runtime", byteLength: 90000, lastWriteUtc: "2026-11-16T10:00:00Z", isActive: true },
        { id: "20261116.log", name: "20261116.log", source: "desktop", byteLength: 120, lastWriteUtc: "2026-11-16T10:01:00Z", isActive: true }
      ], options: { maxFileMiB: 5, maxTotalMiB: 50, retentionDays: 7, maxFiles: 20 }, totalBytes: 90120, overLimit: false, issues: [] };
      window.logPage = (fileId, older) => ({ fileId, text: older ? "Earlier entry\n  stack frame at Earlier()\n" :
        fileId === "20261116.log" ? "Desktop entry\n" : "Runtime entry\n<img src=x onerror=window.logExecuted=true>\nException: example\n  stack frame at Run()\n" + "detail\n".repeat(250),
        snapshotToken: "snapshot-1", startOffset: older ? 0 : 24500, endOffset: older ? 24500 : 90000,
        snapshotLength: 90000, hasEarlier: !older, resetRequired: false, partialFirstLine: !older });
      send = function (type, payload) {
        if (/DiagnosticLog/.test(type)) fileLogCalls.push({ type, payload });
        if (type === "listDiagnosticLogs") return Promise.resolve(JSON.parse(JSON.stringify(logInventory)));
        if (type === "readDiagnosticLog") {
          const result = logPage(payload.fileId, payload.beforeOffset != null);
          if (window.resetLogPage && payload.beforeOffset != null) { window.resetLogPage = false; result.resetRequired = true; }
          if (window.heldLog === payload.fileId) {
            window.heldLog = null;
            const pending = new Promise(resolve => window.releaseHeldLog = () => resolve(result)); pending.requestId = "held-file-log"; return pending;
          }
          return Promise.resolve(result);
        }
        if (["clearDiagnosticLog", "deleteDiagnosticLog", "clearDiagnosticLogs", "pruneDiagnosticLogs", "saveDiagnosticLogOptions"].includes(type)) {
          if (type === "saveDiagnosticLogOptions") logInventory.options = payload;
          const issues = window.partialLogFailure ? [{ fileId: "rnassistant.log", operation: "clear", message: "Locked test file" }] : [];
          return Promise.resolve({ affectedFiles: issues.length ? 1 : 2, reclaimedBytes: 100, issues, inventory: logInventory });
        }
        return original(type, payload);
      };
      cancelBridgeRequest = id => { fileLogCalls.push({ type: "cancelRequest", id }); return Promise.resolve(); };
      window.openFileLogs = () => {
        $("diagnosticsAppView").value = "log"; RNAssistantDiagnostics.open("application");
      };
      openFileLogs();
    });
    await page.locator("#fileLogText").getByText("Runtime entry", { exact: false }).waitFor();
    assert.equal(await page.locator("#fileLogSelect").inputValue(), "rnassistant.log");
    assert.equal(await page.locator("#fileLogText img").count(), 0, "log HTML stays plain text");
    assert.equal(await page.evaluate(() => window.logExecuted), undefined);
    assert.equal(await page.evaluate(() => fileLogCalls.find(call => call.type === "readDiagnosticLog").payload.maxBytes), 65536);
    assert.ok(await page.locator("#fileLogText").evaluate(node => node.childNodes.length) < 5, "one bounded text page, not per-line DOM");

    await page.locator("#fileLogSearch").fill("Exception");
    assert.equal(await page.locator("#fileLogText mark").textContent(), "Exception");
    assert.equal(await page.locator("#fileLogLive").getAttribute("aria-pressed"), "false", "search pauses updates");
    await page.locator("#fileLogSearch").fill("");
    await page.locator("#fileLogEarlier").click();
    await page.waitForFunction(() => $("fileLogText").textContent.startsWith("Earlier entry"));
    assert.doesNotMatch(await page.locator("#fileLogText").textContent(), /Runtime entry/, "paging replaces the old page");
    assert.equal(await page.locator("#fileLogEarlier").isDisabled(), true);
    assert.equal(await page.evaluate(() => fileLogCalls.filter(call => call.type === "readDiagnosticLog").at(-1).payload.snapshotToken), "snapshot-1");

    // Returning to a paused page must preserve it even if the file grew while hidden.
    await page.evaluate(() => { RNAssistantDiagnostics.show("runs"); logInventory.files[0].byteLength++; openFileLogs(); });
    await page.waitForTimeout(100);
    assert.match(await page.locator("#fileLogText").textContent(), /^Earlier entry/);
    await page.locator("#fileLogLatest").click();
    await page.waitForFunction(() => $("fileLogText").textContent.startsWith("Runtime entry"));
    await page.evaluate(() => window.resetLogPage = true);
    await page.locator("#fileLogEarlier").click();
    await page.waitForFunction(() => $("fileLogNotice").textContent.includes("ротации"));
    assert.match(await page.locator("#fileLogText").textContent(), /^Runtime entry/, "invalid snapshot returns to latest");
    assert.equal(await page.locator("#fileLogLive").getAttribute("aria-pressed"), "false");

    // A pending source read cannot overwrite another selected source.
    await page.evaluate(() => window.heldLog = "rnassistant.log");
    await page.locator("#fileLogRefresh").click();
    await page.waitForFunction(() => !!window.releaseHeldLog);
    await page.locator("#fileLogSelect").selectOption("20261116.log");
    await page.waitForFunction(() => $("fileLogText").textContent.startsWith("Desktop entry"));
    await page.evaluate(() => { releaseHeldLog(); window.releaseHeldLog = null; });
    await page.waitForTimeout(50);
    assert.match(await page.locator("#fileLogText").textContent(), /^Desktop entry/);
    assert.ok(await page.evaluate(() => fileLogCalls.some(call => call.type === "cancelRequest" && call.id === "held-file-log")));

    await page.evaluate(() => window.partialLogFailure = true);
    await page.locator("#fileLogActions").click();
    await page.getByRole("menuitem", { name: "Очистить все диагностические логи", exact: true }).click();
    assert.match(await page.locator("dialog").textContent(), /История чатов и документы сохранятся/);
    await page.locator("dialog").getByRole("button", { name: "Очистить", exact: true }).click();
    await page.waitForFunction(() => $("fileLogNotice").textContent.includes("Locked test file"));
    assert.match(await page.locator("#fileLogNotice").textContent(), /частично/);

    await page.locator("#fileLogRetention summary").click();
    await page.locator("#fileLogMaxTotal").fill("3");
    await page.locator("#fileLogSaveOptions").click();
    assert.match(await page.locator("#fileLogNotice").textContent(), /не меньше двух файлов/);
    await page.locator("#fileLogMaxTotal").fill("60");
    await page.locator("#fileLogSaveOptions").click();
    await page.locator("dialog").getByRole("button", { name: "Применить", exact: true }).click();
    await page.waitForFunction(() => fileLogCalls.some(call => call.type === "saveDiagnosticLogOptions"));
    assert.equal(await page.evaluate(() => fileLogCalls.find(call => call.type === "saveDiagnosticLogOptions").payload.maxTotalMiB), 60);

    // Browser lifecycle stops polling; close does not leave a second timer on reopen.
    await page.evaluate(() => RNAssistantDiagnostics.show("runs"));
    const countBefore = await page.evaluate(() => fileLogCalls.length);
    await page.waitForTimeout(2200);
    assert.equal(await page.evaluate(() => fileLogCalls.length), countBefore, "hidden logs do not poll");
    await page.evaluate(() => { RNAssistantDiagnostics.onClose(); openFileLogs(); });
    await page.waitForFunction(() => $("fileLogText").textContent.startsWith("Desktop entry"));

    for (const [width, zoom, theme] of [[1440, 1, "light"], [420, 1.25, "dark"]]) {
      await page.setViewportSize({ width, height: 900 });
      await page.evaluate(({ zoom, theme }) => { document.documentElement.style.zoom = String(zoom); document.documentElement.dataset.theme = theme; }, { zoom, theme });
      await page.waitForTimeout(100);
      assert.ok(await page.locator("#fileLogPane").evaluate(node => node.scrollWidth <= node.clientWidth + 2), "file controls fit the view at " + width);
      assert.ok(await page.locator("#fileLogReadRegion").evaluate(node => node.clientHeight > 80), "reader retains usable height at " + width);
      if (process.env.LOG_SCREENSHOTS) {
        fs.mkdirSync(process.env.LOG_SCREENSHOTS, { recursive: true });
        await page.screenshot({ path: path.join(process.env.LOG_SCREENSHOTS, "logs-" + width + "-" + theme + ".png") });
      }
    }
    await page.locator('[data-log-source="panel"]').click();
    assert.equal(await page.locator("#logBox").isVisible(), true, "transient panel log remains available");
    assert.deepEqual(errors, []);
    console.log("PASS diagnostic files: bounded/plain text, paging/reset, stale source/cancel, paused page, partial mutation, limits, lifecycle and narrow/zoom");
  } finally {
    if (browser) await browser.close(); server.kill();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
