"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const crypto = require("node:crypto");
const source = fs.readFileSync(path.join(__dirname, "../../web/js/app-resource-download.js"), "utf8");
const bytes = new Uint8Array([0, 255, 80, 75, 13, 10, 128]);
const id = "a".repeat(64);

function fixture() {
  const context = vm.createContext({ AbortController, Uint8Array, setTimeout, clearTimeout, crypto: crypto.webcrypto });
  context.window = context;
  vm.runInContext(source, context);
  const data = { leaseId: id, url: "https://rnassistant.local-resource/v1/download/" + id, maxChunkBytes: 3,
    payload: { sha256: crypto.createHash("sha256").update(bytes).digest("hex"), byteLength: bytes.length, contentType: "application/zip" } };
  const calls = [];
  const options = { maxBytes: 24 * 1024 * 1024, isCurrent: () => true,
    fetch: async (url, config) => {
      const params = new URL(url).searchParams;
      const offset = Number(params.get("offset")), count = Number(params.get("count"));
      calls.push({ offset, count });
      assert.equal(config.credentials, "omit");
      assert.equal(config.redirect, "error");
      return new Response(bytes.slice(offset, offset + count), { headers: { "Content-Type": "application/zip" } });
    } };
  return { context, data, options, calls, read: () => context.RNAssistantResourceDownload.read(data, options) };
}

(async function () {
  {
    const f = fixture(), fetch = f.options.fetch;
    let release;
    f.options.fetch = async (...args) => {
      const response = await fetch(...args);
      if (f.calls.length === 1) await new Promise(resolve => { release = resolve; });
      return response;
    };
    const reading = f.read();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(f.calls.length, 1, "no next request is produced before the current response is consumed");
    release();
    assert.deepEqual(await reading, bytes);
    assert.deepEqual(f.calls, [{ offset: 0, count: 3 }, { offset: 3, count: 3 }, { offset: 6, count: 1 }]);
    console.log("PASS download: sequential bounded bytes and exact full-payload integrity");
  }
  {
    const f = fixture();
    f.data.payload.sha256 = "b".repeat(64);
    await assert.rejects(f.read(), /RESOURCE_INTEGRITY_MISMATCH/);
    console.log("PASS download: hash mismatch cannot produce a downloadable bundle");
  }
  {
    for (const size of [2, 4]) {
      const f = fixture();
      f.options.fetch = async () => new Response(new Uint8Array(size), { headers: { "Content-Type": "application/zip" } });
      await assert.rejects(f.read(), size === 2 ? /RESOURCE_SNAPSHOT_UNAVAILABLE/ : /RESOURCE_BATCH_TOO_LARGE/);
    }
    console.log("PASS download: short and oversized response bodies fail closed");
  }
  {
    for (const mutate of [f => { f.data.url = "https://example.com/leak"; }, f => { f.data.maxChunkBytes = 262145; },
      f => { f.data.payload.byteLength = f.options.maxBytes + 1; }]) {
      const f = fixture(); mutate(f);
      await assert.rejects(f.read(), /RESOURCE_DOWNLOAD_INVALID/);
      assert.equal(f.calls.length, 0);
    }
    console.log("PASS download: route and advertised bounds are checked before fetch/allocation");
  }
  {
    const f = fixture(), abort = new AbortController();
    let cancelled = false;
    f.options.signal = abort.signal;
    f.options.fetch = async (_, config) => {
      abort.abort();
      assert.equal(config.signal.aborted, true);
      return new Response(new ReadableStream({ cancel() { cancelled = true; } }), { headers: { "Content-Type": "application/zip" } });
    };
    await assert.rejects(f.read(), /RESOURCE_DOWNLOAD_CANCELLED/);
    assert.equal(cancelled, true, "a cancelled consumer releases its reader");
    console.log("PASS download: cancellation aborts the active request and reader");
  }
  {
    const f = fixture(), fetch = f.options.fetch;
    f.options.fetch = async (...args) => { const response = await fetch(...args); f.options.isCurrent = () => false; return response; };
    await assert.rejects(f.read(), /RESOURCE_DOWNLOAD_CANCELLED/);
    assert.equal(f.calls.length, 1);
    console.log("PASS download: context changes stop delivery without fallback");
  }
  {
    const f = fixture(), originalFetch = f.options.fetch;
    f.data.url = f.data.url.replace("/download/", "/");
    f.data.binary = { payload: f.data.payload }; delete f.data.payload;
    f.data.maxBatchBytes = f.data.maxBatchItems = 3;
    f.options.fetch = (url, config) => {
      assert.ok(url.includes("&limit=")); assert.ok(!url.includes("&count="));
      return originalFetch(url.replace("&limit=", "&count="), config);
    };
    const result = await f.context.RNAssistantResourceDownload.readBinary(f.data, f.options);
    assert.deepEqual(Array.from(result), Array.from(bytes));
    assert.deepEqual(f.calls.map(call => call.offset), [0, 3, 6]);
    f.data.binary.payload.sha256 = "b".repeat(64);
    await assert.rejects(f.context.RNAssistantResourceDownload.readBinary(f.data, f.options), /RESOURCE_INTEGRITY_MISMATCH/);
    f.data.binary.payload.byteLength = 0;
    f.data.binary.payload.sha256 = crypto.createHash("sha256").update("").digest("hex");
    let emptyReads = 0;
    f.options.fetch = async url => { emptyReads++; assert.match(url, /offset=0&limit=1$/);
      return new Response(new Uint8Array(), { headers: { "Content-Type": "application/zip" } }); };
    assert.equal((await f.context.RNAssistantResourceDownload.readBinary(f.data, f.options)).length, 0);
    assert.equal(emptyReads, 1, "empty resources still verify their pinned CAS through the route");
    f.data.maxBatchBytes = f.data.maxBatchItems = 20 * 1024 * 1024;
    await assert.rejects(f.context.RNAssistantResourceDownload.readBinary(f.data, f.options), /RESOURCE_DOWNLOAD_INVALID/);
    console.log("PASS binary download: sequential resource chunks, full hash and no whole-body fallback");
  }
  console.log("OK 7/7");
}()).catch(error => { console.error(error.stack || error); process.exitCode = 1; });
