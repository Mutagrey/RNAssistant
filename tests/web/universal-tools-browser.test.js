"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require("playwright");
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 390, height: 720 } });
    const errors = []; page.on("pageerror", e => errors.push(e.message));
    await page.setContent('<style>body{margin:16px;background:#fafafa;color:#171717;font:14px system-ui}main{max-width:340px;min-width:0}code{font:12px monospace}</style><main><h3>Изменение src/app.js</h3><div id="result"></div><h3>Неподтверждённая запись</h3><div id="unknown"></div></main>');
    await page.addStyleTag({ content: fs.readFileSync(path.join(__dirname, "../../web/css/app-agent.css"), "utf8") });
    for (const file of ["app-text-diff.js", "app-run-changes.js"])
      await page.addScriptTag({ content: fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8") });
    await page.evaluate(() => {
      const before = Array.from({length: 50}, (_, i) => "old " + i).join("\n") + "\n";
      const after = Array.from({length: 50}, (_, i) => "new " + i).join("\n") + "\n" + "long ".repeat(120) + "<img src=x onerror=alert(1)>";
      RNAssistantRunChanges.renderInline(document.getElementById("result"), { complete: true, items: [{ title: "src/app.js", availability: "available", before, after }] });
      RNAssistantRunChanges.renderInline(document.getElementById("unknown"), { complete: true, items: [{ title: "Module1", availability: "unverified", before: "Debug.Print 1", intendedAfter: "Debug.Print 2", intendedAfterExists: true }] });
    });
    assert.equal(await page.locator("#result .inline-diff-line").count(), 40);
    await page.locator("#result button").click();
    assert.equal(await page.locator("#result .inline-diff-line").count(), 80);
    await page.locator("#result button").click();
    assert.ok(await page.locator("#result .inline-text-diff").evaluate(n => n.scrollWidth > n.clientWidth), "long source scrolls within its step");
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), "diff does not widen the page");
    assert.equal(await page.locator("#result img").count(), 0);
    assert.equal(await page.locator("#result details").count(), 0, "no nested full diff disclosures");
    assert.match(await page.locator("#unknown").innerText(), /Предварительный diff · результат не подтверждён/);
    const colors = await page.locator("#result .inline-diff-line.remove, #result .inline-diff-line.add").evaluateAll(rows => [...new Set(rows.map(n => getComputedStyle(n).backgroundColor))]);
    assert.equal(colors.length, 2, "additions and removals have distinct backgrounds");
    await page.locator("#unknown .inline-text-diff").focus();
    assert.equal(await page.evaluate(() => document.activeElement.className), "inline-text-diff");
    await page.locator("#result .inline-text-diff").evaluate(n => { n.style.maxHeight="280px"; });
    await page.screenshot({ path: "/tmp/rna-inline-diff.png", fullPage: true });
    assert.deepEqual(errors, []);
    console.log("PASS universal tools browser: inline diff, pagination, long lines, focus, inert text and unknown label");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode=1; });
