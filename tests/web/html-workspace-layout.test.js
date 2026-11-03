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
    const panel = html.match(/<template id="htmlWorkspaceEditorTemplate">([\s\S]*?)<\/template>/)[1];
    await page.setContent(`<style>${css}\nbody{display:block;height:100vh}</style>${panel}`);
    for (const file of ["vendor/codemirror/codemirror.min.js", "vendor/codemirror/mode/javascript/javascript.min.js",
      "vendor/codemirror/mode/xml/xml.min.js", "vendor/codemirror/mode/css/css.min.js", "vendor/codemirror/mode/htmlmixed/htmlmixed.min.js",
      "app-editors.js", "app-html-workspace-model.js", "app-html-workspace-editor.js"])
      await page.addScriptTag({ content: read("js/" + file) });
    const history = await page.evaluate(() => {
      const root = document.querySelector('[data-html-node="htmlWorkspaceEditor"]');
      window.$ = id => root.querySelector('[data-html-node="' + id + '"]') || (id === 'htmlWorkspaceEditor' ? root : null);
      const sourceText = "<main>HTML workspace</main>\n".repeat(4000);
      const file = { id: "index", path: "pages/dashboard.html", kind: "html", content: sourceText, sourceReadKey: "r1" };
      window.state = { activeChatId: "chat", activeHtmlArtifactId: "r1", htmlWorkspaceMode: "edit",
        htmlWorkspace: { activeFileId: "index", revisionArtifactId: "r1", files: [file], dataSources: [], history: [{ id: "r0", label: "Начальная версия" }] },
        htmlWorkspaceSelection: { type: "file", id: "index" }, artifacts: [] };
      const code = RNAssistantCodeEditor.mount($("htmlWorkspaceEdit"), { text: "", readOnly: false });
      window.workspaceEditor = RNAssistantHtmlWorkspaceEditor.create({ state, node: $, root, code, previewVisible: () => true,
        model: RNAssistantHtmlWorkspaceModel.create(state), artifacts: {},
        source: { current: () => true, ready: () => true, ensure: () => true, message: () => "" },
        preview: { missingVendors: () => [], build: () => '<html><body style="font:16px system-ui;padding:32px;color:#344054"><h1>Отчёт по продажам</h1><p>Текущая версия HTML</p></body></html>' } });
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
    // Browser placement is covered by browser-saved-views; this fixture owns only the editor.
    await page.evaluate(() => { state.htmlWorkspaceMode = "edit"; workspaceEditor.applyMode(); workspaceEditor.render(); });
    for (const width of [1100, 760, 420]) {
      await page.setViewportSize({ width, height: 760 });
      const geometry = await page.locator(".CodeMirror").evaluate(node => ({
        mode: node.CodeMirror.getMode().name, height: node.clientHeight, width: node.clientWidth,
        parentHeight: node.parentNode.clientHeight, parentWidth: node.parentNode.clientWidth,
        border: getComputedStyle(node).borderTopWidth, radius: getComputedStyle(node).borderRadius
      }));
      assert.equal(geometry.mode, "htmlmixed");
      assert.ok(geometry.height > 250, "code fills the available editor height");
      assert.ok(Math.abs(geometry.height - geometry.parentHeight) <= 1, "editor has no outer vertical inset");
      assert.ok(Math.abs(geometry.width - geometry.parentWidth) <= 1, "editor has no outer horizontal inset");
      assert.equal(geometry.border, "0px"); assert.equal(geometry.radius, "0px");
    }
    await page.setViewportSize({ width: 1100, height: 760 });
    if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({ path: process.env.LAYOUT_SCREENSHOT });
    console.log("PASS HTML panel: bounded CodeMirror history, local undo, preview cleanup, syntax mode and full-height source at 1100/760/420 px");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
