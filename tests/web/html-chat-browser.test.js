"use strict";
const assert = require("node:assert/strict");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");
const path = require("node:path");

(async function () {
  const root = path.resolve(__dirname, "../..");
  const server = spawn("python3", ["-B", "prototypes/serve_ui.py", "--port", "0"], { cwd: root });
  const address = await new Promise((resolve, reject) => {
    server.stdout.on("data", chunk => {
      const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(chunk));
      if (match) resolve(match[1]);
    });
    server.once("exit", code => reject(Error("Preview server exited: " + code)));
  });
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1360, height: 900 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto(address + "preview/web/?scenario=ready");
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    await page.locator("#chatInput").fill("Черновик рядом с HTML");
    const formIdentity = await page.evaluate(() => {
      const html = '<main><h1 id="target">Проверка HTML</h1><button>Обычный клик</button></main>';
      const file = { id: "file1", path: "index.html", kind: "html", content: html,
        source: { uri: "rna://chat/preview-chat/html/file1", revision: "r1" },
        sourceReadKey: "rna://chat/preview-chat/html/file1@r1",
        sha256: "a".repeat(64), byteLength: html.length, characters: html.length };
      state.activeHtmlArtifactId = "html-r1";
      state.htmlWorkspace = { revisionArtifactId: "html-r1", activeFileId: "file1", files: [file],
        dataSources: [], history: [], redoHistory: [], redoBranches: [],
        recovery: { status: "empty", canMutate: true, candidates: [] } };
      state.htmlWorkspaceSelection = { type: "file", id: "file1" };
      state.htmlWorkspaceMode = "preview";
      document.getElementById("chatForm").dataset.identity = "one-composer";
      return document.getElementById("chatForm").dataset.identity;
    });
    assert.equal(await page.evaluate(() => RNAssistantChatBrowser.openCurrent()), true);
    await page.frameLocator("#chatBrowser #htmlWorkspacePreviewFrame")
      .getByRole("heading", { name: "Проверка HTML" }).waitFor();
    assert.deepEqual(await page.evaluate(() => [
      document.querySelectorAll("#htmlWorkspacePreviewFrame").length,
      document.getElementById("htmlWorkspacePreview").parentElement.id,
      document.getElementById("inspectHtmlWorkspaceButton").parentElement.className,
      document.getElementById("chatForm").dataset.identity,
      document.getElementById("chatInput").value
    ]), [1, "chatBrowserFrameMount", "preview-surface-bar", formIdentity, "Черновик рядом с HTML"],
    "right browser reuses the frame and central composer");
    assert.equal(await page.locator("#chatBrowser .preview-surface-title").textContent(), "HTML");
    assert.equal(await page.locator("#chatBrowser .preview-surface-path").textContent(), "index.html");
    assert.equal(await page.locator("#inspectHtmlWorkspaceButton").textContent(), "Аннотировать");
    assert.equal(await page.locator("#attachHtmlWorkspaceSelectionButton").isVisible(), false,
      "chat attachment action appears only after an element is selected");
    if (process.env.RNA_HTML_BROWSER_SCREENSHOT) await page.screenshot({
      path: process.env.RNA_HTML_BROWSER_SCREENSHOT, animations: "disabled" });
    await page.locator("#inspectHtmlWorkspaceButton").click();
    await page.waitForFunction(() => document.getElementById("inspectHtmlWorkspaceButton").getAttribute("aria-pressed") === "true");
    await page.frameLocator("#chatBrowser #htmlWorkspacePreviewFrame").locator("#target").click();
    await page.locator(".visual-annotation-editor textarea").waitFor();
    assert.equal(await page.locator("#attachHtmlWorkspaceSelectionButton").isVisible(), false,
      "the right browser uses the inline editor instead of a second attach action");
    if (process.env.RNA_HTML_BROWSER_SELECTED_SCREENSHOT) await page.screenshot({
      path: process.env.RNA_HTML_BROWSER_SELECTED_SCREENSHOT, animations: "disabled" });
    await page.locator(".visual-annotation-editor-actions button").first().click();
    await page.locator("#inspectHtmlWorkspaceButton").click();
    await page.evaluate(() => {
      const frame = document.getElementById("htmlWorkspacePreviewFrame");
      window.frameBeforeHide = frame.contentWindow;
      window.loadAfterOpen = 0;
      frame.addEventListener("load", () => { window.loadAfterOpen++; });
    });
    await page.locator("#chatBrowserHide").click();
    assert.equal(await page.locator("#chatBrowserToggle").getAttribute("aria-expanded"), "false");
    await page.locator("#chatBrowserToggle").click();
    assert.deepEqual(await page.evaluate(() => [
      document.getElementById("htmlWorkspacePreviewFrame").contentWindow === window.frameBeforeHide,
      window.loadAfterOpen, document.getElementById("chatInput").value
    ]), [true, 0, "Черновик рядом с HTML"], "hide/show keeps the live frame and draft");
    await page.locator("#toggleChatInspectorButton").click();
    assert.equal(await page.locator("#chatBrowserToggle").getAttribute("aria-expanded"), "false",
      "diagnostics takes the same right slot");
    await page.locator("#chatBrowserToggle").click();
    assert.equal(await page.locator("#toggleChatInspectorButton").getAttribute("aria-expanded"), "false",
      "returning to the browser closes diagnostics");
    const splitter = page.locator("#chatBrowserSplitter");
    const before = Number(await splitter.getAttribute("aria-valuenow"));
    await splitter.press("ArrowLeft");
    assert.equal(Number(await splitter.getAttribute("aria-valuenow")), before + 12,
      "shared splitter resizes the browser");
    await page.setViewportSize({ width: 680, height: 800 });
    await page.waitForFunction(() => document.getElementById("chatLayout").classList.contains("browser-overlay"));
    assert.ok((await page.locator("#chatBrowser").boundingBox()).width >= 500,
      "narrow browser uses the available work area, not an empty grid track");
    await page.setViewportSize({ width: 1360, height: 900 });
    await page.locator('.section-rail [data-tab="artifacts"]').click();
    assert.deepEqual(await page.evaluate(() => [
      document.getElementById("htmlWorkspacePreview").parentElement.id,
      document.getElementById("inspectHtmlWorkspaceButton").parentElement.className,
      document.getElementById("chatBrowserToggle").hidden
    ]), ["htmlWorkspaceEditor", "html-workspace-toolbar", true],
    "Materials gets the same frame and controls back");
    await page.evaluate(() => {
      document.getElementById("artifactCatalogOverview").hidden = true;
      document.getElementById("artifactCatalogPreview").hidden = true;
      document.getElementById("htmlWorkspaceEditor").hidden = false;
    });
    await page.locator("#openHtmlWorkspaceRightButton").click();
    assert.equal(await page.locator("#chatBrowserToggle").getAttribute("aria-expanded"), "true",
      "Materials opens the exact current HTML on the right");
    await page.locator('.section-rail [data-tab="artifacts"]').click();
    await page.evaluate(() => {
      switchTab("chat");
      state.artifacts.push({ Id: "html-r1", Kind: "html_workspace", Title: "Проверка HTML" });
      appendMessageArtifactCards(document.getElementById("messages"), { ArtifactIds: ["html-r1"] });
      document.querySelector("#messages .chat-artifact-card.kind-html_workspace").id = "browserArtifactCard";
    });
    await page.locator("#browserArtifactCard").click();
    assert.equal(await page.locator("#chatBrowserToggle").getAttribute("aria-expanded"), "true",
      "current HTML chat card opens the right browser");
    await page.locator("#chatBrowserMenu > summary").click();
    await page.locator("#chatBrowserClose").click();
    assert.equal(await page.locator("#chatBrowserToggle").isVisible(), false,
      "explicit close releases the current browser route");
    assert.equal(await page.evaluate(() => RNAssistantChatBrowser.openCurrent()), true);
    await page.evaluate(() => { state.activeHtmlArtifactId = "html-r2"; renderHtmlWorkspace(); });
    assert.deepEqual(await page.evaluate(() => [
      document.getElementById("htmlWorkspacePreview").parentElement.id,
      document.getElementById("chatBrowserToggle").hidden,
      document.getElementById("chatInput").value
    ]), ["htmlWorkspaceEditor", true, "Черновик рядом с HTML"],
    "a different revision revokes the browser without touching the chat draft");
    await page.evaluate(() => {
      state.activeHtmlArtifactId = "html-r1";
      RNAssistantChatBrowser.openCurrent();
      state.activeChatId = "another-chat";
      renderHtmlWorkspace();
    });
    assert.equal(await page.evaluate(() => document.getElementById("htmlWorkspacePreview").parentElement.id),
      "htmlWorkspaceEditor", "a different chat cannot retain the old HTML frame binding");
    assert.deepEqual(errors, []);
    console.log("PASS HTML chat browser: one frame, draft, hide/show, right-slot handoff, resize, return and owner guard");
  } finally { await browser.close(); server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
