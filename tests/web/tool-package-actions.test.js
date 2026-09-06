"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const crypto = require("node:crypto");
const read = file => fs.readFileSync(path.join(__dirname, "../../web", file), "utf8");
const sha = bytes => crypto.createHash("sha256").update(bytes).digest("hex");
function deferred() { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; }

function item() {
  return { revision: "base", id: "excel.echo_vba", host: "Excel", name: "excel.echo_vba", description: "",
    argumentSchemaJson: '{"type":"object"}', executor: "vba", requiresConfirmation: true, mutatesDocument: true,
    mutatesLocalState: false, canSourceHtmlData: false, agentCanRun: false, code: "Option Explicit\r\n",
    readme: "# Before", enabled: true, builtIn: false, riskLevel: 1, useWhen: "", doNotUseWhen: "",
    capabilityStatus: "available", limitations: "", packageVersion: "1.0.0", entryPoint: "RNA_Echo.Run",
    argumentOrder: [], components: [{ name: "RNA_Echo", type: "StdModule", fileName: "RNA_Echo.bas",
      code: "Option Explicit\r\n", codeSha256: "a".repeat(64) }], scope: "global", installationStatus: "not_installed" };
}

function fixture(hooks = {}) {
  const calls = [], logs = [], outputs = [], cancelled = [], closed = [], pending = {}, elements = {};
  const state = { tools: [], selectedToolIndex: 0, selectedToolComponentIndex: 0, activeChatId: "chat" };
  let server = item(), sequence = 0;
  const context = vm.createContext({ window: null, state, AbortController, TextEncoder, TextDecoder, Blob, Uint8Array, setTimeout, clearTimeout, crypto: crypto.webcrypto,
    $: id => elements[id] || (elements[id] = {}), send() {}, log() {}, logToolResult() {}, setControlBusy() {},
    RNAssistantToolStructuredEditor: { create: () => ({}) }, RNAssistantToolDocumentation: { create: () => ({}) },
    RNAssistantToolActions: { create: () => ({}) } });
  context.window = context;
  vm.runInContext(read("js/app-tools.js"), context);
  vm.runInContext(read("js/app-tools-actions.js"), context);
  vm.runInContext(read("js/app-resource-upload.js"), context);
  context.syncSelectedToolFromEditor = () => {};
  state.tools = [context.toolFromContract(server)]; context.acceptToolLibraryState();
  state.tools[0].Readme = "# Saved\r\n" + "Ж".repeat(140000) + "😀"; context.updateToolLibraryDirty();
  context.fetch = async (url, options) => {
    const address = new URL(url), id = address.pathname.split("/").pop(), upload = pending[id];
    assert.equal(options.method, "POST"); assert.equal(options.credentials, "omit"); assert.equal(options.redirect, "error");
    const offset = Number(address.searchParams.get("offset")), count = Number(address.searchParams.get("count"));
    assert.equal(upload.bytes.length, offset); assert.ok(count <= 65536);
    upload.bytes = Buffer.concat([upload.bytes, Buffer.from(await options.body.arrayBuffer())]);
    if (hooks.chunk) await hooks.chunk(options);
    return new Response(JSON.stringify({ leaseId: id, nextOffset: offset + count }));
  };
  function library() { return { type: "rnassistant.toolLibrary", contractVersion: 1, tools: [server] }; }
  function send(action, payload) {
    calls.push({ action, payload }); let response;
    if (action === "beginToolMutationUpload") {
      assert.deepEqual(Object.keys(payload).sort(), ["byteLength", "chatId"]); assert.equal(payload.chatId, "chat");
      const id = (++sequence).toString(16).padStart(64, "0"), lease = { leaseId: id, byteLength: payload.byteLength,
        url: "https://rnassistant.local-resource/v1/upload/" + id, maxChunkBytes: 65536 };
      pending[id] = { bytes: Buffer.alloc(0), lease };
      response = hooks.open ? hooks.open(lease) : lease;
    } else if (action === "cancelToolMutationUpload") {
      assert.equal(payload.chatId, "chat"); closed.push(payload); response = { closed: true };
    } else if (action === "saveTools") {
      assert.deepEqual(Object.keys(payload).sort(), ["chatId", "sha256", "uploadLeaseId"]); assert.equal(payload.chatId, "chat");
      const upload = pending[payload.uploadLeaseId];
      assert.equal(upload.bytes.length, upload.lease.byteLength); assert.equal(payload.sha256, sha(upload.bytes));
      const batch = JSON.parse(upload.bytes.toString("utf8"));
      assert.equal(batch.type, "rnassistant.toolLibraryMutationRequest"); assert.equal(batch.contractVersion, 1);
      const results = batch.mutations.map(mutation => {
        assert.equal(mutation.expectedRevision, "base"); assert.ok(Array.isArray(mutation.components));
        server = { ...server, ...mutation, revision: "saved" };
        return { type: "rnassistant.toolMutationResult", contractVersion: 1, id: mutation.id, revision: "saved",
          previousRevision: "base", status: "ok", message: "saved", dispatch: "may_have_dispatched", effect: "verified_change" };
      });
      response = { type: "rnassistant.toolLibraryMutationResult", contractVersion: 1, results, library: library() };
      if (hooks.save) response = hooks.save(batch, response);
    } else {
      assert.ok(["installVbaTool", "uninstallVbaTool"].includes(action));
      assert.deepEqual(Object.keys(payload).sort(), ["dryRun", "id"]);
      server = { ...server, installationStatus: action === "installVbaTool" ? "installed" : "not_installed" };
      response = { result: { contractVersion: 1, sourceRevision: "source", status: "ok", success: true,
        message: "installed", mayHaveDispatched: true, effect: "verified_change" }, tools: library(),
        Result: { Message: "legacy must not win" }, Tools: [] };
      if (hooks.install) response = hooks.install(response);
    }
    return Object.assign(Promise.resolve(response), { requestId: action + "-request" });
  }
  const actions = context.RNAssistantToolActions.create({
    state, send, cancelRequest: async id => cancelled.push(id), updateWriteState: context.updateToolWriteControls,
    syncSelected() {}, validateSelected: () => true, validateAll() {},
    mutationRequest: context.toolLibraryMutationRequest, captureSave: () => context.toolLibraryRecords(state.tools),
    acknowledgeSave: context.acknowledgeToolSaves, parseMutation: context.toolLibraryMutationFromContract,
    parseLibrary: context.toolLibraryItemsFromContract, reconcile: context.reconcileToolLibraryCatalog,
    renderTools() {}, renderEditor() {}, setBusy() {}, setJsonOutput: value => outputs.push(value),
    setTextOutput: value => outputs.push(value), log: (message, level) => logs.push({ message, level })
  });
  return { context, state, actions, calls, logs, outputs, cancelled, closed, pending, elements };
}

