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
const assets = ["index.html", "probe.css", "probe.js", "selectors.js", "snapshot.js", "engine.js", "executor.js", "worker.js"];

(async () => {
    // The loopback server is a test host only. The portable page also runs from file:// below.
    const server = http.createServer((request, response) => {
        const file = request.url.slice(1);
        response.setHeader("Cache-Control", "no-store");
        if (file === "busy-worker.js" || file === "foreign-worker.js") {
            response.setHeader("Content-Type", "text/javascript");
            response.end('postMessage({type:"ready",protocol:1,revision:"tabular-m0-1"}); onmessage = event => {' +
                (file === "busy-worker.js"
                    ? 'postMessage({type:"progress",id:event.data.id,progress:{completedCells:0,totalCells:1}}); while(true) {}'
                    : 'postMessage({type:"result",id:event.data.id+1,result:{version:1,engineRevision:"tabular-m0-1"}});') + '}');
        } else if (assets.includes(file)) {
            response.setHeader("Content-Type", file.endsWith(".js") ? "text/javascript" : file.endsWith(".css") ? "text/css" : "text/html");
            response.end(fs.readFileSync(path.join(root, file)));
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
        assert.deepEqual(errors, []);
        await offline.close();
        console.log("PASS tabular portable: offline file://, separate scripts, picker/download, Worker parity and no stale result after failure");
        console.log("Tabular M0 assets: " + assets.length + " files, " + assets.reduce((sum, file) => sum + fs.statSync(path.join(root, file)).size, 0) + " bytes; no vendor dependencies");
    } finally {
        if (browser) await browser.close();
        await new Promise(resolve => server.close(resolve));
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
