"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const crypto = require("node:crypto");
const sha = bytes => crypto.createHash("sha256").update(bytes).digest("hex");
const tick = () => new Promise(resolve => setImmediate(resolve));

function fixture(text = '\uFEFF{"dup":9007199254740993123,"dup":"<script>😀"}', truncated = false) {
  const context = vm.createContext({ AbortController, Uint8Array, TextDecoder, setTimeout, clearTimeout, crypto: crypto.webcrypto });
  context.window = context;
  ["app-resource-download.js", "app-trajectory-payload.js"].forEach(file => {
    vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), context);
  });
  const bytes = new TextEncoder().encode(text), id = "a".repeat(64), calls = [], closes = [], cancels = [];
  const metadata = { chatId: "chat", eventId: "event", contentType: "application/json", returnedCharacters: text.length,
    sha256: truncated ? "b".repeat(64) : sha(bytes), byteLength: bytes.length + (truncated ? 100 : 0), textTruncated: truncated,
    data: { leaseId: id, url: "https://rnassistant.local-resource/v1/download/" + id, maxChunkBytes: 7,
      payload: { sha256: sha(bytes), byteLength: bytes.length, contentType: "text/plain; charset=utf-8" } } };
  const options = { isCurrent: () => true, cancelRequest: async id => { cancels.push(id); },
    send(type, payload) {
      calls.push(type);
      if (type === "resourceDataClose") { closes.push(payload); return Promise.resolve(); }
      assert.deepEqual(JSON.parse(JSON.stringify(payload)), { chatId: "chat", eventId: "event" });
      return Object.assign(Promise.resolve(metadata), { requestId: "request-1" });
    },
    async fetch(url, config) {
      calls.push("fetch");
      assert.equal(config.cache, "no-store");
      const params = new URL(url).searchParams, offset = Number(params.get("offset")), count = Number(params.get("count"));
      return new Response(bytes.slice(offset, offset + count), { headers: { "Content-Type": "text/plain; charset=utf-8" } });
    } };
  return { context, text, bytes, metadata, options, calls, closes, cancels,
    read: () => context.RNAssistantTrajectoryPayload.read("chat", "event", options) };
}

(async function () {
  for (const truncated of [false, true]) {
    const f = fixture(undefined, truncated), result = await f.read();
    assert.equal(result.text, f.text, "BOM, duplicate keys, numeric lexemes and UTF-8 chunks are exact");
    assert.equal(result.textTruncated, truncated);
    assert.equal(f.closes.length, 1);
    assert.equal(f.closes[0].workspaceId, "trajectory-payload");
  }
  console.log("PASS trajectory payload: exact verified bytes, source/preview identities and close");
  {
    const f = fixture(""), result = await f.read();
    assert.equal(result.text, "");
    assert.equal(f.calls.includes("fetch"), false, "empty exact payload requires no GET");
    assert.equal(f.closes.length, 1);
    console.log("PASS trajectory payload: empty is verified full content, not missing evidence");
  }
  {
    for (const mutate of [m => { m.chatId = "foreign"; }, m => { m.eventId = "other"; },
      m => { m.data.payload.contentType = "text/html"; }, m => { m.sha256 = "c".repeat(64); },
      m => { m.byteLength = 32 * 1024 * 1024 + 1; }, m => { m.returnedCharacters = 524289; },
      m => { m.data.payload.byteLength = 4 * 524289 + 1; }, m => { m.data.url = "https://example.com"; }]) {
      const f = fixture(); mutate(f.metadata);
      await assert.rejects(f.read(), /RESOURCE_DOWNLOAD_INVALID/);
      assert.equal(f.calls.includes("fetch"), false);
      assert.equal(f.closes.length, 1);
    }
    console.log("PASS trajectory payload: exact binding and advertised bounds checked before fetch");
  }
  {
    const f = fixture("preview", true);
    f.metadata.data.payload.sha256 = "f".repeat(64);
    await assert.rejects(f.read(), /RESOURCE_INTEGRITY_MISMATCH/);
    assert.equal(f.closes.length, 1);
    const invalid = fixture("x");
    invalid.options.fetch = async () => new Response(new Uint8Array([255]), { headers: { "Content-Type": "text/plain; charset=utf-8" } });
    invalid.metadata.sha256 = invalid.metadata.data.payload.sha256 = sha(new Uint8Array([255]));
    await assert.rejects(invalid.read(), /encoded data|encoding/i);
    assert.equal(invalid.closes.length, 1);
    const extent = fixture(); extent.metadata.returnedCharacters--;
    await assert.rejects(extent.read(), /RESOURCE_DOWNLOAD_INVALID/);
    console.log("PASS trajectory payload: hash, UTF-8 and decoded extent fail closed");
  }
  {
    const f = fixture(), send = f.options.send, abort = new AbortController();
    let resolve;
    f.options.signal = abort.signal;
    f.options.send = (type, payload) => type === "getChatEventPayload"
      ? Object.assign(new Promise(done => { resolve = done; }), { requestId: "late" }) : send(type, payload);
    const reading = f.read(), rejected = assert.rejects(reading, /RESOURCE_DOWNLOAD_CANCELLED/);
    abort.abort();
    assert.deepEqual(f.cancels, ["late"]);
    resolve(f.metadata);
    await rejected;
    assert.equal(f.calls.includes("fetch"), false);
    assert.equal(f.closes.length, 1, "late metadata still revokes the old owner's lease");
    console.log("PASS trajectory payload: cancel propagates to capture and closes late metadata");
  }
  {
    const f = fixture();
    let entered;
    const fetching = new Promise(resolve => { entered = resolve; });
    f.options.fetch = (_, config) => new Promise((resolve, reject) => {
      config.signal.addEventListener("abort", () => reject(new Error("fetch cancelled")), { once: true });
      entered();
    });
    const rejected = assert.rejects(f.read(), /cancelled/);
    await fetching;
    f.context.RNAssistantTrajectoryPayload.cancelAll();
    await rejected;
    assert.equal(f.closes.length, 1);
    console.log("PASS trajectory payload: owner cancellation aborts active data read");
  }
  {
    const f = fixture(), send = f.options.send, pending = [];
    f.options.send = (type, payload) => type === "getChatEventPayload"
      ? new Promise(resolve => pending.push(resolve)) : send(type, payload);
    const first = f.read(), second = f.read();
    await assert.rejects(f.read(), /RESOURCE_BACKPRESSURE/);
    assert.equal(pending.length, 2, "no unbounded capture queue");
    f.options.isCurrent = () => false;
    const rejections = [first, second].map(reading => assert.rejects(reading, /RESOURCE_DOWNLOAD_CANCELLED/));
    pending.forEach(resolve => resolve(f.metadata));
    await Promise.all(rejections);
    await tick();
    assert.equal(f.closes.length, 2);
    const closing = fixture(), originalSend = closing.options.send;
    let releaseClose, closeStarted;
    const started = new Promise(resolve => { closeStarted = resolve; });
    closing.options.send = (type, payload) => {
      const sent = originalSend(type, payload);
      if (type !== "resourceDataClose") return sent;
      return new Promise(resolve => { releaseClose = resolve; closeStarted(); });
    };
    const late = assert.rejects(closing.read(), /RESOURCE_DOWNLOAD_CANCELLED/);
    await started;
    closing.options.isCurrent = () => false;
    releaseClose();
    await late;
    console.log("PASS trajectory payload: bounded captures and stale context never render");
  }
  console.log("OK 7/7");
})().catch(error => { console.error(error); process.exitCode = 1; });
