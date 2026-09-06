"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm"), crypto = require("node:crypto");
const read = file => fs.readFileSync(path.join(__dirname, "../../web", file), "utf8");
const sha = bytes => crypto.createHash("sha256").update(bytes).digest("hex");
function deferred() { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; }
function fixture(text = "# Справка\r\n" + "ж".repeat(140000) + "😀") {
  const bytes = Buffer.from(text), calls = [], closed = [], cancelled = [], errors = [], elements = {};
  const tool = { Id: "common.inspect", Revision: "library", BuiltIn: true };
  const state = { host: "Excel", tools: [tool], selectedToolIndex: 0, selectedInstructionKind: "tool", activeChatId: "chat", toolEditorPage: "docs" };
  const response = { type: "rnassistant.toolLibraryDocumentation", contractVersion: 1, chatId: "chat", toolId: tool.Id, revision: tool.Revision,
    resource: { uri: "rna://catalog/builtin-tools-excel/common.inspect/documentation", revision: "r_exact" },
    data: { leaseId: "a".repeat(64), url: "https://rnassistant.local-resource/v1/download/" + "a".repeat(64), maxChunkBytes: 65536,
      payload: { sha256: sha(bytes), byteLength: bytes.length, contentType: "text/markdown; charset=utf-8" } } };
  const context = vm.createContext({ AbortController, TextDecoder, Uint8Array, setTimeout, clearTimeout, crypto: crypto.webcrypto,
    $: id => elements[id] || (elements[id] = { textContent: "", classList: { toggle() {} } }),
    async fetch(url, config) {
      calls.push("fetch"); assert.equal(config.credentials, "omit"); assert.equal(config.redirect, "error");
      const params = new URL(url).searchParams, offset = Number(params.get("offset")), count = Number(params.get("count"));
      return new Response(bytes.subarray(offset, offset + count), { headers: { "Content-Type": "text/markdown; charset=utf-8" } });
    }
  });
  context.window = context;
  for (const file of ["app-resource-download.js", "app-tools-documentation.js"]) vm.runInContext(read("js/" + file), context);
  const options = { state, log: error => errors.push(error), cancelRequest: async id => cancelled.push(id),
    send(action, payload) {
      calls.push(action);
      if (action === "resourceDataClose") { closed.push(payload); return Promise.resolve(); }
      assert.equal(action, "getToolDocumentation");
      assert.deepEqual(Object.keys(payload).sort(), ["chatId", "contractVersion", "expectedRevision", "toolId", "type"]);
      assert.equal(payload.chatId, "chat"); assert.equal(payload.expectedRevision, tool.Revision);
      return Object.assign(Promise.resolve(response), { requestId: "docs" });
    }
  };
  const docs = context.RNAssistantToolDocumentation.create(options);
  return { context, state, tool, bytes, text, response, options, docs, elements, calls, closed, cancelled, errors };
}
(async () => {
  {
    for (const text of [undefined, "", "\ufeff# Literal leading character\r\n"]) {
      const f = fixture(text); f.state.toolEditorPage = "main"; f.docs.prepare(f.tool); await f.docs.ensure();
      assert.equal(f.calls.length, 0);
      f.state.toolEditorPage = "docs"; await f.docs.ensure();
      assert.equal(f.elements.toolDocumentationMarkdown.textContent, f.text); assert.equal(f.errors.length, 0);
      assert.equal(f.closed.length, 1); assert.equal(f.closed[0].chatId, "chat"); assert.equal(f.closed[0].workspaceId, "tool-editor");
      await f.docs.ensure(); assert.equal(f.calls.filter(call => call === "getToolDocumentation").length, 1, "exact selected cache is reused");
      assert.equal(f.tool.Readme, undefined); assert.equal(f.state.toolDocumentationCache, undefined, "no state-wide unbounded body dictionary");
    }
    console.log("PASS tool documentation: lazy complete Unicode/CRLF download, shared chunks and exact selected cache");
  }
  {
    for (const change of [f => { f.response.markdown = "legacy"; }, f => { f.response.chatId = "foreign"; },
      f => { f.response.revision = "stale"; }, f => { f.response.resource.revision = ""; },
      f => { f.response.resource.uri = "rna://catalog/builtin-tools-word/common.inspect/documentation"; },
      f => { f.response.resource.uri = "rna://catalog/builtin-tools-excel/common.other/documentation"; },
      f => { f.response.data.url = "https://foreign/docs"; }, f => { f.response.data.payload.byteLength = 2 * 1024 * 1024 + 1; },
      f => { f.response.data.payload.sha256 = "f".repeat(64); }, f => { f.context.fetch = async () => new Response(new Uint8Array([255])); }]) {
      const f = fixture(); change(f); await f.docs.ensure();
      assert.equal(f.elements.toolDocumentationMarkdown.textContent, ""); assert.equal(f.errors.length, 1); assert.equal(f.closed.length, 1);
    }
    console.log("PASS tool documentation: inline legacy, wrong address/revision, size, truncation and integrity failures cannot render");
  }
  {
    for (const change of [f => { f.state.activeChatId = "other"; }, f => { f.state.tools = [{ ...f.tool }]; },
      f => { f.state.toolEditorPage = "main"; }, f => { f.state.selectedInstructionKind = "skill"; },
      f => { f.tool.Revision = "new"; }, f => { f.state.bridgeUnavailable = true; }, f => f.docs.cancel()]) {
      const f = fixture(), pending = deferred(), send = f.options.send;
      f.options.send = (action, payload) => action === "getToolDocumentation" ? Object.assign(pending.promise, { requestId: "late" }) : send(action, payload);
      const reading = f.docs.ensure(); change(f); pending.resolve(f.response); await reading;
      assert.equal(f.elements.toolDocumentationMarkdown.textContent, ""); assert.equal(f.closed.length, 1);
      assert.equal(f.closed[0].chatId, "chat"); assert.equal(f.calls.includes("fetch"), false); assert.equal(f.errors.length, 0);
    }
    const f = fixture(), pending = deferred(); f.context.fetch = async () => pending.promise;
    const reading = f.docs.ensure(); await new Promise(resolve => setImmediate(resolve)); f.docs.cancel();
    pending.resolve(new Response(f.bytes)); await reading; assert.equal(f.closed.length, 1); assert.equal(f.elements.toolDocumentationMarkdown.textContent, "");
    console.log("PASS tool documentation: cancelled and late chat/selection/page/revision downloads close once without stale UI");
  }
  {
    const f = fixture(), opens = [], send = f.options.send;
    f.options.send = (action, payload) => { if (action !== "getToolDocumentation") return send(action, payload);
      const pending = deferred(); opens.push(pending); return Object.assign(pending.promise, { requestId: "pending" }); };
    const a = f.docs.ensure(); f.docs.cancel(); const b = f.docs.ensure(); f.docs.cancel(); await f.docs.ensure();
    assert.equal(opens.length, 2, "cancelled opens retain their pending capacity until settled");
    opens.forEach((pending, index) => pending.resolve({ ...f.response, data: { ...f.response.data, leaseId: String(index + 1).repeat(64) } }));
    await Promise.all([a, b]); assert.equal(f.closed.length, 2); assert.equal(f.cancelled.length, 2);
    f.options.send = send; await f.docs.ensure(); assert.equal(f.elements.toolDocumentationMarkdown.textContent, f.text);
    f.docs.prepare({ BuiltIn: false }); assert.equal(f.elements.toolDocumentationMarkdown.textContent, "");
    f.docs.prepare(f.tool); await f.docs.ensure(); assert.equal(f.calls.filter(call => call === "getToolDocumentation").length, 2, "leaving the selected builtin drops its clean body");
    console.log("PASS tool documentation: bounded pending opens, cancellation and selection-scoped cache disposal");
  }
  {
    for (const file of ["app-chat-state.js", "app-chat-session.js", "app-prompts.js"]) assert.ok(read("js/" + file).includes("cancelToolDocumentationRead()"));
    assert.ok(read("js/app-tools.js").includes('window.addEventListener("pagehide", cancelToolDocumentationRead)'));
    assert.ok(!read("js/app-tools-documentation.js").includes("toolDocumentationCache"));
    assert.ok(!read("js/app-tools-documentation.js").includes("toolDocumentationRequests"));
    assert.ok(read("index.html").includes("app-tools-documentation.js?v=tool-docs-20260906-1"));
    console.log("PASS tool documentation: lifecycle and cache cutover ship together with no inline reader");
  }
  console.log("OK 5/5");
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
