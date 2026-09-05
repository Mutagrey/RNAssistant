"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { createHash } = require("node:crypto");
const read = name => fs.readFileSync(path.join(__dirname, "../../web/", name), "utf8");
const hash = bytes => createHash("sha256").update(bytes).digest("hex");
const tick = () => new Promise(resolve => setImmediate(resolve));
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; };

function fixture() {
  const calls = [], chunks = [], cancelled = [], statuses = [];
  const state = { chat: "chat-a", project: [], name: "Module1", code: "'Привет\r\nSub Main()\r\nEnd Sub\r\n",
    guard: "b".repeat(64), available: true, saved: 0, applied: 0 };
  const lease = { leaseId: "a".repeat(64), url: "https://rnassistant.local-resource/v1/upload/" + "a".repeat(64), maxChunkBytes: 262144 };
  const f = { calls, chunks, cancelled, statuses, state, lease, open: null, finish: null, project: null, fetchHook: null };
  const context = vm.createContext({ Blob, TextEncoder, TextDecoder, AbortController, setTimeout, clearTimeout,
    crypto: { subtle: { digest: async (_, bytes) => createHash("sha256").update(bytes).digest() } } });
  context.window = context;
  context.fetch = async (url, options) => {
    assert.equal(options.method, "POST"); assert.equal(options.redirect, "error"); assert.equal(options.credentials, "omit");
    if (f.fetchHook) return f.fetchHook(url, options);
    const query = new URL(url).searchParams, bytes = Buffer.from(await options.body.arrayBuffer());
    assert.equal(bytes.length, Number(query.get("count"))); assert.ok(bytes.length <= 262144);
    chunks.push(bytes);
    return { ok: true, json: async () => ({ leaseId: lease.leaseId, nextOffset: Number(query.get("offset")) + bytes.length }) };
  };
  const send = (type, payload) => {
    calls.push({ type, payload });
    const promise = (async () => {
      assert.equal(Object.hasOwn(payload, "code"), false, "ordinary control messages never carry source code");
      if (type === "beginVbaModuleUpload") {
        assert.deepEqual(Object.keys(payload).sort(), ["byteLength", "chatId"]);
        lease.byteLength = payload.byteLength;
        if (f.open) await f.open.promise;
        return lease;
      }
      if (type === "cancelVbaModuleUpload") return { closed: true };
      if (type === "saveVbaModule" || type === "createVbaModule") return f.finish ? f.finish.promise : { success: true };
      if (type === "getVbaProject") return f.project ? f.project.promise : { success: true };
      throw new Error(type);
    })();
    promise.requestId = "request-" + calls.length;
    return promise;
  };
  vm.runInContext(read("js/app-resource-upload.js"), context);
  vm.runInContext(read("js/app-vba-actions.js"), context);
  f.actions = context.RNAssistantVbaActions.create({ send, cancelRequest: async id => cancelled.push(id),
    getChatId: () => state.chat, getProject: () => state.project, isAvailable: () => state.available,
    getModuleName: () => state.name, getEditorCode: () => state.code, getModuleHash: () => state.guard,
    setStatus: text => statuses.push(text), log: () => {}, previewDiff: () => {},
    markSaved: () => state.saved++, applyProjectResponse: () => state.applied++, loadSelectedModule: async () => {}, selectModule: () => {} });
  f.writes = () => calls.filter(x => /^(save|create)VbaModule$/.test(x.type));
  return f;
}

