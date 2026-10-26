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
    const fixtureHtml = '<main style="padding:40px;min-height:1400px"><h1 id="target" style="position:relative;width:260px;height:68px">Карточка заказа' +
      '<button id="next" style="position:absolute;right:-13px;top:-13px;width:30px;height:30px">+</button></h1>' +
      '<button id="action">Обычный клик</button><output id="count">0</output></main>' +
      '<script>document.getElementById("action").onclick=()=>{const n=document.getElementById("count");n.textContent=String(Number(n.textContent)+1)}<\/script>';
    const seedWorkspace = () => page.evaluate(html => {
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
    }, fixtureHtml);
    await seedWorkspace();
    await page.evaluate(() => { switchTab("artifacts"); RNAssistantArtifactCatalog.editor(); });
    await page.frameLocator("#tab-artifacts #htmlWorkspacePreviewFrame").locator("#target").waitFor();
    await page.waitForFunction(() => !document.getElementById("inspectHtmlWorkspaceButton").disabled);
    assert.equal(await page.locator("#inspectHtmlWorkspaceButton").textContent(), "Аннотировать",
      "Materials exposes the same annotation control");
    await page.locator("#inspectHtmlWorkspaceButton").click();
    await page.frameLocator("#tab-artifacts #htmlWorkspacePreviewFrame").locator("#target").click({ position: { x: 20, y: 28 } });
    await page.locator("#htmlWorkspacePreview .visual-annotation-editor textarea").waitFor();
    await page.locator("#htmlWorkspacePreview .visual-annotation-editor button").first().click();
    await page.locator("#inspectHtmlWorkspaceButton").click();
    await page.evaluate(() => switchTab("chat"));
    assert.equal(await page.evaluate(() => RNAssistantChatBrowser.openCurrent()), true);
    const frame = page.frameLocator("#chatBrowser #htmlWorkspacePreviewFrame");
    await frame.locator("#target").waitFor();
    await page.waitForFunction(() => !document.getElementById("inspectHtmlWorkspaceButton").disabled);
    await page.waitForFunction(() => !document.getElementById("chatLayout").classList.contains("browser-transition"));
    const offToggle = await page.locator("#inspectHtmlWorkspaceButton").evaluate(node => getComputedStyle(node).backgroundColor);
    await page.locator("#inspectHtmlWorkspaceButton").click();
    await page.waitForFunction(() => document.getElementById("inspectHtmlWorkspaceButton").getAttribute("aria-pressed") === "true");
    const onToggle = await page.locator("#inspectHtmlWorkspaceButton").evaluate(node => getComputedStyle(node).backgroundColor);
    assert.notEqual(onToggle, offToggle, "annotation toggle has a distinct selected fill");
    await frame.locator("#target").hover({ position: { x: 5, y: 5 } });
    assert.match(await frame.locator("[data-rna-visual-tooltip]").textContent(), /h1#target\s+\d+ × \d+/);
    if (process.env.RNA_ANNOTATION_HOVER_SCREENSHOT) await page.screenshot({
      path: process.env.RNA_ANNOTATION_HOVER_SCREENSHOT, animations: "disabled" });
    await frame.locator("#target").click();
    await page.locator(".visual-annotation-editor textarea").waitFor({ timeout: 5000 });
    await page.locator(".visual-annotation-editor textarea").fill("Сделай заголовок зелёным");
    if (process.env.RNA_ANNOTATION_EDITOR_SCREENSHOT) await page.screenshot({
      path: process.env.RNA_ANNOTATION_EDITOR_SCREENSHOT, animations: "disabled" });
    await page.locator(".visual-annotation-editor .primary").click();
    await page.locator(".visual-annotation-list > summary").waitFor();
    await page.locator(".visual-annotation-marker").waitFor();
    assert.equal(await page.locator(".visual-annotation-marker").textContent(),
      await page.evaluate(() => String(state.draftAttachments[0].visualAnnotation.annotationNumber)));
    const markerSize = await page.locator(".visual-annotation-marker").evaluate(node => {
      const bounds = node.getBoundingClientRect(); return [bounds.width, bounds.height];
    });
    assert.ok(Math.abs(markerSize[0] - markerSize[1]) < 1, "page number stays circular");
    const badgeSize = await page.locator(".visual-annotation-list-row .visual-annotation-number").evaluate(node => {
      const bounds = node.getBoundingClientRect(); return [bounds.width, bounds.height];
    });
    assert.ok(Math.abs(badgeSize[0] - badgeSize[1]) < 1, "composer number stays circular");
    assert.equal(await page.evaluate(() => state.draftAttachments.length), 1);
    await page.locator(".visual-annotation-marker").click();
    await page.locator(".visual-annotation-editor textarea").waitFor();
    assert.equal(await page.locator(".visual-annotation-editor textarea").inputValue(), "Сделай заголовок зелёным",
      "the page number reopens its annotation while selection mode is active");
    await page.locator(".visual-annotation-editor button").first().click();
    await frame.locator("#next").click();
    await page.locator(".visual-annotation-editor textarea").waitFor();
    assert.match(await page.locator(".visual-annotation-editor .visual-annotation-target").textContent(), /button/,
      "a nearby element can be selected immediately after the first annotation");
    await page.locator(".visual-annotation-editor button").first().click();
    assert.equal(await page.evaluate(() => state.draftAttachments.length), 1,
      "canceling the second selection keeps the first annotation");
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
    assert.equal(await page.locator(".visual-annotation-marker").count(), 0,
      "switching annotation mode off clears numbers from the HTML");
    await frame.locator("#action").click();
    assert.equal(await frame.locator("#count").textContent(), "1", "HTML is interactive after mode off");
    await page.locator(".visual-annotation-list > summary").click();
    await page.locator(".visual-annotation-list-row button").nth(1).click();
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
    await page.locator("#inspectHtmlWorkspaceButton").click();
    await page.waitForFunction(() => document.getElementById("inspectHtmlWorkspaceButton").getAttribute("aria-pressed") === "true");
    await frame.locator("#target").click();
    await page.locator(".visual-annotation-editor textarea").fill("Комментарий после перезапуска");
    await page.locator(".visual-annotation-editor textarea").press("Escape");
    assert.equal(await page.locator(".visual-annotation-editor").count(), 0);
    assert.equal(await page.locator("#inspectHtmlWorkspaceButton").getAttribute("aria-pressed"), "true",
      "first Escape collapses the editor and keeps selection mode");
    await page.locator("#inspectHtmlWorkspaceButton").press("Escape");
    assert.equal(await page.locator("#inspectHtmlWorkspaceButton").getAttribute("aria-pressed"), "false",
      "second Escape exits selection mode");
    assert.equal(await page.locator(".visual-annotation-marker").count(), 0,
      "unfinished annotation stays in the draft but no longer covers HTML outside selection mode");
    await page.evaluate(() => RNAssistantChatDrafts.flushOffice(state.activeChatId));
    assert.equal(await page.evaluate(() => RNAssistantChatDrafts.readOffice(state.activeChatId).pendingVisualAnnotation.Comment),
      "Комментарий после перезапуска");
    await page.reload();
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    assert.equal(await page.evaluate(() => RNAssistantChatDrafts.readOffice(state.activeChatId).pendingVisualAnnotation.Comment),
      "Комментарий после перезапуска", "unfinished annotation survives WebView recreation");
    await page.locator("#chatInput").fill("Сообщение с аннотацией");
    assert.equal(await page.locator("#sendButton").isDisabled(), true,
      "an unfinished annotation cannot be silently omitted from the message");
    await seedWorkspace();
    assert.equal(await page.evaluate(() => RNAssistantChatBrowser.openCurrent()), true);
    await frame.locator("#target").waitFor();
    await page.waitForFunction(() => !document.getElementById("inspectHtmlWorkspaceButton").disabled);
    assert.equal(await page.locator(".visual-annotation-marker").count(), 0);
    await page.locator(".visual-annotation-list > summary").click();
    await page.locator(".visual-annotation-list-row button").nth(1).click();
    assert.equal(await page.locator(".visual-annotation-editor textarea").inputValue(),
      "Комментарий после перезапуска");
    await page.locator(".visual-annotation-editor .primary").click();
    await page.waitForFunction(() => state.draftAttachments.length === 1 &&
      !RNAssistantChatDrafts.readOffice(state.activeChatId).pendingVisualAnnotation);
    await page.evaluate(() => {
      const committed = Object.assign({}, state.draftAttachments[0]);
      saveOfficeDraft(state.activeChatId, "", [], true, 1, null);
      state.draftAttachments = [];
      state.messages = (state.messages || []).concat({ Id: "sent-annotation", Role: "user",
        Content: "Исправь выбранный элемент", Attachments: [committed] });
      state.messageTotalCount = state.messages.length;
      renderAttachmentDrafts(); renderMessages({ fullReset: true });
    });
    await page.locator(".visual-annotation-message").waitFor();
    assert.match(await page.locator(".visual-annotation-message").textContent(), /Комментарий после перезапуска/);
    assert.equal(await page.locator(".visual-annotation-marker.is-sent").count(), 0,
      "sent annotation numbers remain hidden while selection mode is off");
    await page.locator("#inspectHtmlWorkspaceButton").click();
    await page.locator(".visual-annotation-marker.is-sent").waitFor();
    assert.equal(await page.locator(".visual-annotation-marker.is-sent").textContent(),
      await page.evaluate(() => String(state.messages.at(-1).Attachments[0].visualAnnotation.annotationNumber)));
    await page.locator(".visual-annotation-message button").click();
    await page.locator(".visual-annotation-sent").waitFor();
    if (process.env.RNA_SENT_ANNOTATION_SCREENSHOT) await page.screenshot({
      path: process.env.RNA_SENT_ANNOTATION_SCREENSHOT, animations: "disabled" });
    assert.equal(await page.locator(".visual-annotation-sent textarea").count(), 0,
      "sent annotations open read-only from the immutable message");
    await page.locator(".visual-annotation-sent button").click();
    await page.evaluate(() => { switchTab("artifacts"); RNAssistantArtifactCatalog.editor(); });
    await page.frameLocator("#tab-artifacts #htmlWorkspacePreviewFrame").locator("#target").waitFor();
    await page.waitForFunction(() => !document.getElementById("inspectHtmlWorkspaceButton").disabled);
    await page.locator("#inspectHtmlWorkspaceButton").click();
    await page.locator("#tab-artifacts .visual-annotation-marker.is-sent").waitFor();
    await page.locator("#inspectHtmlWorkspaceButton").click();
    assert.equal(await page.locator("#tab-artifacts .visual-annotation-marker").count(), 0,
      "Materials is clean after exiting annotation mode following a right-panel round trip");
    await page.locator("#inspectHtmlWorkspaceButton").click();
    await page.frameLocator("#tab-artifacts #htmlWorkspacePreviewFrame").locator("#target").click();
    await page.locator(".visual-annotation-editor textarea").press("Enter");
    await page.waitForFunction(() => state.draftAttachments.length === 1 &&
      !RNAssistantChatDrafts.readOffice(state.activeChatId).pendingVisualAnnotation);
    assert.equal(await page.evaluate(() => state.draftAttachments[0].visualAnnotation.comment), "",
      "Enter stages an element-only annotation without inventing a comment");
    assert.match(await page.locator(".visual-annotation-list-body").textContent(), /Только элемент/);
    await page.evaluate(() => switchTab("chat"));
    assert.equal(await page.locator("#sendButton").isEnabled(), true,
      "an element-only annotation can be submitted without typed chat text");
    assert.deepEqual(errors, []);
    console.log("PASS HTML annotations: editor, CAS, draft and sent markers, immutable message view, protected pending recovery");
  } finally {
    await browser.close(); server.kill();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
