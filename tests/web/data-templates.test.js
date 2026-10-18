"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { chromium } = require("playwright");
const root = path.resolve(__dirname, "../../web");
const read = file => fs.readFileSync(path.join(root, file), "utf8");
const fixtureRoot = fs.mkdtempSync(path.join(os.tmpdir(), "rna-source-picker-"));
const fixtureFolder = path.join(fixtureRoot, "Reports");
const largeSource = path.join(fixtureRoot, "Large-80MiB.xlsx");
const selected = (name, contents = "selection fixture") => ({ name, mimeType: "application/octet-stream", buffer: Buffer.from(contents) });

(async () => {
  fs.mkdirSync(path.join(fixtureFolder, "nested"), { recursive: true });
  fs.writeFileSync(path.join(fixtureFolder, "revenue.csv"), "month,value\n2026-01,120\n");
  fs.writeFileSync(path.join(fixtureFolder, "nested", "entities.json"), '[{"code":"0007"}]');
  fs.closeSync(fs.openSync(largeSource, "w"));
  fs.truncateSync(largeSource, 80 * 1024 * 1024); // Sparse selection fixture, not a valid workbook/parser benchmark.
  const browser = await chromium.launch({ headless: true,
    ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1160, height: 940 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    const index = read("data-templates/index.html");
    const css = [...index.matchAll(/<link rel="stylesheet" href="([^"?]+)[^"]*">/g)]
      .map(match => read("data-templates/" + match[1])).join("\n");
    await page.setContent(`<html lang="ru"><style>${css}</style><main class="data-templates" id="dataTemplates"></main></html>`);
    for (const file of ["data-templates/ui/source-intake.js", "data-templates/ui/operation-view.js", "data-templates/ui/loader-view.js", "data-templates/ui/shell.js", "data-templates/ui/loader.js"])
      await page.addScriptTag({ path: path.join(root, file) });
    await page.evaluate(() => {
      const container = document.getElementById("dataTemplates");
      DataTemplatesShell.mount(container, "core/index.html");
      window.loader = DataTemplatesLoader.create();
      loader.bind(container);
    });
    assert.equal(await page.locator("#workspaceSettings").count(), 0);
    assert.equal(await page.locator("#dtFiles").getAttribute("accept"), ".xlsx,.csv,.json");
    await page.locator("#dtTabSources").focus();
    await page.keyboard.press("ArrowRight");
    assert.equal(await page.locator("#dtViewTemplates").isVisible(), true);
    await page.keyboard.press("End");
    assert.equal(await page.locator("#dtViewFormat").isVisible(), true);
    assert.match(await page.locator("#dtViewFormat").innerText(), /Схема ядра/);
    await page.locator("#dtTabSources").click();
    if (process.env.RNA_DATA_SCREENSHOTS) {
      fs.mkdirSync(process.env.RNA_DATA_SCREENSHOTS, { recursive: true });
      await page.screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "sources-empty-light.png"), animations: "disabled" });
    }

    await page.locator("#dtFiles").setInputFiles([selected("Отчёт за январь.xlsx"), selected("revenue.csv"), selected("entities.json"), selected("<img onerror=alert(1)>.json"), selected("empty.csv", ""), selected("notes.exe")]);
    await page.waitForFunction(() => document.getElementById("dtFileCount").textContent === "6");
    assert.equal(await page.locator(".dt-source-row input:checked").count(), 4);
    assert.equal(await page.locator(".dt-source-row input:disabled").count(), 2);
    assert.equal(await page.locator(".dt-source-row img").count(), 0);
    await page.evaluate(() => {
      const transfer = new DataTransfer();
      for (let i = 0; i < 2; i++) transfer.items.add(new File(["same metadata"], "repeat.csv", { lastModified: 1 }));
      document.getElementById("dtDropzone").dispatchEvent(new DragEvent("drop", { bubbles: true, cancelable: true, dataTransfer: transfer }));
    });
    await page.waitForFunction(() => document.getElementById("dtFileCount").textContent === "8");
    assert.equal(await page.locator(".dt-file-state", { hasText: "Возможный повтор" }).count(), 2);
    await page.locator(".dt-list-options summary").click();
    await page.locator("#dtSourceState").selectOption("attention");
    assert.equal(await page.locator(".dt-source-row").count(), 4, "attention includes unavailable files and possible duplicates");
    await page.locator("#dtSourceState").selectOption("");
    await page.locator("#dtSourceFilter").selectOption("json");
    assert.equal(await page.locator(".dt-source-row").count(), 2);
    await page.locator("#dtSelectVisible").uncheck();
    assert.equal(await page.locator(".dt-source-row input:checked").count(), 0);
    await page.locator("#dtSourceFilter").selectOption("");
    await page.locator("#dtSourceSearch").fill("январь");
    assert.equal(await page.locator(".dt-source-row").count(), 1);
    await page.locator(".dt-file-name").click();
    assert.match(await page.locator("#dtDetailNote").innerText(), /Содержимое ещё не прочитано/);
    await page.locator("#dtSourceSearch").fill("missing");
    assert.equal(await page.locator("#dtNoMatches").isVisible(), true);
    await page.locator("#dtSourceSearch").fill("");
    console.log("PASS loader: real file selection, inert metadata, honest format hints, duplicate choices and filters");

    await page.locator("#dtClearSources").click();
    await page.locator("#dtFolder").setInputFiles(fixtureFolder);
    await page.waitForFunction(() => document.getElementById("dtFileCount").textContent === "2");
    assert.match(await page.locator("#dtSourceList").innerText(), /Reports\/nested\/entities.json/);
    await page.evaluate(() => {
      const transfer = new DataTransfer();
      transfer.items.add(new File(["file metadata only"], "Budget.xlsx"));
      document.getElementById("dtDropzone").dispatchEvent(new DragEvent("drop", { bubbles: true, cancelable: true, dataTransfer: transfer }));
    });
    await page.waitForFunction(() => document.getElementById("dtFileCount").textContent === "3");
    assert.equal(await page.evaluate(() => typeof RNAssistantWorkspace), "undefined", "standalone selection has no native bridge");
    console.log("PASS loader: folder picker preserves nested paths; file drop shares the same selection path");

    // A directory reader returns multiple batches; a late callback must not refill a cancelled/new context.
    await page.evaluate(() => {
      window.dropEntries = entry => {
        const event = new Event("drop", { bubbles: true, cancelable: true });
        Object.defineProperty(event, "dataTransfer", { value: { types: ["Files"], items: [{ kind: "file", webkitGetAsEntry: () => entry }] } });
        document.getElementById("dtDropzone").dispatchEvent(event);
      };
      window.fileEntry = name => ({ name, isFile: true, file: done => done(new File(["data"], name)) });
      let batch = 0;
      dropEntries({ name: "Batch", isDirectory: true, createReader: () => ({ readEntries: done => done(++batch <= 2 ? [fileEntry("part" + batch + ".csv")] : []) }) });
    });
    await page.waitForFunction(() => document.getElementById("dtFileCount").textContent === "5");
    assert.match(await page.locator("#dtSourceList").innerText(), /Batch\/part2.csv/);
    await page.evaluate(() => dropEntries({ name: "Slow", isDirectory: true, createReader: () => ({ readEntries: done => { window.finishDirectory = done; } }) }));
    await page.waitForFunction(() => typeof window.finishDirectory === "function");
    await page.waitForFunction(() => document.querySelector('[data-operation-part="current"]').textContent === "Slow");
    assert.equal(await page.locator('[data-operation-part="progress"]').getAttribute("value"), null, "unknown directory total must not invent a percentage");
    await page.locator("#dtTabMapping").click();
    assert.equal(await page.locator("#dtOperation").isVisible(), true, "operation view remains visible across domain sections");
    await page.evaluate(() => { loader.unbind(); loader.bind(document.getElementById("dataTemplates")); });
    assert.equal(await page.locator("#dtOperation").getAttribute("data-state"), "running", "remounting presentation cannot cancel the owner operation");
    assert.equal(await page.locator("#dtFormatLabels span").count(), 3, "remount does not duplicate content");
    await page.evaluate(() => loader.setContext("", false, false));
    assert.equal(await page.locator("#dtOperation").getAttribute("data-state"), "running", "returning to the workspace tab refreshes its state without cancelling enumeration");
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    if (process.env.RNA_DATA_SCREENSHOTS)
      await page.screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "operation-running.png"), animations: "disabled" });
    await page.locator("#dtCancelIntake").click();
    await page.waitForFunction(() => document.getElementById("dtCancelIntake").hidden);
    await page.evaluate(() => finishDirectory([fileEntry("late.json")]));
    assert.equal(await page.locator("#dtFileCount").innerText(), "5");
    assert.match(await page.locator("#dtIntakeStatus").innerText(), /отменено/);
    assert.equal(await page.locator("#dtOperation").getAttribute("data-state"), "cancelled");
    await page.evaluate(() => {
      window.finishDirectory = null;
      dropEntries({ name: "Switch", isDirectory: true, createReader: () => ({ readEntries: done => { window.finishDirectory = done; } }) });
    });
    await page.waitForFunction(() => typeof window.finishDirectory === "function");
    await page.evaluate(() => {
      loader.setContext("binding-new", false, true);
      finishDirectory([fileEntry("wrong-project.json")]);
    });
    assert.equal(await page.locator("#dtSourceQueue").isVisible(), false);
    assert.equal(await page.locator("#dtOperation").isVisible(), false, "old operation is detached from the new context");
    assert.match(await page.locator("#dtIntakeStatus").innerText(), /Контекст проекта изменился/);
    console.log("PASS loader: complete directory batches, cancellation and context change discard late selections");

    await page.locator("#dtFiles").setInputFiles([selected("kept.csv")]);
    await page.waitForFunction(() => document.getElementById("dtFileCount").textContent === "1");
    await page.evaluate(() => {
      let batch = 0;
      dropEntries({ name: "Large", isDirectory: true, createReader: () => ({ readEntries: done => done(batch++ === 0 ? Array.from({ length: 500 }, (_, i) => fileEntry(i + ".csv")) : []) }) });
    });
    await page.waitForFunction(() => document.getElementById("dtIntakeStatus").dataset.error === "true");
    assert.equal(await page.locator("#dtFileCount").innerText(), "1", "an over-limit batch is not partially added");
    assert.match(await page.locator("#dtSourceList").innerText(), /kept.csv/);
    await page.evaluate(() => dropEntries({ name: "Unavailable", isDirectory: true, createReader: () => ({ readEntries: (_, failed) => failed(new DOMException("denied", "NotReadableError")) }) }));
    await page.waitForFunction(() => document.getElementById("dtIntakeStatus").textContent.startsWith("Не удалось прочитать"));
    assert.equal(await page.locator("#dtFileCount").innerText(), "1");
    console.log("PASS loader: bounded intake and directory read failures preserve the existing list");

    await page.evaluate(() => {
      window.contentReads = 0;
      const forbiddenRead = () => { contentReads++; throw new Error("Selection must not read source bodies"); };
      for (const method of ["text", "arrayBuffer", "stream"]) Blob.prototype[method] = forbiddenRead;
      for (const method of ["readAsText", "readAsArrayBuffer", "readAsDataURL", "readAsBinaryString"]) FileReader.prototype[method] = forbiddenRead;
    });
    await page.locator("#dtClearSources").click();
    await page.locator("#dtFiles").setInputFiles(largeSource);
    await page.waitForFunction(() => document.getElementById("dtFileCount").textContent === "1");
    assert.match(await page.locator("#dtSelectionCount").innerText(), /80 МБ/);
    assert.equal(await page.locator("#dtSourceLoadNote").isVisible(), true);
    assert.match(await page.locator('[data-operation-part="detail"]').innerText(), /80 МБ/);
    assert.equal(await page.locator("#dtOperation").getAttribute("data-state"), "completed");
    assert.equal(await page.locator('[data-operation-part="progress"]').getAttribute("value"), "1");
    assert.equal(await page.evaluate(() => contentReads), 0);
    const progressEvidence = await page.evaluate(async () => {
      const updates = [], controller = new AbortController();
      const files = Array.from({ length: 100 }, (_, i) => new File(["abc"], i + ".csv"));
      await DataTemplatesSourceIntake.fromFiles(files, { signal: controller.signal, progress: snapshot => updates.push({ snapshot, at: performance.now() }) });
      return updates;
    });
    const last = progressEvidence.at(-1).snapshot;
    assert.equal(last.completedFiles, 100); assert.equal(last.totalFiles, 100); assert.equal(last.sourceBytes, 300);
    for (let i = 1; i < progressEvidence.length - 1; i++)
      assert.ok(progressEvidence[i].at - progressEvidence[i - 1].at >= 90, "intermediate progress updates are coalesced");
    assert.equal(await page.evaluate(() => contentReads), 0);
    console.log("PASS loader: 80 MiB metadata selection without reading bodies, exact/coalesced progress and view remount");

    await page.locator("#dtClearSources").click();
    await page.locator("#dtFiles").setInputFiles(Array.from({ length: 55 }, (_, i) => selected("report-" + (i + 1) + ".csv", "x".repeat(i + 1))));
    await page.waitForFunction(() => document.getElementById("dtFileCount").textContent === "55");
    assert.equal(await page.locator(".dt-source-row").count(), 50, "only one bounded page enters the DOM");
    assert.equal(await page.locator("#dtPageRange").innerText(), "1–50 из 55");
    await page.locator("#dtNextPage").click();
    assert.equal(await page.locator(".dt-source-row").count(), 5);
    assert.equal(await page.locator("#dtPageRange").innerText(), "51–55 из 55");
    await page.locator("#dtSelectVisible").uncheck();
    assert.match(await page.locator("#dtSelectionCount").innerText(), /Выбрано: 50 из 55/);
    await page.locator("#dtSourceState").selectOption("excluded");
    assert.equal(await page.locator(".dt-source-row").count(), 5);
    assert.equal(await page.locator("#dtPagination").isVisible(), false);
    await page.locator("#dtSourceState").selectOption("included");
    await page.locator(".dt-source-row input").first().click(); // The unchecked row leaves this filtered view immediately.
    assert.equal(await page.locator(".dt-source-row").count(), 49, "a toggled row leaves the filter without breaking focus");
    assert.equal(await page.locator(":focus").getAttribute("data-source-toggle"), await page.locator(".dt-source-row input").first().getAttribute("data-source-toggle"));
    await page.locator("#dtSourceState").selectOption("");
    await page.locator("#dtSourceSort").selectOption("size");
    assert.equal(await page.locator(".dt-file-name").first().innerText(), "report-55.csv");
    await page.locator("#dtSourceSort").selectOption("name");
    assert.equal(await page.locator(".dt-file-name").nth(1).innerText(), "report-2.csv", "names use natural numeric ordering");
    for (const width of [1160, 360]) {
      await page.setViewportSize({ width, height: 940 });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, "expanded filters and pager fit at " + width);
      if (process.env.RNA_DATA_SCREENSHOTS)
        await page.locator("#dtSourceQueue").screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "source-pages-" + width + ".png"), animations: "disabled" });
    }
    await page.setViewportSize({ width: 1160, height: 940 });
    await page.locator("#dtSourcePage").selectOption("1");
    assert.equal(await page.locator(".dt-file-name").first().innerText(), "report-51.csv");
    await page.locator("#dtSourceSearch").fill("report-1.");
    assert.equal(await page.locator(".dt-source-row").count(), 1, "search resets the page before displaying matches");
    await page.locator("#dtSourceSearch").fill("");
    await page.locator("#dtNextPage").click();
    for (let i = 0; i < 5; i++) await page.locator("[data-source-remove]").first().click();
    assert.equal(await page.locator("#dtPagination").isVisible(), false, "removing the last page returns to the preceding page");
    assert.equal(await page.locator(".dt-source-row").count(), 50);
    console.log("PASS loader: bounded pages, page-scoped selection, attention/status filters, natural sorting and focus after removal");

    await page.locator("#dtClearSources").click();
    await page.locator("#dtFiles").setInputFiles([selected("Управленческий отчёт.xlsx"), selected("План продаж.csv"), selected("Справочник подразделений.json")]);
    await page.waitForFunction(() => document.getElementById("dtFileCount").textContent === "3");
    await page.locator(".dt-list-options summary").click();
    for (const width of [1160, 740, 360]) {
      await page.setViewportSize({ width, height: 940 });
      for (const theme of ["light", "dark"]) {
        await page.emulateMedia({ colorScheme: theme });
        for (const view of ["sources", "templates", "parameters", "mapping", "results", "format"]) {
          if (width <= 640) await page.locator("#dtViewSelect").selectOption(view);
          else await page.locator('[data-dt-view="' + view + '"]').click();
          assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth && document.getElementById("dataTemplates").scrollWidth <= innerWidth), true, width + " " + theme + " " + view + " fits");
        }
        if (width <= 640) await page.locator("#dtViewSelect").selectOption("sources");
        else await page.locator("#dtTabSources").click();
        if (process.env.RNA_DATA_SCREENSHOTS && width !== 740) {
          await page.locator("#dataTemplates").evaluate(panel => { panel.scrollTop = 0; });
          await page.screenshot({ path: path.join(process.env.RNA_DATA_SCREENSHOTS, "sources-" + width + "-" + theme + ".png"), animations: "disabled" });
        }
      }
    }
    assert.deepEqual(errors, []);
    console.log("PASS loader: keyboard navigation, all views in narrow/wide light/dark layouts, no browser errors");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => fs.rmSync(fixtureRoot, { recursive: true, force: true }));
