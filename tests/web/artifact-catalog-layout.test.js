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
    const panel = index.slice(index.indexOf('      <section class="panel" id="tab-artifacts">'), index.indexOf('      <section class="panel" id="tab-diagnostics"'));
    await page.setContent(`<style>${css}\nbody{display:block;height:100vh}</style><div class="shell app-shell"><header class="workspace-topbar"><button id="productMobileMenuButton"></button></header><button id="productNavigationScrim" class="navigation-scrim"></button><button id="workspaceDiagnostics" hidden></button><main class="content">${panel.replace('class="panel"', 'class="panel active"')}</main></div>`);
    await page.evaluate(() => {
      window.$ = id => document.getElementById(id);
      const uri = name => "rna://chat/doc/artifact/" + name + "/revision/1";
      window.fixtureItems = [
        { title: "Отчёт по продажам", kind: "html_workspace", resourceUri: uri("html_ws_a_r1"), linked: false, selected: false },
        { title: "Другой HTML-проект", kind: "html_workspace", resourceUri: uri("html_ws_b_r1"), linked: false },
        { title: "Данные за октябрь.json", kind: "file", resourceUri: uri("artifact_c"), linked: false }
      ].map((item, index) => ({ ...item, documentId: "doc", documentTitle: "Продажи.xlsx",
        updatedUtc: "2026-10-18T10:" + String(30 - index).padStart(2, "0") + ":00Z", canTransfer: true, canDelete: true }));
      window.state = { activeChatId: "chat", chatNavigationVersion: 1, artifacts: [], htmlWorkspaceSelection: {} };
      state.artifactLibrary = { sessionRevision: 3 };
      window.RNAssistantArtifactVisuals = { libraryHead: artifact => artifact.libraryHead };
      state.artifacts = fixtureItems.slice(0, 2).map((item, index) => ({ id: "head-" + index, resourceUri: item.resourceUri,
        libraryHead: { artifactId: "head-" + index, resourceUri: item.resourceUri, revision: 3, historyCount: 3,
          resourceClass: "versioned_aggregate" } }));
      window.commands = []; window.historyRequests = []; window.exportRequests = []; window.historyWait = null;
      window.renderHtmlWorkspace = () => RNAssistantHtmlWorkspaceTree.render({ root: $("htmlWorkspaceTree"), selected: {}, files: [], artifacts: [] });
      window.applyChatStateForChat = () => {};
      window.send = async (command, args) => {
        commands.push(command);
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
        Data: [], ExternalDependencies: [] }, Views: [] }) : "Другой HTML-проект\n\n1. Собрать данные\n2. Проверить результат") };
      window.RNAssistantHtmlWorkspacePreview = { ensureVendors: async () => {}, build: () => '<style>body{font:15px system-ui;padding:20px;color:#243246}h1{font-size:24px}.card{padding:20px;background:#f3f6fa;border-radius:12px}</style><h1>Продажи за октябрь</h1><div class="card">Выручка<br><strong style="font-size:30px">1 240 000 ₽</strong></div>' };
    });
    for (const file of ["app-icons.js", "app-navigation-tree.js", "app-action-menu.js", "app-navigation-pane.js", "app-dock-workspace.js", "app-tree-adapter.js", "vendor/purify.min.js", "vendor/marked.min.js", "app-markdown.js", "app-viewer-registry.js", "app-text-viewer.js", "app-json-viewer.js", "app-html-workspace-tree.js", "app-artifact-versions.js", "app-artifact-catalog.js", "app-panel-toggle.js", "app-layout.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(() => { window.openTargets = [];
      window.RNAssistantMaterialView = { target: (item, context) => Object.assign({ view: 'material' }, item, context) };
      window.nativeOpenTarget = RNAssistantDockWorkspace.openTarget;
      RNAssistantDockWorkspace.openTarget = (target, options) => { openTargets.push(target); return true; };
      RNAssistantArtifactCatalog.show("document"); initializeSplitPanes(); });
    await page.locator("#htmlWorkspaceTree").getByText("В документе", { exact: true }).waitFor();
    assert.equal(await page.locator("#htmlWorkspaceTree").getByText("HTML-проекты", { exact: true }).count(), 1,
      "source adapter preserves the HTML project group inside its owner");
    await page.locator("#artifactCatalogOverview").waitFor({ state: "visible" });
    assert.equal(await page.locator("#artifactCatalogOverviewRows tr").count(), 3, "demo-style catalog table uses the loaded projection");
    assert.equal(await page.locator("#htmlWorkspaceSearchInput").count(), 0, "catalog and tree use one material search");
    assert.equal(await page.locator("#artifactNavSearch").count(), 1);
    await page.evaluate(() => { window.createCalls = 0;
      document.getElementById("addPlanButton").addEventListener("click", () => createCalls++); });
    await page.locator("#artifactOverviewCreate").click();
    assert.equal(await page.locator(".rna-action-menu-popup button svg").count(), 6);
    await page.getByRole("menuitem", { name: "План", exact: true }).click();
    assert.equal(await page.evaluate(() => createCalls), 1, "main Add entry uses the existing material action owner");
    if (process.env.LAYOUT_SCREENSHOT) {
      await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT.replace(/\.png$/, "-overview-1100.png") });
    }
    await page.setViewportSize({ width: 420, height: 760 });
    await page.waitForFunction(() => document.querySelector(".shell").classList.contains("navigation-overlay"));
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), "overview does not overflow narrow pane");
    if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT.replace(/\.png$/, "-overview-420.png") });
    await page.setViewportSize({ width: 1100, height: 760 });
    await page.evaluate(() => { fixtureItems[0].linked = true; return RNAssistantArtifactCatalog.show(); });
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
      await page.waitForFunction(width => document.querySelector(".shell").classList.contains(width > 560 ? "navigation-wide" : "navigation-overlay"), width);
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
    await page.locator("#htmlWorkspaceTree").getByText("Другой HTML-проект", { exact: true }).click();
    await page.evaluate(() => { releaseHistory(); historyWait = null; });
    await page.locator("#artifactCatalogActions .artifact-action-menu > summary").click();
    await versions.locator("summary").click();
    await versions.getByRole("button", { name: /v3 · текущая/ }).waitFor();
    assert.equal(await page.evaluate(() => historyRequests.at(-1).headArtifactId), "head-1", "each file requests its own history");
    await versions.getByRole("button", { name: /v2 · другая ветка/ }).click();
    await page.getByRole("heading", { name: "Другой HTML-проект · v2", exact: true }).waitFor();
    assert.equal(await page.evaluate(() => exportRequests.at(-1).resourceUri), await page.evaluate(() => fixtureItems[1].resourceUri + "-v2"), "late history cannot select another file's revision");
    await page.locator("#artifactCatalogActions .artifact-action-menu > summary").click();
    await page.getByRole("button", { name: "К текущей версии", exact: true }).click();
    await page.locator("#artifactCatalogActions .artifact-action-menu > summary").click();
    page.once("dialog", dialog => dialog.accept());
    await page.getByRole("button", { name: "Удалить из документа…" }).click();
    await page.locator("#artifactCatalogOverview").waitFor({ state: "visible" });
    assert.equal(await page.locator("#htmlWorkspaceTree").getByText("Другой HTML-проект", { exact: true }).count(), 0);
    await page.locator("#htmlWorkspaceTree").getByText("Данные за октябрь.json", { exact: true }).click();
    assert.equal(await page.evaluate(() => openTargets.at(-1).resourceUri), await page.evaluate(() => fixtureItems.at(-1).resourceUri), "non-HTML selection delegates the exact object to Dockview");
    assert.equal(await page.evaluate(() => commands.filter(c => c === "changeArtifactLink").length), 0, "browsing linked HTML, revisions and files never changes chat context");
    assert.equal(await page.evaluate(() => nativeOpenTarget({ view: "material", resourceUri: fixtureItems[0].resourceUri, chatId: "other", documentId: "doc", navigationVersion: 1 })), false, "stale scope is not retargeted");
    // Shared loading/error/paging states; navigation never replaces an open exact viewer.
    const selectedTitle = await page.locator('#artifactCatalogPreviewTitle').textContent();
    await page.evaluate(() => {
      window.catalogSend = send;
      send = async (command, args) => {
        if (command !== 'listArtifactCatalog') return catalogSend(command, args);
        if (window.catalogFailure) throw new Error('Каталог временно недоступен');
        return { chatId:'chat', sessionRevision:3, documentId:'doc', items:fixtureItems.filter(item=>item.title.includes(args.query || '')) };
      };
    });
    await page.getByRole('button',{name:'Поиск материалов',exact:true}).click();
    await page.locator('#artifactNavSearch').fill('нет такого файла');
    await page.waitForFunction(()=>document.getElementById('artifactCatalogStatus').textContent==='Артефакты не найдены.');
    assert.equal(await page.locator('#artifactCatalogPreviewTitle').textContent(),selectedTitle,'filter keeps the exact opened resource');
    await page.evaluate(()=>{ catalogFailure=true; RNAssistantArtifactCatalog.show(); });
    await page.getByRole('button',{name:'Повторить загрузку',exact:true}).waitFor();
    await page.evaluate(()=>{ catalogFailure=false; });
    await page.getByRole('button',{name:'Повторить загрузку',exact:true}).click();
    await page.getByRole('button',{name:'Повторить загрузку',exact:true}).waitFor({state:'hidden'});
    await page.evaluate(()=>{
      window.pendingCatalogs=[];
      send=(command,args)=>command==='listArtifactCatalog'?new Promise(resolve=>pendingCatalogs.push(resolve)):catalogSend(command,args);
      RNAssistantArtifactCatalog.show(); RNAssistantArtifactCatalog.show();
    });
    await page.evaluate(()=>pendingCatalogs[1]({chatId:'chat',sessionRevision:3,documentId:'doc',items:[{...fixtureItems[0],title:'Новый каталог'}]}));
    await page.locator('#htmlWorkspaceTree').getByText('Новый каталог',{exact:true}).waitFor();
    await page.evaluate(()=>pendingCatalogs[0]({chatId:'chat',sessionRevision:3,documentId:'doc',items:[{...fixtureItems[0],title:'Устаревший каталог'}]}));
    assert.equal(await page.locator('#htmlWorkspaceTree').getByText('Устаревший каталог',{exact:true}).count(),0);
    await page.evaluate(()=>{
      send=async(command,args)=>command==='listArtifactCatalog'?{chatId:'chat',sessionRevision:3,documentId:'doc',nextCursor:'next',items:Array.from({length:2000},(_,i)=>({...fixtureItems[0],title:'Материал '+i,kind:'file',resourceUri:'rna://chat/doc/artifact/'+i+'/revision/1'}))}:catalogSend(command,args);
      RNAssistantArtifactCatalog.show();
    });
    await page.locator('#artifactCatalogStatus').filter({hasText:'Показаны первые 500'}).waitFor();
    assert.equal(await page.locator('#htmlWorkspaceTree [data-tree-key^="catalog::"]').count(),500,'bounded metadata rendering');
    assert.equal(await page.locator('#artifactCatalogMore').isVisible(),false,'bound is explicit, not an endless append');
    assert.equal(errors.length, 0, errors.join("\n"));
    console.log("PASS artifact layout: 1100/420 px, lazy paged per-file versions, exact preview/download, dirty/late guards and deletion");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
