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
    assert.equal((await native.text()).includes("preview/fixtures.js"), false);
    assert.equal((await preview.text()).includes("mock-bridge.js"), true);
    assert.equal((await preview.text()).includes('src="/preview/fixtures.js"'), true);
    assert.equal((await comparison.text()).includes('src="/preview/web/?scenario=ready"'), true);

    await normal.goto(server.address + "preview/web/?scenario=ready");
    await normal.waitForFunction(() => state.activeChatId === "preview-chat" && state.skills.length === 1 && !state.initializePromise);
    assert.equal(await normal.evaluate(() => state.bridgeUnavailable), false);
    const previewExamples = await normal.evaluate(() => ({
      skill: PrototypeFixtures.skill,
      report: PrototypeFixtures.report,
      chat: PrototypeFixtures.chat
    }));
    assert.deepEqual(await normal.evaluate(() => ({
      title: state.chats[0].Title,
      question: state.messages[0].content || state.messages[0].Content,
      answer: state.messages[1].content || state.messages[1].Content
    })), await normal.evaluate(() => ({
      title: PrototypeFixtures.chat.title,
      question: PrototypeFixtures.chat.user,
      answer: PrototypeFixtures.chat.assistant
    })));
    assert.equal(await normal.evaluate(() => state.activeRunViewState.lifecycle), "completed");
    assert.equal(await normal.locator("#messages .chat-artifact-card").count(), 1);
    assert.deepEqual(await normal.evaluate(() => send("getRunChanges", {
      chatId: "preview-chat", runId: "preview-run-initial"
    })), { chatId: "preview-chat", runId: "preview-run-initial", complete: true, items: [] });
    assert.equal(await normal.locator("#chatSessionList [data-tree-key='chat:preview-chat']").count(), 1);
    const progress = [];
    await normal.evaluate(() => {
      window.__previewProgress = [];
      chrome.webview.addEventListener("message", event => {
        if (event.data.type === "progress" && event.data.id === "inbox:preview-chat")
          window.__previewProgress.push(event.data.payload);
      });
    });
    await normal.locator("#chatInput").fill("Что в отчёте?");
    await normal.locator("#sendButton").click();
    await normal.waitForFunction(() => state.messages.some(message =>
      String(message.Content || message.content || "").includes("Это смоделированный ответ")));
    progress.push(...await normal.evaluate(() => window.__previewProgress));
    assert.ok(progress.some(item => item.phase === "thinking"));
    assert.equal(progress.filter(item => item.phase === "streaming").length, 2);
    assert.equal(await normal.locator("#chatInput").inputValue(), "");
    await normal.locator('.section-rail [data-section="library"]').click();
    await normal.locator('#instructionsList [data-tree-key="skill:0"]').click();
    assert.equal(await normal.locator("#skillEditorPanel .rna-editor-tabs.skill-page-tabs").count(), 1);
    assert.equal(await normal.locator("#skillActionsMenu.rna-action-menu .rna-action-menu-body > button svg").count(), 4);
    assert.equal(await normal.locator('.skill-page-tabs [data-skill-page="instruction"][aria-current="page"]').count(), 1);
    const productTabStyle = await normal.locator(".skill-page-tabs").evaluate(tabs => {
      const style = getComputedStyle(tabs);
      return { background: style.backgroundColor, border: style.borderBottomColor,
        borderWidth: style.borderBottomWidth, width: tabs.getBoundingClientRect().width,
        paneWidth: tabs.closest(".skill-editor-pane").getBoundingClientRect().width,
        contentGap: document.querySelector("#skillForm label").getBoundingClientRect().top - tabs.getBoundingClientRect().bottom };
    });
    assert.ok(productTabStyle.width >= productTabStyle.paneWidth - 1);
    assert.ok(productTabStyle.contentGap >= 31 && productTabStyle.contentGap <= 33);
    await normal.waitForFunction(() => state.skills[0]._sourceLoaded[""] === true);
    const original = await normal.evaluate(() => getCodeEditorValue("skillBodyInput"));
    assert.equal(original, await normal.evaluate(() => PrototypeFixtures.skill));
    await normal.locator("#skillEditorPanel .CodeMirror").click();
    await normal.keyboard.press("ControlOrMeta+End");
    await normal.keyboard.type("\nПроверка mock bridge.");
    assert.equal(await normal.locator("#saveSkillsButton").isVisible(), true);
    await normal.locator("#saveSkillsButton").click();
    await normal.waitForFunction(() => state.skills[0].Revision === "preview-skill-2" && !state.skillLibraryDirty);
    assert.match(await normal.evaluate(() => getCodeEditorValue("skillBodyInput")), /Проверка mock bridge/);
    await normal.locator("#skillActionsMenu summary").click();
    const productMenuStyle = await normal.locator("#skillActionsMenu .rna-action-menu-body").evaluate(menu => {
      const style = getComputedStyle(menu);
      return { background: style.backgroundColor, border: style.borderColor,
        radius: style.borderRadius, padding: style.padding, shadow: style.boxShadow };
    });
    await normal.locator("#skillPreviewToggleButton").click();
    assert.equal(await normal.locator("#skillActionsMenu[open]").count(), 0);
    assert.equal(await normal.locator("#skillPreviewToggleButton svg").count(), 1);
    assert.equal(await normal.locator("#skillPreviewToggleButton .rna-action-menu-label").textContent(), "Вернуться к редактору");
    await normal.locator("#skillActionsMenu summary").click();
    await normal.locator("#skillPreviewToggleButton").click();
    await normal.locator("#skillActionsMenu summary").click();
    await normal.locator("#cloneSkillButton").click();
    await normal.waitForFunction(() => state.skills.length === 2 && state.skillLibraryDirty);
    await normal.locator("#saveSkillsButton").click();
    await normal.waitForFunction(() => state.skills.length === 2 && !state.skillLibraryDirty &&
      state.skills.some(skill => skill.Id === "excel.sales_analysis.copy" && skill.Revision === "preview-skill-3"));
    await normal.locator('[data-skill-page="files"]').click();
    assert.equal(await normal.locator('.skill-page-tabs [data-skill-page="files"][aria-current="page"]').count(), 1);
    normal.once("dialog", dialog => dialog.accept("details.md"));
    await normal.locator("#addSkillReferenceButton").click();
    await normal.locator("#saveSkillsButton").click();
    await normal.waitForFunction(() => !state.skillLibraryDirty &&
      state.skills[state.selectedSkillIndex].References.some(reference => reference.Path === "references/details.md" && reference.Revision));
    await normal.locator("#skillFileList .skill-file-row").nth(1).getByText("Открыть").click();
    await normal.waitForFunction(() => state.skills[state.selectedSkillIndex]._sourceLoaded["references/details.md"] === true);
    assert.match(await normal.evaluate(() => getCodeEditorValue("skillBodyInput")), /# details/);
    assert.equal(await normal.locator("#skillForm.rna-skill-fields").count(), 1);
    await normal.locator('#instructionsList [data-tree-key="tool:0"]').click();
    assert.equal(await normal.locator("#toolEditorPanel .rna-editor-tabs.tool-page-tabs").count(), 1);
    assert.equal(await normal.locator("#toolActionsMenu .rna-action-menu-check #toolEnabledInput").count(), 1);
    await normal.locator('.tool-page-tabs [data-tool-page="schema"]').click();
    assert.equal(await normal.locator('.tool-page-tabs [data-tool-page="schema"][aria-current="page"]').count(), 1);
    assert.equal(await normal.locator('[data-tool-page-view="schema"]').isVisible(), true);
    await normal.locator('.section-rail [data-section="vba"]').click();
    assert.equal(await normal.locator("#tab-vba .rna-editor-tabs.vba-mode-toggle").count(), 1);
    await normal.locator("#refreshVbaEmptyButton").click();
    await normal.waitForFunction(() => state.vba.modules.length > 0);
    assert.equal(await normal.evaluate(() => state.vba.modules[0].code),
      await normal.evaluate(() => PrototypeFixtures.vba));
    await normal.locator('.vba-mode-toggle [data-vba-mode="diff"]').click();
    assert.equal(await normal.locator('.vba-mode-toggle [data-vba-mode="diff"][aria-current="page"]').count(), 1);
    assert.equal(await normal.locator('[data-vba-view="diff"]').isVisible(), true);
    await normal.locator('.section-rail [data-section="artifacts"]').click();
    await normal.locator("#artifactCatalogOverviewRows .artifact-overview-link").first().waitFor();
    assert.equal(await normal.locator("#artifactCatalogOverviewRows tr").count(), 3);
    await normal.locator("#artifactCatalogOverviewRows .artifact-overview-link").first().click();
    await normal.waitForFunction(() => document.querySelector("#artifactDetailPreview").textContent.includes("Москва"));
    assert.ok((await normal.locator("#artifactDetailPreview").textContent()).includes("18,4%"));
    await normal.locator("#artifactCatalogAll").click();
    await normal.locator("#artifactCatalogOverviewRows").getByRole("button", { name: "Продажи по регионам.json" }).click();
    await normal.locator("#artifactCatalogPreviewBody .rn-json-viewer").waitFor();
    await normal.locator("#artifactCatalogPreviewBody").getByRole("button", { name: "Текст", exact: true }).click();
    assert.ok((await normal.locator("#artifactCatalogPreviewBody").textContent()).includes("7420000"));
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
    assert.deepEqual(await demo.evaluate(() => ({
      skill: Prototype.fixtures.skill,
      report: Prototype.fixtures.report,
      chat: Prototype.fixtures.chat
    })), previewExamples);
    await demo.locator("#main .rna-skill-fields").waitFor({ state: "attached" });
    assert.equal(await demo.locator("#main .rna-editor-tabs.tabs").count(), 1);
    assert.equal(await demo.locator("#main .rna-action-menu").count(), 1);
    await demo.locator("#main .rna-action-menu summary").click();
    assert.equal(await demo.locator("#main .rna-action-menu[open]").count(), 1);
    assert.deepEqual(await demo.locator("#main .rna-action-menu-body").evaluate(menu => {
      const style = getComputedStyle(menu);
      return { background: style.backgroundColor, border: style.borderColor,
        radius: style.borderRadius, padding: style.padding, shadow: style.boxShadow };
    }), productMenuStyle);
    await demo.keyboard.press("Escape");
    assert.equal(await demo.locator("#main .rna-action-menu[open]").count(), 0);
    assert.equal(await demo.locator('#main .tabs [data-go="library/skill"][aria-current="page"]').count(), 1);
    assert.deepEqual(await demo.locator("#main .tabs").evaluate(tabs => {
      const style = getComputedStyle(tabs);
      return { background: style.backgroundColor, border: style.borderBottomColor, borderWidth: style.borderBottomWidth };
    }), { background: productTabStyle.background, border: productTabStyle.border,
      borderWidth: productTabStyle.borderWidth });
    assert.deepEqual(await demo.locator("#main .rna-skill-fields label").allTextContents(),
      ["ID навыка", "ПриложениеCommonExcelWordPowerPointOutlook", "Описание навыкаСравнение периодов и анализ продаж по регионам", " Доступен агенту"]);
    await demo.close();

    const sideBySide = await browser.newPage();
    await sideBySide.goto(server.address + "product-preview.html");
    await sideBySide.locator("#compare").click();
    assert.equal(await sideBySide.locator("#compare").getAttribute("aria-pressed"), "true");
    assert.equal(await sideBySide.locator("#demo-pane").isVisible(), true);
    await sideBySide.frameLocator("#demo").locator("#section-rail").waitFor({ state: "attached" });
    await sideBySide.waitForFunction(() => {
      const view = document.getElementById("product").contentWindow;
      return view.state && view.state.activeChatId === "preview-chat" && !view.state.initializePromise;
    });
    await sideBySide.locator("#run-example").click();
    await sideBySide.waitForFunction(() => document.getElementById("product").contentWindow.state.messages.length >= 4);
    await sideBySide.close();

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
    console.log("PASS product mock preview: isolated bridge, shared fixtures/components, animated chat, actions, comparison, empty/error scenarios");
  } finally {
    await browser.close();
    server.process.kill("SIGTERM");
    if (server.process.exitCode === null) await once(server.process, "exit");
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
