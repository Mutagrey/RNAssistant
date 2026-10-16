"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), http = require("node:http");
const { chromium } = require("playwright");
const root = path.resolve(__dirname, "../../web/tabular"), input = fs.readFileSync(path.join(root, "examples/matrix-xlsx-extraction.json"), "utf8");
// Virtual leading space makes a valid ZIP32 80 MiB larger without allocating a whole file.
// This checks bounded random reads, not representative workbook parsing performance.
const prefix = 80 * 1048576, archive = fs.readFileSync(path.join(root, "examples/matrix-source.xlsx"));
const end = archive.length - 22, directory = archive.readUInt32LE(end + 16);
for (let position = directory; position < end;) {
    archive.writeUInt32LE(archive.readUInt32LE(position + 42) + prefix, position + 42);
    position += 46 + archive.readUInt16LE(position + 28) + archive.readUInt16LE(position + 30) + archive.readUInt16LE(position + 32);
}
archive.writeUInt32LE(directory + prefix, end + 16);
const length = prefix + archive.length, reads = [];
const scripts = ["selectors.js", "snapshot.js", "json.js", "decimal.js", "sources.js", "byte-source.js", "expression.js", "template.js", "transform.js", "engine.js", "executor.js"];
const assets = new Set([...scripts, "worker.js", "xlsx-zip.js", "xlsx-reader.js", "js/vendor/sax.js"]);
const fakeWorkers = {
    "old.js": 'postMessage({type:"ready",protocol:4,revision:"tabular-core-2"});',
    "range.js": 'request({offset:0,count:262145});',
    "foreign.js": 'request({id:event.data.id+1});',
    "sequence.js": 'request({sequence:2});',
    "concurrent.js": 'request(); request({sequence:2});',
    "batch.js": 'request(); postMessage({type:"batch",id:event.data.id,batch:{version:1,sequence:1,records:[{}]}});',
    "result.js": 'request(); postMessage({type:"result",id:event.data.id,result:{version:2,engineRevision:"tabular-core-2",kind:"extraction-result",recordCount:0,batchCount:0,coverage:{complete:true}}});',
    "amplification.js": 'request({sequence:++sequence});'
};

