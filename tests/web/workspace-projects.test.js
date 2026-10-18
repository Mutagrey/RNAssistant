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
    const page = await browser.newPage({ viewport: { width: 740, height: 840 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    const index = read("index.html");
    const css = [...index.matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)].map(match => read(match[1])).join("\n");
    const tabs = index.match(/<nav class="section-rail"[\s\S]*?<\/nav>/)[0];
    const section = index.match(/<section class="panel" id="tab-workspace">[\s\S]*?<\/section>/)[0].replace('class="panel"', 'class="panel active"');
    await page.setContent(`<style>${css}</style><div class="shell app-shell">${tabs}<main class="content">${section}</main></div>`);
    await page.evaluate(() => {
      document.querySelector(".workspace-projects").append(document.getElementById("dtHostWorkspace").content.cloneNode(true));
      window.calls = [];
      window.nativeState = { type: "rnassistant.workspaceState", contractVersion: 1, revision: 0, workspace: null, project: null };
      window.send = (type, payload) => {
        calls.push({ type, payload });
        if (type === "getWorkspaceState") return Promise.resolve(JSON.parse(JSON.stringify(nativeState)));
        return new Promise((resolve, reject) => { window.succeed = response => resolve(JSON.parse(JSON.stringify(response))); window.fail = reject; });
      };
    });
    await page.addScriptTag({ path: path.join(root, "js/app-workspace-projects.js") });
    await page.evaluate(() => { RNAssistantWorkspace.bind(); return RNAssistantWorkspace.show(); });
    assert.equal(await page.locator("#workspaceSettings").getAttribute("open"), "", "folder setup is visible initially");
    assert.equal(await page.locator("#workspaceProjectOpen").isDisabled(), true);
    await page.locator("#workspaceRoot").fill("C:\\Projects\\Reports");
    await page.locator("#workspaceOpen").click();
    await page.evaluate(() => {
      nativeState = { ...nativeState, revision: 1, workspace: { bindingId: "binding-a", workspaceId: "ws-a", rootPath: "C:\\Projects\\Reports", readOnly: false } };
      succeed(nativeState);
    });
    await page.waitForFunction(() => !document.getElementById("workspaceProjectFields").disabled);
    await page.locator("#workspaceProjectPath").fill("reports/project.json");
    await page.locator("#workspaceProjectName").fill("Quarterly reports");
    await page.locator("#workspaceProjectCreate").click();
    assert.equal(await page.locator("#workspaceOpen").isDisabled(), true, "root cannot switch during a write");
    await page.evaluate(() => document.getElementById("workspaceProjectCreate").click());
    assert.equal(await page.evaluate(() => calls.filter(call => call.type === "createTabularProject").length), 1, "double click admits one create");
    await page.evaluate(() => {
      nativeState = { ...nativeState, revision: 2, project: { name: '<img src=x onerror="window.injected=true">', manifestPath: "reports/project.json", formatVersion: 2, sources: [] } };
      succeed(nativeState);
    });
    await page.waitForFunction(() => !document.getElementById("workspaceProjectResult").hidden);
    assert.equal(await page.locator("#workspaceProjectTitle img").count(), 0, "project metadata stays inert");
    assert.equal(await page.evaluate(() => window.injected), undefined);
    console.log("PASS workspace UI: shared typed binding, create receipt, single flight and inert metadata");

    await page.locator("#workspaceReadOnly").check();
    await page.locator("#workspaceOpen").click();
    await page.evaluate(() => {
      nativeState = { ...nativeState, revision: 3, workspace: { ...nativeState.workspace, bindingId: "binding-ro", readOnly: true }, project: null };
      succeed(nativeState);
    });
    await page.waitForFunction(() => !document.getElementById("workspaceOpen").disabled);
    assert.equal(await page.locator("#workspaceProjectCreate").isDisabled(), true);
    await page.locator("#workspaceReadOnly").uncheck();
    assert.equal(await page.locator("#workspaceProjectCreate").isDisabled(), true, "checkbox edit cannot upgrade the current binding");
    await page.locator("#workspaceProjectOpen").click();
    await page.evaluate(() => fail(Object.assign(new Error("stale"), { code: "workspace_binding_changed" })));
    await page.waitForFunction(() => document.getElementById("workspaceStatus").getAttribute("role") === "alert");
    assert.equal(await page.locator("#workspaceProjectOpen").isDisabled(), true, "foreign binding disables subsequent operations");
    await page.evaluate(() => { nativeState = { ...nativeState, revision: 4, workspace: { ...nativeState.workspace, bindingId: "binding-b", readOnly: false } }; });
    await page.locator("#workspaceRefresh").click();
    await page.waitForFunction(() => !document.getElementById("workspaceProjectCreate").disabled);
    console.log("PASS workspace UI: read-only and stale selections cannot authorize writes");

    await page.locator("#workspaceProjectCreate").click();
    await page.evaluate(() => fail(Object.assign(new Error("uncertain"), { code: "effect_unknown" })));
    await page.waitForFunction(() => !document.getElementById("workspaceReconcile").hidden);
    assert.equal(await page.locator("#workspaceProjectResult").isVisible(), false, "unknown write has no success preview");
    const creates = await page.evaluate(() => calls.filter(call => call.type === "createTabularProject").length);
    await page.locator("#workspaceProjectPath").fill("other/project.json");
    await page.locator("#workspaceReconcile").click();
    assert.deepEqual(await page.evaluate(() => calls.at(-1)), { type: "reconcileWorkspaceFile", payload: { bindingId: "binding-b", manifestPath: "reports/project.json" } }, "inspection pins the uncertain path");
    await page.evaluate(() => succeed({ state: nativeState, outcome: "UnknownAfterDispatch" }));
    await page.waitForFunction(() => document.getElementById("workspaceStatus").textContent.startsWith("Не удалось проверить"));
    assert.equal(await page.locator("#workspaceProjectResult").isVisible(), false, "malformed receipt cannot prove file absence or success");
    await page.locator("#workspaceReconcile").click();
    await page.evaluate(() => succeed({ state: { ...nativeState, revision: 5, project: null }, exists: true, outcome: "UnknownAfterDispatch" }));
    await page.waitForFunction(() => document.getElementById("workspaceStatus").textContent.startsWith("Файл существует"));
    assert.equal(await page.locator("#workspaceProjectResult").isVisible(), false);
    assert.equal(await page.evaluate(() => calls.filter(call => call.type === "createTabularProject").length), creates, "inspection never retries creation");
    console.log("PASS workspace UI: uncertain write requires explicit inspection without replay or false success");

    await page.setViewportSize({ width: 360, height: 800 });
    for (const theme of ["light", "dark"]) {
      await page.evaluate(value => document.documentElement.setAttribute("data-theme", value), theme);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, "narrow " + theme + " layout fits");
      assert.equal(await page.locator(".section-rail .rail-button").evaluateAll(tabs => tabs.every(tab => {
        const box = tab.getBoundingClientRect();
        return box.width >= tab.scrollWidth && box.left >= 0 && box.right <= innerWidth;
      })), true, "all section tabs fit at narrow width");
    }
    if (process.env.RNA_WORKSPACE_SCREENSHOT) await page.screenshot({ path: process.env.RNA_WORKSPACE_SCREENSHOT, fullPage: true });
    assert.deepEqual(errors, []);
    console.log("PASS workspace UI: narrow light/dark layout and no browser errors");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
