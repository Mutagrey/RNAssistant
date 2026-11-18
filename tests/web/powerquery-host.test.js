"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const host = fs.readFileSync(path.join(__dirname, "../../web/powerquery/host.js"), "utf8");
const request = { requestId: "analysis1", source: 'let x = "</script><script>evil()</script>" in x', sourceSha256: "a".repeat(64) };
function fixture() {
    const state = { output: [], input: [], stopped: 0, listener: null, worker: null };
    const bridge = {
        postMessage(value) { state.output.push(JSON.parse(value)); },
        addEventListener(name, handler) { assert.equal(name, "message"); state.listener = handler; }
    };
    class Worker {
        constructor(url) { assert.equal(url, "/worker.js"); state.worker = this; }
        postMessage(value) { state.input.push(value); }
        terminate() { state.stopped++; }
    }
    vm.runInNewContext(host, { window: { chrome: { webview: bridge } }, Worker });
    state.ready = () => state.worker.onmessage({ data: { type: "ready" } });
    state.start = (value = request) => state.listener({ data: { type: "analyze", request: value } });
    state.complete = (value = {}) => state.worker.onmessage({ data: { type: "complete", result: {
        requestId: request.requestId, sourceSha256: request.sourceSha256,
        syntax: "passed", parserVersion: "5.0.0", diagnostics: [], reason: null, ...value
    } } });
    return state;
}
{
    const f = fixture(); f.ready(); f.start();
    assert.deepEqual(f.output, [{ type: "ready" }]);
    assert.equal(f.input.length, 1);
    assert.equal(f.input[0].request.source, request.source, "M remains exact inert text");
    f.complete(); f.complete(); f.start();
    assert.equal(f.output.length, 2, "one terminal with no late message");
    assert.equal(f.output[1].type, "complete"); assert.equal(f.stopped, 1);
}
{
    for (const wrong of [{ requestId: "other" }, { sourceSha256: "b".repeat(64) }]) {
        const f = fixture(); f.ready(); f.start(); f.complete(wrong);
        assert.equal(f.output[1].type, "failed"); assert.equal(f.stopped, 1);
    }
}
{
    for (const run of [f => f.start(), f => { f.ready(); f.ready(); },
        f => { f.ready(); f.start(); f.start(); },
        f => { f.ready(); f.start({ ...request, source: "x".repeat(262145) }); }]) {
        const f = fixture(); run(f);
        assert.equal(f.output.at(-1).type, "failed"); assert.equal(f.stopped, 1);
        f.complete(); assert.notEqual(f.output.at(-1).type, "complete");
    }
}
{
    const f = fixture(); f.ready(); f.start(); f.complete({ reason: "x".repeat(33000) });
    assert.equal(f.output[1].type, "failed"); assert.equal(f.stopped, 1);
    let prevented = false;
    const g = fixture(); g.worker.onerror({ preventDefault() { prevented = true; } });
    assert.equal(g.output[0].type, "failed"); assert.equal(g.stopped, 1); assert.ok(prevented);
}
console.log("PASS Power Query host: exact handshake/correlation, inert source, one terminal, bounds and worker failure (4 groups)");
