"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const crypto = require("node:crypto");
const read = file => fs.readFileSync(path.join(__dirname, "../../web", file), "utf8");
const sha = bytes => crypto.createHash("sha256").update(bytes).digest("hex");
function deferred() { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; }

function fixture(readme = "# Справка\r\n" + "ж".repeat(140000) + "😀", kind = "custom") {
  const body = { argumentSchemaJson: '{"type":"object","properties":{}}', code: "Option Explicit\r\n", readme,
    components: [{ name: "RNA_Test", type: "StdModule", fileName: "RNA_Test.bas", code: "Option Explicit\r\n", codeSha256: "b".repeat(64) }] };
  const bytes = Buffer.from(JSON.stringify(body)), calls = [], closes = [], errors = [], cancelled = [], elements = {};
  const wire = { revision: "base", id: "excel.source", host: "Excel", name: "excel.source", description: "Description",
    source: { sha256: sha(bytes), byteLength: bytes.length }, executor: kind === "builtin" ? "builtin" : "vba",
    requiresConfirmation: true, mutatesDocument: true, mutatesLocalState: false, canSourceHtmlData: false, agentCanRun: false,
    enabled: true, builtIn: kind === "builtin", riskLevel: 1, useWhen: "", doNotUseWhen: "", capabilityStatus: "available", limitations: "",
    packageVersion: "1.0.0", entryPoint: "RNA_Test.Run", argumentOrder: [], scope: kind === "document" ? "document" : "global", installationStatus: "not_installed" };
  const response = { type: "rnassistant.toolSourceRead", contractVersion: 1, chatId: "chat", toolId: wire.id, revision: "base",
    sources: [{ uri: kind === "document" ? "rna://vba/document-authority/component/" + sha("rna_test") :
      "rna://catalog/" + (kind === "builtin" ? "builtin-tools-excel" : "tools") + "/excel.source/source", revision: "r_exact" }],
    data: { leaseId: "a".repeat(64), url: "https://rnassistant.local-resource/v1/download/" + "a".repeat(64), maxChunkBytes: 65536,
      payload: { sha256: sha(bytes), byteLength: bytes.length, contentType: "application/json; charset=utf-8" } } };
  const state = { host: "Excel", tools: [], selectedToolIndex: 0, selectedInstructionKind: "tool", activeChatId: "chat" };
  const context = vm.createContext({ AbortController, TextEncoder, TextDecoder, Uint8Array, setTimeout, clearTimeout, crypto: crypto.webcrypto,
    state, $: id => elements[id] || (elements[id] = { value: "", checked: false }), setControlBusy() {}, logToolResult() {},
    log: error => errors.push(error), cancelBridgeRequest: async id => cancelled.push(id),
    RNAssistantToolStructuredEditor: { create: () => ({}) }, RNAssistantToolDocumentation: { create: () => ({ cancel() {} }) }, RNAssistantToolActions: { create: () => ({}) },
    send(action, payload) {
      calls.push(action);
      if (action === "resourceDataClose") { closes.push(payload); return Promise.resolve(); }
      assert.equal(action, "readToolSource"); assert.deepEqual(Object.keys(payload).sort(), ["chatId", "contractVersion", "expectedRevision", "toolId", "type"]);
      assert.equal(payload.chatId, "chat"); assert.equal(payload.expectedRevision, "base");
      return Object.assign(Promise.resolve(response), { requestId: "read" });
    },
    async fetch(url, config) {
      calls.push("fetch"); assert.equal(config.redirect, "error"); assert.equal(config.credentials, "omit");
      const params = new URL(url).searchParams, offset = Number(params.get("offset")), count = Number(params.get("count"));
      return new Response(bytes.subarray(offset, offset + count), { headers: { "Content-Type": "application/json; charset=utf-8" } });
    }
  });
  context.window = context;
  for (const file of ["app-resource-download.js", "app-tools.js"]) vm.runInContext(read("js/" + file), context);
  context.renderToolEditor = () => context.updateToolWriteControls();
  const tool = context.toolFromContract(wire); state.tools = [tool]; context.acceptToolLibraryState();
  return { context, state, tool, wire, body, bytes, response, calls, closes, errors, cancelled, elements,
    load: () => context.loadSelectedToolSource(tool) };
}

