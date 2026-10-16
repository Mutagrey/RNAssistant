"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path");
const { spawn } = require("node:child_process"), { createInterface } = require("node:readline");
const { chromium } = require("playwright");
const root = path.resolve(__dirname, "../.."), dll = path.join(root, "tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll");
const input = fs.readFileSync(path.join(root, "web/data-templates/core/examples/matrix-xlsx-extraction.json"), "utf8");

function nativeHost(channelMode) {
    assert.ok(fs.existsSync(dll), "Build the host-neutral Harness once before this test.");
    const child = spawn("dotnet", [dll, "--tabular-source-host", ...(channelMode ? [channelMode] : [])], { cwd: root, stdio: ["pipe", "pipe", "pipe"] });
    const pending = [], queued = []; let errorText = "", ended = false, commandHandler;
    child.stderr.on("data", bytes => { errorText += bytes.toString(); });
    const lines = createInterface({ input: child.stdout });
    lines.on("line", line => {
        try {
            const value = JSON.parse(line);
            if (value.command) { Promise.resolve(commandHandler(value.command)).catch(error => { errorText += error.message; child.kill(); }); return; }
            const waiter = pending.shift(); if (waiter) waiter.resolve(value); else queued.push(value);
        }
        catch (error) { errorText += line + "\n"; }
    });
    const exited = new Promise(resolve => child.on("exit", code => {
        ended = true; for (const waiter of pending.splice(0)) waiter.reject(new Error(errorText || "Native host exited: " + code)); resolve(code);
    }));
    function next() {
        if (queued.length) return Promise.resolve(queued.shift());
        if (ended) return Promise.reject(new Error(errorText || "Native host already exited"));
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => { child.kill(); reject(new Error("Native host timed out: " + errorText)); }, 10000);
            pending.push({ resolve: value => { clearTimeout(timer); resolve(value); }, reject: error => { clearTimeout(timer); reject(error); } });
        });
    }
    return { next, onCommand(handler) { commandHandler = handler; }, message(json) { if (!ended) child.stdin.write("message " + json + "\n"); },
        async result() { child.stdin.write("result\n"); return next(); },
        async status() { child.stdin.write("status\n"); return next(); },
        async revoke() { child.stdin.write("cancel\n"); assert.deepEqual(await next(), { revoked: true }); },
        async close() {
            if (!ended) { child.stdin.write("stop\n"); const result = await next(); assert.deepEqual(result, { stopped: true, activeStorage: false }); }
            assert.equal(await exited, 0, errorText); lines.close();
        } };
}

