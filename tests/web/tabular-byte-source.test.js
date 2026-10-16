"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const context = vm.createContext({ Blob, AbortController });
vm.runInContext(fs.readFileSync(path.resolve(__dirname, "../../web/data-templates/core/byte-source.js"), "utf8"), context);
const api = context.RNTabularByteSource;
const rejects = (promise, code) => assert.rejects(promise, error => error.code === code);

(async () => {
    let calls = 0, closes = 0;
    const bytes = new Uint8Array([11, 22, 33, 44]);
    const source = api.create({ byteLength: 4, label: "Exact source", readRange: (offset, count) => {
        calls++; return bytes.subarray(offset, offset + count);
    }, close: () => { closes++; } });
    const result = await source.readRange(1, 2);
    assert.deepEqual([...result], [22, 33]); bytes[1] = 99;
    assert.equal(result[0], 22, "provider buffer reuse cannot mutate accepted bytes");
    for (const [offset, count] of [[-1, 1], [0, 0], [0.5, 1], [0, 1.5], [3, 2], [Number.MAX_SAFE_INTEGER, 1], [0, api.maximumReadBytes + 1]])
        await rejects(source.readRange(offset, count), "SOURCE_RANGE_INVALID");
    assert.equal(calls, 1, "invalid reads never reach the provider");
    source.dispose(); source.dispose(); assert.equal(closes, 1);
    await rejects(source.readRange(0, 1), "SOURCE_CLOSED");
    assert.throws(() => api.create({ byteLength: Number.MAX_SAFE_INTEGER + 1, label: "bad", readRange() {}, close() {} }), error => error.code === "SOURCE_CONTRACT_INVALID");
    console.log("PASS byte source: fixed length, exact ranges, detached bytes, bounds before dispatch and close once");

    let release, providerSignal, entered;
    const started = new Promise(resolve => { entered = resolve; });
    const waiting = api.create({ byteLength: 2, label: "Pending", readRange: (offset, count, signal) => {
        providerSignal = signal; entered(); return new Promise(resolve => { release = resolve; });
    }, close() {} });
    const pending = waiting.readRange(0, 1); await started;
    await rejects(waiting.readRange(1, 1), "SOURCE_READ_PENDING");
    waiting.dispose(); await rejects(pending, "SOURCE_CLOSED");
    assert.equal(providerSignal.aborted, true);
    release(new Uint8Array([7])); await Promise.resolve();
    await rejects(waiting.readRange(0, 1), "SOURCE_CLOSED");
    console.log("PASS byte source: single flight and prompt revocation even if the provider ignores cancellation");

    for (const value of [new Uint8Array(1), new Uint8Array(3), [1, 2], "12", new Uint16Array(1), new Uint8Array(new SharedArrayBuffer(2))]) {
        const invalid = api.create({ byteLength: 2, label: "Invalid", readRange: () => value, close() {} });
        await rejects(invalid.readRange(0, 2), "SOURCE_READ_INVALID"); invalid.dispose();
    }
    const asyncClose = api.create({ byteLength: 1, label: "Bad close", readRange() {}, close: async () => {} });
    assert.throws(() => asyncClose.dispose(), error => error.code === "SOURCE_CLOSE_INVALID");
    assert.equal(asyncClose.closed, true, "invalid cleanup cannot retain usable access");
    const blob = api.fromBlob(new Blob([new Uint8Array([3, 2, 1])]));
    assert.deepEqual([...await blob.readRange(1, 2)], [2, 1]); blob.dispose();
    console.log("PASS byte source: malformed/shared replies rejected, synchronous close required, Blob adapter uses the same contract");
})().catch(error => { console.error(error); process.exitCode = 1; });
