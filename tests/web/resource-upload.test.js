"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "../../web/js/app-attachments.js"), "utf8");
const id = "a".repeat(64), url = "https://rnassistant.local-resource/v1/upload/" + id;
const file = new Blob([new Uint8Array([0, 255, 65, 13, 10, 128, 42])], { type: "image/png" });
file.name = "image.png";

function fixture() {
  const calls = [], chunks = [], events = new EventTarget();
  const context = vm.createContext({ AbortController, setTimeout, clearTimeout });
  context.window = context;
  context.addEventListener = events.addEventListener.bind(events);
  context.removeEventListener = events.removeEventListener.bind(events);
  const lease = { leaseId: id, url, byteLength: file.size, maxChunkBytes: 3 };
  context.send = async (type, payload) => {
    calls.push({ type, payload });
    assert.equal(payload.chatId, "chat-a");
    if (type === "beginChatResourceUpload") {
      assert.deepEqual(Object.keys(payload).sort(), ["byteLength", "chatId", "contentType", "fileName"]);
      return lease;
    }
    if (type === "completeChatResourceUpload") return { resource: { Id: "draft-a" } };
    if (type === "cancelChatResourceUpload") return { closed: true };
    if (type === "discardChatResourceDraft") return { deleted: true };
    throw new Error("unexpected control message: " + type);
  };
  context.fetch = async (target, options) => {
    const params = new URL(target).searchParams;
    assert.equal(options.method, "POST");
    assert.equal(options.credentials, "omit");
    assert.equal(options.redirect, "error");
    assert.equal(options.body.type, "application/octet-stream");
    const bytes = new Uint8Array(await options.body.arrayBuffer());
    assert.equal(bytes.length, Number(params.get("count")));
    assert.ok(bytes.length <= 3);
    chunks.push(...bytes);
    return { ok: true, json: async () => ({ leaseId: id, nextOffset: Number(params.get("offset")) + bytes.length }) };
  };
  vm.runInContext(source, context);
  return { context, calls, chunks, lease, events };
}

(async function () {
  {
    const f = fixture();
    const realFetch = f.context.fetch;
    let release;
    f.context.fetch = async (...args) => {
      if (!f.chunks.length) await new Promise(resolve => { release = resolve; });
      return realFetch(...args);
    };
    const operation = f.context.uploadChatResourceFile("chat-a", file);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(f.chunks.length, 0);
    assert.equal(f.calls.length, 1, "no completion or queued producer while a chunk is unacknowledged");
    release();
    assert.equal((await operation).resource.Id, "draft-a");
    assert.deepEqual(f.chunks, Array.from(new Uint8Array(await file.arrayBuffer())), "binary bytes are unchanged");
    assert.deepEqual(f.calls.map(call => call.type), ["beginChatResourceUpload", "completeChatResourceUpload"]);
    console.log("PASS upload: bounded sequential raw bytes, control metadata only");
  }
  {
    const f = fixture();
    f.context.fetch = async () => ({ ok: true, json: async () => ({ leaseId: "b".repeat(64), nextOffset: 3 }) });
    await assert.rejects(f.context.uploadChatResourceFile("chat-a", file), /RESOURCE_CURSOR_INVALID/);
    assert.deepEqual(f.calls.map(call => call.type), ["beginChatResourceUpload", "cancelChatResourceUpload"]);
    console.log("PASS upload: mismatched acknowledgement cancels without completion or retry");
  }
  {
    const f = fixture();
    f.lease.url = "https://example.com/upload";
    await assert.rejects(f.context.uploadChatResourceFile("chat-a", file), /RESOURCE_UPLOAD_INVALID/);
    assert.equal(f.chunks.length, 0, "a noncanonical route never receives file bytes");
    console.log("PASS upload: canonical capability route validation precedes data dispatch");
  }
  {
    const f = fixture();
    const signal = new AbortController();
    f.context.fetch = async (_, options) => {
      signal.abort();
      assert.equal(options.signal.aborted, true, "cancellation aborts the active fetch");
      throw new Error("aborted");
    };
    await assert.rejects(f.context.uploadChatResourceFile("chat-a", file, signal.signal), /aborted/);
    assert.equal(f.calls.at(-1).type, "cancelChatResourceUpload");
    assert.equal(f.calls.filter(call => call.type === "completeChatResourceUpload").length, 0);
    console.log("PASS upload: cancellation aborts data transport and releases the lease");
  }
  {
    const f = fixture(), send = f.context.send;
    f.context.send = async (type, payload) => {
      const value = await send(type, payload);
      if (type === "completeChatResourceUpload") f.events.dispatchEvent(new Event("pagehide"));
      return value;
    };
    await assert.rejects(f.context.uploadChatResourceFile("chat-a", file), /RESOURCE_UPLOAD_CANCELLED/);
    assert.equal(f.calls.at(-1).type, "discardChatResourceDraft", "a known late draft is not leaked after consumer close");
    assert.equal(f.calls.at(-1).payload.id, "draft-a");
    console.log("PASS upload: page close discards a known late completion");
  }
  assert.equal(/fileToBase64|readAsDataURL|send\("stageChatResource"/.test(source), false);
  console.log("PASS upload: legacy bridge body path is removed");
  console.log("OK 6/6");
}()).catch(error => { console.error(error.stack || error); process.exitCode = 1; });
