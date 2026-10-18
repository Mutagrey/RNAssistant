"use strict";

const assert = require("node:assert/strict");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { once } = require("node:events");
const { chromium } = require("playwright");

const root = path.resolve(__dirname, "../..");

async function serve() {
  const process = spawn("python3", ["-B", "prototypes/serve_ui.py", "--port", "0"], { cwd: root });
  const address = await new Promise((resolve, reject) => {
    let output = "";
    process.stdout.on("data", chunk => {
      output += chunk.toString();
      const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)\n/.exec(output);
      if (match) resolve(match[1]);
    });
    process.once("exit", code => reject(new Error("Preview server exited: " + code)));
  });
  return { process, address };
}

(async () => {
  const server = await serve();
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const normal = await browser.newPage();
    const errors = [];
    normal.on("pageerror", error => errors.push(error.message));
    const native = await normal.request.get(server.address + "web/");
    const preview = await normal.request.get(server.address + "preview/web/");
    const comparison = await normal.request.get(server.address + "product-preview.html");
    assert.equal(native.status(), 200);
    assert.equal(preview.status(), 200);
    assert.equal(comparison.status(), 200);
    assert.equal((await native.text()).includes("mock-bridge.js"), false);
    assert.equal((await preview.text()).includes("mock-bridge.js"), true);
    assert.equal((await comparison.text()).includes('src="/preview/web/?scenario=ready"'), true);

    await normal.goto(server.address + "preview/web/?scenario=ready");
    await normal.waitForFunction(() => state.activeChatId === "preview-chat" && state.skills.length === 1 && !state.initializePromise);
    assert.equal(await normal.evaluate(() => state.bridgeUnavailable), false);
    assert.equal(await normal.locator("#chatSessionList [data-tree-key='chat:preview-chat']").count(), 1);
    await normal.locator("#chatInput").fill("Что в отчёте?");
    await normal.locator("#sendButton").click();
    await normal.waitForFunction(() => state.messages.some(message => message.Content === "Это ответ демонстрационного сценария. Модель и Office не запускались."));
    assert.equal(await normal.locator("#chatInput").inputValue(), "");
    await normal.locator('.section-rail [data-section="library"]').click();
    await normal.locator('#instructionsList [data-tree-key="skill:0"]').click();
    await normal.waitForFunction(() => state.skills[0]._sourceLoaded[""] === true);
    const original = await normal.evaluate(() => getCodeEditorValue("skillBodyInput"));
    assert.match(original, /Анализ продаж/);
    await normal.locator("#skillEditorPanel .CodeMirror").click();
    await normal.keyboard.press("ControlOrMeta+End");
    await normal.keyboard.type("\nПроверка mock bridge.");
    assert.equal(await normal.locator("#saveSkillsButton").isVisible(), true);
    await normal.locator("#saveSkillsButton").click();
    await normal.waitForFunction(() => state.skills[0].Revision === "preview-skill-2" && !state.skillLibraryDirty);
    assert.match(await normal.evaluate(() => getCodeEditorValue("skillBodyInput")), /Проверка mock bridge/);
    await normal.locator("#skillActionsMenu summary").click();
    await normal.locator("#cloneSkillButton").click();
    await normal.waitForFunction(() => state.skills.length === 2 && state.skillLibraryDirty);
    await normal.locator("#saveSkillsButton").click();
    await normal.waitForFunction(() => state.skills.length === 2 && !state.skillLibraryDirty &&
      state.skills.some(skill => skill.Id === "excel.sales_analysis.copy" && skill.Revision === "preview-skill-3"));
    await normal.locator('[data-skill-page="files"]').click();
    normal.once("dialog", dialog => dialog.accept("details.md"));
    await normal.locator("#addSkillReferenceButton").click();
    await normal.locator("#saveSkillsButton").click();
    await normal.waitForFunction(() => !state.skillLibraryDirty &&
      state.skills[state.selectedSkillIndex].References.some(reference => reference.Path === "references/details.md" && reference.Revision));
    await normal.locator("#skillFileList .skill-file-row").nth(1).getByText("Открыть").click();
    await normal.waitForFunction(() => state.skills[state.selectedSkillIndex]._sourceLoaded["references/details.md"] === true);
    assert.match(await normal.evaluate(() => getCodeEditorValue("skillBodyInput")), /# details/);
    assert.equal(await normal.locator("#skillForm.rna-skill-fields").count(), 1);
    await normal.locator('.section-rail [data-section="artifacts"]').click();
    await normal.locator("#artifactCatalogOverviewRows .artifact-overview-link").waitFor();
    await normal.locator("#artifactCatalogOverviewRows .artifact-overview-link").click();
    await normal.waitForFunction(() => document.querySelector("#artifactCatalogPreviewBody").textContent.includes("Москва"));
    assert.equal(await normal.locator("#artifactCatalogStatus").textContent(), "");
    await normal.locator('.section-rail [data-section="workspace"]').click();
    await normal.waitForFunction(() => document.querySelector("#wsSelectedList").textContent.includes("Москва.xlsx"));
    await normal.waitForFunction(() => document.querySelector("#dtNativeCatalogList").textContent.includes("sales") &&
      document.querySelector("#dtNativeParametersRows").textContent.includes("Выручка"));
    await normal.locator("#wsSourceBrowse").click();
    await normal.locator('#wsBrowseList [data-ws-file="Самара.xlsx"]').check();
    await normal.locator("#wsSourceSave").click();
    await normal.waitForFunction(() => document.querySelector("#wsSourcePersistence").textContent.includes("Состав сохранён") &&
      document.querySelector("#wsSelectedList").textContent.includes("Самара.xlsx"));
    await normal.locator('#dtNavTree [data-tree-key="dt:templates"] .navigation-tree-row').click();
    await normal.locator("#dtNativeCatalogList button").waitFor();
    await normal.locator("#dtNativeCatalogList button").click();
    await normal.waitForFunction(() => document.querySelector("#dtNativeCatalogCode").textContent.includes('"sales"'));
    await normal.locator('#dtNavTree [data-tree-key="dt:parameters"] .navigation-tree-row').click();
    await normal.waitForFunction(() => document.querySelector("#dtNativeParametersRows").textContent.includes("Выручка"));
    assert.deepEqual(errors, []);
    await normal.close();

    const demo = await browser.newPage();
    await demo.goto(server.address + "#/library/skill");
    await demo.locator("#main .rna-skill-fields").waitFor({ state: "attached" });
    assert.deepEqual(await demo.locator("#main .rna-skill-fields label").allTextContents(),
      ["ID навыка", "ПриложениеCommonExcelWordPowerPointOutlook", "Описание навыкаСравнение периодов и анализ продаж по регионам", " Доступен агенту"]);
    await demo.close();

    const failed = await browser.newPage();
    failed.on("pageerror", error => errors.push(error.message));
    await failed.goto(server.address + "preview/web/?scenario=error");
    await failed.locator('.section-rail [data-section="library"]').click();
    await failed.locator('#instructionsList [data-tree-key="skill:0"]').click();
    await failed.waitForFunction(() => document.querySelector("#logBox").textContent.includes("Демонстрационная ошибка чтения навыка"));
    assert.equal(await failed.evaluate(() => !!state.skills[0]._sourceLoaded[""]), false);
    assert.deepEqual(errors, []);
    await failed.close();

    const empty = await browser.newPage();
    await empty.goto(server.address + "preview/web/?scenario=empty");
    await empty.waitForFunction(() => !state.initializePromise && !state.bridgeUnavailable);
    assert.deepEqual(await empty.evaluate(() => ({ chats: state.chats.length, skills: state.skills.length })), { chats: 0, skills: 0 });
    await empty.close();
    console.log("PASS product mock preview: isolated bridge, shared skill form, chat, skill/reference writes, material/project reads and empty/error scenarios");
  } finally {
    await browser.close();
    server.process.kill("SIGTERM");
    if (server.process.exitCode === null) await once(server.process, "exit");
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
