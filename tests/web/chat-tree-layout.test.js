"use strict";
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
    const page = await browser.newPage({ viewport: { width: 740, height: 600 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.setContent('<div class="chat-workspace" style="height:600px"><aside class="chat-sidebar">' +
      '<button id="toggleChatSidebarButton"></button><div id="chatOfficeLaunchMount"></div>' +
      '<div id="chatSessionList" class="chat-session-list"></div></aside><div class="splitter"></div><main class="chat-main"></main></div>');
    for (const file of ["app.css", "app-chat.css", "app-theme.css", "app-product-tokens.css", "app-product-components.css"])
      await page.addStyleTag({ content: read("css/" + file) });
    await page.evaluate(() => {
      window.$ = id => document.getElementById(id);
      window.RNAssistantRunViewState = { fromChatSummary: () => null };
      window.officeHostChatUnavailableReason = () => "";
      window.state = {
        host: "Excel", activeChatId: "first", chats: [
          { id: "first", title: "Обзор", host: "Excel", documentKey: "one", documentTitle: "Отчёт.xlsx", isCurrentDocument: true },
          { id: "second", title: "Детали", host: "Excel", documentKey: "one", documentTitle: "Отчёт.xlsx", isCurrentDocument: true },
          { id: "third", title: "Бюджет", host: "Word", documentKey: "two", documentTitle: "Бюджет.docx" }
        ], documents: [], collapsedChatDocuments: {}, initializedChatDocuments: {},
        chatRuns: {}, activeSends: {}, officeHostChatAvailable: true
      };
      window.selectedChats = [];
      window.selectChat = id => {
        selectedChats.push(id);
        state.activeChatId = id;
        renderChatSessionList(state.chats);
      };
      window.createOfficeHostChat = () => {};
      window.createDocumentChat = () => {};
      window.activateDocument = () => {};
      window.openActiveDocument = () => {};
      window.deleteDocument = () => {};
      window.renameChat = () => {};
      window.deleteChat = () => {};
    });
    for (const file of ["app-utils.js", "app-navigation-tree.js", "app-chat-state.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    await page.evaluate(() => renderChatSessionList(state.chats));

    const launcher = page.locator("#chatOfficeLaunchMount .chat-office-launch-list");
    await launcher.locator("summary").click();
    assert.equal(await launcher.locator(".chat-office-launch-row:visible").count(), 4);
    await page.keyboard.press("Escape");
    assert.equal(await launcher.getAttribute("open"), null);
    assert.equal(await page.evaluate(() => document.activeElement.className), "chat-office-launch-heading");

    const tree = page.getByRole("tree", { name: "Документы и чаты" });
    const current = tree.getByRole("treeitem", { name: "Обзор" });
    const closedDocument = tree.getByRole("treeitem", { name: /Бюджет.docx/ });
    assert.equal(await tree.getByRole("treeitem").count(), 4, "closed document hides its chat");
    assert.equal(await current.getAttribute("tabindex"), "0");
    await current.focus();
    await page.keyboard.press("ArrowDown");
    assert.equal(await page.evaluate(() => document.activeElement.dataset.treeKey), "chat:second");
    await page.keyboard.press("ArrowDown");
    assert.match(await page.evaluate(() => document.activeElement.getAttribute("aria-label")), /Бюджет.docx/);
    await page.keyboard.press("ArrowRight");
    assert.equal(await closedDocument.getAttribute("aria-expanded"), "true");
    await page.keyboard.press("ArrowRight");
    assert.equal(await page.evaluate(() => document.activeElement.dataset.treeKey), "chat:third");
    await page.keyboard.press("ArrowLeft");
    assert.match(await page.evaluate(() => document.activeElement.getAttribute("aria-label")), /Бюджет.docx/);

    const toggle = closedDocument.locator(":scope > .navigation-tree-row > .navigation-tree-expander");
    await toggle.focus();
    await page.keyboard.press("Enter");
    assert.equal(await closedDocument.getAttribute("aria-expanded"), "false", "button Enter toggles once");
    await page.evaluate(() => { state.activeChatId = "third"; renderChatSessionList(state.chats); });
    assert.equal(await closedDocument.getAttribute("aria-expanded"), "true", "new selection is revealed");
    assert.equal(await tree.getByRole("treeitem", { name: "Бюджет", exact: true }).getAttribute("aria-current"), "page");

    await tree.getByRole("treeitem", { name: "Бюджет", exact: true }).locator(".navigation-tree-label").focus();
    await page.keyboard.press("Enter");
    assert.deepEqual(await page.evaluate(() => selectedChats), ["third"], "button Enter selects once");
    await tree.getByRole("treeitem", { name: "Бюджет", exact: true }).focus();
    await page.keyboard.press("Shift+F10");
    assert.equal(await page.evaluate(() => document.activeElement.getAttribute("aria-label")), "Переименовать чат");
    await launcher.locator("summary").click();
    await launcher.getByRole("button", { name: /Word/ }).focus();
    await page.evaluate(() => { state.activeChatId = "first"; renderChatSessionList(state.chats); });
    await page.waitForFunction(() => document.activeElement.dataset.treeKey === "chat:first");
    assert.equal(await page.evaluate(() => document.activeElement.dataset.treeKey), "chat:first",
      "a created chat takes focus after menu replacement");
    if (process.env.CHAT_TREE_SCREENSHOT) await page.screenshot({ path: process.env.CHAT_TREE_SCREENSHOT });
    assert.deepEqual(errors, []);
    console.log("PASS chat tree browser: visible selection, roving keyboard focus and single actions");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
