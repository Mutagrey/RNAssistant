"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const http = require("node:http");
const path = require("node:path");
const { pathToFileURL } = require("node:url");
const { chromium } = require("playwright");
const root = path.resolve(__dirname, "../../web/tabular");
const fixture = path.join(root, "examples/disjoint-capture.json");
const input = fs.readFileSync(fixture, "utf8");
const matrixFixture = path.join(root, "examples/matrix-extraction.json"), matrixInput = fs.readFileSync(matrixFixture, "utf8");
const xlsxFixture = path.join(root, "examples/matrix-xlsx-extraction.json"), xlsxInput = fs.readFileSync(xlsxFixture, "utf8");
const { source: xlsxSource } = require("./tabular-xlsx-fixture");
const assets = ["index.html", "probe.css", "probe.js", "selectors.js", "snapshot.js", "json.js", "expression.js", "template.js", "matrix.js", "engine.js", "executor.js", "worker.js", "xlsx-zip.js", "xlsx-reader.js", "js/vendor/sax.js"];
const assetPath = file => path.join(root, file === "js/vendor/sax.js" ? "../" + file : file);

(async () => {
    // The loopback server is a test host only. The portable page also runs from file:// below.
    const server = http.createServer((request, response) => {
        const file = request.url.slice(1);
        response.setHeader("Cache-Control", "no-store");
        if (file === "busy-worker.js" || file === "foreign-worker.js" || file === "foreign-batch-worker.js") {
            response.setHeader("Content-Type", "text/javascript");
            response.end('postMessage({type:"ready",protocol:3,revision:"tabular-m2-xlsx-1"}); onmessage = event => {' +
                (file === "foreign-batch-worker.js" ? 'postMessage({type:"batch",id:event.data.id,batch:{version:1,sequence:2,records:[{}]}});' : file === "busy-worker.js"
                    ? 'postMessage({type:"progress",id:event.data.id,progress:{completedCells:0,totalCells:1}}); while(true) {}'
                    : 'postMessage({type:"result",id:event.data.id+1,result:{version:1,engineRevision:"tabular-m2-xlsx-1"}});') + '}');
        } else if (assets.includes(file)) {
            response.setHeader("Content-Type", file.endsWith(".js") ? "text/javascript" : file.endsWith(".css") ? "text/css" : "text/html");
            response.end(fs.readFileSync(assetPath(file)));
        } else { response.writeHead(404); response.end(); }
    });
    await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
    let browser;
    try {
        browser = await chromium.launch({ headless: true,
            ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
        const page = await browser.newPage();
        const errors = [];
        page.on("pageerror", error => errors.push(error.message));
        await page.goto("http://127.0.0.1:" + server.address().port + "/index.html");
        await page.locator("#capture").setInputFiles(fixture);
        await page.locator("#run").click();
        await page.waitForFunction(() => document.getElementById("status").textContent.startsWith("Проверено 10"));
        const workerResult = JSON.parse(await page.locator("#result").textContent());
        assert.deepEqual(workerResult.cells.map(cell => cell.address), ["C5", "D5", "J5", "K5", "P5", "C12", "D12", "J12", "K12", "P12"]);

        const lifecycle = await page.evaluate(async input => {
            const make = (extra = {}) => RNTabularExecutor.create({ mode: "worker", workerUrl: "worker.js", ...extra });
            const code = promise => promise.then(() => "unexpected-success", error => error.code);
            const executor = make(), controller = new AbortController();
            const pending = executor.inspect(input, { signal: controller.signal, onProgress: () => controller.abort() });
            const busy = await code(executor.inspect(input));
            const cancelled = await code(pending);
            const rerun = await executor.inspect(input, {
                onProgress: () => { const viewer = document.getElementById("viewer"); if (viewer) viewer.remove(); }
            });
            const reset = await code(executor.inspect(input, { onProgress: () => executor.dispose() }));
            const disposed = await code(executor.inspect(input));
            const next = make(), afterReload = await next.inspect(input); next.dispose();
            const stale = make({ expectedRevision: "changed-revision" });
            const revision = await code(stale.inspect(input)); stale.dispose();
            const foreign = make({ workerUrl: "foreign-worker.js" });
            const foreignRun = await code(foreign.inspect(input)); foreign.dispose();
            const absent = make({ workerUrl: "missing-worker.js" });
            const unavailable = await code(absent.inspect(input)); absent.dispose();
            const nativeWorker = window.Worker;
            window.Worker = undefined;
            const disabled = make(), noWorker = await code(disabled.inspect(input)); disabled.dispose();
            window.Worker = nativeWorker;
            const running = make({ workerUrl: "busy-worker.js" });
            let enteredLoop = false;
            const timeout = await code(running.inspect(input, { deadlineMs: 1000, onProgress: () => { enteredLoop = true; } }));
            running.dispose();
            const spinning = make({ workerUrl: "busy-worker.js" }), stop = new AbortController();
            const hardCancel = await code(spinning.inspect(input, { signal: stop.signal, onProgress: () => stop.abort() }));
            spinning.dispose();
            const cooperative = make({ mode: "cooperative" });
            const already = new AbortController(); already.abort();
            const beforeStart = await code(cooperative.inspect(input, { signal: already.signal }));
            const deadline = await code(cooperative.inspect(input, { deadlineMs: 10, onProgress: () => {
                const until = performance.now() + 20;
                while (performance.now() < until) {} // Model a delayed host callback, not an unbounded operator.
            } })); cooperative.dispose();
            return { busy, cancelled, rerun: rerun.cells.length, reset, disposed, afterReload: afterReload.cells.length,
                revision, foreignRun, unavailable, noWorker, timeout, enteredLoop, hardCancel, beforeStart, deadline };
        }, input);
        assert.deepEqual(lifecycle, { busy: "EXECUTOR_BUSY", cancelled: "EXECUTION_CANCELLED", rerun: 10,
            reset: "EXECUTOR_DISPOSED", disposed: "EXECUTOR_DISPOSED", afterReload: 10,
            revision: "EXECUTOR_REVISION_MISMATCH", foreignRun: "EXECUTOR_PROTOCOL_INVALID",
            unavailable: "EXECUTOR_UNAVAILABLE", noWorker: "EXECUTOR_UNAVAILABLE",
            timeout: "EXECUTION_TIMEOUT", enteredLoop: true, hardCancel: "EXECUTION_CANCELLED",
            beforeStart: "EXECUTION_CANCELLED", deadline: "EXECUTION_TIMEOUT" });
        assert.deepEqual(errors, []);
        console.log("PASS tabular Worker: cancellation, hard deadline, viewer-independent lifetime, reload and revision/run guards");

        const matrixEvidence = await page.evaluate(async input => {
            const make = (extra = {}) => RNTabularExecutor.create({ mode: "worker", workerUrl: "worker.js", ...extra });
            const code = promise => promise.then(() => "unexpected-success", error => error.code || error.message);
            const executor = make(); let release, entered, finished = false;
            const firstBatch = new Promise(resolve => { entered = resolve; }), records = [];
            const pending = executor.extract(input, { onBatch: async batch => {
                records.push(...batch.records); entered(); await new Promise(resolve => { release = resolve; });
            } }).then(value => { finished = true; return value; });
            await firstBatch;
            await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
            const held = !finished; release(); const summary = await pending;
            const stop = new AbortController();
            const cancelled = await code(executor.extract(input, { signal: stop.signal, onBatch: async () => stop.abort() }));
            const sinkFailure = await code(executor.extract(input, { onBatch: async () => { throw new Error("sink rejected"); } }));
            executor.dispose();
            const forged = make({ workerUrl: "foreign-batch-worker.js" });
            const outOfOrder = await code(forged.extract(input, { onBatch: async () => { throw new Error("must not reach sink"); } })); forged.dispose();
            const direct = make({ mode: "cooperative" }), directRecords = [];
            const directSummary = await direct.extract(input, { onBatch: async batch => directRecords.push(...batch.records) }); direct.dispose();
            return { held, summary, records, cancelled, sinkFailure, outOfOrder, directSummary, directRecords };
        }, matrixInput);
        assert.equal(matrixEvidence.held, true, "the worker cannot complete before its result batch is acknowledged");
        assert.equal(matrixEvidence.cancelled, "EXECUTION_CANCELLED");
        assert.equal(matrixEvidence.sinkFailure, "sink rejected");
        assert.equal(matrixEvidence.outOfOrder, "EXECUTOR_PROTOCOL_INVALID");
        assert.equal(matrixEvidence.records.length, 16);
        assert.deepEqual(matrixEvidence.summary, matrixEvidence.directSummary);
        assert.deepEqual(matrixEvidence.records, matrixEvidence.directRecords);
        console.log("PASS tabular matrix Worker: acknowledged batches, cancellation, consumer failure, sequence guard and core parity");

        const offline = await browser.newContext({ offline: true, acceptDownloads: true });
        const portable = await offline.newPage();
        portable.on("pageerror", error => errors.push(error.message));
        await portable.goto(pathToFileURL(path.join(root, "index.html")).href);
        assert.equal(await portable.locator("#mode").inputValue(), "cooperative");
        await portable.locator("#capture").setInputFiles(fixture);
        await portable.locator("#run").click();
        await portable.waitForFunction(() => document.getElementById("status").textContent.startsWith("Проверено 10"));
        assert.deepEqual(JSON.parse(await portable.locator("#result").textContent()), workerResult);
        const downloadPromise = portable.waitForEvent("download");
        await portable.locator("#download").click();
        const download = await downloadPromise;
        assert.deepEqual(JSON.parse(fs.readFileSync(await download.path(), "utf8")), workerResult);
        const changed = JSON.parse(input); changed.selection += " | I12";
        await portable.locator("#capture").setInputFiles({ name: "incomplete.json", mimeType: "application/json", buffer: Buffer.from(JSON.stringify(changed)) });
        await portable.locator("#run").click();
        await portable.waitForFunction(() => document.getElementById("status").textContent.startsWith("CAPTURE_INCOMPLETE"));
        assert.equal(await portable.locator("#download").isDisabled(), true);
        assert.equal(await portable.locator("#result").textContent(), "Нет результата.");
        await portable.locator("#capture").setInputFiles({ name: "invalid-utf8.json", mimeType: "application/json",
            buffer: Buffer.from([0xff, 0xfe]) });
        await portable.waitForFunction(() => document.getElementById("status").textContent.includes("как UTF-8"));
        assert.equal(await portable.locator("#run").isDisabled(), true);
        assert.equal(await portable.locator("#download").isDisabled(), true);
        await portable.locator("#operation").selectOption("extract");
        await portable.locator("#capture").setInputFiles(matrixFixture);
        await portable.locator("#run").click();
        await portable.waitForFunction(() => document.getElementById("status").textContent.startsWith("Получено 16"));
        const report = JSON.parse(await portable.locator("#result").textContent());
        assert.deepEqual(report.preview.records, matrixEvidence.records);
        assert.equal(report.preview.complete, true);
        assert.deepEqual({ ...report, preview: undefined }, { ...matrixEvidence.summary, preview: undefined });
        const matrixDownloadPromise = portable.waitForEvent("download");
        await portable.locator("#download").click();
        const matrixDownload = await matrixDownloadPromise;
        assert.deepEqual(JSON.parse(fs.readFileSync(await matrixDownload.path(), "utf8")), report);
        const invalidMatrix = JSON.parse(matrixInput); invalidMatrix.template.value.expr = "window.alert(1)";
        await portable.locator("#capture").setInputFiles({ name: "invalid-template.json", mimeType: "application/json", buffer: Buffer.from(JSON.stringify(invalidMatrix)) });
        await portable.locator("#run").click();
        await portable.waitForFunction(() => document.getElementById("status").textContent.startsWith("EXPRESSION_TYPE"));
        assert.equal(await portable.locator("#download").isDisabled(), true);
        assert.equal(await portable.locator("#result").textContent(), "Нет результата.");
        assert.deepEqual(errors, []);
        await offline.close();
        console.log("PASS tabular portable: offline file:// inspection/extraction, picker/report, Worker parity and no stale result after failure");

        const xlsxPage = await browser.newPage();
        xlsxPage.on("pageerror", error => errors.push(error.message));
        const externalRequests = [];
        xlsxPage.on("request", request => { if (!request.url().startsWith("http://127.0.0.1:")) externalRequests.push(request.url()); });
        await xlsxPage.goto("http://127.0.0.1:" + server.address().port + "/index.html");
        // A File goes straight to the Worker. The UI must not materialize its bytes or parse XML/ZIP.
        await xlsxPage.evaluate(() => {
            for (const method of ["arrayBuffer", "text", "stream"]) {
                const original = Blob.prototype[method];
                Blob.prototype[method] = function (...args) {
                    if (this instanceof File && this.name.endsWith(".xlsx")) throw new Error("UI read an XLSX body");
                    return original.apply(this, args);
                };
            }
        });
        await xlsxPage.locator("#operation").selectOption("extractXlsx");
        await xlsxPage.locator("#capture").setInputFiles(xlsxFixture);
        assert.equal(await xlsxPage.locator("#run").isDisabled(), true);
        await xlsxPage.locator("#sourceFile").setInputFiles(xlsxSource);
        await xlsxPage.locator("#run").click();
        await xlsxPage.waitForFunction(() => document.getElementById("status").textContent.startsWith("Получено 16"));
        const xlsxReport = JSON.parse(await xlsxPage.locator("#result").textContent());
        assert.equal(xlsxReport.sourceCapabilities.numeric, "exact-lexeme");
        assert.equal(xlsxReport.sourceCapabilities.cacheFreshness, "unknown");
        assert.equal(xlsxReport.sourceDiagnostics.missingCaches, 1);
        assert.equal(xlsxReport.preview.records[6].value.value, "2000");
        assert.equal(xlsxReport.preview.records[6].provenance.value.evidence[0].cell.formula.text, "SUM(R12:AC12)");
        const lifecycleXlsx = await xlsxPage.evaluate(async input => {
            const file = document.getElementById("sourceFile").files[0];
            const executor = RNTabularExecutor.create({ mode: "worker", workerUrl: "worker.js" }), code = promise => promise.then(() => "unexpected-success", error => error.code);
            let batches = 0, bytes = 0; const controller = new AbortController();
            const cancelled = await code(executor.extractXlsx(input, file, { signal: controller.signal, onBatch: async () => { batches++; }, onProgress: progress => {
                if (progress.phase === "reading-xlsx" && progress.partBytes > 0) { bytes = progress.partBytes; controller.abort(); }
            } }));
            const rerun = await executor.extractXlsx(input, file, { onBatch: async () => {} }); executor.dispose();
            const cooperative = RNTabularExecutor.create({ mode: "cooperative" });
            const fallback = await code(cooperative.extractXlsx(input, file, { onBatch: async () => {} })); cooperative.dispose();
            const invalid = RNTabularExecutor.create({ mode: "worker", workerUrl: "worker.js" });
            const empty = await code(invalid.extractXlsx(input, new Blob([]), { onBatch: async () => {} }));
            const large = await code(invalid.extractXlsx(input, new Blob([new ArrayBuffer(RNTabularEngine.maxSourceBytes + 1)]), { onBatch: async () => {} })); invalid.dispose();
            return { cancelled, batches, readBytes: bytes > 0, rerun: rerun.recordCount, fallback, empty, large };
        }, xlsxInput);
        assert.deepEqual(lifecycleXlsx, { cancelled: "EXECUTION_CANCELLED", batches: 0, readBytes: true, rerun: 16, fallback: "XLSX_WORKER_REQUIRED", empty: "XLSX_INPUT_LIMIT", large: "XLSX_INPUT_LIMIT" });
        await xlsxPage.locator("#sourceFile").setInputFiles({ name: "invalid.xlsx", mimeType: "application/octet-stream", buffer: Buffer.from("not a zip") });
        assert.equal(await xlsxPage.locator("#download").isDisabled(), true);
        await xlsxPage.locator("#run").click();
        await xlsxPage.waitForFunction(() => document.getElementById("status").textContent.startsWith("XLSX_ZIP_INVALID"));
        assert.equal(await xlsxPage.locator("#result").textContent(), "Нет результата.");
        assert.deepEqual(externalRequests, []);
        console.log("PASS XLSX Worker: real file picker, no UI body reads, source progress/cancel, no cooperative fallback, rerun and stale-result clearing");
        assert.deepEqual(errors, []);
        console.log("Tabular assets: " + assets.length + " files, " + assets.reduce((sum, file) => sum + fs.statSync(assetPath(file)).size, 0) + " bytes; includes pinned sax XML parser");
    } finally {
        if (browser) await browser.close();
        await new Promise(resolve => server.close(resolve));
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
