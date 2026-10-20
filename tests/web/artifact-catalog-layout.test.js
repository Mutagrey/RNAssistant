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
      ].map((item, index) => ({ ...item, documentId: "doc", documentTitle: "Продажи.xlsx",
        updatedUtc: "2026-10-18T10:" + String(30 - index).padStart(2, "0") + ":00Z", canTransfer: true, canDelete: true }));
      window.state = { activeChatId: "chat", chatNavigationVersion: 1, artifacts: [], htmlWorkspaceSelection: {} };
      state.artifactLibrary = { sessionRevision: 3 };
      window.RNAssistantArtifactVisuals = { libraryHead: artifact => artifact.libraryHead };
      state.artifacts = fixtureItems.slice(0, 2).map((item, index) => ({ id: "head-" + index, resourceUri: item.resourceUri,
        libraryHead: { artifactId: "head-" + index, resourceUri: item.resourceUri, revision: 3, historyCount: 3,
          resourceClass: index ? "versioned_document" : "versioned_aggregate" } }));
      window.historyRequests = []; window.exportRequests = []; window.historyWait = null;
      window.renderHtmlWorkspace = () => RNAssistantHtmlWorkspaceTree.render({ root: $("htmlWorkspaceTree"), selected: {}, files: [], artifacts: [] });
      window.applyChatStateForChat = () => {};
      window.send = async (command, args) => {
        if (command === "listArtifactCatalog") return { chatId: "chat", sessionRevision: 3, documentId: "doc", documents: [{ id: "doc", title: "Продажи.xlsx" }], items: fixtureItems };
        if (command === "getArtifactLibraryHistory") {
          historyRequests.push(args);
          const index = args.headArtifactId === "head-0" ? 0 : 1;
          const revisions = args.cursor ? [1] : [3, 2];
          const result = { chatId: "chat", sessionRevision: 3, headArtifactId: args.headArtifactId, nextCursor: args.cursor ? null : "older",
            items: revisions.map(revision => ({ artifactId: "artifact-" + index + "-" + revision, revision,
              resourceUri: fixtureItems[index].resourceUri + (revision === 3 ? "" : "-v" + revision),
              title: fixtureItems[index].title, createdUtc: "2026-10-14T10:00:00Z", isHead: revision === 3,
              relation: revision === 3 ? "head" : revision === 2 ? "branch" : "ancestor" })) };
          if (historyWait) await historyWait;
          return result;
        }
        if (command === "exportArtifact") { exportRequests.push(args); return { contentType: args.resourceUri.includes("html_ws") ? "application/json" : "text/plain", fileName: "artifact.txt", data: { leaseId: "preview", uri: args.resourceUri } }; }
        if (command === "closeArtifactTransfer") return {};
        if (command === "deleteDocumentArtifact") { fixtureItems = fixtureItems.filter(item => item.resourceUri !== args.resourceUri); return {}; }
        throw new Error(command);
      };
      window.RNAssistantResourceDownload = { read: async data => new TextEncoder().encode(data.uri.includes("html_ws") ? JSON.stringify({ Project: {
        EntryPath: "index.html", Files: [{ Path: "index.html", Kind: "html", Content: "<h1>" + (data.uri.endsWith("-v1") ? "Старая версия" : "Продажи") + "</h1>" },
          { Path: "styles.css", Kind: "css", Content: data.uri.endsWith("-v1") ? "body { color: red; }" : "body { color: blue; }" }],
        Data: [], ExternalDependencies: [] }, Views: [] }) : "План исследования\n\n1. Собрать данные\n2. Проверить результат") };
      window.RNAssistantHtmlWorkspacePreview = { ensureVendors: async () => {}, build: () => '<style>body{font:15px system-ui;padding:20px;color:#243246}h1{font-size:24px}.card{padding:20px;background:#f3f6fa;border-radius:12px}</style><h1>Продажи за октябрь</h1><div class="card">Выручка<br><strong style="font-size:30px">1 240 000 ₽</strong></div>' };
    });
    for (const file of ["app-icons.js", "app-navigation-tree.js", "app-tree-adapter.js", "vendor/purify.min.js", "vendor/marked.min.js", "app-markdown.js", "app-viewer-registry.js", "app-text-viewer.js", "app-json-viewer.js", "app-html-workspace-tree.js", "app-artifact-versions.js", "app-artifact-catalog.js", "app-layout.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(() => { RNAssistantArtifactCatalog.show("document"); initializeSplitPanes(); });
    await page.locator("#htmlWorkspaceTree").getByText("В документе", { exact: true }).waitFor();
    assert.equal(await page.locator("#htmlWorkspaceTree").getByText("HTML-проекты", { exact: true }).count(), 0,
      "catalog groups by ownership rather than format");
    await page.locator("#artifactCatalogOverview").waitFor({ state: "visible" });
    assert.equal(await page.locator("#artifactCatalogOverviewRows tr").count(), 3, "demo-style catalog table uses the loaded projection");
    assert.equal(await page.locator("#htmlWorkspaceSearchInput").count(), 0, "catalog and tree use one material search");
    assert.equal(await page.locator("#artifactNavSearch").count(), 1);
    await page.evaluate(() => { window.createCalls = 0;
      document.getElementById("addPlanButton").addEventListener("click", () => createCalls++); });
    await page.locator("#artifactCreateMenu > summary").click();
    assert.equal(await page.locator("#artifactCreateMenu .html-workspace-menu-items button svg").count(), 6);
    await page.locator("#artifactCreateMenu").getByRole("button", { name: "План" }).click();
    assert.equal(await page.evaluate(() => createCalls), 1, "main Add entry uses the existing material action owner");
    if (process.env.LAYOUT_SCREENSHOT) {
      await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT.replace(/\.png$/, "-overview-1100.png") });
    }
    await page.setViewportSize({ width: 420, height: 760 });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), "overview does not overflow narrow pane");
    if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT.replace(/\.png$/, "-overview-420.png") });
    await page.setViewportSize({ width: 1100, height: 760 });
    await page.locator("#artifactCatalogOverview").getByRole("button", { name: "Отчёт по продажам" }).click();
    await page.locator("#artifactCatalogPreviewBody iframe").waitFor({ state: "visible" });
    await page.evaluate(() => RNAssistantArtifactCatalog.show());
    assert.ok(await page.locator("#artifactCatalogPreview").isVisible(), "reopening the section keeps its current material");
    await page.evaluate(() => { state.htmlWorkspaceDirty = true; });
    await page.locator("#artifactCatalogAll").click();
    assert.ok(await page.locator("#artifactCatalogPreview").isVisible(), "dirty draft blocks return to catalog");
    await page.evaluate(() => { state.htmlWorkspaceDirty = false; });
    await page.locator("#artifactCatalogAll").click();
    await page.locator("#artifactCatalogOverview").waitFor({ state: "visible" });
    await page.locator("#artifactCatalogOverview").getByRole("button", { name: "Отчёт по продажам" }).click();
    await page.locator("#artifactCatalogPreviewBody iframe").waitFor({ state: "visible" });
    assert.equal(await page.evaluate(() => historyRequests.length), 0, "history stays lazy");
    for (const width of [1100, 420]) {
      await page.setViewportSize({ width, height: 760 });
      const sidebar = await page.locator("#htmlWorkspaceSidebar").boundingBox();
      const viewer = await page.locator("#artifactCatalogPreview").boundingBox();
      const content = await page.locator("#artifactCatalog").boundingBox();
      if (width > 560) {
        assert.ok(sidebar.x + sidebar.width <= viewer.x + 1, "tree stays to the left of viewer at " + width);
        assert.ok(Math.abs(sidebar.y - content.y) < 2, "viewer column is alongside tree at " + width);
      } else assert.equal(await page.locator("#htmlWorkspaceSidebar").isVisible(), false, "compact layout shows the central viewer");
      assert.ok(viewer.height > 400, "preview fills the available height at " + width);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), "no page overflow at " + width);
      if (width > 560) assert.ok(await page.locator("#htmlWorkspaceTree").isVisible());
      assert.equal(await page.locator("#artifactCatalogPreviewBody pre").isVisible(), false);
      if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT.replace(/\.png$/, "-" + width + ".png") });
    }
    await page.locator("#artifactCatalogPreviewBody").getByRole("button", { name: "Код", exact: true }).click();
    assert.equal(await page.locator("#artifactCatalogPreviewBody pre").isVisible(), true);
    assert.equal(await page.locator("#artifactCatalogPreviewBody iframe").isVisible(), false);
    const versions = page.locator(".artifact-version-menu");
    await page.locator("#artifactCatalogActions .artifact-action-menu > summary").click();
    const artifactActions = page.locator("#artifactCatalogActions .artifact-action-menu > .html-workspace-menu-items > button");
    assert.ok(await artifactActions.count() > 0);
    assert.equal(await artifactActions.locator("svg").count(), await artifactActions.count(),
      "material actions have an icon for every command");
    await versions.locator("summary").click();
    await versions.getByRole("button", { name: /v3 · текущая/ }).waitFor();
    assert.equal(await versions.getByRole("button", { name: /v3 · текущая/ }).getAttribute("aria-current"), "true");
    assert.equal(await versions.getByRole("button", { name: /v2 · другая ветка/ }).count(), 1);
    await versions.getByRole("button", { name: "Показать ещё 50" }).click();
    await versions.getByRole("button", { name: /^v1 / }).waitFor();
    const menuBox = await versions.locator(".artifact-version-options").boundingBox();
    assert.ok(menuBox.x >= 0 && menuBox.x + menuBox.width <= 421, "version menu fits narrow pane");
    if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT.replace(/\.png$/, "-menu.png") });
    assert.equal(await page.evaluate(() => historyRequests[1].cursor), "older");
    const exportsBeforeDirtyNavigation = await page.evaluate(() => exportRequests.length);
    await page.evaluate(() => { state.htmlWorkspaceDirty = true; });
    await versions.getByRole("button", { name: /^v1 / }).click();
    assert.ok(await page.locator("#artifactCatalogPreviewStatus").getByText("Сохраните или отмените правки перед изменением артефактов.", { exact: true }).isVisible());
    assert.equal(await page.evaluate(() => exportRequests.length), exportsBeforeDirtyNavigation, "dirty draft blocks version navigation");
    await page.evaluate(() => { state.htmlWorkspaceDirty = false; state.htmlWorkspaceMode = "edit";
      state.activeHtmlArtifactId = "unchanged-head"; state.htmlWorkspaceSelection = { type: "file", id: "css" };
      state.htmlWorkspace = { files: [{ id: "css", path: "styles.css" }] }; });
    await versions.locator("summary").click();
    await page.locator("#artifactCatalogPreviewBody select").selectOption("styles.css");
    await versions.locator("summary").click();
    await versions.getByRole("button", { name: /^v1 / }).click();
    await page.getByRole("heading", { name: "Отчёт по продажам · v1" }).waitFor();
    await page.waitForFunction(() => document.querySelector("#artifactCatalogPreviewBody pre")?.textContent === "body { color: red; }");
    assert.equal(await page.locator("#artifactCatalogPreviewBody select").inputValue(), "styles.css", "selected HTML member survives version browsing");
    assert.equal(await page.evaluate(() => state.activeHtmlArtifactId), "unchanged-head", "browsing never changes working revision");
    assert.equal(await page.evaluate(() => exportRequests.at(-1).resourceUri), await page.evaluate(() => fixtureItems[0].resourceUri + "-v1"));
    assert.equal(await page.getByRole("button", { name: "Подключить к чату", exact: true }).count(), 0, "historical view has no current-head mutation actions");
    await page.locator("#artifactCatalogActions .artifact-action-menu > summary").click();
    await page.getByRole("button", { name: "Скачать v1", exact: true }).click();
    await page.waitForFunction(() => exportRequests.some(request => !request.preview && request.resourceUri.endsWith("-v1")));
    await page.getByRole("button", { name: "К текущей версии", exact: true }).click();
    await page.getByRole("heading", { name: "Отчёт по продажам", exact: true }).waitFor();
    await page.evaluate(() => { window.historyWait = new Promise(resolve => { window.releaseHistory = resolve; }); });
    await page.locator("#artifactCatalogActions .artifact-action-menu > summary").click();
    await versions.locator("summary").click();
    await page.waitForFunction(() => historyRequests.length === 3);
    await page.setViewportSize({ width: 1100, height: 760 });
    await page.locator("#htmlWorkspaceTree").getByText("План исследования", { exact: true }).click();
    await page.evaluate(() => { releaseHistory(); historyWait = null; });
    await page.locator("#artifactCatalogActions .artifact-action-menu > summary").click();
    await versions.locator("summary").click();
    await versions.getByRole("button", { name: /v3 · текущая/ }).waitFor();
    assert.equal(await page.evaluate(() => historyRequests.at(-1).headArtifactId), "head-1", "each file requests its own history");
    await versions.getByRole("button", { name: /v2 · другая ветка/ }).click();
    await page.getByRole("heading", { name: "План исследования · v2", exact: true }).waitFor();
    assert.equal(await page.evaluate(() => exportRequests.at(-1).resourceUri), await page.evaluate(() => fixtureItems[1].resourceUri + "-v2"), "late history cannot select another file's revision");
    await page.locator("#artifactCatalogActions .artifact-action-menu > summary").click();
    await page.getByRole("button", { name: "К текущей версии", exact: true }).click();
    await page.locator("#artifactCatalogActions .artifact-action-menu > summary").click();
    page.once("dialog", dialog => dialog.accept());
    await page.getByRole("button", { name: "Удалить из документа…" }).click();
    await page.getByText("Выберите артефакт", { exact: true }).waitFor();
    assert.equal(await page.locator("#htmlWorkspaceTree").getByText("План исследования", { exact: true }).count(), 0);
    await page.locator("#htmlWorkspaceTree").getByText("Данные за октябрь.json", { exact: true }).click();
    assert.equal(await versions.count(), 0, "originals without revision history have no chooser");
    assert.equal(errors.length, 0, errors.join("\n"));
    console.log("PASS artifact layout: 1100/420 px, lazy paged per-file versions, exact preview/download, dirty/late guards and deletion");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