(async () => {
    const browser = await chromium.launch({ headless: true,
        ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    try {
        for (const mode of ["success", "cancel", "sink-failure", "native-revocation"]) {
            const host = nativeHost(), page = await browser.newPage();
            try {
                const ready = await host.next();
                await page.goto(ready.entry);
                await page.waitForFunction(() => !!window.RNTabularNativeRuntime);
                if (mode === "success") {
                    const blocked = await page.evaluate(async () => {
                        window.cspViolations = [];
                        document.addEventListener("securitypolicyviolation", event => window.cspViolations.push(event.effectiveDirective));
                        const script = document.createElement("script"); script.textContent = "window.injected = true"; document.head.append(script);
                        const frame = document.createElement("iframe"); frame.src = location.href; document.body.append(frame);
                        let network = false;
                        try { await fetch("https://example.invalid/no-network"); } catch { network = true; }
                        // DevTools evaluation bypasses unsafe-eval. Run this check
                        // from an ordinary browser event after the CDP call returns.
                        const button = document.createElement("button"); button.id = "csp-eval"; button.textContent = "Check";
                        button.onclick = () => { try { new Function("return 1")(); window.cspEvaluation = false; } catch { window.cspEvaluation = true; } };
                        document.body.append(button);
                        const blob = URL.createObjectURL(new Blob(["postMessage('unexpected')"], { type: "text/javascript" }));
                        const worker = await new Promise(resolve => {
                            try {
                                const candidate = new Worker(blob), timer = setTimeout(() => { candidate.terminate(); resolve(false); }, 2000);
                                candidate.onmessage = () => { clearTimeout(timer); candidate.terminate(); resolve(false); };
                                candidate.onerror = event => { event.preventDefault(); clearTimeout(timer); candidate.terminate(); resolve(true); };
                            } catch { resolve(true); }
                        });
                        URL.revokeObjectURL(blob);
                        return { inline: !window.injected, network, worker };
                    });
                    assert.deepEqual(blocked, { inline: true, network: true, worker: true });
                    await page.locator("#csp-eval").click();
                    assert.equal(await page.evaluate(() => window.cspEvaluation), true);
                    assert.equal(await page.evaluate(() => window.cspViolations.includes("frame-src")), true);
                }
                const execution = page.evaluate(async ({ input, mode }) => {
                    const controller = new AbortController(), records = []; let completed = 0, aborted = 0;
                    const sink = {
                        async writeBatch(batch) {
                            if (mode === "sink-failure") throw Object.assign(new Error("Sink failed"), { code: "TEST_SINK_FAILED" });
                            records.push(...batch.records);
                        }, async complete() {
                            completed++;
                            if (mode === "native-revocation") await new Promise(resolve => { window.releaseNativeComplete = resolve; });
                        }, abort() { aborted++; records.length = 0; }
                    };
                    let count = null, error = null;
                    try {
                        const output = await RNTabularNativeRuntime.extractXlsx(input, { signal: controller.signal, sink,
                            onProgress: () => { if (mode === "cancel") controller.abort(); } });
                        count = output.recordCount;
                    } catch (problem) { error = problem.code || problem.message; }
                    const repeated = await RNTabularNativeRuntime.extractXlsx(input, { sink }).then(() => "unexpected", problem => problem.code);
                    return { count, error, completed, aborted, repeated, records: records.length,
                        sample: records[6] && records[6].value.value, formula: records[6] && records[6].provenance.value.evidence[0].cell.formula.text };
                }, { input, mode });
                if (mode === "native-revocation") {
                    await page.waitForFunction(() => typeof window.releaseNativeComplete === "function");
                    await host.revoke();
                    await page.evaluate(() => window.releaseNativeComplete());
                }
                const result = await execution;
                assert.equal(result.repeated, "NATIVE_SESSION_USED");
                const status = await host.status();
                assert.equal(status.closed, true); assert.equal(status.activeStorage, false);
                if (mode === "success") {
                    assert.deepEqual({ ...result, repeated: undefined }, { count: 16, error: null, completed: 1, aborted: 0, repeated: undefined,
                        records: 16, sample: "2000", formula: "SUM(R12:AC12)" });
                    assert.ok(status.reads > 5 && status.bytes < 3 * 2107, "bounded real native binary reads");
                } else {
                    assert.equal(result.error, mode === "cancel" ? "EXECUTION_CANCELLED"
                        : mode === "native-revocation" ? "NATIVE_SOURCE_CLOSE_FAILED" : "TEST_SINK_FAILED");
                    assert.equal(result.completed, mode === "native-revocation" ? 1 : 0);
                    assert.equal(result.aborted, 1); assert.equal(result.records, 0);
                }
                assert.equal((await page.request.get(ready.entry)).status(), 410, "revoked bundle addresses are unusable");
                console.log(`PASS native resource/Worker: ${mode}, exact retained file, pinned modules, drained native ownership`);
            } finally { await page.close(); await host.close(); }
        }
        for (const mode of ["success", "sink-failure", "hold-batch", "hold-validation", "bad-ack"]) {
            const host = nativeHost(mode), page = await browser.newPage();
            try {
                const ready = await host.next();
                await page.exposeFunction("nativeSend", json => host.message(json));
                await page.addInitScript(() => {
                    window.nativeMessages = [];
                    window.chrome = window.chrome || {};
                    window.chrome.webview = {
                        addEventListener(type, handler) { window.nativeReceive = data => handler({ data }); },
                        postMessage(json) { window.nativeMessages.push(JSON.parse(json)); void window.nativeSend(json); }
                    };
                });
                host.onCommand(json => page.evaluate(({ json, mode }) => {
                    const command = JSON.parse(json);
                    if (mode === "bad-ack" && command.kind === "Acknowledge") command.sequence++;
                    window.nativeReceive(command);
                }, { json, mode }));
                await page.goto(ready.entry);
                if (mode.startsWith("hold-")) {
                    assert.deepEqual(await host.next(), { held: mode === "hold-batch" ? "Batch" : "Validate" });
                    const kinds = await page.evaluate(() => nativeMessages.map(message => message.kind));
                    assert.equal(kinds.filter(kind => kind === "Batch").length, 1);
                    assert.equal(kinds.includes("Succeeded"), false, "no success while native sink is pending");
                    if (mode === "hold-batch") assert.equal(kinds.includes("Validate"), false, "native batch backpressure");
                    await host.revoke();
                }
                const result = await host.result();
                if (mode === "success") {
                    assert.deepEqual(result, { result: true, count: 16, error: null, staged: 16, validated: true, aborts: 0,
                        sample: "2000", formula: "SUM(R12:AC12)" });
                    const status = await host.status();
                    assert.equal(status.closed, true); assert.equal(status.activeStorage, false);
                } else {
                    assert.equal(result.count, null); assert.equal(result.staged, 0); assert.equal(result.aborts, 1);
                    assert.equal(result.validated, false);
                    assert.equal(result.error, mode === "sink-failure" ? "test_sink_failed"
                        : mode === "bad-ack" ? "NATIVE_CHANNEL_INVALID" : "OperationCanceledException");
                }
                console.log(`PASS native typed channel/Worker: ${mode}, acknowledged batches, provisional native sink`);
            } finally { await page.close(); await host.close(); }
        }
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
