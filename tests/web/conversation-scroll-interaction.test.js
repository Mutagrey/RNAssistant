"use strict";
const assert = require("node:assert/strict");
const path = require("node:path");
const { chromium } = require("playwright");
const root = path.resolve(__dirname, "../..");

(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 740, height: 650 } });
    await page.setContent(`<style>
      body { margin: 0; } .messages { box-sizing: border-box; height: 400px; overflow-y: auto; overflow-anchor: none; }
      .message { min-height: 200px; flex-shrink: 0; }
      #card-body { height: 280px; }
    </style><div id="messages" class="messages"></div><button id="bottom">К последним</button>`);
    await page.addScriptTag({ path: path.join(root, "web/js/app-conversation-view.js") });
    await page.evaluate(() => {
      const box = document.getElementById("messages");
      window.view = RNAssistantConversationView.get(box, { button: document.getElementById("bottom") });
      view.bind(); view.begin({ source: "office", sessionId: "chat" });
      box.innerHTML = `<article class="message">Первое сообщение</article><article class="message">Второе сообщение</article>
        <article class="message"><details id="card"><summary>Карточка действия</summary><div id="card-body">Детали</div></details></article>`;
      view.bottom(false);
    });
    await page.locator("#card summary").click();
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const before = await page.evaluate(() => ({
      top: document.getElementById("messages").scrollTop,
      cardTop: document.querySelector("#card summary").getBoundingClientRect().top,
      following: view.shouldFollow()
    }));
    assert.equal(before.following, false, "opening a card pauses automatic tail following");
    await page.evaluate(() => {
      const tail = document.createElement("article"); tail.className = "message"; tail.textContent = "Новый ответ";
      document.getElementById("messages").appendChild(tail);
      view.sync(view.shouldFollow(), false);
    });
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const after = await page.evaluate(() => ({
      top: document.getElementById("messages").scrollTop,
      cardTop: document.querySelector("#card summary").getBoundingClientRect().top
    }));
    assert.deepEqual(after, { top: before.top, cardTop: before.cardTop }, "new content keeps the opened card in place");
    await page.locator("#bottom").click();
    assert.equal(await page.evaluate(() => view.shouldFollow()), true, "explicit return to latest resumes following");
    await page.evaluate(() => view.dispose());
    console.log("PASS conversation scroll: opened card stays in place during new content");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