(async () => {
  {
    const f = fixture(); f.state.code += "'" + "ж".repeat(140000);
    assert.equal(await f.actions.saveModule(), true);
    const bytes = Buffer.concat(f.chunks), payload = f.writes()[0].payload;
    assert.equal(bytes.toString("utf8"), f.state.code); assert.ok(f.chunks.length > 1);
    assert.equal(payload.sourceSha256, hash(bytes)); assert.equal(payload.expectedCodeSha256, f.state.guard);
    assert.equal(payload.chatId, "chat-a"); assert.equal(payload.uploadLeaseId, f.lease.leaseId);
    assert.equal(f.state.saved, 1); assert.equal(f.state.applied, 1);
    const empty = fixture();
    assert.equal(await empty.actions.createModule("UserForm1", "MSForm", ""), true);
    assert.equal(empty.chunks.length, 0); assert.equal(empty.writes()[0].payload.sourceSha256, hash(Buffer.alloc(0)));
    assert.equal(empty.writes()[0].payload.componentType, "MSForm");
    console.log("PASS VBA upload: exact chunked source, metadata-only save/create and empty body");
  }
  {
    for (const invalid of [f => { f.lease.url = "https://foreign/upload"; }, f => { f.lease.maxChunkBytes = 262145; },
      f => { f.state.code = "x".repeat(1000001); }, f => { f.state.code = "\ud800"; },
      f => { f.state.guard = ""; },
      f => { f.fetchHook = async () => ({ ok: true, json: async () => ({ leaseId: "foreign", nextOffset: 1 }) }); }]) {
      const f = fixture(); invalid(f);
      assert.equal(await f.actions.saveModule(), false); assert.equal(f.writes().length, 0); assert.equal(f.state.saved, 0);
    }
    console.log("PASS VBA upload: invalid route, bounds, Unicode and acknowledgements block dispatch");
  }
  {
    for (const change of [f => { f.state.chat = "chat-b"; }, f => { f.state.name = "Module2"; },
      f => { f.state.project = []; }, f => { f.state.code += "'new"; }, f => { f.state.available = false; },
      f => { f.actions.cancelWrite(); assert.equal(f.cancelled.length, 1); }]) {
      const f = fixture(); f.open = deferred();
      const pending = f.actions.saveModule(); await tick();
      assert.equal(await f.actions.saveModule(), false, "busy writes are not queued");
      change(f); f.open.resolve();
      assert.equal(await pending, false); assert.equal(f.writes().length, 0);
      assert.equal(f.calls.filter(x => x.type === "cancelVbaModuleUpload").length, 1, "late capability closes once");
    }
    console.log("PASS VBA upload: pending-open cancellation and stale chat/project/selection/edit never dispatch");
  }
  {
    const f = fixture(); let started = deferred();
    f.fetchHook = (_, options) => new Promise((resolve, reject) => {
      options.signal.addEventListener("abort", () => reject(new Error("aborted")), { once: true }); started.resolve();
    });
    const pending = f.actions.saveModule(); await started.promise;
    f.actions.cancelWrite();
    assert.equal(await pending, false); assert.equal(f.writes().length, 0);
    assert.equal(f.calls.filter(x => x.type === "cancelVbaModuleUpload").length, 1);
    console.log("PASS VBA upload: cancellation aborts the active raw-byte transfer");
  }
  {
    for (const result of [null, { success: false, message: "unknown after dispatch" }]) {
      const f = fixture(); f.finish = deferred(); const pending = f.actions.saveModule(); await tick();
      f.finish.resolve(result); assert.equal(await pending, false);
      assert.equal(f.writes().length, 1); assert.equal(f.state.saved, 0); assert.equal(f.state.applied, 0);
    }
    const f = fixture(); f.finish = deferred(); const pending = f.actions.saveModule(); await tick();
    f.state.code += "'new edit"; f.actions.cancelWrite(); f.finish.resolve({ success: true });
    assert.equal(await pending, false); assert.equal(f.writes().length, 1); assert.equal(f.state.saved, 0);
    assert.equal(f.state.applied, 0); assert.ok(f.statuses.at(-1).includes("не подтверждён"));
    console.log("PASS VBA upload: uncertain or late mutation response cannot mark new edits saved or replay a write");
  }
  {
    const f = fixture(); f.project = deferred(); const pending = f.actions.saveModule(); await tick();
    f.state.code += "'edited during refresh"; f.project.resolve({ success: true });
    assert.equal(await pending, true); assert.equal(f.state.applied, 0, "post-save refresh cannot replace newer editor text");
    for (const name of ["app-chat-state.js", "app-chat-session.js", "app-vba-project.js"])
      assert.ok(read("js/" + name).includes("cancelVbaModuleWrite()"));
    assert.ok(read("js/app-vba.js").includes('window.addEventListener("pagehide", cancelVbaModuleWrite)'));
    const page = read("index.html");
    for (const name of ["app-resource-upload.js", "app-attachments.js", "app-vba-actions.js"])
      assert.ok(page.includes(name + "?v=vba-upload-20260906-1"));
    assert.ok(page.indexOf("app-resource-upload.js") < page.indexOf("app-vba-actions.js"));
    console.log("PASS VBA upload: late refresh, lifecycle wiring and shared uploader delivery");
  }
  console.log("OK 6/6");
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
