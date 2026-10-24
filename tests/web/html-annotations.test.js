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
    const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto(address + "preview/web/?scenario=ready");
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    await page.evaluate(() => {
      const html = '<main style="padding:40px;min-height:1400px"><h1 id="target">Карточка заказа</h1>' +
        '<button id="action">Обычный клик</button><output id="count">0</output></main>' +
        '<script>document.getElementById("action").onclick=()=>{const n=document.getElementById("count");n.textContent=String(Number(n.textContent)+1)}<\/script>';
      state.activeHtmlArtifactId = "html-r1";
      state.artifacts = [{ Id: "html-r1", Kind: "html_workspace", Title: "Карточка", Revision: 1 }];
      state.htmlWorkspace = { revisionArtifactId: "html-r1", activeFileId: "file1",
        files: [{ id: "file1", path: "index.html", kind: "html", content: html,
          source: { uri: "rna://chat/preview-chat/html/file1", revision: "r1" },
          sourceReadKey: "rna://chat/preview-chat/html/file1@r1", sha256: "a".repeat(64),
          byteLength: html.length, characters: html.length }],
        dataSources: [], history: [], redoHistory: [], redoBranches: [],
        recovery: { status: "empty", canMutate: true, candidates: [] } };
      state.htmlWorkspaceSelection = { type: "file", id: "file1" };
      state.htmlWorkspaceMode = "preview";
      window.annotationCalls = [];
      const originalSend = window.send;
      window.send = function (type, payload) {
        if (type === "stageHtmlVisualSelection") {
          annotationCalls.push({ type, payload });
          const id = "annotation-" + annotationCalls.filter(call => call.type === type).length;
          return Promise.resolve({ resource: { Id: id, FileName: "html-selection-h1.json",
            Kind: "file", Size: 180, Status: "ready" } });
        }
        if (type === "discardChatResourceDraft") {
          annotationCalls.push({ type, payload });
          return Promise.resolve({});
        }
        return originalSend(type, payload);
      };
    });
    assert.equal(await page.evaluate(() => RNAssistantChatBrowser.openCurrent()), true);
    const frame = page.frameLocator("#chatBrowser #htmlWorkspacePreviewFrame");
    await frame.locator("#target").waitFor();
    await page.waitForFunction(() => !document.getElementById("chatLayout").classList.contains("browser-transition"));
    await page.locator("#inspectHtmlWorkspaceButton").click();
    await page.waitForFunction(() => document.getElementById("inspectHtmlWorkspaceButton").getAttribute("aria-pressed") === "true");
    await frame.locator("#target").click();
    await page.locator(".visual-annotation-editor textarea").waitFor({ timeout: 5000 });
    await page.locator(".visual-annotation-editor textarea").fill("Сделай заголовок зелёным");
    if (process.env.RNA_ANNOTATION_EDITOR_SCREENSHOT) await page.screenshot({
      path: process.env.RNA_ANNOTATION_EDITOR_SCREENSHOT, animations: "disabled" });
    await page.locator(".visual-annotation-editor .primary").click();
    await page.locator(".visual-annotation-list > summary").waitFor();
    await page.locator(".visual-annotation-marker").waitFor();
    assert.equal(await page.locator(".visual-annotation-marker").textContent(), "1");
    assert.equal(await page.evaluate(() => state.draftAttachments.length), 1);
    if (process.env.RNA_ANNOTATION_LIST_SCREENSHOT) await page.screenshot({
      path: process.env.RNA_ANNOTATION_LIST_SCREENSHOT, animations: "disabled" });
    await page.locator(".visual-annotation-list > summary").click();
    if (process.env.RNA_ANNOTATION_EXPANDED_SCREENSHOT) await page.screenshot({
      path: process.env.RNA_ANNOTATION_EXPANDED_SCREENSHOT, animations: "disabled" });
    await page.locator(".visual-annotation-list > summary").click();
    const beforeScroll = await page.locator(".visual-annotation-marker").evaluate(node => node.getBoundingClientRect().top);
    await frame.locator("body").evaluate(node => node.ownerDocument.defaultView.scrollTo(0, 70));
    await page.waitForFunction(top => {
      const marker = document.querySelector(".visual-annotation-marker");
      return marker && marker.getBoundingClientRect().top < top - 30;
    }, beforeScroll);
    await frame.locator("body").evaluate(node => node.ownerDocument.defaultView.scrollTo(0, 700));
    await page.waitForFunction(() => document.querySelectorAll(".visual-annotation-marker").length === 0);
    await page.locator(".visual-annotation-list > summary").click();
    await page.locator(".visual-annotation-list-row button").first().click();
    await page.locator(".visual-annotation-marker").waitFor();
    assert.ok(await frame.locator("body").evaluate(node => node.ownerDocument.defaultView.scrollY) < 700,
      "Show returns to the exact watched element");
    await page.locator(".visual-annotation-list > summary").click();
    await page.locator("#inspectHtmlWorkspaceButton").click();
    await frame.locator("#action").click();
    assert.equal(await frame.locator("#count").textContent(), "1", "HTML is interactive after mode off");
    await page.locator(".visual-annotation-marker").click();
    await page.locator(".visual-annotation-editor textarea").fill("Сделай заголовок синим");
    await page.locator(".visual-annotation-editor .primary").click();
    await page.waitForFunction(() => state.draftAttachments[0] && state.draftAttachments[0].Id === "annotation-2");
    assert.equal(await page.evaluate(() => annotationCalls.filter(call => call.type === "stageHtmlVisualSelection").length), 2);
    assert.equal(await page.evaluate(() => annotationCalls.some(call => call.type === "discardChatResourceDraft" &&
      call.payload.id === "annotation-1")), true, "replaced CAS draft is discarded after persistence");
    await page.locator(".visual-annotation-list > summary").click();
    assert.match(await page.locator(".visual-annotation-list-body").textContent(), /Сделай заголовок синим/);
    await page.locator(".visual-annotation-list-row button").last().click();
    await page.waitForFunction(() => state.draftAttachments.length === 0);
    assert.equal(await page.locator(".visual-annotation-marker").count(), 0);
    assert.equal(await page.locator(".visual-annotation-list").count(), 0);
    assert.deepEqual(errors, []);
    console.log("PASS HTML annotations: editor, CAS draft, marker, scroll/reveal, mode off, edit and removal");
  } finally {
    await browser.close(); server.kill();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
