"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const crypto = require("node:crypto");

function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}
function fixture(source = "{}") {
  const nodes = new Map(), closes = [], renders = [], fetches = [];
  function node(id) {
    if (!nodes.has(id)) {
      const classes = new Set();
      nodes.set(id, { value: "draft", textContent: "", disabled: false,
        setAttribute() {}, classList: {
          add: name => classes.add(name), remove: name => classes.delete(name),
          contains: name => classes.has(name),
          toggle: (name, force) => force ? classes.add(name) : classes.delete(name)
        } });
    }
    return nodes.get(id);
  }
  const bytes = Buffer.from(source, "utf8");
  const lease = { leaseId: "a".repeat(64), url: "https://rnassistant.local-resource/v1/download/" + "a".repeat(64),
    payload: { byteLength: bytes.length, sha256: crypto.createHash("sha256").update(bytes).digest("hex"),
      contentType: "text/plain; charset=utf-8" }, maxChunkBytes: 65536 };
  const c = vm.createContext({ AbortController, TextDecoder, Uint8Array, setTimeout, clearTimeout,
    crypto: crypto.webcrypto, state: { activeChatId: "chat-a", draftAttachments: [] }, $: node,
    send: async (method, payload) => {
      if (method === "closeResourceData") { closes.push(payload); return {}; }
      assert.equal(method, "inspectPromptContext");
      return { chatId: payload.chatId, rawData: lease, rawTruncated: true };
    }, fetchImpl: async (url, options) => {
      fetches.push(options);
      const query = new URL(url).searchParams;
      const offset = Number(query.get("offset")), count = Number(query.get("count"));
      return new Response(bytes.subarray(offset, offset + count), { headers: { "Content-Type": lease.payload.contentType } });
    }
  });
  vm.runInContext('window = globalThis; fetch = function(url, options) { if (this !== window) throw new Error("Illegal invocation"); return fetchImpl(url, options); };', c);
  for (const name of ["app-resource-download.js", "app-context-inspector.js"])
    vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", name), "utf8"), c);
  c.clearPromptContextRawViewer = () => {};
  c.renderPromptContextInspector = snapshot => renders.push(snapshot);
  return { c, lease, node, closes, renders, fetches };
}
function assertClosed(f) {
  assert.equal(f.closes.length, 1);
  assert.equal(f.closes[0].chatId, "chat-a");
  assert.equal(f.closes[0].workspaceId, "context-inspector");
  assert.equal(f.closes[0].leaseId, f.lease.leaseId);
}

(async () => {
  const raw = '\uFEFF{"text":"' + "語😀".repeat(60000) + '"}';
  const success = fixture(raw);
  await success.c.loadPromptContextInspector(true);
  assert.equal(success.c.promptContextInspectorRawText, raw);
  assert.equal(success.renders.length, 1);
  assert.ok(success.fetches.length > 1, "source uses bounded chunks and bound native fetch");
  assertClosed(success);
  success.c.closePromptContextInspector();
  assert.equal(success.c.promptContextInspectorRawText, "");
  assert.equal(success.c.promptContextInspectorSnapshot, null);
  console.log("PASS context inspector download: exact UTF-8, chunking and cache lifecycle");

  const invalid = fixture();
  invalid.lease.payload.sha256 = "b".repeat(64);
  await invalid.c.loadPromptContextInspector(true);
  assert.equal(invalid.c.promptContextInspectorRawText, "");
  assert.equal(invalid.renders.length, 0);
  assert.match(invalid.node("promptContextInspectorError").textContent, /INTEGRITY_MISMATCH/);
  assertClosed(invalid);
  const legacy = fixture();
  legacy.c.send = async () => ({ rawRequestJson: "legacy body" });
  await legacy.c.loadPromptContextInspector(true);
  assert.equal(legacy.renders.length, 0);
  assert.equal(legacy.c.promptContextInspectorRawText, "");
  assert.match(legacy.node("promptContextInspectorError").textContent, /DOWNLOAD_INVALID/);
  console.log("PASS context inspector download: corrupt and legacy inline responses fail closed");

  const late = fixture(), first = deferred(), second = deferred();
  const originalSend = late.c.send;
  let calls = 0;
  late.c.send = (method, payload) => method === "inspectPromptContext"
    ? (++calls === 1 ? first.promise : second.promise) : originalSend(method, payload);
  const oldRequest = late.c.loadPromptContextInspector(true);
  await late.c.loadPromptContextInspector(true);
  assert.equal(calls, 1, "only one request per open panel");
  late.c.closePromptContextInspector();
  late.node("promptContextInspector").classList.remove("hidden");
  const newRequest = late.c.loadPromptContextInspector(false);
  const newOperation = late.c.promptContextInspectorRequest;
  first.resolve({ rawData: late.lease });
  await oldRequest;
  assertClosed(late);
  assert.equal(late.renders.length, 0);
  assert.equal(late.c.promptContextInspectorRequest, newOperation, "late cleanup cannot reset a newer request");
  second.resolve({ chatId: "chat-a" });
  await newRequest;
  assert.equal(late.renders.length, 1);
  console.log("PASS context inspector download: late response closes its lease without changing reopened panel");

  const cancelled = fixture(), fetching = deferred();
  let fetchSignal;
  cancelled.c.fetchImpl = (url, options) => {
    fetchSignal = options.signal;
    fetching.resolve();
    return new Promise((resolve, reject) => options.signal.addEventListener("abort", () => reject(new Error("aborted")), { once: true }));
  };
  const pending = cancelled.c.loadPromptContextInspector(true);
  await fetching.promise;
  cancelled.c.state.activeChatId = "chat-b";
  cancelled.c.syncPromptContextInspectorState();
  await pending;
  assert.equal(fetchSignal.aborted, true);
  assert.equal(cancelled.renders.length, 0);
  assert.equal(cancelled.c.promptContextInspectorRawText, "");
  assertClosed(cancelled);
  console.log("PASS context inspector download: chat switch aborts fetch and closes original owner lease");
})().catch(error => { console.error(error); process.exitCode = 1; });
