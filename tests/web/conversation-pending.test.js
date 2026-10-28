"use strict";
const assert = require("node:assert/strict");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");
const root = path.resolve(__dirname, "../..");

(async () => {
  const server = spawn("python3", ["-B", "prototypes/serve_ui.py", "--port", "0"], { cwd: root });
  try {
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
      const page = await browser.newPage(), errors = [];
      page.on("pageerror", error => errors.push(error.message));
      await page.goto(address + "preview/web/?scenario=ready");
      await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
      await page.evaluate(() => RNAssistantProjectChats.setScope("projects"));
      await page.waitForFunction(() => !!RNAssistantProjectChats.snapshot()?.sessionId);
      await page.evaluate(() => {
        const original = send;
        window.slowCatalog = { release: null };
        window.send = async function (type, payload) {
          const result = await original(type, payload);
          if (type === "getProjectChats" && slowCatalog.armed) {
            await new Promise(resolve => { slowCatalog.release = resolve; });
            slowCatalog.armed = false;
          }
          if (type === "submitProjectChat") slowCatalog.armed = true;
          return result;
        };
      });
      await page.locator("#chatInput").fill("Проверка квитанции при медленном каталоге");
      await page.locator("#chatInput").press("Enter");
      await page.waitForFunction(() => !!slowCatalog.release);
      assert.equal(await page.evaluate(() => RNAssistantProjectChats.isBusy()), false,
        "an accepted input releases its submit barrier before catalog refresh finishes");
      assert.equal(await page.evaluate(() => RNAssistantConversationInput.isPending("workspace", {
        workspaceId: RNAssistantProjectChats.workspaceId(),
        sessionId: RNAssistantProjectChats.snapshot().sessionId
      })), false);
      await page.evaluate(() => slowCatalog.release());
      assert.deepEqual(errors, []);
      await page.close();
      const revisit = await browser.newPage(), revisitErrors = [];
      revisit.on("pageerror", error => revisitErrors.push(error.message));
      await revisit.goto(address + "preview/web/?scenario=ready");
      await revisit.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
      await revisit.evaluate(() => RNAssistantProjectChats.setScope("projects"));
      await revisit.waitForFunction(() => !!RNAssistantProjectChats.snapshot()?.sessionId);
      const addressBefore = await revisit.evaluate(() => ({
        workspaceId: RNAssistantProjectChats.workspaceId(), sessionId: RNAssistantProjectChats.snapshot().sessionId
      }));
      await revisit.evaluate(() => {
        const original = send;
        window.lateSubmit = { release: null };
        window.send = async function (type, payload) {
          const result = await original(type, payload);
          if (type === "submitProjectChat") {
            await new Promise(resolve => { lateSubmit.release = resolve; });
            throw Error("Old visit submit error");
          }
          return result;
        };
      });
      await revisit.locator("#chatInput").fill("Запрос до повторного открытия чата");
      await revisit.locator("#chatInput").press("Enter");
      await revisit.waitForFunction(() => !!lateSubmit.release);
      await revisit.evaluate(async target => {
        const other = target.sessionId === "preview-project-clean" ? "preview-project-review" : "preview-project-clean";
        await RNAssistantProjectChats.open(target.workspaceId, other);
        await RNAssistantProjectChats.open(target.workspaceId, target.sessionId);
        lateSubmit.release();
      }, addressBefore);
      await revisit.waitForFunction(() => !RNAssistantProjectChats.isBusy());
      assert.doesNotMatch(await revisit.locator("#workspaceChatNotice").textContent(), /Old visit submit error/,
        "a late error from a previous visit cannot replace the reopened chat notice");
      assert.deepEqual(revisitErrors, []);
      await revisit.close();
      console.log("PASS conversation pending: early Workspace release and A-B-A response fencing");
    } finally { await browser.close(); }
  } finally { server.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
