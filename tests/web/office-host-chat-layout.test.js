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
    const css = [...read("index.html").matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)]
      .map(match => read(match[1])).join("\n");
    await page.setContent(`<style>${css}</style><div class="chat-workspace" style="height:100vh">
      <aside class="chat-sidebar"><div id="chatSessionList" class="chat-session-list"></div></aside>
      <div class="splitter"></div><div class="chat-main"></div></div>`);
    await page.evaluate(() => {
      window.state = { host: "Excel", activeChatId: "excel-chat", officeHostChatAvailable: true,
        chatNavigationPending: 1, messages: [] };
      window.currentActiveSend = () => null;
      window.hasActiveMessageEdit = () => false;
      window.confirmDiscardHtmlWorkspaceChanges = () => true;
      window.log = () => {};
      window.calls = [];
      state.chatNavigationPromise = new Promise(resolve => { window.completeNavigation = () => {
        state.chatNavigationPending--; resolve();
      }; });
      window.send = (type, payload) => {
        if (type === "reportClientTiming") return Promise.resolve({});
        calls.push({ type, payload });
        return new Promise((resolve, reject) => { window.succeed = resolve; window.fail = reject; });
      };
    });
    for (const script of ["app-chat-state.js", "app-chat-session.js", "app-chat.js"])
      await page.addScriptTag({ content: read("js/" + script) });
    await page.evaluate(() => {
      window.renderChatSessions = () => {
        const list = document.getElementById("chatSessionList");
        list.replaceChildren(); renderOfficeHostLaunchRows(list, "");
      };
      window.applyInitState = init => { Object.assign(state, init); };
      renderChatSessions();
    });
    await page.getByText("Новый документ Office").click();
    const powerPoint = page.getByRole("button", { name: /PowerPoint/ });
    await powerPoint.click();
    await page.getByRole("status").waitFor();
    assert.match(await page.getByRole("status").innerText(), /PowerPoint/);
    assert.equal(await page.evaluate(() => calls.length), 0, "actual click waits for prior navigation");
    await page.evaluate(() => completeNavigation());
    await page.waitForFunction(() => calls.length === 1);
    await page.evaluate(() => fail({ detail: "PowerPoint: " + "ДлиннаяДиагностика".repeat(20) }));
    await page.getByRole("alert").waitFor();
    assert.equal(await page.evaluate(() => state.host), "Excel");
    assert.equal(await page.evaluate(() => calls.length), 1, "failure never retries itself");
    for (const theme of ["light", "dark"]) {
      await page.evaluate(theme => document.documentElement.setAttribute("data-theme", theme), theme);
      for (const width of [360, 740]) {
        await page.setViewportSize({ width, height: 600 });
        assert.ok(await page.getByRole("alert").isVisible(), "failure is visible at " + width);
        assert.ok(await page.getByRole("alert").evaluate(node => node.scrollWidth <= node.clientWidth + 1),
          "long errors wrap inside the actual sidebar in " + theme);
      }
    }
    await powerPoint.click();
    await page.waitForFunction(() => calls.length === 2);
    await page.evaluate(() => succeed({ host: "PowerPoint", chatId: "ppt-chat", init: {
      host: "PowerPoint", activeChatId: "ppt-chat", tools: ["powerpoint.slide.add"], skills: ["skills.powerpoint.deck.building"]
    } }));
    await page.waitForFunction(() => !state.officeHostChatPending && state.host === "PowerPoint");
    assert.equal(await page.getByRole("alert").count(), 0);
    assert.equal(await page.evaluate(() => state.tools[0]), "powerpoint.slide.add");
    assert.equal(await page.evaluate(() => state.skills[0]), "skills.powerpoint.deck.building");
    assert.deepEqual(errors, []);
    console.log("PASS Office chat browser: actual PowerPoint click, queue, visible wrapping errors, explicit retry and target initialization");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