(async () => {
    const server = http.createServer((request, response) => {
        const url = new URL(request.url, "http://localhost"), file = url.pathname.slice(1);
        response.setHeader("Cache-Control", "no-store");
        if (file === "source") {
            const offset = Number(url.searchParams.get("offset")), count = Number(url.searchParams.get("count"));
            if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(count) || offset < 0 || count < 1 || count > 262144 || offset > length - count) {
                response.writeHead(400); response.end(); return;
            }
            reads.push({ offset, count }); const chunk = Buffer.alloc(count), start = Math.max(prefix, offset), stop = Math.min(length, offset + count);
            if (start < stop) archive.copy(chunk, start - offset, start - prefix, stop - prefix);
            response.setHeader("Content-Type", "application/octet-stream"); response.end(chunk); return;
        }
        if (file === "index.html") {
            response.setHeader("Content-Type", "text/html; charset=utf-8");
            response.setHeader("Content-Security-Policy", "default-src 'none'; script-src 'self'; worker-src 'self'; connect-src 'self'; base-uri 'none'");
            response.end('<!doctype html><html><head>' + scripts.map(file => `<script src="${file}"></script>`).join("") + '</head><body></body></html>'); return;
        }
        response.setHeader("Content-Type", "text/javascript");
        if (Object.prototype.hasOwnProperty.call(fakeWorkers, file)) {
            response.end(file === "old.js" ? fakeWorkers[file] : 'postMessage({type:"ready",protocol:5,revision:"tabular-core-2"}); let sequence=0; onmessage=event=>{' +
                'const request=extra=>postMessage({type:"source-read",id:event.data.id,protocol:5,revision:"tabular-core-2",sequence:1,offset:0,count:1,...extra});' +
                fakeWorkers[file] + '};'); return;
        }
        if (!assets.has(file)) { response.writeHead(404); response.end(); return; }
        response.end(fs.readFileSync(path.join(root, file === "js/vendor/sax.js" ? "../" + file : file)));
    });
    await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
    const browser = await chromium.launch({ headless: true,
        ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    try {
        const page = await browser.newPage(), errors = []; page.on("pageerror", error => errors.push(error.message));
        await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
        await page.evaluate(({ input, length }) => {
            window.input = input; window.sourceLength = length;
            window.code = promise => promise.then(() => "unexpected-success", error => error.code || error.message);
            window.make = (file = "worker.js") => RNTabularExecutor.create({ mode: "worker", workerUrl: file });
            window.reader = (offset, count, signal) => fetch(`source?offset=${offset}&count=${count}`, { signal }).then(response => response.arrayBuffer());
            window.source = (readRange = reader, close = () => {}, byteLength = sourceLength) => RNTabularByteSource.create({ byteLength, label: "Pinned book.xlsx", readRange, close });
            window.sink = () => ({ records: [], completed: 0, aborted: 0,
                async writeBatch(batch) { this.records.push(...batch.records); },
                async complete() { this.completed++; }, abort() { this.aborted++; this.records = []; } });
        }, { input, length });
        const parsed = await page.evaluate(async () => {
            const executor = make(), output = sink(); let closes = 0, busy = 0, maximumBusy = 0;
            const bytes = source(async (...args) => { maximumBusy = Math.max(maximumBusy, ++busy); try { return await reader(...args); } finally { busy--; } }, () => { closes++; });
            const result = await executor.extractXlsxSource(input, bytes, { sink: output }); executor.dispose();
            return { count: result.recordCount, sourceBytes: result.sourceDiagnostics.sourceBytes, closes, maximumBusy, closed: bytes.closed,
                completed: output.completed, aborted: output.aborted, sample: output.records[6].value.value, formula: output.records[6].provenance.value.evidence[0].cell.formula.text };
        });
        assert.deepEqual(parsed, { count: 16, sourceBytes: length, closes: 1, maximumBusy: 1, closed: true, completed: 1, aborted: 0, sample: "2000", formula: "SUM(R12:AC12)" });
        assert.ok(reads.length > 5 && reads.every(read => read.count <= 262144));
        assert.ok(reads.reduce((sum, read) => sum + read.count, 0) < 1024 * 1024, "80 MiB source is never transferred whole");
        console.log("PASS pull-source Worker: real XLSX oracle from sparse 80 MiB source, bounded binary transfers and close once");

        const cancellation = await page.evaluate(async () => {
            const executor = make();
            async function stalled(mode) {
                const output = sink(), controller = new AbortController(); let enter, release, closes = 0, providerSignal;
                const entered = new Promise(resolve => { enter = resolve; });
                const bytes = source((offset, count, signal) => { providerSignal = signal; enter(); return new Promise(resolve => { release = () => resolve(new Uint8Array(count)); }); }, () => { closes++; });
                const result = code(executor.extractXlsxSource(input, bytes, { sink: output, signal: controller.signal, deadlineMs: mode === "deadline" ? 200 : 5000 }));
                await entered;
                if (mode === "cancel") controller.abort();
                if (mode === "source-close") bytes.dispose();
                if (mode === "dispose") executor.dispose();
                const outcome = await result; release(); await Promise.resolve();
                return { outcome, closes, aborted: output.aborted, completed: output.completed, signal: providerSignal.aborted };
            }
            const cancelled = await stalled("cancel"), revoked = await stalled("source-close"), timed = await stalled("deadline");
            const output = sink(), rerun = await executor.extractXlsxSource(input, source(), { sink: output });
            const disposed = await stalled("dispose");
            return { cancelled, revoked, timed, disposed, rerun: rerun.recordCount };
        });
        for (const [name, outcome] of [["cancelled", "EXECUTION_CANCELLED"], ["revoked", "SOURCE_CLOSED"], ["timed", "EXECUTION_TIMEOUT"], ["disposed", "EXECUTOR_DISPOSED"]])
            assert.deepEqual(cancellation[name], { outcome, closes: 1, aborted: 1, completed: 0, signal: true });
        assert.equal(cancellation.rerun, 16);
        console.log("PASS pull-source lifecycle: cancellation, source revocation, deadline/disposal and ignored late data without replay");

        const invalid = await page.evaluate(async () => {
            const results = [];
            for (const file of ["old.js", "range.js", "foreign.js", "sequence.js", "concurrent.js", "batch.js", "result.js", "amplification.js"]) {
                const executor = make(file), output = sink(); let calls = 0, closes = 0;
                const bytes = source(() => { calls++; return file === "amplification.js" ? new Uint8Array(1) : new Promise(() => {}); }, () => { closes++; }, 2);
                const outcome = await code(executor.extractXlsxSource(input, bytes, { sink: output, deadlineMs: 2000 })); executor.dispose();
                results.push({ file, outcome, calls, closes, aborted: output.aborted, completed: output.completed });
            }
            return results;
        });
        for (const result of invalid) {
            assert.equal(result.outcome, result.file === "old.js" ? "EXECUTOR_REVISION_MISMATCH" : "EXECUTOR_PROTOCOL_INVALID", result.file);
            assert.equal(result.closes, 1); assert.equal(result.aborted, 1); assert.equal(result.completed, 0);
            assert.equal(result.calls, result.file === "amplification.js" ? 6 : ["concurrent.js", "batch.js", "result.js"].includes(result.file) ? 1 : 0);
        }
        console.log("PASS pull-source protocol: old/foreign/oversized/out-of-order/concurrent reads and premature output rejected before success");

        const failures = await page.evaluate(async () => {
            const executor = make(), results = [];
            for (const mode of ["short", "provider", "close", "async-close", "preflight"]) {
                const output = sink(); let closes = 0, calls = 0;
                const bytes = source(mode === "short" ? () => new Uint8Array(1) : mode === "provider"
                    ? () => Promise.reject(Object.assign(new Error("Exact payload unavailable"), { code: "snapshot_unavailable" }))
                    : (...args) => { calls++; return reader(...args); },
                    () => { closes++; if (mode === "close") throw new Error("lease failed to close"); if (mode === "async-close") return Promise.resolve(); });
                const request = mode === "preflight" ? JSON.stringify({ ...JSON.parse(input), version: 1 }) : input;
                const outcome = await code(executor.extractXlsxSource(request, bytes, { sink: output }));
                results.push({ mode, outcome, calls, closes, aborted: output.aborted, records: output.records.length });
            }
            executor.dispose(); return results;
        });
        for (const result of failures) {
            assert.equal(result.outcome, result.mode === "short" ? "SOURCE_READ_INVALID" : result.mode === "provider" ? "snapshot_unavailable"
                : result.mode === "preflight" ? "VERSION_UNSUPPORTED" : "SOURCE_CLOSE_FAILED");
            if (result.mode === "preflight") assert.equal(result.calls, 0, "request preflight precedes expensive source input");
            assert.equal(result.closes, 1); assert.equal(result.aborted, 1); assert.equal(result.records, 0);
        }
        assert.deepEqual(errors, []);
        console.log("PASS pull-source failures: malformed/unavailable bytes and failed closure revoke provisional output");

        const invalidReplies = await page.evaluate(async () => {
            const results = [];
            for (const mode of ["id", "sequence", "offset", "length", "protocol"]) results.push(await new Promise((resolve, reject) => {
                const worker = new Worker("worker.js"), timeout = setTimeout(() => { worker.terminate(); reject(new Error("Worker did not refuse " + mode)); }, 2000);
                worker.onmessage = event => {
                    const message = event.data;
                    if (message.type === "ready") worker.postMessage({ type: "extractXlsx", id: 1, protocol: 5, revision: "tabular-core-2", input,
                        source: { byteLength: sourceLength, label: "Pinned source" } });
                    else if (message.type === "source-read") {
                        const reply = { ...message, type: "source-bytes", bytes: new ArrayBuffer(message.count) };
                        if (mode === "length") reply.bytes = new ArrayBuffer(1); else reply[mode]++;
                        worker.postMessage(reply, [reply.bytes]);
                    } else if (message.type === "error") { clearTimeout(timeout); worker.terminate(); resolve(message.error.code); }
                    else if (message.type === "result" || message.type === "batch") { clearTimeout(timeout); worker.terminate(); reject(new Error("Unverified bytes produced output")); }
                };
            }));
            return results;
        });
        assert.deepEqual(invalidReplies, Array(5).fill("EXECUTOR_PROTOCOL_INVALID"));
        console.log("PASS Worker source receipts: foreign run, sequence, offset, length and protocol cannot satisfy a pending read");
    } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
