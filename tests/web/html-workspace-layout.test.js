"use strict";
// Real CodeMirror history and shipped panel markup; no Office/VSTO required.
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
    const page = await browser.newPage({ viewport: { width: 1100, height: 760 } });
    const html = read("index.html");
    const css = [...html.matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)].map(match => read(match[1])).join("\n");
    const panel = html.slice(html.indexOf('<section class="panel" id="tab-artifacts">'), html.indexOf('<section class="panel" id="tab-settings">'));
    await page.setContent(`<style>${css}\nbody{display:block}#tab-artifacts{display:flex;height:100vh}</style>${panel}`);
    for (const file of ["vendor/codemirror/codemirror.min.js", "vendor/codemirror/mode/javascript/javascript.min.js",
      "app-editors.js", "app-html-workspace-model.js", "app-html-workspace-editor.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    const history = await page.evaluate(() => {
      window.$ = id => document.getElementById(id);
      const sourceText = "<main>HTML workspace</main>\n".repeat(4000);
      const file = { id: "index", path: "pages/dashboard.html", kind: "html", content: sourceText, sourceReadKey: "r1" };
      window.state = { activeChatId: "chat", activeHtmlArtifactId: "r1", htmlWorkspaceMode: "edit",
        htmlWorkspace: { activeFileId: "index", revisionArtifactId: "r1", files: [file], dataSources: [], history: [{ id: "r0", label: "Начальная версия" }] },
        htmlWorkspaceSelection: { type: "file", id: "index" }, artifacts: [] };
      window.workspaceEditor = RNAssistantHtmlWorkspaceEditor.create({ state,
        model: RNAssistantHtmlWorkspaceModel.create(state), artifacts: {},
        source: { current: () => true, ready: () => true, ensure: () => true, message: () => "" },
        preview: { build: () => '<html><body style="font:16px system-ui;padding:32px;color:#344054"><h1>Отчёт по продажам</h1><p>Текущая версия HTML</p></body></html>' } });
      initializeCodeEditors(["htmlWorkspaceEditorInput"]);
      workspaceEditor.render();
      const cm = document.querySelector(".CodeMirror").CodeMirror;
      for (let revision = 2; revision <= 160; revision++) {
        state.activeHtmlArtifactId = file.sourceReadKey = "r" + revision;
        file.content = sourceText + revision;
        workspaceEditor.render();
      }
      const afterRevisions = cm.historySize();
      cm.replaceRange("draft ", { line: 0, ch: 0 }, null, "+input");
      file.content = cm.getValue();
      workspaceEditor.render();
      const localUndo = cm.historySize().undo;
      state.activeChatId = "another-chat";
      workspaceEditor.render();
      const afterChatSwitch = cm.historySize();
      return { afterRevisions, localUndo, afterChatSwitch };
    });
    const preview = await page.evaluate(() => {
      const cm = document.querySelector(".CodeMirror").CodeMirror;
      state.htmlWorkspaceMode = "preview";
      workspaceEditor.applyMode(); workspaceEditor.render(); workspaceEditor.updateStatus();
      const hiddenSource = cm.getValue();
      workspaceEditor.sync();
      return { hiddenSource, afterPreview: cm.historySize(), source: state.htmlWorkspace.files[0].content.slice(0, 6) };
    });
    assert.deepEqual(history.afterRevisions, { undo: 0, redo: 0 }, "160 large programmatic revisions retain no editor undo bodies");
    assert.equal(history.localUndo, 1, "unchanged renders preserve local typing undo");
    assert.deepEqual(history.afterChatSwitch, { undo: 0, redo: 0 }, "another chat cannot undo into the previous editor");
    assert.equal(preview.hiddenSource, "", "preview releases the hidden code buffer");
    assert.deepEqual(preview.afterPreview, { undo: 0, redo: 0 });
    assert.equal(preview.source, "draft ", "empty hidden editor cannot overwrite the draft");
    await page.frameLocator("#htmlWorkspacePreviewFrame").getByRole("heading", { name: "Отчёт по продажам" }).waitFor({ timeout: 5000 });

    for (const width of [1100, 760, 420]) {
      await page.setViewportSize({ width, height: 760 });
      const bounds = await page.locator("#htmlWorkspaceEditor").boundingBox();
      for (const id of ["undoHtmlWorkspaceButton", "redoHtmlWorkspaceButton", "exportHtmlWorkspaceButton", "saveHtmlWorkspaceButton", "htmlWorkspaceMoreActions"]) {
        const box = await page.locator("#" + id).boundingBox();
        assert.ok(box.x >= bounds.x && box.x + box.width <= bounds.x + bounds.width + 1, `${id} fits at ${width}px`);
      }
      assert.ok((await page.locator("#htmlWorkspacePreviewFrame").boundingBox()).height > 250, "toolbar leaves room for the preview");
      await page.locator("#htmlWorkspaceMoreActions > summary").click();
      const reload = await page.locator("#reloadHtmlWorkspaceSourceButton").boundingBox();
      assert.ok(reload.x >= 0 && reload.x + reload.width <= width, "actions menu remains inside the panel");
      await page.locator("#htmlWorkspaceMoreActions > summary").click();
      await page.locator(".html-workspace-create-menu > summary").click();
      const create = await page.locator("#addHtmlDataButton").boundingBox();
      const sidebar = await page.locator("#htmlWorkspaceSidebar").boundingBox();
      assert.ok(create.y + create.height <= sidebar.y + sidebar.height + 1, "creation options are not clipped by an empty sidebar");
      await page.locator(".html-workspace-create-menu > summary").click();
    }
    await page.setViewportSize({ width: 1100, height: 760 });
    if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT });
    console.log("PASS HTML panel: bounded CodeMirror history, local undo, preview cleanup and responsive toolbar");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
