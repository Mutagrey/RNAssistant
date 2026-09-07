"use strict";

// Real layout regression. Run with Playwright available in NODE_PATH.
// BROWSER_EXECUTABLE may point to a locally installed Chromium/Chrome.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { chromium } = require("playwright");
const root = path.resolve(__dirname, "../..");
const read = file => fs.readFileSync(path.join(root, "web", file), "utf8");

(async () => {
  const browser = await chromium.launch({
    headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {})
  });
  try {
    const page = await browser.newPage();
    const html = read("index.html");
    const editor = html.match(/<section[^>]+id="toolEditorPanel"[\s\S]*?<\/section>/)[0];
    const css = [...html.matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)]
      .map(match => read(match[1])).join("\n");
    await page.setContent(`<style>${css}</style>${editor}`);
    await page.addScriptTag({ path: path.join(root, "web/js/vendor/codemirror/codemirror.min.js") });
    await page.addScriptTag({ path: path.join(root, "web/js/app-tools-structured.js") });
    await page.evaluate(() => {
      window.$ = id => document.getElementById(id);
      $("toolEditorPanel").classList.remove("hidden");
      document.querySelectorAll("[data-tool-page-view]").forEach(node => node.classList.add("hidden"));
      $("vbaToolEditor").classList.remove("hidden");
      $("toolComponentSelect").add(new Option("RNA_NewTool · StdModule"));
      $("toolComponentNameInput").value = "RNA_NewTool";
      window.codeEditor = CodeMirror.fromTextArea($("toolCodeInput"), { lineNumbers: true });
      codeEditor.getWrapperElement().classList.add("rn-code-editor", "rn-code-editor-toolCodeInput");
      codeEditor.setValue("Option Explicit\n\nPublic Sub Execute()\n    Debug.Print \"hello\"\nEnd Sub");
      const state = { toolSchemaVisualDraft: {
        type: "object", properties: {
          query: { type: "string", minLength: 1, maxLength: 200, description: "Literal query over capability ids and compact metadata." },
          kind: { type: "string", enum: ["tool", "skill"], description: "Optional exact capability kind." }
        }, required: ["query"], additionalProperties: false
      } };
      RNAssistantToolStructuredEditor.create({ state }).renderRunArguments();
    });
    // Includes a narrow pane in a wide window: viewport breakpoints alone cannot fix it.
    for (const [viewport, pane] of [[1440, 1100], [1440, 360], [600, 560], [320, 300]]) {
      await page.setViewportSize({ width: viewport, height: 850 });
      for (const theme of ["light", "dark"]) {
        await page.evaluate(({ pane, theme }) => {
          document.documentElement.dataset.theme = theme;
          Object.assign($("toolEditorPanel").style, { width: pane + "px", height: "800px" });
        }, { pane, theme });
        for (const tab of ["implementation", "test", "implementation"]) {
          await page.evaluate(tab => {
            document.querySelectorAll("[data-tool-page-view]").forEach(node =>
              node.classList.toggle("hidden", node.dataset.toolPageView !== tab));
            codeEditor.refresh();
          }, tab);
          if (process.env.LAYOUT_SCREENSHOT) await page.screenshot({
            path: process.env.LAYOUT_SCREENSHOT.replace(/\.png$/, `-${pane}-${theme}-${tab}.png`)
          });
          const geometry = await page.evaluate(tab => {
            const rect = node => { const r = node.getBoundingClientRect(); return { x: r.x, y: r.y, right: r.right, bottom: r.bottom, width: r.width, height: r.height }; };
            const pane = rect($("toolEditorPanel"));
            const active = document.querySelector(`[data-tool-page-view="${tab}"]`);
            const controls = [...active.querySelectorAll("input, select, button, .CodeMirror")]
              .filter(node => node.getBoundingClientRect().width > 0);
            return {
              overflow: controls.filter(node => { const r = rect(node); return r.x < pane.x - 1 || r.right > pane.right + 1; }).map(node => node.id || node.className),
              toolbar: rect(document.querySelector(".component-toolbar")),
              fields: rect($("toolComponentNameInput")),
              editor: rect(codeEditor.getWrapperElement()),
              testWidth: rect(active).width,
              inputs: controls.filter(node => node.tagName !== "BUTTON").map(node => rect(node).height),
              outputVisible: $("toolRunOutput").getBoundingClientRect().height > 0
            };
          }, tab);
          assert.deepEqual(geometry.overflow, [], `${viewport}/${pane} ${theme} ${tab}: controls contained`);
          if (tab === "implementation") {
            assert.ok(geometry.fields.y >= geometry.toolbar.bottom, "metadata follows toolbar vertically");
            assert.ok(geometry.editor.width >= pane - 10, "code uses the full pane width");
            assert.ok(geometry.editor.y > geometry.fields.bottom, "code follows metadata");
          } else {
            assert.ok(geometry.testWidth <= 760, "form has a readable maximum width");
            assert.ok(geometry.inputs.every(height => height >= 34), "dynamic inputs have consistent usable height");
            assert.equal(geometry.outputVisible, false, "empty result does not leave a blank panel");
          }
        }
      }
    }
    console.log("PASS Tool Library browser layout: 4 sizes, both themes, repeated tab changes");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