(async () => {
  {
    for (const kind of ["custom", "builtin", "document"]) for (const text of ["", "\ufeff# Справка\r\n" + "ж".repeat(140000) + "😀"]) {
      const f = fixture(text, kind); await f.load();
      assert.equal(f.tool.Readme, text, f.errors.join("; ")); assert.equal(f.tool._sourceLoaded, true); assert.equal(f.closes.length, 1); assert.equal(f.errors.length, 0);
      assert.equal(f.closes[0].workspaceId, "tool-editor"); assert.equal(f.closes[0].chatId, "chat");
      f.context.updateToolLibraryDirty(); assert.equal(f.state.toolLibraryDirty, false, "hydration is not an authoring edit");
      await f.load(); assert.equal(f.calls.filter(call => call === "readToolSource").length, 1, "selected clean source is reused");
    }
    console.log("PASS tool source: exact custom/builtin/document snapshots, bounded chunks, Unicode and hydration-neutral baseline");
  }
  {
    for (const mutate of [f => { f.response.chatId = "foreign"; }, f => { f.response.revision = "new"; },
      f => { f.response.sources[0].uri = "rna://catalog/tools/other/source"; }, f => { f.response.sources[0].revision = ""; },
      f => { f.response.type = "legacy"; }, f => { f.response.data.url = "https://foreign/source"; },
      f => { f.response.data.payload.sha256 = "f".repeat(64); }, f => { f.response.data.payload.byteLength--; },
      f => { f.context.fetch = async () => new Response(new Uint8Array([255])); },
      f => { f.bytes[0] = 65; f.wire.source.sha256 = sha(f.bytes); f.tool.Source.sha256 = sha(f.bytes); f.response.data.payload.sha256 = sha(f.bytes); }]) {
      const f = fixture(); mutate(f); await f.load();
      assert.equal(!!f.tool._sourceLoaded, false); assert.equal(f.tool.Code, undefined); assert.equal(f.errors.length, 1); assert.equal(f.closes.length, 1);
      f.context.syncSelectedToolFromEditor(); assert.equal(f.tool.Readme, undefined, "failed read never becomes an empty draft");
      assert.equal(f.elements.cloneToolButton.disabled, true); assert.equal(f.elements.runToolButton.disabled, true);
      f.tool.Description = "Edit";
      assert.throws(() => f.context.toolLibraryMutations(), /не загружен/);
    }
    const f = fixture(); assert.throws(() => f.context.toolFromContract({ ...f.wire, code: "inline" }), /typed package/);
    f.tool.Source.byteLength = 16 * 1024 * 1024 + 1; await f.load(); assert.equal(f.calls.length, 0, "oversize rejected before request");
    console.log("PASS tool source: invalid metadata, partial/corrupt bytes, old inline catalog and oversized sources fail closed");
  }
  {
    for (const change of [f => { f.state.activeChatId = "other"; }, f => { f.state.tools = []; },
      f => { f.state.selectedInstructionKind = "skill"; }, f => { f.tool.Revision = "changed"; },
      f => { f.tool.Source.sha256 = "f".repeat(64); }, f => f.context.cancelToolSourceRead()]) {
      const f = fixture(), pending = deferred(), send = f.context.send;
      f.context.send = (action, payload) => action === "readToolSource" ? Object.assign(pending.promise, { requestId: "late" }) : send(action, payload);
      const reading = f.load(); change(f); pending.resolve(f.response); await reading;
      assert.equal(!!f.tool._sourceLoaded, false); assert.equal(f.closes.length, 1); assert.equal(f.closes[0].chatId, "chat");
      assert.equal(f.calls.includes("fetch"), false); assert.equal(f.errors.length, 0);
    }
    const f = fixture(), pending = deferred(); f.context.fetch = async () => pending.promise;
    const reading = f.load(); await new Promise(resolve => setImmediate(resolve)); f.context.cancelToolSourceRead();
    pending.resolve(new Response(f.bytes)); await reading; assert.equal(!!f.tool._sourceLoaded, false); assert.equal(f.closes.length, 1);
    console.log("PASS tool source: old-chat/selection/revision responses and cancelled downloads close once without hydration");
  }
  {
    const f = fixture(), opens = [], send = f.context.send;
    f.context.send = (action, payload) => { if (action !== "readToolSource") return send(action, payload);
      const pending = deferred(); opens.push(pending); return Object.assign(pending.promise, { requestId: "pending" + opens.length }); };
    const a = f.load(); f.context.cancelToolSourceRead(); const b = f.load(); f.context.cancelToolSourceRead();
    await f.load(); assert.equal(opens.length, 2, "cancelled openings retain shared pending slots until settled");
    opens.forEach((pending, index) => pending.resolve({ ...f.response, data: { ...f.response.data, leaseId: String(index + 1).repeat(64) } }));
    await Promise.all([a, b]); assert.equal(f.closes.length, 2); assert.equal(f.context.toolSourceReadPending, 0);
    console.log("PASS tool source: pending opens stay bounded through cancellation and close late leases");
  }
  {
    const f = fixture(); await f.load(); f.context.syncSelectedToolFromEditor = () => {};
    f.tool.Description = "Local metadata";
    f.context.reconcileToolLibraryCatalog([f.context.toolFromContract(f.wire)]);
    assert.equal(f.state.tools[0], f.tool); assert.ok(f.state.toolLibraryBaselineItems.every(record => !record.entity), "baseline never owns hidden full-source entities");
    f.context.trimToolSourceCache(null); assert.equal(f.tool.Code, undefined, "clean source cache is selection-bounded even with local metadata edits");
    f.tool.Description = f.wire.description; await f.load(); f.tool.Readme = "# Local source draft";
    f.context.trimToolSourceCache(null); assert.equal(f.tool.Readme, "# Local source draft");
    const changed = { ...f.wire, revision: "new", source: { sha256: "f".repeat(64), byteLength: 100 } };
    f.context.reconcileToolLibraryCatalog([f.context.toolFromContract(changed)]);
    assert.equal(f.state.tools[0], f.tool); assert.equal(f.tool._sourceConflict, true); assert.equal(f.tool.Readme, "# Local source draft");
    assert.throws(() => f.context.toolLibraryMutations(), /изменился/); assert.equal(f.elements.saveToolsButton.disabled, true);
    const clean = fixture(); await clean.load();
    clean.context.reconcileToolLibraryCatalog([clean.context.toolFromContract({ ...clean.wire, revision: "new", source: changed.source })]);
    assert.equal(clean.state.tools[0].Readme, undefined); assert.equal(clean.state.toolLibraryDirty, false);
    console.log("PASS tool source: clean cache invalidation and dirty draft conflicts preserve text without silent rebase");
  }
  {
    for (const file of ["app-chat-state.js", "app-chat-session.js", "app-prompts.js"]) assert.ok(read("js/" + file).includes("cancelToolSourceRead()"));
    assert.ok(read("js/app-tools.js").includes('window.addEventListener("pagehide", cancelToolSourceRead)'));
    assert.ok(!read("js/app-tools.js").includes("function readTools()"));
    for (const file of ["app-tools.js", "app-tools-actions.js", "app-prompts.js", "app-chat-state.js", "app-chat-session.js"])
      assert.ok(read("index.html").includes(file + "?v=" +
        (/app-chat-/.test(file) ? "html-write-20260906-1" : file === "app-prompts.js" ? "prompt-source-20260906-1" : "tool-docs-20260906-1")));
    console.log("PASS tool source: shared download/lifecycle cutover shipped without old whole-catalog serializer");
  }
  console.log("OK 6/6");
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
