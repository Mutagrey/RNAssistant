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
    const page = await browser.newPage();
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto(address + "preview/web/?scenario=ready");
    await page.waitForFunction(() => state.activeChatId === "preview-chat" && !state.initializePromise);
    const result = await page.evaluate(async () => {
      await RNAssistantProjectChats.setScope("projects");
      const first = await send("connectProjectWorkspace", { rootPath: "/tmp/rna-mock-first" });
      await send("connectProjectWorkspace", { rootPath: "/tmp/rna-mock-second" });
      await send("deleteProjectHistory", { workspaceId: first.selectedWorkspaceId });
      const third = await send("connectProjectWorkspace", { rootPath: "/tmp/rna-mock-third" });
      return { ids: third.projects.map(item => item.workspaceId), selected: third.selectedWorkspaceId };
    });
    assert.equal(new Set(result.ids).size, result.ids.length,
      "a new mock workspace must not reuse the ID of another visible project");
    await page.evaluate(selected => RNAssistantProjectChats.refresh({ workspaceId: selected }), result.selected);
    assert.ok(await page.locator("#projectChatTree").getByText("rna-mock-third", { exact: true }).count());
    assert.deepEqual(errors, []);
    console.log("PASS preview workspace identity after history deletion");
  } finally {
    await browser.close();
    server.kill();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
