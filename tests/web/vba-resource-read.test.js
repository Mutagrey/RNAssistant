"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const crypto = require("node:crypto");
const sha = bytes => crypto.createHash("sha256").update(bytes).digest("hex");
const page = fs.readFileSync(path.join(__dirname, "../../web/index.html"), "utf8");

function fixture(text = "Sub Main()\r\n'😀\r\nEnd Sub\r\n") {
  const module = { name: "Module1" }, calls = [], errors = [], closes = [], cancelled = [];
  const bytes = new TextEncoder().encode(text), id = "a".repeat(64);
  const metadata = { chatId: "chat", moduleName: "Module1", componentType: "StdModule", lineCount: 3,
    totalCharacters: text.length, codeSha256: sha(text.replace(/\r\n/g, "\n").replace(/\r/g, "\n").replace(/\n$/, "")),
    resource: { uri: "rna://vba/doc/component/key", revision: "r_source" },
    data: { leaseId: id, url: "https://rnassistant.local-resource/v1/download/" + id, maxChunkBytes: 7,
      payload: { sha256: sha(bytes), byteLength: bytes.length, contentType: "text/plain; charset=utf-8" } } };
  const elements = { vbaModuleSelect: { value: "Module1" }, vbaStatus: { textContent: "" }, vbaModuleSearchInput: { value: "" } };
  const context = vm.createContext({ AbortController, Uint8Array, TextDecoder, setTimeout, clearTimeout, crypto: crypto.webcrypto,
    state: { activeChatId: "chat", vba: { modules: [module] } }, $: id => elements[id], log: error => errors.push(error),
    cancelBridgeRequest: async id => { cancelled.push(id); },
    send(type, payload) {
      calls.push(type);
      if (type === "resourceDataClose") { closes.push(payload); return Promise.resolve(); }
      assert.equal(type, "getVbaModule");
      assert.deepEqual(JSON.parse(JSON.stringify(payload)), { chatId: "chat", moduleName: "Module1" });
      return Object.assign(Promise.resolve(metadata), { requestId: "read-1" });
    },
    async fetch(url, config) {
      calls.push("fetch"); assert.equal(config.redirect, "error");
      const params = new URL(url).searchParams, offset = Number(params.get("offset")), count = Number(params.get("count"));
      return new Response(bytes.slice(offset, offset + count), { headers: { "Content-Type": "text/plain; charset=utf-8" } });
    } });
  context.window = context;
  ["app-resource-download.js", "app-vba-project.js"].forEach(file => {
    vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), context);
  });
  context.renderSelectedVbaModule = () => {};
  context.renderVbaModuleList = () => {};
  return { context, text, metadata, module, errors, calls, closes, cancelled, elements, read: () => context.loadSelectedVbaModule() };
}

(async function () {
  {
    for (const text of ["Sub Main()\r\n'😀\r\nEnd Sub\r\n", ""]) {
      const f = fixture(text); await f.read();
      assert.equal(f.module.code, text);
      assert.equal(f.module.codeSha256, f.metadata.codeSha256, "write guard is not replaced with raw transport hash");
      assert.equal(f.module.resource.revision, "r_source");
      assert.equal(f.context.hasVbaModuleCode(f.module), true);
      assert.equal(f.closes.length, 1);
      assert.equal(f.closes[0].workspaceId, "vba-editor");
      assert.equal(f.errors.length, 0);
    }
    console.log("PASS VBA resource read: exact UTF-8/CRLF and empty source preserve runtime write guard");
  }
  {
    for (const mutate of [m => { m.chatId = "other"; }, m => { m.moduleName = "Other"; }, m => { m.resource.revision = ""; },
      m => { m.totalCharacters = 1000001; }, m => { m.codeSha256 = ""; }, m => { m.data.payload.byteLength = 4000001; },
      m => { m.data.url = "https://example.com"; }]) {
      const f = fixture(); mutate(f.metadata); await f.read();
      assert.equal(f.context.hasVbaModuleCode(f.module), false);
      assert.equal(f.calls.includes("fetch"), false);
      assert.equal(f.closes.length, 1);
      assert.equal(f.errors.length, 1);
    }
    for (const mutate of [m => { m.totalCharacters--; }, m => { m.data.payload.sha256 = "f".repeat(64); }]) {
      const f = fixture(); mutate(f.metadata); await f.read();
      assert.equal(f.context.hasVbaModuleCode(f.module), false);
      assert.equal(f.closes.length, 1);
      assert.equal(f.errors.length, 1);
    }
    console.log("PASS VBA resource read: wrong source, partial bytes and corruption cannot enable editing");
  }
  {
    for (const change of ["selection", "project", "chat", "cancel"]) {
      const f = fixture(), send = f.context.send;
      let release;
      f.context.send = (type, payload) => type === "getVbaModule"
        ? Object.assign(new Promise(resolve => { release = resolve; }), { requestId: "late" }) : send(type, payload);
      const reading = f.read(); await f.read();
      assert.equal(f.context.vbaModuleReadPending, 1, "duplicate selection does not start another capture");
      if (change === "selection") f.elements.vbaModuleSelect.value = "Other";
      if (change === "project") f.context.state.vba.modules = [{ name: "Module1" }];
      if (change === "chat") f.context.state.activeChatId = "other";
      if (change === "cancel") { f.context.cancelVbaModuleRead(); assert.deepEqual(f.cancelled, ["late"]); }
      release(f.metadata); await reading;
      assert.equal(f.context.hasVbaModuleCode(f.module), false);
      assert.equal(f.calls.includes("fetch"), false);
      assert.equal(f.closes.length, 1);
    }
    console.log("PASS VBA resource read: late selection/project/chat responses are closed without applying code");
  }
  {
    const f = fixture(); let entered;
    const fetching = new Promise(resolve => { entered = resolve; });
    f.context.fetch = (_, config) => new Promise((resolve, reject) => {
      config.signal.addEventListener("abort", () => reject(new Error("cancelled")), { once: true }); entered();
    });
    const reading = f.read(); await fetching;
    f.context.cancelVbaModuleRead(); await reading;
    assert.equal(f.closes.length, 1);
    assert.equal(f.context.hasVbaModuleCode(f.module), false);
    assert.equal(f.context.vbaModuleReadPending, 0);
    const source = fs.readFileSync(path.join(__dirname, "../../web/js/app-vba.js"), "utf8");
    assert.match(source, /pagehide.*cancelVbaModuleRead/);
    ["app-chat-state.js", "app-chat-session.js"].forEach(file => {
      assert.match(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), /cancelVbaModuleRead\(\)/);
    });
    ["app-vba.js", "app-vba-project.js"].forEach(file => {
      assert.ok(page.includes(file + "?v=vba-upload-20260906-1"));
    });
    ["app-chat-state.js", "app-chat-session.js"].forEach(file => {
      assert.ok(page.includes(file + "?v=skill-resource-20260906-1"));
    });
    console.log("PASS VBA resource read: cancellation aborts active transfer and is wired to owner lifecycle");
  }
  console.log("OK 4/4");
})().catch(error => { console.error(error); process.exitCode = 1; });
