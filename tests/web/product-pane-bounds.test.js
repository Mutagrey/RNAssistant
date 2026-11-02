"use strict";

const assert = require("node:assert/strict");
const path = require("node:path");
const { chromium } = require("playwright");

(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 320, height: 560 } });
    await page.goto("file://" + path.resolve(__dirname, "../../web/index.html"));
    for (const width of [320, 280]) {
      await page.setViewportSize({ width, height: 560 });
      const result = await page.evaluate(() => {
        document.documentElement.style.setProperty("--ui-font-scale", "1.3");
        document.body.setAttribute("data-ui-font-scale", "1.3");
        renderComposerPresentation({ running: false });
        const form = document.getElementById("chatForm");
        const input = document.getElementById("chatInput");
        const toolbar = form.querySelector(".composer-toolbar");
        input.value = "Длинный черновик\n".repeat(20);
        resizeComposerInput(input);
        const expanded = input.getBoundingClientRect().height;
        input.value = "";
        resizeComposerInput(input);
        const empty = input.getBoundingClientRect().height;
        const idleToolbar = toolbar.getBoundingClientRect().height;
        renderComposerPresentation({ running: true, queue: false });
        const bounds = form.getBoundingClientRect();
        const controlsFit = ["chatModelButton", "sendButton", "stopButton"].every(id => {
          const rect = document.getElementById(id).getBoundingClientRect();
          return rect.width >= 30 && rect.left >= bounds.left && rect.right <= bounds.right &&
            rect.top >= bounds.top && rect.bottom <= bounds.bottom;
        });
        return { expanded, empty, idleToolbar, controlsFit };
      });
      assert.ok(result.expanded > result.empty && result.empty < 80,
        `cleared composer returns to two lines at ${width}px`);
      assert.ok(result.idleToolbar <= 42, `idle toolbar stays on one row at ${width}px`);
      assert.equal(result.controlsFit, true, `send and stop remain visible at ${width}px`);
    }

    for (const width of [1280, 320]) {
      await page.setViewportSize({ width, height: 760 });
      await page.evaluate(() => switchTab("artifacts"));
      const heights = await page.evaluate(() => {
        const content = document.getElementById("artifactCatalog");
        return { content: content.getBoundingClientRect().height,
          dock: content.parentElement.getBoundingClientRect().height,
          dockClass: content.parentElement.className };
      });
      assert.match(heights.dockClass, /dock-view/);
      assert.ok(Math.abs(heights.content - heights.dock) < 1,
        `materials fill the Dockview page at ${width}px`);
    }
    console.log("PASS product panes: compact composer controls and full-height materials");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
