"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require("playwright");

(async function () {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage();
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.setContent('<button id="toggle" disabled>Выбрать элемент</button><span id="status"></span>' +
      '<iframe id="preview" sandbox="allow-scripts allow-modals" style="width:600px;height:320px"></iframe>');
    const web = path.resolve(__dirname, "../../web/js");
    await page.addScriptTag({ path: path.join(web, "app-visual-inspector.js") });
    await page.addScriptTag({ path: path.join(web, "app-html-workspace-preview.js") });
    await page.evaluate(() => {
      document.documentElement.style.setProperty("--accent", "#3A63F2");
      document.documentElement.style.setProperty("--accent-ink", "#FFFFFF");
    });
    const markup = '<!doctype html><html><head><title>Fixture</title></head><body style="margin:0;height:900px">' +
      '<section id="card" data-ui-id="safe-card" style="margin:30px;padding:12px;border:1px solid">' +
      '<span>Visible text</span><input id="secret" value="private-password">' +
      '<div contenteditable="true">private editable text</div><span data-private>private descendant</span>' +
      '<button id="action">Run</button></section><output id="count">0</output>' +
      '<script>document.getElementById("action").addEventListener("click",function(){' +
      'var count=document.getElementById("count");count.textContent=String(Number(count.textContent)+1);});<\/script>' +
      '</body></html>';
    await page.evaluate(html => {
      window.RNAssistantHtmlVendorRuntime = { used: () => [] };
      const frame = document.getElementById("preview");
      window.inspector = RNAssistantVisualInspector.createHost({ frame,
        button: document.getElementById("toggle"), status: document.getElementById("status"),
        onSelectionChanged: available => { window.selectionAvailable = available; } });
      window.loadPreview = () => {
        const generation = inspector.reset();
        frame.srcdoc = RNAssistantHtmlWorkspacePreview.build({
          files: [{ id: "index.html", path: "index.html", kind: "html", content: html }],
          dataSources: [], inspectGeneration: generation
        });
        return generation;
      };
      window.loadPreview();
    }, markup);
    await page.locator("#toggle:not([disabled])").waitFor();
    const frame = page.frameLocator("#preview");
    await frame.locator("#action").click();
    assert.equal(await frame.locator("#count").textContent(), "1", "ordinary click works while off");

    await page.locator("#toggle").click();
    await page.waitForFunction(() => document.getElementById("toggle").getAttribute("aria-pressed") === "true");
    assert.equal(await page.locator("#toggle").getAttribute("aria-pressed"), "true");
    await frame.locator("#card").hover({ position: { x: 5, y: 5 } });
    const beforeScroll = await frame.locator("html").evaluate(node => {
      const box = node.querySelector('[data-rna-visual-highlight]');
      const tip = node.querySelector('[data-rna-visual-tooltip]');
      return box && tip && { top: box.getBoundingClientRect().top,
        pointerEvents: getComputedStyle(box).pointerEvents,
        border: getComputedStyle(box).borderTopColor, label: tip.textContent };
    });
    assert.equal(beforeScroll.pointerEvents, "none");
    assert.equal(beforeScroll.border, "rgb(58, 99, 242)", "hover follows the selected UI accent");
    assert.match(beforeScroll.label, /section#card\s+\d+ × \d+/, "hover names the exact element and its size");
    assert.doesNotMatch(beforeScroll.label, /private|password/i, "hover label omits sensitive data");
    await page.evaluate(() => {
      document.documentElement.style.setProperty("--accent", "#9BC8A5");
      document.documentElement.style.setProperty("--accent-ink", "#101216");
    });
    await frame.locator('[data-rna-visual-highlight]').evaluate(() => new Promise(resolve =>
      requestAnimationFrame(() => requestAnimationFrame(resolve))));
    assert.equal(await frame.locator('[data-rna-visual-highlight]').evaluate(node => getComputedStyle(node).borderTopColor),
      "rgb(155, 200, 165)", "active highlight follows an accent/theme change");
    await page.mouse.move(5, 5);
    await frame.locator('[data-rna-visual-highlight]').evaluate(() => new Promise(resolve => requestAnimationFrame(resolve)));
    assert.equal(await frame.locator('[data-rna-visual-highlight]').evaluate(node => node.style.display), "none",
      "hover clears when the pointer leaves the preview before selection");
    await frame.locator("#card").hover({ position: { x: 5, y: 5 } });
    await frame.locator("#card").click({ position: { x: 5, y: 5 } });
    const selection = await page.evaluate(() => inspector.selection());
    const selectionRecord = await page.evaluate(() => inspector.selectionRecord());
    assert.equal(selectionRecord.generation, await page.evaluate(() => inspector.generation()));
    assert.deepEqual(selectionRecord.selection, selection, "composer capture binds the current frame generation");
    assert.equal(await page.evaluate(() => selectionAvailable), true);
    assert.equal(selection.stableUiId, "safe-card");
    assert.equal(selection.tag, "section");
    assert.ok(selection.selector.includes("section:nth-of-type"));
    assert.ok(selection.textExcerpt.includes("Visible text"));
    assert.ok(!/private|password/i.test(JSON.stringify(selection)), "sensitive descendants and values are absent");
    assert.ok(!/private|password/i.test(await page.locator("#status").textContent()));
    const beforeResize = await frame.locator('[data-rna-visual-highlight]').evaluate(node => node.getBoundingClientRect().width);
    await page.locator("#preview").evaluate(node => { node.style.width = "500px"; });
    await frame.locator("html").evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const afterResize = await frame.locator('[data-rna-visual-highlight]').evaluate(node => node.getBoundingClientRect().width);
    assert.ok(afterResize < beforeResize, "overlay follows viewport resize");
    await frame.locator("#action").click();
    assert.equal(await frame.locator("#count").textContent(), "1", "active inspector captures clicks");
    await frame.locator("#secret").click();
    const input = await page.evaluate(() => inspector.selection());
    assert.deepEqual(input.attributes, {}, "form fields expose no attributes or values");
    assert.equal(input.textExcerpt, "");
    await frame.locator("body").evaluate(node => {
      const nested = node.ownerDocument.createElement("iframe");
      nested.id = "nested"; nested.style.cssText = "display:block;width:80px;height:40px";
      node.appendChild(nested);
    });
    await frame.locator("#nested").hover();
    await page.locator("#status").getByText(/недоступен/).waitFor();
    await frame.locator("#nested").click();
    assert.equal(await page.evaluate(() => inspector.selection()), null, "nested frame is explicitly unsupported");
    assert.equal(await page.evaluate(() => inspector.selectionRecord()), null);
    assert.match(await page.locator("#status").textContent(), /недоступен/);

    await frame.locator("#card").hover();
    await frame.locator("body").evaluate(node => node.ownerDocument.defaultView.scrollTo(0, 20));
    await frame.locator("html").evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const afterScroll = await frame.locator("html").evaluate(node =>
      node.querySelector('[data-rna-visual-highlight]').getBoundingClientRect().top);
    assert.ok(afterScroll < beforeScroll.top, "overlay follows scrolling without changing layout");
    await frame.locator("body").evaluate(node => node.ownerDocument.defaultView.scrollTo(0, 300));
    await frame.locator("html").evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    assert.match(await frame.locator('[data-rna-visual-tooltip]').textContent(), /^body\s+/,
      "stationary pointer inspects the element now beneath it after scrolling");
    await frame.locator("#action").press("Escape");
    assert.equal(await page.locator("#toggle").getAttribute("aria-pressed"), "false");
    assert.equal(await page.evaluate(() => selectionAvailable), false);
    await frame.locator("#action").click();
    assert.equal(await frame.locator("#count").textContent(), "2", "Escape restores ordinary clicks");
    assert.equal(await frame.locator('[data-rna-visual-highlight]').evaluate(node => node.style.display), "none");

    await page.locator("#toggle").click();
    const old = await page.evaluate(() => inspector.generation());
    await page.evaluate(() => loadPreview());
    await page.locator("#toggle:not([disabled])").waitFor();
    assert.equal(await page.locator("#toggle").getAttribute("aria-pressed"), "false", "replacement revokes active selection");
    await frame.locator("body").evaluate((node, generation) => node.ownerDocument.defaultView.parent.postMessage({
      type: "rnassistant-visual-inspector-selected", generation, selection: { tag: "fake" }
    }, "*"), old);
    assert.equal(await page.evaluate(() => inspector.selection()), null, "old frame generation cannot select");
    await page.locator("#toggle").click();
    await frame.locator("#card").click({ position: { x: 5, y: 5 } });
    assert.equal((await page.evaluate(() => inspector.selection())).stableUiId, "safe-card", "replacement frame is usable");
    await frame.locator("#card").hover();
    await frame.locator("#card").evaluate(node => { node.remove(); node.ownerDocument.dispatchEvent(new Event("scroll")); });
    await frame.locator("html").evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    await page.waitForFunction(() => inspector.selection() === null);
    assert.equal(await page.evaluate(() => inspector.selection()), null, "removed selection is invalidated");
    assert.deepEqual(errors, []);

    const html = fs.readFileSync(path.resolve(__dirname, "../../web/index.html"), "utf8");
    const css = [...html.matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)]
      .map(match => fs.readFileSync(path.resolve(__dirname, "../../web", match[1]), "utf8")).join("\n");
    const panel = html.slice(html.indexOf('<section class="panel" id="tab-artifacts">'),
      html.indexOf('<section class="panel" id="tab-settings">'));
    const layout = await browser.newPage();
    await layout.setContent('<style>' + css + '\nbody{display:block}#tab-artifacts{display:flex;height:100vh}</style>' + panel);
    const toggle = layout.locator("#inspectHtmlWorkspaceButton");
    const offBackground = await toggle.evaluate(node => getComputedStyle(node).backgroundColor);
    await toggle.evaluate(node => node.setAttribute("aria-pressed", "true"));
    assert.notEqual(await toggle.evaluate(node => getComputedStyle(node).backgroundColor), offBackground,
      "the shared toggle style also marks selection in Materials");
    for (const width of [1100, 760, 420]) {
      await layout.setViewportSize({ width, height: 760 });
      const bounds = await layout.locator("#htmlWorkspaceEditor").boundingBox();
      for (const id of ["inspectHtmlWorkspaceButton", "attachHtmlWorkspaceSelectionButton", "htmlWorkspaceMoreActions"]) {
        const box = await layout.locator("#" + id).boundingBox();
        assert.ok(box.x >= bounds.x && box.x + box.width <= bounds.x + bounds.width + 1,
          id + " fits at " + width + "px");
      }
      await layout.locator("#htmlWorkspaceMoreActions > summary").click();
      const menu = await layout.locator("#reloadHtmlWorkspaceSourceButton").boundingBox();
      assert.ok(menu.x >= 0 && menu.x + menu.width <= width, "existing actions menu fits at " + width + "px");
      await layout.locator("#htmlWorkspaceMoreActions > summary").click();
    }
    console.log("PASS visual inspector: isolated preview, bounded selection, sensitive descendants, click/Escape, scroll, generation");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
