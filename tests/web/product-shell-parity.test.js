"use strict";
const assert = require("node:assert/strict");
const path = require("node:path");
const { chromium } = require("playwright");

(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const demo = await browser.newPage({ viewport: { width: 1100, height: 760 } });
    await demo.goto("file://" + path.resolve(__dirname, "../../prototypes/ui/index.html") + "#/settings/connection");
    const reference = await demo.evaluate(() => {
      const heading = document.querySelector("#global-nav .workspace-heading");
      const main = document.querySelector("#main .page-header");
      const splitter = document.querySelector(".split-handle");
      const css = getComputedStyle(document.documentElement);
      return { navWidth: heading.getBoundingClientRect().width, navHeight: heading.getBoundingClientRect().height,
        mainHeight: main.getBoundingClientRect().height, splitWidth: splitter.getBoundingClientRect().width,
        line: getComputedStyle(heading).borderBottomColor,
        tokens: ["--line", "--line-strong", "--radius", "--danger"].map(key => css.getPropertyValue(key).trim()) };
    });
    const page = await browser.newPage({ viewport: { width: 1074, height: 760 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto("file://" + path.resolve(__dirname, "../../web/index.html"));
    const productTokens = await page.evaluate(() => {
      const css = getComputedStyle(document.documentElement);
      return ["--line", "--line-strong", "--radius", "--danger"].map(key => css.getPropertyValue(key).trim());
    });
    assert.deepEqual(productTokens, reference.tokens, "the product uses the demo palette, borders and radii");

    const sections = [
      ['.rail-button[data-tab="chat"]', "#chatLayout", ".chat-sidebar-head", ".chatbar"],
      ['.rail-button[data-tab="artifacts"]', "#htmlWorkspaceLayout", ".artifact-catalog-toolbar", ".artifact-overview-heading"],
      ["#libraryRootTab", "#instructionsLayout", ".product-nav-heading", ".instruction-editor-content:not(.hidden) .resource-editor-head"],
      ['.rail-button[data-tab="vba"]', "#vbaLayout", ".product-nav-heading", ".vba-editor-head"],
      ['.rail-button[data-tab="workspace"]', ".workspace-projects", ".dt-nav-heading", ".dt-header"],
      ['.rail-button[data-tab="settings"]', "#settingsLayout", ".settings-nav-heading", ".settings-page.active .settings-heading"]
    ];
    for (const [button, layout, nav, main] of sections) {
      await page.locator(button).click();
      const geometry = await page.evaluate(({ layout, nav, main }) => {
        const root = document.querySelector(layout);
        const rect = selector => {
          const node = Array.from(root.querySelectorAll(selector)).find(element => element.getBoundingClientRect().height);
          assertNode(node, selector);
          const box = node.getBoundingClientRect(), style = getComputedStyle(node);
          return { x: box.x, y: box.y, width: box.width, height: box.height,
            borderWidth: style.borderBottomWidth, borderColor: style.borderBottomColor };
        };
        function assertNode(node, selector) { if (!node) throw new Error("Missing visible " + selector); }
        const split = root.querySelector(":scope > .splitter").getBoundingClientRect();
        return { nav: rect(nav), main: rect(main), split: { x: split.x, width: split.width } };
      }, { layout, nav, main });
      const name = button;
      assert.equal(geometry.nav.y, 0, name + " nav starts at top");
      assert.equal(geometry.main.y, 0, name + " main header starts at top");
      assert.equal(geometry.nav.width, reference.navWidth, name + " nav width matches demo");
      assert.equal(geometry.nav.height, reference.navHeight, name + " nav line matches main line");
      assert.equal(geometry.main.height, reference.mainHeight, name + " main header matches demo");
      assert.equal(geometry.nav.borderWidth, "1px", name + " nav has a line");
      assert.equal(geometry.main.borderWidth, "1px", name + " main has a line");
      assert.equal(geometry.nav.borderColor, reference.line);
      assert.equal(geometry.main.borderColor, reference.line);
      assert.equal(geometry.split.x, geometry.nav.x + geometry.nav.width, name + " divider joins nav");
      assert.equal(geometry.split.width, reference.splitWidth);
      assert.equal(geometry.main.x, geometry.split.x + geometry.split.width, name + " main joins divider");
    }

    await page.locator('.rail-button[data-tab="artifacts"]').click();
    await page.locator('[data-nav-search-toggle="artifactNavSearch"]').click();
    await page.locator("#artifactNavSearch").fill("отчёт");
    assert.equal(await page.locator("#htmlWorkspaceSearchInput").inputValue(), "отчёт",
      "sidebar and catalog share one material query");
    await page.locator("#artifactNavSearch").press("Escape");
    assert.equal(await page.locator("#htmlWorkspaceSearchInput").inputValue(), "");
    await page.locator("#libraryRootTab").click();
    await page.locator('[data-nav-search-toggle="skillSearchInput"]').click();
    assert.equal(await page.locator("#skillSearchInput").isVisible(), true);
    await page.locator("#skillSearchInput").press("Escape");
    assert.equal(await page.locator("#skillSearchInput").isVisible(), false);

    await page.locator('.rail-button[data-tab="workspace"]').click();
    assert.equal(await page.locator(".dt-nav-leaf .navigation-tree-icon svg").count(), 7,
      "project leaves use demo SVG icons");
    await page.locator("#dtNavProjectToggle").click();
    assert.equal(await page.locator('#dtNavTree > ul > li > ul').isVisible(), false);
    await page.locator("#dtNavProjectToggle").click();
    await page.locator("#dtNavSearchToggle").click();
    await page.locator("#dtNavSearch").fill("рез");
    assert.deepEqual(await page.locator(".dt-nav-leaf:visible").allTextContents(), ["Результаты"]);
    await page.locator("#dtNavSearch").press("Escape");
    assert.equal(await page.locator(".dt-nav-leaf:visible").count(), 7);
    assert.deepEqual(errors, []);
    console.log("PASS product shell: demo palette and joined 52px headers in all six sections; project tree icons/search/collapse");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
