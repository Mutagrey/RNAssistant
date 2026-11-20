"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require("playwright");
const root = path.resolve(__dirname, "../..");

(async function () {
  const executablePath = process.env.PLAYWRIGHT_EXECUTABLE_PATH ||
    (fs.existsSync("/usr/bin/chromium") ? "/usr/bin/chromium" : undefined);
  const browser = await chromium.launch({ headless: true, executablePath });
  try {
    const page = await browser.newPage({ viewport: { width: 1120, height: 760 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.setContent('<!doctype html><html lang="ru"><body><main id="host" class="product-viewer"></main></body></html>');
    for (const file of ["app-product-tokens.css", "app-product-components.css", "app-product-navigation.css", "app-workspace.css", "app-text-viewer.css", "app-markdown-graph.css", "app-product-viewers.css"])
      await page.addStyleTag({ path: path.join(root, "web/css", file) });
    await page.addStyleTag({ content: "*{box-sizing:border-box}html,body{height:100%}body{margin:0;padding:20px;background:var(--canvas);font:14px/1.6 system-ui;color:var(--ink)}main{height:100%;padding:16px;background:var(--surface);border-radius:8px}pre{white-space:pre-wrap;margin:0;padding:12px;font-size:12px}.rn-markdown-viewer-note{color:var(--ink-muted)}" });
    for (const file of ["vendor/purify.min.js", "app-icons.js", "app-navigation-tree.js", "app-panel-toggle.js", "app-viewer-registry.js", "app-navigation-pane.js", "app-material-surface.js", "app-markdown-graph.js", "app-text-viewer.js", "app-viewer-ui.js", "app-artifact-viewer-actions.js"])
      await page.addScriptTag({ path: path.join(root, "web/js", file) });
    // The production viewer uses exactly this pinned source carrier.
    const carrier = fs.readFileSync(path.join(root, "web/js/vendor/vis-network.source.js"), "utf8");
    const marker = 'window.RNAssistantHtmlVendorRuntime.register("vis-network", ';
    assert.ok(carrier.includes(marker), "Pinned vis.Network 10.1.2 is present");
    const packageSource = JSON.parse(carrier.slice(carrier.indexOf(marker) + marker.length, carrier.lastIndexOf(");"))).js;
    await page.addScriptTag({ content: packageSource });
    await page.evaluate(() => {
      window.markdown = () => { throw new Error("Saved document used the message renderer"); };
      window.fixtureText = "# Документы проекта\n\n## Обзор\n\nСвязанные материалы: [Методика](./Методика.md).\n\n## Обзор\n\nДругая версия раздела.\n";
      window.fixtureAnalysis = {
        profile: "markdig-fixture", characterCount: fixtureText.length,
        html: '<h1 id="documents">Документы проекта</h1><h2 id="overview">Обзор</h2><p>Связанные материалы: <a href="./Методика.md">Методика</a>.</p><h2 id="overview-1">Обзор</h2><p>Другая версия раздела.</p><script>window.badScript=true</script><a href="javascript:window.badScript=true" onclick="window.badScript=true">Опасная ссылка</a>',
        sections: [
          { id: "s1", parentId: null, title: "Документы проекта", level: 1, anchor: "documents", start: 0, end: fixtureText.length, headingEnd: 20, line: 1 },
          { id: "s2", parentId: "s1", title: "Обзор", level: 2, anchor: "overview", start: 22, end: 76, headingEnd: 30, line: 3 },
          { id: "s3", parentId: "s1", title: "Обзор", level: 2, anchor: "overview-1", start: 77, end: fixtureText.length, headingEnd: 86, line: 7 }
        ],
        links: [{ destination: "./Методика.md", text: "Методика", start: 52, end: 75, line: 5, isImage: false }]
      };
      window.presentation = {};
      window.copied = [];
      window.analysisCalls = 0;
      window.mount = options => RNAssistantViewerRegistry.mount("markdown", document.getElementById("host"), Object.assign({
        text: fixtureText, fullText: fixtureText, complete: true, sourceComplete: true, requireAnalysis: true,
        presentation: presentation, onCopy: text => { copied.push(text); },
        onAnalyze: () => { analysisCalls++; return Promise.resolve(fixtureAnalysis); }
      }, options));
      mount();
    });
    await page.locator(".rn-markdown-viewer-rendered h1").waitFor();
    const fullHeight = await page.evaluate(() => ({
      viewer: document.querySelector(".rn-markdown-viewer").getBoundingClientRect().height,
      layout: document.querySelector(".rn-markdown-viewer-layout").getBoundingClientRect().height,
      host: document.getElementById("host").clientHeight
    }));
    assert.ok(fullHeight.viewer >= fullHeight.host - 34, "viewer fills the available content height");
    assert.ok(fullHeight.layout >= 500, "rendered content and outline share the available height");
    assert.equal(await page.locator(".rn-markdown-viewer-retry").isVisible(), false,
      "successful analysis keeps the retry action out of the toolbar");
    const renderedStyle = await page.locator(".rn-markdown-viewer-rendered").evaluate(node => {
      const style = getComputedStyle(node); return { border: style.borderTopWidth, padding: style.paddingTop, background: style.backgroundColor };
    });
    assert.equal(renderedStyle.border, "0px", "document body has no card frame");
    assert.ok(parseFloat(renderedStyle.padding) >= 12, "document body keeps page padding");
    assert.equal(renderedStyle.background, "rgba(0, 0, 0, 0)", "document body uses the viewer surface");
    assert.equal(await page.locator(".rn-markdown-viewer-rendered script").count(), 0);
    assert.equal(await page.locator(".rn-markdown-viewer-rendered [onclick], .rn-markdown-viewer-rendered [href^='javascript:']").count(), 0);
    assert.equal(await page.getByRole("button", { name: "HTML", exact: true }).count(), 0,
      "generated HTML is not a user-facing document mode");
    assert.equal(await page.evaluate(() => !!window.badScript), false);
    assert.equal(await page.locator("[data-tree-key='section:s1'] [data-tree-key^='section:']").count(), 2);
    assert.equal(await page.evaluate(() => analysisCalls), 1);
    await page.getByRole("button", { name: "Исходник Markdown", exact: true }).click();
    assert.equal(await page.locator(".rn-text-viewer-content").textContent(), await page.evaluate(() => fixtureText));
    assert.equal(await page.locator(".rn-text-viewer-source").evaluate(node => getComputedStyle(node).borderTopWidth), "0px",
      "source mode is edge-to-edge without its own frame");
    assert.equal(await page.evaluate(() => !!window.badScript), false);
    await page.getByRole("button", { name: "Копировать содержимое", exact: true }).click();
    assert.equal(await page.evaluate(() => copied.at(-1)), await page.evaluate(() => fixtureText));
    await page.getByRole("button", { name: "Просмотр Markdown", exact: true }).click();
    await page.locator("[data-tree-select='section:s3']").click();
    assert.equal(await page.locator("#overview-1").count(), 1);
    await page.getByRole("button", { name: "Граф связей Markdown", exact: true }).click();
    await page.locator(".rna-md-graph-canvas canvas").first().waitFor();
    assert.equal(await page.locator(".rna-md-graph").count(), 1, "Shared Markdown view mounts vis.Network");
    await page.getByRole("button", { name: "Иерархия", exact: true }).click();
    assert.equal(await page.locator(".rna-md-graph-canvas canvas").count(), 1,
      "Layout switching retains the interactive canvas");
    await page.getByRole("button", { name: "Просмотр Markdown", exact: true }).click();
    await page.locator(".rn-markdown-viewer-rendered a[role='link']").first().click();
    assert.match(await page.locator(".rn-markdown-viewer-note").textContent(), /Методика\.md/);
    assert.equal(page.url(), "about:blank", "Relative links must not navigate the host surface");
    assert.equal(await page.evaluate(() => analysisCalls), 1, "Mode changes must reuse the same analysis");
    console.log("PASS Markdown: shared analysis, duplicate sections, inert HTML, exact source and safe links");

    const screenshots = process.env.RNASSISTANT_MARKDOWN_SCREENSHOTS;
    if (screenshots) {
      fs.mkdirSync(screenshots, { recursive: true });
      await page.screenshot({ path: path.join(screenshots, "markdown-wide.png") });
    }
    await page.setViewportSize({ width: 420, height: 760 });
    assert.equal(await page.locator(".rn-markdown-viewer-navigation").evaluate(node => getComputedStyle(node).position), "absolute");
    await page.locator(".rn-markdown-viewer-navigation").waitFor({ state: "hidden" });
    assert.equal(await page.locator(".rn-markdown-viewer-navigation").isVisible(), false,
      "entering narrow layout collapses the outline before it covers the document");
    await page.getByRole("button", { name: "Оглавление и ссылки", exact: true }).click();
    assert.equal(await page.locator(".rn-markdown-viewer-navigation").isVisible(), true);
    await page.locator("[data-tree-select='section:s2']").click();
    assert.equal(await page.locator(".rn-markdown-viewer-navigation").isVisible(), false, "Selecting a heading closes the narrow overlay");
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);
    await page.getByRole("button", { name: "Оглавление и ссылки", exact: true }).click();
    assert.equal(await page.locator(".rn-markdown-viewer-navigation").isVisible(), true);
    await page.getByRole("button", { name: "Оглавление и ссылки", exact: true }).click();
    if (screenshots) await page.screenshot({ path: path.join(screenshots, "markdown-narrow.png") });
    console.log("PASS Markdown: narrow overlay, navigation dismissal and no horizontal overflow");

    await page.evaluate(() => {
      RNAssistantViewerRegistry.unmount(document.getElementById("host"));
      window.partialCalls = 0;
      mount({ fullText: null, text: "# Fragment", complete: false, sourceComplete: false,
        onAnalyze: () => { partialCalls++; return fixtureAnalysis; } });
    });
    assert.equal(await page.getByRole("button", { name: "Просмотр Markdown", exact: true }).isDisabled(), true);
    assert.equal(await page.locator(".rn-markdown-viewer-rendered").count(), 0);
    assert.equal(await page.evaluate(() => partialCalls), 0);
    await page.evaluate(() => {
      const host = document.getElementById("host");
      host.classList.add("artifact-detail-preview", "is-plan-preview");
      mount({ presentation: {} });
    });
    assert.equal(await page.locator(".rn-markdown-viewer-tabs").isVisible(), false,
      "Plan preview keeps its existing single-document chrome");
    assert.equal(await page.locator(".rn-markdown-viewer-rendered").evaluate(node => getComputedStyle(node).borderTopWidth), "0px",
      "Plan preview shares the frameless document surface");
    await page.evaluate(() => {
      document.getElementById("host").classList.remove("artifact-detail-preview", "is-plan-preview");
      RNAssistantViewerRegistry.unmount(document.getElementById("host"));
    });
    await page.setViewportSize({ width: 1120, height: 760 });
    await page.evaluate(() => {
      const host = document.getElementById("host"); RNAssistantViewerRegistry.unmount(host); host.replaceChildren();
      window.materialShell = RNAssistantMaterialSurface.create({ kind: "markdown", title: "markdown-demo.md",
        meta: "MockDocument.docx · New chat · Версия 1", statusMeta: "Markdown · 1 КБ · v1" });
      window.materialActions = document.createElement("div"); materialActions.className = "resource-editor-actions";
      const menu = document.createElement("button"); menu.className = "icon-btn compact"; menu.textContent = "⋯"; menu.setAttribute("aria-label", "Действия с материалом");
      materialActions.appendChild(menu); materialShell.setCommonActions(materialActions);
      host.appendChild(materialShell.element); materialShell.content.__rnMaterialSurface = materialShell;
      RNAssistantViewerRegistry.mount("markdown", materialShell.content, {
        text: fixtureText, fullText: fixtureText, complete: true, sourceComplete: true, requireAnalysis: true,
        presentation: {}, onAnalyze: () => Promise.resolve(fixtureAnalysis), onCopy: text => { copied.push(text); }
      });
    });
    await page.locator(".rn-markdown-viewer-rendered h1").waitFor();
    assert.equal(await page.locator(".rn-markdown-viewer-toolbar").count(), 1,
      "Material Markdown uses one shared top row");
    assert.equal(await page.locator(".material-surface-title .resource-editor-title").textContent(), "markdown-demo.md");
    assert.equal(await page.getByRole("button", { name: "Действия с материалом", exact: true }).count(), 1,
      "Material actions move into the same shared top row");
    assert.equal(await page.getByRole("button", { name: "Граф связей Markdown", exact: true }).count(), 1);
    await page.getByRole("button", { name: "Граф связей Markdown", exact: true }).click();
    await page.locator(".material-surface-statusbar").getByText(/Узлы 2 · Связи 1/).waitFor();
    assert.equal(await page.locator(".rna-md-graph [role='status']").count(), 0, "only the shared Material status bar shows graph counts");
    assert.match(await page.getByRole("button", { name: "Иерархия", exact: true }).getAttribute("class"), /\bicon-btn\b/);
    await page.getByRole("button", { name: "Просмотр Markdown", exact: true }).click();
    const treeSpacing = await page.evaluate(() => ({
      sidebar: parseFloat(getComputedStyle(document.querySelector(".material-surface-sidebar")).paddingTop),
      navigation: parseFloat(getComputedStyle(document.querySelector(".material-surface-sidebar .rn-markdown-viewer-navigation")).paddingTop),
      nested: parseFloat(getComputedStyle(document.querySelector(".material-surface-sidebar .navigation-tree-list .navigation-tree-list")).marginLeft)
    }));
    assert.equal(treeSpacing.sidebar, 0, "Material sidebar does not add a second outer inset");
    assert.ok(treeSpacing.navigation <= 4 && treeSpacing.nested <= 8, "tree indentation uses compact shared spacing: " + JSON.stringify(treeSpacing));
    assert.match(await page.locator(".material-surface-statusbar").textContent(), /заголов|ссылок|символов/);
    await page.getByRole("button", { name: "Найти в материале", exact: true }).click();
    assert.equal(await page.locator(".material-surface-search").evaluate(node => getComputedStyle(node).position), "absolute",
      "common search expands over the header instead of squeezing document controls");
    await page.locator(".material-surface-search-input").fill("Обзор");
    await page.waitForFunction(() => document.querySelectorAll(".rn-markdown-search-match").length >= 2);
    assert.match(await page.locator(".material-surface-search-status").textContent(), /из/);
    assert.equal(await page.locator(".material-surface-sidebar .navigation-tree").count(), 1,
      "outline and text search share the common Material search");
    await page.evaluate(() => { RNAssistantViewerRegistry.unmount(materialShell.content); materialShell.destroy(); });
    await page.evaluate(() => {
      const host = document.getElementById("host"); host.replaceChildren();
      window.toggleShell = RNAssistantMaterialSurface.create({ title: "Sidebar fixture" });
      const navigation = document.createElement("nav"); navigation.textContent = "Navigation";
      toggleShell.setNavigation({ element: navigation, available: true }); host.appendChild(toggleShell.element);
    });
    assert.equal(await page.locator(".material-surface-navigation-toggle").isVisible(), true,
      "shared Shell-style sidebar toggle is available");
    await page.locator(".material-surface-navigation-toggle").click();
    assert.equal(await page.locator(".material-surface-sidebar").isVisible(), false,
      "the shared Shell sidebar toggle collapses Material navigation");
    await page.evaluate(() => { toggleShell.destroy(); document.getElementById("host").replaceChildren(); });
    await page.evaluate(() => {
      const host = document.getElementById("host");
      window.textShell = RNAssistantMaterialSurface.create({ title: "notes.txt", statusMeta: "TEXT · 7 Б" });
      host.appendChild(textShell.element); textShell.content.__rnMaterialSurface = textShell;
      const text = "first\nsecond";
      RNAssistantViewerRegistry.mount("text", textShell.content, {
        text, fullText: text, complete: true, sourceComplete: true, onCopy: value => { copied.push(value); }
      });
    });
    assert.match(await page.locator(".material-surface-statusbar").textContent(), /2 строк/);
    assert.equal(await page.locator(".material-surface-search .product-nav-search-toggle").isDisabled(), false,
      "the same search affordance is available to plain text");
    assert.equal(await page.locator(".material-surface-actions").getByRole("button", { name: "Копировать всё", exact: true }).count(), 1);
    await page.evaluate(() => { RNAssistantViewerRegistry.unmount(textShell.content); textShell.destroy(); document.getElementById("host").replaceChildren(); });
    await page.evaluate(() => {
      mount({ presentation: {}, onAnalyze: () => new Promise(resolve => { window.finishLate = resolve; }) });
    });
    await page.waitForFunction(() => !!window.finishLate);
    await page.getByRole("button", { name: "Исходник Markdown", exact: true }).click();
    await page.evaluate(() => finishLate(fixtureAnalysis));
    await page.waitForFunction(() => document.querySelector(".rn-markdown-viewer-note-text").textContent === "");
    assert.equal(await page.locator(".rn-markdown-viewer-rendered").count(), 0, "Late analysis must preserve the user's source choice");
    await page.evaluate(() => {
      mount({ presentation: {}, onAnalyze: () => new Promise(resolve => { window.finishDisposed = resolve; }) });
    });
    await page.waitForFunction(() => !!window.finishDisposed);
    await page.evaluate(() => {
      RNAssistantViewerRegistry.unmount(document.getElementById("host"));
      finishDisposed(fixtureAnalysis);
    });
    await page.waitForTimeout(20);
    assert.equal(await page.locator("#host").textContent(), "");
    await page.evaluate(() => mount({ presentation: {}, onAnalyze: () => Promise.reject(new Error("Разбор недоступен")) }));
    const analysisRetry = page.getByRole("button", { name: "Повторить анализ", exact: true });
    await analysisRetry.waitFor();
    assert.equal(await analysisRetry.evaluate(node => node.closest(".rn-markdown-viewer-note") !== null), true,
      "analysis retry stays with its explanatory status instead of the common toolbar");
    assert.equal(await page.locator(".rn-markdown-viewer-rendered").count(), 0);
    assert.equal(await page.locator(".rn-text-viewer-content").textContent(), await page.evaluate(() => fixtureText));
    console.log("PASS Markdown: partial documents, late source choice, disposal and explicit analysis error");

    const result = await page.evaluate(async () => {
      const uri = "rna://test/markdown/revision/1", state = { activeChatId: "chat", artifactViewerPages: {} };
      const viewer = { status: "ready", complete: true, fullText: fixtureText };
      state.artifactViewerPages[uri] = viewer;
      let calls = 0, resolve;
      const actions = RNAssistantArtifactViewerActions.create({ state, send: () => {
        calls++; return new Promise(done => { resolve = done; });
      } });
      const first = actions.analyzeMarkdown(fixtureText, { resourceUri: uri });
      const second = actions.analyzeMarkdown(fixtureText, { resourceUri: uri });
      await Promise.resolve();
      const pending = actions.hasPending(); resolve(fixtureAnalysis); await Promise.all([first, second]);
      await actions.analyzeMarkdown(fixtureText, { resourceUri: uri });
      const cachedCalls = calls;
      let mismatch = false;
      try { await actions.analyzeMarkdown("wrong", { resourceUri: uri }); } catch (_) { mismatch = true; }
      delete viewer.markdownAnalysis;
      const late = actions.analyzeMarkdown(fixtureText, { resourceUri: uri });
      await Promise.resolve(); actions.closeAll(); resolve(fixtureAnalysis);
      let stale = false; try { await late; } catch (_) { stale = true; }
      return { pending, cachedCalls, mismatch, stale, cacheCount: Object.keys(state.artifactViewerPages).length };
    });
    assert.deepEqual(result, { pending: true, cachedCalls: 1, mismatch: true, stale: true, cacheCount: 0 });
    assert.deepEqual(errors, []);
    console.log("PASS Markdown: exact-source cache, coalesced analysis and closed-owner rejection");
  } finally { await browser.close(); }
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
