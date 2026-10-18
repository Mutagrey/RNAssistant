"use strict";
const assert = require("node:assert/strict");
const path = require("node:path");
const { chromium } = require("playwright");

(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const demo = await browser.newPage({ viewport: { width: 1100, height: 760 } });
    const product = await browser.newPage({ viewport: { width: 1074, height: 760 } });
    const errors = [];
    demo.on("pageerror", error => errors.push("demo: " + error.message));
    product.on("pageerror", error => errors.push("product: " + error.message));
    await demo.goto("file://" + path.resolve(__dirname, "../../prototypes/ui/index.html") + "#/chat/main");
    await product.goto("file://" + path.resolve(__dirname, "../../web/index.html"));
    assert.equal(await demo.evaluate(() => typeof RNAssistantNavigationTree.render), "function");
    assert.equal(await product.evaluate(() => typeof RNAssistantNavigationTree.render), "function");

    const demoRoutes = ["chat/main", "materials/all", "library/all", "vba/code", "projects/sources", "settings/connection"];
    for (const route of demoRoutes) {
      await demo.goto("file://" + path.resolve(__dirname, "../../prototypes/ui/index.html") + "#/" + route);
      const tree = demo.locator("[data-product-tree][role=tree]");
      assert.equal(await tree.count(), 1, "demo has one shared tree for " + route);
      assert.ok(await tree.locator(".navigation-tree-row").count(), "demo tree has shared rows for " + route);
    }

    const sections = [
      ["artifacts", "#htmlWorkspaceTree"], ["instructions", "#instructionsList"],
      ["vba", "#vbaModuleList"], ["workspace", "#dtNavTree"], ["settings", "#settingsNavTree"]
    ];
    for (const [section, selector] of sections) {
      if (section === "instructions") await product.locator("#libraryRootTab").click();
      else await product.locator('.rail-button[data-tab="' + section + '"]').click();
      const tree = product.locator(selector);
      assert.equal(await tree.getAttribute("role"), "tree", section + " uses shared ARIA tree");
      assert.ok(await tree.evaluate(node => node.classList.contains("navigation-tree")));
      if (section !== "artifacts" && section !== "vba")
        assert.ok(await tree.locator(".navigation-tree-row").count(), section + " has shared rows");
    }
    await product.locator('.rail-button[data-tab="chat"]').click();
    await product.evaluate(() => {
      state.chats = [{ id: "shared", title: "Проверка дерева", host: "Excel", documentKey: "doc",
        documentTitle: "Отчёт.xlsx", isCurrentDocument: true }];
      state.documents = []; state.activeChatId = "shared";
      renderChatSessionList(state.chats);
    });
    const chat = product.locator(".chat-navigation-tree");
    assert.equal(await chat.getAttribute("role"), "tree");
    assert.equal(await chat.locator(".navigation-tree-item").count(), 2);

    await demo.goto("file://" + path.resolve(__dirname, "../../prototypes/ui/index.html") + "#/settings/connection");
    await product.locator('.rail-button[data-tab="settings"]').click();
    const sharedStyle = async (page, selector) => page.locator(selector).first().evaluate(node => {
      const style = getComputedStyle(node);
      return [style.minHeight, style.borderRadius, style.paddingLeft, style.fontSize];
    });
    assert.deepEqual(await sharedStyle(product, "#settingsNavTree .navigation-tree-row"),
      await sharedStyle(demo, "[data-product-tree] .navigation-tree-row"), "demo and product use the same tree row geometry");
    assert.deepEqual(errors, []);
    console.log("PASS shared tree: one renderer and common row geometry across demo and all six product sections");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
