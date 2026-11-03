"use strict";
// Slow storage, bounded history and short Dock panes using the production UI.
const assert = require("node:assert/strict");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");
(async () => {
  const server = spawn("python3", ["-B", "prototypes/serve_ui.py", "--port", "0"], { cwd: path.resolve(__dirname, "../..") });
  server.stderr.resume();
  let browser;
  try {
    const address = await new Promise((resolve, reject) => {
      server.once("error", reject);
      server.stdout.on("data", bytes => { const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(bytes)); if (match) resolve(match[1]); });
    });
    browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    page.setDefaultTimeout(10000);
    const errors = []; page.on("pageerror", error => errors.push(error.message));
    await page.goto(address + "preview/web/?scenario=ready");
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    await page.evaluate(() => {
      window.historyReads = []; window.historyCancels = [];
      const kinds = ["step.started", "model.request.prepared", "llm.response", "model.response.accepted", "tool.call.recorded",
        "tool.execution.finished", "step.ended", "run.summary.created", "assistant.message.appended", "ui.projected"];
      const rows = Array.from({ length: 1000 }, (_, index) => {
        const attempt = Math.floor(index / kinds.length), kind = kinds[index % kinds.length], tool = kind.startsWith("tool.");
        return { id: "row-" + index, kind, status: kind === "model.request.prepared" ? "prepared" : "completed",
          runId: "slow-run", stepId: "step-" + attempt, modelAttemptId: "attempt-" + attempt,
          toolCallId: tool ? "call-" + attempt : "", toolId: tool ? "core.read" : "",
          firstSequence: index + 1, lastSequence: index + 1, sourceEventSeqs: [index + 1], sourceEventIds: ["event-" + index],
          dataJson: JSON.stringify({ requestId: "request-" + attempt }), createdUtc: "2026-11-03T10:00:00Z" };
      });
      const original = send;
      send = (type, payload) => {
        if (type !== "getChatTrajectory") return original(type, payload);
        const offset = Number(payload.cursor || 0), end = offset + payload.pageSize;
        const entry = { payload, id: "slow-read-" + historyReads.length };
        const pending = new Promise((resolve, reject) => {
          entry.finish = () => resolve({ chatId: payload.chatId, workspaceId: payload.workspaceId, view: payload.view,
            rows: rows.slice(offset, end), totalRows: rows.length, totalMatches: rows.length,
            hasMore: end < rows.length, nextCursor: end < rows.length ? String(end) : null });
          entry.fail = () => reject(new Error("Storage unavailable"));
        });
        pending.requestId = entry.id; historyReads.push(entry); return pending;
      };
      cancelBridgeRequest = async id => { historyCancels.push(id); };
      openRunJournal({ chatId: state.activeChatId, runId: "slow-run" });
    });
    const workspace = page.locator("#trajectoryWorkspace"), nav = page.locator(".rn-run-journal-nav"), detail = page.locator(".rn-run-journal-detail");
    const settle = async () => {
      await page.evaluate(() => historyReads.at(-1).finish());
      await page.waitForFunction(() => !document.getElementById("refreshTrajectoryButton").disabled);
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    };
    assert.equal(await workspace.isVisible(), true, "loading reserves the reader area");
    const loadingBounds = await workspace.boundingBox();
    assert.equal(await workspace.getAttribute("aria-busy"), "true");
    await page.evaluate(() => openRunJournal({ chatId: state.activeChatId, runId: "slow-run" }));
    assert.equal(await page.evaluate(() => historyReads.length), 1, "opening the same pending query does not restart storage");
    assert.equal(await page.evaluate(() => historyCancels.length), 0);
    await settle();
    assert.deepEqual(await workspace.boundingBox(), loadingBounds, "loading does not shift the reading area");
    assert.ok(await nav.locator('[role="treeitem"]').count() < 30, "collapsed event branches have no eager DOM");
    assert.equal(await nav.evaluate(node => node.scrollTop), 0, "initial selection scrolls the row, not its entire subtree");
    for (let pageIndex = 1; pageIndex < 5; pageIndex++) {
      await nav.evaluate(node => { node.scrollTop = 160; });
      const scroll = await nav.evaluate(node => node.scrollTop);
      await page.locator("#loadMoreTrajectoryButton").click();
      assert.equal(await nav.isVisible(), true);
      if (pageIndex === 1) {
        await page.evaluate(() => historyReads.at(-1).fail());
        await page.waitForFunction(() => !document.getElementById("loadMoreTrajectoryButton").disabled);
        assert.equal(await nav.isVisible(), true, "a failed next page does not hide existing evidence");
        await page.locator("#loadMoreTrajectoryButton").click();
      }
      await settle();
      assert.equal(await nav.evaluate(node => node.scrollTop), scroll, "appending preserves the reading position");
    }
    assert.match(await page.locator("#trajectoryStatus").textContent(), /1000 из 1000/);
    assert.ok(await nav.locator('[role="treeitem"]').count() < 110, "1000 source events keep only request roots mounted");
    assert.equal(await page.locator("#loadMoreTrajectoryButton").isVisible(), false);
    const request = nav.getByRole("button", { name: "Запрос 1", exact: true });
    await request.click();
    await nav.locator('[data-tree-key="row-1"]').waitFor();
    assert.match(await detail.textContent(), /Ответ принят/);
    const mounted = await nav.locator('[role="treeitem"]').count();
    await request.click(); await request.click();
    assert.equal(await nav.locator('[role="treeitem"]').count(), mounted, "re-expanding reuses already mounted children");
    await request.locator('..').locator('..').focus(); await page.keyboard.press("ArrowDown");
    assert.equal(await page.evaluate(() => document.activeElement.dataset.treeKey), "row-1", "keyboard follows DOM order after deferred expansion");
    await page.keyboard.press("Enter");
    assert.equal(await detail.getAttribute("data-selection-id"), "row-1");
    assert.equal(await nav.locator('[aria-current="page"][role="treeitem"]').count(), 1);

    await page.evaluate(() => { window.retainedNav = document.querySelector('.rn-run-journal-nav'); });
    await page.locator("#refreshTrajectoryButton").click();
    assert.equal(await nav.evaluate(node => node === retainedNav), true, "refresh keeps the existing tree while storage is pending");
    assert.equal(await detail.getAttribute("data-selection-id"), "row-1");
    await page.evaluate(() => historyReads.at(-1).fail());
    await page.waitForFunction(() => !document.getElementById("refreshTrajectoryButton").disabled);
    assert.equal(await nav.evaluate(node => node === retainedNav), true, "refresh failure leaves the loaded evidence readable");
    assert.match(await page.locator("#trajectoryStatus").textContent(), /Storage unavailable/);
    await page.locator("#refreshTrajectoryButton").click(); await settle();
    assert.equal(await detail.getAttribute("data-selection-id"), "row-1", "successful refresh restores selection");

    for (const width of [600, 390]) {
      await page.setViewportSize({ width, height: 400 });
      assert.ok((await detail.boundingBox()).height > 60, "short Dock panes keep details visible");
      assert.equal(await page.locator("#tab-diagnostics").evaluate(node => node.scrollWidth > node.clientWidth + 1), false);
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.locator("#refreshTrajectoryButton").click();
    const interrupted = await page.evaluate(() => historyReads.length - 1);
    await page.locator('[data-diagnostics-tab="application"]').click();
    assert.equal(await page.evaluate(index => historyCancels.includes(historyReads[index].id), interrupted), true);
    await page.locator('[data-diagnostics-tab="runs"]').click();
    assert.equal(await page.evaluate(() => historyReads.length), interrupted + 2, "return resumes exactly once");
    await settle();
    await page.evaluate(index => historyReads[index].finish(), interrupted);
    assert.equal(await detail.getAttribute("data-selection-id"), "row-1", "late cancelled data cannot replace the resumed view");

    await page.evaluate(() => openRunJournal({ owner: "office", chatId: "preview-summary", runId: "other-run" }));
    assert.equal(await nav.count(), 0, "another source never displays the previous chat's tree while loading");
    assert.equal(await workspace.getAttribute("aria-busy"), "true");
    await page.evaluate(() => historyReads.at(-1).fail());
    await page.waitForFunction(() => !document.getElementById("refreshTrajectoryButton").disabled);
    assert.equal(await workspace.isVisible(), true);
    assert.match(await workspace.textContent(), /История недоступна/);
    assert.equal(await page.locator("#loadMoreTrajectoryButton").isVisible(), false, "another source does not retain the old page cursor control");
    assert.deepEqual(errors, []);
    console.log("PASS diagnostics loading: single pending read, retained refresh, lazy 1000-event tree, short panes and cancellation");
  } finally { if (browser) await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
