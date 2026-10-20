"use strict";
const assert = require("node:assert/strict");
const path = require("node:path");
const { chromium } = require("playwright");

(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const demo = await browser.newPage({ viewport: { width: 1100, height: 760 } });
    const demoErrors = [];
    demo.on("pageerror", error => demoErrors.push(error.message));
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
    assert.equal(await demo.locator('#global-nav [data-action="navigation"]').count(), 0,
      "the demo only keeps the navigation toggle beside the central heading");
    assert.equal(await demo.locator("#demoNavigationToggle").count(), 1);
    const demoHeaderSpacing = () => demo.evaluate(() => {
      const header = document.querySelector("#main .page-header").getBoundingClientRect();
      const toggle = document.querySelector("#demoNavigationToggle").getBoundingClientRect();
      const title = document.querySelector("#main .page-heading").getBoundingClientRect();
      return [Math.round(toggle.left - header.left), Math.round(title.left - toggle.right)];
    });
    assert.deepEqual(await demoHeaderSpacing(), [10, 6]);
    const page = await browser.newPage({ viewport: { width: 1074, height: 760 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto("file://" + path.resolve(__dirname, "../../web/index.html"));
    const productTokens = await page.evaluate(() => {
      const css = getComputedStyle(document.documentElement);
      return ["--line", "--line-strong", "--radius", "--danger"].map(key => css.getPropertyValue(key).trim());
    });
    assert.deepEqual(productTokens, reference.tokens, "the product uses the demo palette, borders and radii");

    const navigationToggle = page.locator("#productMobileMenuButton");
    for (const key of ["Enter", "Space"]) {
      await navigationToggle.press(key);
      assert.equal(await navigationToggle.evaluate(node => node.matches(":focus-visible")), true, "keyboard use shows the panel toggle focus");
      await navigationToggle.click();
      assert.deepEqual(await navigationToggle.evaluate(node => [node === document.activeElement,
        node.matches(":focus-visible"), getComputedStyle(node).outlineStyle]), [true, false, "none"],
        "a pointer click after keyboard use keeps focus without the stale keyboard outline");
    }
    await page.waitForFunction(() => !document.querySelector(".shell").classList.contains("navigation-transition"));

    const sections = [
      ['.rail-button[data-tab="chat"]', "#chatLayout", ".chat-sidebar-head", ".chatbar"],
      ['.rail-button[data-tab="artifacts"]', "#htmlWorkspaceLayout", ".artifact-catalog-toolbar", ".artifact-overview-heading"],
      ["#libraryRootTab", "#instructionsLayout", ".product-nav-heading", ".instruction-editor-content:not(.hidden) .resource-editor-head"],
      ['.rail-button[data-tab="vba"]', "#vbaLayout", ".product-nav-heading", ".vba-editor-head"],
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
            paddingLeft: style.paddingLeft, borderWidth: style.borderBottomWidth, borderColor: style.borderBottomColor };
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
      const toggle = await page.locator("#productMobileMenuButton").evaluate(element => {
        const box = element.getBoundingClientRect();
        return { x: box.x, y: box.y, display: getComputedStyle(element).display };
      });
      assert.equal(toggle.display, "grid", name + " keeps the navigation toggle beside the title");
      assert.equal(toggle.x, geometry.main.x + 10, name + " keeps a narrow gap from the panel edge");
      assert.equal(geometry.main.paddingLeft, "50px", name + " reserves the shared title gap");
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

    await page.locator("#libraryRootTab").click();
    await page.locator("#instructionsLayout > .splitter").focus();
    await page.keyboard.press("Home");
    await page.locator("#instructionsLayout > .global-nav").waitFor({ state: "hidden" });
    assert.equal(await page.locator("#instructionsLayout > .global-nav").isVisible(), false);
    await page.locator('.rail-button[data-tab="settings"]').click();
    await page.locator("#settingsLayout > .global-nav").waitFor({ state: "hidden" });
    assert.equal(await page.locator("#settingsLayout > .global-nav").isVisible(), false,
      "one navigation preference applies to every section");
    await page.locator("#productMobileMenuButton").click();
    await page.locator("#settingsLayout > .global-nav").waitFor({ state: "visible" });
    await page.waitForFunction(() => document.querySelector("#settingsLayout > .global-nav").getBoundingClientRect().width === 290);
    assert.equal(await page.locator("#settingsLayout > .global-nav").isVisible(), true);
    await page.setViewportSize({ width: 700, height: 760 });
    await page.waitForFunction(() => document.querySelector(".shell").classList.contains("navigation-compact"));
    assert.equal(await page.locator(".shell").evaluate(element => element.classList.contains("navigation-transition")), true,
      "resizing into compact animates the navigation collapse");
    const resizePositions = await page.evaluate(async () => {
      const main = document.querySelector("#settingsLayout .settings-pages"), positions = [];
      for (let frame = 0; frame < 8; frame += 1) {
        await new Promise(resolve => requestAnimationFrame(resolve));
        positions.push(Math.round(main.getBoundingClientRect().left));
      }
      return positions;
    });
    assert.ok(new Set(resizePositions).size > 2, "the central panel moves through intermediate positions on resize");
    await page.locator("#settingsLayout > .global-nav").waitFor({ state: "hidden" });
    assert.equal(await page.locator("#settingsLayout > .global-nav").isVisible(), false);
    const menuOffsets = await page.evaluate(async () => {
      const button = document.getElementById("productMobileMenuButton");
      const main = document.querySelector("#settingsLayout .settings-pages");
      const offsets = [];
      button.click();
      for (let frame = 0; frame < 16; frame += 1) {
        await new Promise(resolve => requestAnimationFrame(resolve));
        offsets.push(Math.abs(button.getBoundingClientRect().left - main.getBoundingClientRect().left - 10));
      }
      return offsets;
    });
    assert.ok(Math.max(...menuOffsets) < 2, "navigation button follows the moving main header");
    await page.locator("#settingsLayout > .global-nav").waitFor({ state: "visible" });
    await page.waitForFunction(() => document.querySelector("#settingsLayout > .global-nav").getBoundingClientRect().width === 290);
    assert.equal(await page.locator("#settingsLayout > .global-nav").isVisible(), true);
    assert.equal(await page.locator("#settingsLayout > .global-nav").evaluate(element => element.getBoundingClientRect().width), 290);
    await page.setViewportSize({ width: 390, height: 760 });
    await page.waitForFunction(() => document.querySelector(".shell").classList.contains("navigation-single"));
    assert.equal(await page.locator(".section-rail").isVisible(), false);
    for (const [button, , , main] of sections) {
      await page.evaluate(selector => document.querySelector(selector).click(), button);
      const inset = await page.evaluate(selector => {
        const heading = Array.from(document.querySelectorAll(".panel.active " + selector))
          .find(element => element.getBoundingClientRect().height);
        return heading && getComputedStyle(heading).paddingLeft;
      }, main);
      assert.equal(inset, "48px", button + " keeps the title clear of the toggle in single-pane mode");
    }
    await demo.setViewportSize({ width: 390, height: 760 });
    await demo.waitForFunction(() => document.querySelector("#app-shell").classList.contains("navigation-single"));
    await demo.waitForFunction(() => !document.querySelector("#app-shell").classList.contains("navigation-transition"));
    assert.deepEqual(await demoHeaderSpacing(), [8, 6], "the demo keeps the same compact button spacing");
    await demo.locator("#demoNavigationToggle").click();
    await demo.locator("#global-nav").waitFor({ state: "visible" });
    assert.equal(await demo.locator("#global-nav").isVisible(), true);
    assert.equal(await demo.locator('#global-nav [data-action="navigation"]').count(), 0);
    await demo.locator("#demoNavigationToggle").click();
    await demo.locator("#global-nav").waitFor({ state: "hidden" });
    await demo.setViewportSize({ width: 1360, height: 900 });
    await demo.evaluate(() => { location.hash = "#/chat/main"; });
    const inspectorToggle = demo.locator("#demoInspectorToggle");
    await inspectorToggle.waitFor({ state: "visible" });
    assert.equal(await demo.locator('[data-panel-toggle="right"]').count(), 1, "the demo has one shared right-panel toggle");
    await inspectorToggle.press("Enter");
    await demo.waitForFunction(() => document.getElementById("demoInspectorToggle").getAttribute("aria-expanded") === "true");
    assert.equal(await inspectorToggle.getAttribute("aria-expanded"), "true", "the shared toggle opens the demo inspector once");
    assert.equal(await demo.evaluate(() => document.activeElement.matches('#demoInspectorToggle, .inspector-compact-back') &&
      document.activeElement.matches(':focus-visible')), true, "the demo preserves keyboard focus on its visible panel control after rendering");
    await inspectorToggle.click();
    await demo.waitForFunction(() => document.getElementById("demoInspectorToggle").getAttribute("aria-expanded") === "false");
    assert.equal(await inspectorToggle.getAttribute("aria-expanded"), "false", "the shared toggle closes the demo inspector once");
    assert.equal(await inspectorToggle.evaluate(node => getComputedStyle(node).outlineStyle), "none", "pointer reopening/rendering does not inherit a keyboard outline");
    assert.deepEqual(demoErrors, []);
    assert.deepEqual(errors, []);
    console.log("PASS product shell: demo palette, joined built-in headers and shared navigation collapse");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