(async () => {
  {
    for (const action of ["save", "installVba", "uninstallVba"]) {
      const f = fixture(); await f.actions[action]();
      assert.equal(f.state.toolLibraryWriting, false); assert.equal(f.logs.at(-1).level, undefined, f.logs.map(log => log.message).join("; "));
      if (action === "uninstallVba") {
        assert.deepEqual(f.calls.map(call => call.action), ["uninstallVbaTool"]);
        assert.equal(f.state.toolLibraryDirty, true, "package status does not discard unsaved authoring text");
      } else {
        assert.deepEqual(f.calls.map(call => call.action), ["beginToolMutationUpload", "saveTools", "cancelToolMutationUpload"]
          .concat(action === "installVba" ? ["installVbaTool"] : []));
        assert.equal(f.closed.length, 1); assert.equal(f.state.toolLibraryDirty, false);
        assert.match(f.state.tools[0].Readme, /😀$/);
      }
      if (action !== "save") assert.equal(f.outputs.at(-1).effect, "verified_change", "typed result wins over legacy shape");
    }
    const empty = fixture(); empty.state.tools[0].Readme = ""; await empty.actions.save();
    assert.equal(empty.state.tools[0].Readme, ""); assert.equal(empty.state.toolLibraryDirty, false);
    console.log("PASS tool package actions: Save and pre-install share bounded hashed uploads; typed install/remove and empty source remain intact");
  }
  {
    for (const action of ["save", "installVba"]) {
      let f;
      f = fixture({ save: (_, response) => { f.state.tools[0].Readme = "# Later edit"; return response; } });
      await f.actions[action]();
      assert.equal(f.state.tools[0].Readme, "# Later edit"); assert.equal(f.state.toolLibraryDirty, true);
      assert.equal(f.calls.filter(call => call.action === "installVbaTool").length, 0, "a later draft cannot be implicitly installed");
      if (action === "installVba") assert.match(f.logs.at(-1).message, /Определение изменилось/);
    }
    let f;
    f = fixture({ install: response => { f.state.tools[0].Readme = "# During install"; return response; } });
    await f.actions.installVba();
    assert.equal(f.state.tools[0].Readme, "# During install"); assert.equal(f.state.toolLibraryDirty, true);
    const partial = fixture({ save: (batch, response) => {
      response.results[1].status = "error"; response.results[1].effect = "none"; response.results[1].message = "second package failed";
      response.library.tools = [{ ...item(), ...batch.mutations[0], revision: "saved" }, { ...item(), id: "excel.other" }];
      return response;
    } });
    partial.state.tools.push(partial.context.toolFromContract({ ...item(), id: "excel.other" }));
    partial.context.setToolLibraryBaseline(partial.state.tools);
    partial.state.tools[0].Readme += "\nSubmitted"; partial.state.tools[1].Readme = "# Pending second";
    await partial.actions.installVba();
    assert.equal(partial.state.tools[0]._baseRevision, "saved", "a verified prefix is acknowledged even when the next package fails");
    assert.equal(partial.state.tools[1].Readme, "# Pending second"); assert.equal(partial.state.toolLibraryDirty, true);
    assert.equal(partial.calls.some(call => call.action === "installVbaTool"), false);
    console.log("PASS tool package actions: verified prefix and package status preserve failed members and later authoring drafts");
  }
  {
    for (const stage of ["begin", "chunk", "dispatched"]) {
      const reached = deferred(), release = deferred();
      const f = fixture({
        open: async lease => { if (stage === "begin") { reached.resolve(); await release.promise; } return lease; },
        chunk: async options => { if (stage === "chunk") {
          reached.resolve(); await new Promise((resolve, reject) => options.signal.addEventListener("abort", () => reject(new Error("aborted")), { once: true }));
        } },
        save: async (_, response) => { reached.resolve(); await release.promise; return response; }
      });
      const draft = f.state.tools[0], writing = f.actions.installVba();
      await reached.promise; f.state.activeChatId = "other"; f.actions.cancelWrite();
      assert.equal(f.state.toolLibraryWriting, true); await f.actions.save();
      release.resolve(); await writing;
      assert.equal(f.closed.length, 1); assert.equal(f.state.tools[0], draft);
      assert.equal(f.state.toolLibraryWriting, false);
      assert.equal(f.calls.filter(call => call.action === "saveTools").length, stage === "dispatched" ? 1 : 0);
      assert.equal(f.calls.filter(call => call.action === "installVbaTool").length, 0);
      if (stage === "dispatched") assert.match(f.logs.at(-1).message, /результат записи не подтверждён/);
      if (stage !== "chunk") assert.ok(f.cancelled.includes((stage === "begin" ? "beginToolMutationUpload" : "saveTools") + "-request"));
    }
    console.log("PASS tool package actions: begin/fetch/late-response cancellation retains one writer, closes old-chat leases and never installs or retries");
  }
  {
    for (const mode of ["unicode", "size", "count", "malformed", "unknown", "error"]) {
      const f = fixture({ save: (_, response) => {
        if (mode === "malformed") return {};
        response.results[0].status = mode; response.results[0].effect = mode === "unknown" ? "unknown" : "none";
        response.results[0].message = "not saved"; response.library.tools = [item()]; return response;
      } });
      if (mode === "unicode") f.state.tools[0].Readme = "\ud800";
      if (mode === "size") f.state.tools[0].Readme = "x".repeat(16 * 1024 * 1024 + 1);
      if (mode === "count") f.context.toolLibraryMutations = () => new Array(257).fill({});
      await f.actions.installVba();
      const dispatched = ["malformed", "unknown", "error"].includes(mode);
      assert.equal(Object.keys(f.pending).length, dispatched ? 1 : 0); assert.equal(f.closed.length, dispatched ? 1 : 0);
      assert.equal(f.calls.filter(call => call.action === "installVbaTool").length, 0);
      assert.equal(f.state.toolLibraryDirty, true);
      if (["malformed", "unknown"].includes(mode)) assert.match(f.logs.at(-1).message, /результат записи не подтверждён/);
      else if (!dispatched) assert.match(f.logs.at(-1).message, /RESOURCE_(UPLOAD_INVALID|BATCH_TOO_LARGE)/);
    }
    console.log("PASS tool package actions: bounds fail before allocation; malformed/unknown/failed save stops installation and retains drafts");
  }
  {
    for (const file of ["app-chat-state.js", "app-chat-session.js"]) assert.ok(read("js/" + file).includes("cancelToolLibraryWrite()"));
    assert.ok(read("js/app-tools.js").includes('window.addEventListener("pagehide", cancelToolLibraryWrite)'));
    for (const file of ["app-tools.js", "app-tools-actions.js", "app-chat-state.js", "app-chat-session.js"])
      assert.ok(read("index.html").includes(file + "?v=tool-upload-20260906-1"));
    assert.ok(!read("js/app-tools-actions.js").includes('"saveTools", options.mutationRequest()'));
    console.log("PASS tool package actions: both inline save consumers are removed and lifecycle cancellation is wired");
  }
  console.log("OK 5/5");
})().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
