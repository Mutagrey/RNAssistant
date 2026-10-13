"use strict";
// Real Chromium geometry and tree interaction, with a local bridge fixture.
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
    const page = await browser.newPage({ viewport: { width: 1100, height: 760 } });
    page.setDefaultTimeout(5000);
    const errors = []; page.on("pageerror", error => errors.push(error.message));
    const index = read("index.html");
    const css = [...index.matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)].map(match => read(match[1])).join("\n");
    const panel = index.slice(index.indexOf('      <section class="panel" id="tab-artifacts">'), index.indexOf('      <section class="panel" id="tab-settings">'));
    await page.setContent(`<style>${css}\nbody{display:block;height:100vh}</style>${panel.replace('class="panel"', 'class="panel active"')}`);
    await page.evaluate(() => {
      window.$ = id => document.getElementById(id);
      const uri = name => "rna://chat/doc/artifact/" + name + "/revision/1";
      window.fixtureItems = [
        { title: "Отчёт по продажам", kind: "html_workspace", resourceUri: uri("html_ws_a_r1"), linked: false },
        { title: "План исследования", kind: "markdown", resourceUri: uri("artifact_md_b_r1"), linked: false },
        { title: "Данные за октябрь.json", kind: "file", resourceUri: uri("artifact_c"), linked: false }
      ].map(item => ({ ...item, documentId: "doc", documentTitle: "Продажи.xlsx", canTransfer: true, canDelete: true }));
      window.state = { activeChatId: "chat", chatNavigationVersion: 1, artifacts: [], htmlWorkspaceSelection: {} };
      window.renderHtmlWorkspace = () => RNAssistantHtmlWorkspaceTree.render({ root: $("htmlWorkspaceTree"), selected: {}, files: [], artifacts: [] });
      window.applyChatStateForChat = () => {};
      window.send = async (command, args) => {
        if (command === "listArtifactCatalog") return { chatId: "chat", sessionRevision: 3, documentId: "doc", documents: [{ id: "doc", title: "Продажи.xlsx" }], items: fixtureItems };
        if (command === "exportArtifact") return { contentType: args.resourceUri.includes("html_ws") ? "application/json" : "text/plain", data: { leaseId: "preview", uri: args.resourceUri } };
        if (command === "closeArtifactTransfer") return {};
        if (command === "deleteDocumentArtifact") { fixtureItems = fixtureItems.filter(item => item.resourceUri !== args.resourceUri); return {}; }
        throw new Error(command);
      };
      window.RNAssistantResourceDownload = { read: async data => new TextEncoder().encode(data.uri.includes("html_ws") ? JSON.stringify({ Project: {
        EntryPath: "index.html", Files: [{ Path: "index.html", Kind: "html", Content: "<h1>Продажи</h1>" }], Data: [], ExternalDependencies: [] }, Views: [] }) : "План исследования\n\n1. Собрать данные\n2. Проверить результат") };
      window.RNAssistantHtmlWorkspacePreview = { ensureVendors: async () => {}, build: () => '<style>body{font:15px system-ui;padding:20px;color:#243246}h1{font-size:24px}.card{padding:20px;background:#f3f6fa;border-radius:12px}</style><h1>Продажи за октябрь</h1><div class="card">Выручка<br><strong style="font-size:30px">1 240 000 ₽</strong></div>' };
    });
    for (const file of ["vendor/wunderbaum.umd.min.js", "app-tree-adapter.js", "vendor/purify.min.js", "vendor/marked.min.js", "app-markdown.js", "app-viewer-registry.js", "app-text-viewer.js", "app-json-viewer.js", "app-html-workspace-tree.js", "app-artifact-catalog.js", "app-layout.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(() => { RNAssistantArtifactCatalog.show("document"); initializeSplitPanes(); });
    await page.getByText("Отчёт по продажам", { exact: true }).click();
    await page.locator("#artifactCatalogPreviewBody iframe").waitFor({ state: "visible" });
    for (const width of [1100, 420]) {
      await page.setViewportSize({ width, height: 760 });
      const sidebar = await page.locator("#htmlWorkspaceSidebar").boundingBox();
      const viewer = await page.locator("#artifactCatalogPreview").boundingBox();
      assert.ok(sidebar.x + sidebar.width <= viewer.x + 1, "tree stays to the left of viewer at " + width);
      assert.ok(Math.abs(sidebar.y - viewer.y) < 65, "viewer is alongside tree at " + width);
      assert.ok(viewer.height > 400, "preview fills the available height at " + width);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), "no page overflow at " + width);
      assert.ok(await page.locator("#htmlWorkspaceTree").isVisible());
      assert.equal(await page.locator("#artifactCatalogPreviewBody pre").isVisible(), false);
      if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT.replace(/\.png$/, "-" + width + ".png") });
    }
    await page.locator("#artifactCatalogPreviewBody").getByRole("button", { name: "Код", exact: true }).click();
    assert.equal(await page.locator("#artifactCatalogPreviewBody pre").isVisible(), true);
    assert.equal(await page.locator("#artifactCatalogPreviewBody iframe").isVisible(), false);
    await page.getByText("План исследования", { exact: true }).click();
    await page.locator("#artifactCatalogActions summary").click();
    page.once("dialog", dialog => dialog.accept());
    await page.getByRole("button", { name: "Удалить из документа…" }).click();
    await page.getByText("Выберите артефакт", { exact: true }).waitFor();
    assert.equal(await page.locator("#htmlWorkspaceTree").getByText("План исследования", { exact: true }).count(), 0);
    assert.equal(errors.length, 0, errors.join("\n"));
    console.log("PASS artifact layout: tree/view at 1100 and 420 px, source toggle and confirmed document deletion");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
