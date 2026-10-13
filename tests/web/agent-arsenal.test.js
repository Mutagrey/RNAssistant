"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path");
const { chromium } = require("playwright");
const root = path.resolve(__dirname, "../../web"), read = name => fs.readFileSync(path.join(root, name), "utf8");
(async () => {
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1100, height: 760 } }), errors = [];
    page.on("pageerror", error => errors.push(error.message));
    const html = read("index.html");
    await page.setContent(html.replace(/<script\b[^>]*>[\s\S]*?<\/script>/g, "")
      .replace(/<meta http-equiv="Content-Security-Policy"[^>]*>/, "").replace(/<link\b[^>]*>/g, ""));
    for (const match of html.matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g))
      await page.addStyleTag({ content: read(match[1]) });
    await page.evaluate(() => {
      window.$ = id => document.getElementById(id);
      window.state = { activeChatId: "one", chatProjectionRevisions: { one: 1, two: 1 } };
      window.activeChatSummary = () => ({ IsCurrentDocument: true, Mode: "agent" });
      window.calls = [];
      window.fixture = id => ({ type: "rnassistant.agentArsenal", contractVersion: 1, chatId: id,
        revision: 1, historical: false, prepared: false, pending: false, skillCount: 2, toolCount: 2,
        details: { Policy: "agent/Ask", Selected: ["Анализ таблицы", "Подготовка отчёта"].map((name, i) => ({
          Id: "skill" + i, Name: name + " " + id, Description: "Анализ данных и проверка формул в рабочей книге.", Status: "selected",
          Tools: ["special"], References: ["references/rules.md"], Revision: "abc123" })),
          Tools: [{ Id: "read", Name: "Прочитать ресурс", Description: "Чтение данных", Origin: "direct", Skills: [] },
            { Id: "special", Name: "Проверить отчёт", Description: "Проверка результата", Origin: "skill", Skills: ["skill0", "skill1"], RequiresConfirmation: true }],
          Available: [{ Id: "missing", Name: "Office", Description: "Работа с книгой", Status: "unavailable", Reason: "Excel не подключён", Tools: [], References: [] }] } });
      window.send = (type, request) => {
        calls.push({ type, request });
        if (window.hold) return new Promise(resolve => { window.release = () => resolve(fixture(request.chatId)); });
        const response = fixture(request.chatId);
        if (!request.includeDetails) delete response.details;
        return Promise.resolve(response);
      };
    });
    await page.addScriptTag({ content: read("js/app-arsenal.js") });
    await page.evaluate(() => document.dispatchEvent(new Event("DOMContentLoaded")));
    await page.waitForFunction(() => document.getElementById("agentArsenalCounts").textContent === "2");
    for (const width of [1100, 740, 360]) {
      await page.setViewportSize({ width, height: 760 });
      await page.evaluate(width => document.querySelector(".chat-workspace").classList.toggle("is-sidebar-hidden", width < 800), width);
      await page.locator("#agentArsenalButton").click();
      await page.waitForFunction(() => document.getElementById("agentArsenalContent").textContent.includes("Анализ таблицы one"));
      const box = await page.locator("#agentArsenalPanel").boundingBox();
      assert.ok(box.x >= 0 && box.x + box.width <= width + 1, "Panel fits " + width);
      await page.locator("#agentArsenalSearch").fill("");
      await page.locator("#agentArsenalSkillsTab").click();
      if (await page.locator('[data-arsenal-key="skill:skill0"]').getAttribute("open") === null)
        await page.locator('[data-arsenal-key="skill:skill0"] > summary').click();
      assert.match(await page.locator('[data-arsenal-key="skill:skill0"]').textContent(), /Общий инструмент/);
      if (await page.locator('[data-arsenal-key="available"]').getAttribute("open") === null)
        await page.locator('[data-arsenal-key="available"] > summary').click();
      await page.waitForFunction(() => document.getElementById("agentArsenalContent").textContent.includes("Excel не подключён"));
      await page.locator("#agentArsenalToolsTab").click();
      await page.locator("#agentArsenalSearch").fill("отчёт");
      assert.equal(await page.locator(".arsenal-tool").count(), 1);
      await page.locator('[data-arsenal-key="tool:special"] > summary').click();
      assert.match(await page.locator('[data-arsenal-key="tool:special"]').textContent(), /Общий инструмент: Анализ таблицы one, Подготовка отчёта one/);
      await page.locator("#agentArsenalSearch").focus();
      await page.evaluate(() => { state.chatProjectionRevisions.one++; RNAssistantArsenal.sync(); });
      await page.waitForTimeout(250);
      assert.equal(await page.locator("#agentArsenalSearch").inputValue(), "отчёт");
      assert.equal(await page.evaluate(() => document.activeElement.id), "agentArsenalSearch");
      await page.locator("#agentArsenalSearch").fill("");
      await page.locator("#agentArsenalToolsTab").focus();
      await page.keyboard.press("ArrowLeft");
      assert.equal(await page.locator("#agentArsenalSkillsTab").getAttribute("aria-selected"), "true");
      assert.equal(await page.locator('[data-arsenal-key="skill:skill0"]').getAttribute("open"), "");
      await page.keyboard.press("Escape");
      assert.equal(await page.evaluate(() => document.activeElement.id), "agentArsenalButton");
      assert.equal(await page.locator("#agentArsenalPanel").isVisible(), false);
    }
    await page.evaluate(() => { window.hold = true; });
    await page.locator("#agentArsenalButton").click();
    await page.waitForFunction(() => !!window.release);
    await page.evaluate(() => { state.activeChatId = "two"; window.hold = false; RNAssistantArsenal.sync(); });
    await page.waitForFunction(() => document.getElementById("agentArsenalContent").textContent.includes("Анализ таблицы two"));
    await page.evaluate(() => window.release());
    await page.waitForTimeout(50);
    assert.doesNotMatch(await page.locator("#agentArsenalContent").textContent(), /Анализ таблицы one/);
    assert.equal(await page.evaluate(() => calls.every(call => call.type === "getAgentArsenal")), true, "Opening only reads metadata");
    if (process.env.LAYOUT_SCREENSHOT) {
      await page.setViewportSize({ width: 1100, height: 760 });
      await page.evaluate(() => document.querySelector(".chat-workspace").classList.remove("is-sidebar-hidden"));
      await page.screenshot({ animations: "disabled", path: process.env.LAYOUT_SCREENSHOT });
      await page.evaluate(() => document.documentElement.setAttribute("data-theme", "dark"));
      await page.setViewportSize({ width: 360, height: 760 });
      await page.evaluate(() => document.querySelector(".chat-workspace").classList.add("is-sidebar-hidden"));
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      await page.screenshot({ animations: "disabled", path: process.env.LAYOUT_SCREENSHOT.replace(/\.png$/, "-dark.png") });
    }
    await page.setViewportSize({ width: 360, height: 480 });
    await page.evaluate(() => {
      document.documentElement.setAttribute("data-theme", "dark");
      document.querySelector(".chat-workspace").classList.add("is-sidebar-hidden");
      const baseFixture = fixture;
      window.fixture = id => {
        const result = baseFixture(id), source = result.details.Tools[0];
        result.details.Tools = Array.from({ length: 60 }, (_, i) => ({ ...source, Id: "read" + i, Name: "Прочитать данные из ресурса " + i }));
        result.toolCount = result.details.Tools.length;
        return result;
      };
      RNAssistantArsenal.refresh();
    });
    await page.waitForFunction(() => document.getElementById("agentArsenalToolCount").textContent === "60");
    await page.locator("#agentArsenalToolsTab").click();
    assert.equal(await page.locator(".arsenal-tool").count(), 60, "Complete tool list remains available");
    const shortPanel = await page.locator("#agentArsenalPanel").boundingBox();
    assert.ok(shortPanel.y + shortPanel.height <= 480, "Short pane contains panel and scrollable list");
    await page.evaluate(() => { document.getElementById("agentArsenalBody").scrollTop = 180; RNAssistantArsenal.refresh(); });
    await page.waitForTimeout(50);
    assert.equal(await page.locator("#agentArsenalBody").evaluate(node => node.scrollTop), 180, "Updates preserve list scroll");
    await page.keyboard.press("Escape");
    await page.locator("#agentArsenalButton").click();
    await page.locator("#chatInput").dispatchEvent("pointerdown");
    assert.equal(await page.locator("#agentArsenalPanel").isVisible(), false, "Outside click closes without stealing composer focus");
    assert.deepEqual(errors, []);
    console.log("PASS arsenal: exact counts/shared tools, restrictions, inert open, retained disclosure/search, chat race, keyboard, 1100/740/360 px");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
