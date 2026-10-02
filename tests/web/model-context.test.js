"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const crypto = require("node:crypto");
class Node {
  constructor(tag = "div") { this.tag = tag; this.children = []; this.listeners = {}; this.dataset = {}; this.value = ""; this.isConnected = true; this.classes = new Set(); this.classList = {
    add: x => this.classes.add(x), remove: x => this.classes.delete(x), contains: x => this.classes.has(x),
    toggle: (x, force) => force ? this.classes.add(x) : this.classes.delete(x)
  }; }
  set textContent(value) { this.text = value; this.children = []; }
  get textContent() { return this.text || ""; }
  appendChild(node) { this.children.push(node); node.parent = this; return node; }
  replaceChildren() { this.children.forEach(n => n.isConnected = false); this.children = []; this.text = ""; }
  remove() { if (this.parent) this.parent.children = this.parent.children.filter(n => n !== this); this.isConnected = false; }
  setAttribute() {}
  addEventListener(type, fn) { (this.listeners[type] ||= []).push(fn); }
  fire(type) { return Promise.all((this.listeners[type] || []).map(fn => fn.call(this, { key: "", preventDefault() {}, stopPropagation() {} }))); }
  focus() {}
  querySelectorAll() { return []; }
}
async function flush() { for (let i = 0; i < 25; i++) await new Promise(resolve => setImmediate(resolve)); }
function fixture() {
  const nodes = new Map(), mounts = [], calls = [], closes = [], timers = new Map(), leases = new Map(); let timer = 0;
  const get = id => { if (!nodes.has(id)) nodes.set(id, new Node()); return nodes.get(id); };
  const requestText = '{"model":"test","messages":[{"role":"system","content":"rules"},{"role":"user","content":"' + "語😀".repeat(110000) + '"}],"seed":9007199254740993123456789}';
  const events = [{ eventId: "r1", sequence: 3, createdUtc: "2026-10-02T10:00:00Z", type: "llm.request", hasPayload: true, trace: { RequestId: "one", Model: "test", Streaming: true } }];
  let revision = 3, hold = null, leaseCount = 0;
  const c = vm.createContext({ AbortController, TextDecoder, TextEncoder, Uint8Array, Blob, URL, Response,
    setTimeout: (fn, ms) => { timers.set(++timer, { fn, ms }); return timer; }, clearTimeout: id => timers.delete(id),
    crypto: crypto.webcrypto, state: { activeChatId: "chat", draftAttachments: [] }, $: get,
    document: { createElement: tag => new Node(tag), body: new Node(), activeElement: new Node() },
    activeChatSummary: () => ({ revision }), promptContextResourceDraftIds: () => [], closePromptContextInspector() {},
    cancelBridgeRequest: async () => {}, copyTextResult() {},
    send: async (type, query) => {
      calls.push({ type, query });
      if (type === "resourceDataClose") { closes.push(query); return {}; }
      if (type === "getModelContext") {
        if (query.requestEventId) return { chatId: "chat", revision, events: events.filter(e => e.eventId === query.requestEventId || e.trace.RequestId === events.find(r => r.eventId === query.requestEventId).trace.RequestId) };
        return query.knownRevision === revision ? { chatId: "chat", revision, unchanged: true } : { chatId: "chat", revision, events: events.filter(e => e.type === "llm.request").slice().reverse() };
      }
      if (hold) await hold;
      const text = type === "inspectModelContextPreview" ? '{"messages":[{"role":"user","content":"draft"}]}' : query.eventId === "response1" ? '{"Content":"# Answer"}' : requestText;
      const bytes = Buffer.from(text), id = (++leaseCount).toString(16).padStart(64, "0"); leases.set(id, bytes);
      const data = { leaseId: id, url: "https://rnassistant.local-resource/v1/download/" + id, maxChunkBytes: 65536,
        payload: { sha256: crypto.createHash("sha256").update(bytes).digest("hex"), byteLength: bytes.length, contentType: "application/json" } };
      return type === "inspectModelContextPreview" ? { chatId: "chat", rawData: data, generatedUtc: "2026-10-02T10:00:00Z" } : { chatId: "chat", eventId: query.eventId, data };
    },
    fetch: async url => { const parsed = new URL(url), bytes = leases.get(parsed.pathname.split("/").pop());
      const offset = Number(parsed.searchParams.get("offset")), count = Number(parsed.searchParams.get("count"));
      return new Response(bytes.subarray(offset, offset + count), { headers: { "Content-Type": "application/json" } }); }
  });
  c.window = c;
  c.RNAssistantViewerRegistry = { mount(kind, node, options) { mounts.push({ kind, node, options }); }, unmount() {} };
  for (const name of ["app-resource-download.js", "app-model-context.js"]) vm.runInContext(fs.readFileSync("web/js/" + name, "utf8"), c);
  c.bindModelContextActions();
  return { c, get, mounts, calls, closes, requestText, events, timers,
    bump() { revision++; }, hold(value) { hold = value; },
    async tick() { const entry = Array.from(timers).find(([, t]) => t.ms === 2000); timers.delete(entry[0]); entry[1].fn(); await flush(); }
  };
}
(async () => {
  const f = fixture(), api = f.c.RNAssistantModelContext;
  const raw = '{"n":9007199254740993123456789,"n":2,"text":"escaped \\\" }, : [ ","nested":[{"x":true}]}';
  const parts = api.members(raw);
  assert.equal(parts[0].raw, "9007199254740993123456789");
  assert.equal(parts[1].key, "n", "duplicate keys retained");
  assert.equal(parts[3].raw, '[{"x":true}]');
  assert.equal(api.members('["a,b",{"nested":[1,2]}]').length, 2);
  api.open(); await flush();
  assert.equal(f.closes.length, 1, "exact download lease closes after reading");
  assert.equal(f.get("modelContextAttempt").value, "r1");
  f.get("modelContextTab").value = "json"; await f.get("modelContextTab").fire("change"); await flush();
  assert.equal(f.mounts.at(-1).options.text, f.requestText, "large request is not clipped or reserialized");
  const count = f.mounts.length;
  f.bump(); await f.tick();
  assert.equal(f.mounts.length, count, "same request keeps expanded viewers while other events arrive");
  f.events.push({ eventId: "response1", sequence: 6, type: "llm.response", hasPayload: true, createdUtc: "2026-10-02T10:00:30Z", trace: { RequestId: "one" } });
  f.bump(); await f.tick();
  f.get("modelContextTab").value = "response"; await f.get("modelContextTab").fire("change"); await flush();
  const responseDetails = f.get("modelContextBody").children[0].children.find(n => n.tag === "details");
  responseDetails.open = true; await responseDetails.fire("toggle"); await flush();
  assert.equal(f.mounts.at(-1).kind, "markdown", "response message renders as Markdown independently of envelope");
  assert.equal(f.mounts.at(-1).options.fullText, "# Answer");
  f.get("modelContextTab").value = "json"; await f.get("modelContextTab").fire("change"); await flush();
  f.events.push({ eventId: "r2", sequence: 9, type: "llm.request", hasPayload: true, createdUtc: "2026-10-02T10:01:00Z", trace: { RequestId: "two" } });
  f.bump(); await f.tick();
  assert.equal(f.get("modelContextAttempt").value, "r2", "follow newest attempt");
  f.get("modelContextAttempt").value = "r1"; await f.get("modelContextAttempt").fire("change"); await flush();
  f.events.push({ eventId: "r3", sequence: 12, type: "llm.request", createdUtc: "2026-10-02T10:02:00Z", trace: { RequestId: "three" } });
  f.bump(); await f.tick(); assert.equal(f.get("modelContextAttempt").value, "r1", "historical selection pauses following");
  f.get("modelContextMode").value = "preview"; await f.get("modelContextMode").fire("change"); await flush();
  assert.match(f.get("modelContextStatus").textContent, /Предпросмотр/);
  f.get("chatInput").value = "changed"; await f.tick(); assert.match(f.get("modelContextStatus").textContent, /устарел/);
  f.c.state.activeChatId = "another"; api.sync(); assert.ok(f.get("modelContextDialog").classList.contains("hidden"));
  const late = fixture(); let release;
  late.hold(new Promise(resolve => release = resolve)); late.c.RNAssistantModelContext.open(); await flush();
  late.c.RNAssistantModelContext.close(); release(); await flush();
  assert.equal(late.closes.length, 1, "late payload acquisition closes its lease even after dialog closed");
  assert.equal(late.mounts.length, 0, "late payload never renders into a closed or other chat");
  console.log("PASS model context: exact JSON, full download, following, preview staleness and cancellation");
})().catch(error => { console.error(error); process.exitCode = 1; });
