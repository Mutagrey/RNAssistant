"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const crypto = require("node:crypto").webcrypto;
const context = vm.createContext({ AbortController, TextDecoder, TextEncoder, crypto, setTimeout, clearTimeout, btoa });
context.window = context;
context.alert = () => {};
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-html-resource-export.js"), "utf8"), context,
  { filename: "app-html-resource-export.js" });
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-html-workspace-preview.js"), "utf8"), context,
  { filename: "app-html-workspace-preview.js" });
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-artifact-viewer-actions.js"), "utf8"), context,
  { filename: "app-artifact-viewer-actions.js" });
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-html-workspace-actions.js"), "utf8"), context,
  { filename: "app-html-workspace-actions.js" });

(async function () {
  const exactJson = "{\"duplicate\":1,\"duplicate\":2,\"large\":9007199254740993,\"html\":\"</script>\"}";
  const assembled = context.RNAssistantHtmlWorkspacePreview.build({
    activeFileId: "index.html",
    files: [{ id: "index.html", path: "index.html", kind: "html", content: "<main>export</main>" }],
    dataSources: [{
      id: "bound", name: "bound", json: exactJson,
      binding: { resource: { uri: "rna://test/data", revision: "r1" }, policy: "exact", view: "table" }
    }],
    hostBridge: true
  });
  assert.ok(!assembled.includes(exactJson), "resource payloads are never embedded in workspace HTML");
  assert.doesNotMatch(assembled, /RNAssistantData|RNAssistant\.data|rna:\/\/test/);
  console.log("PASS HTML export: reference-only assembly has no legacy eager dataset");
  const detached = vm.createContext({ addEventListener() {}, setTimeout, clearTimeout });
  detached.window = detached; detached.parent = detached;
  for (const match of assembled.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/gi)) vm.runInContext(match[1], detached);
  assert.deepEqual(Array.from(detached.RN.resources.names()), ["bound"]);
  await assert.rejects(detached.RN.resources.open("bound"), /RESOURCE_HOST_REQUIRED/);
  assert.throws(() => context.RNAssistantHtmlWorkspacePreview.build({
    dataSources: [{ name: "bound" }], hostBridge: false
  }), /RESOURCE_EXPORT_REQUIRED/);
  console.log("PASS HTML export: incomplete standalone exports are blocked before download");

  const ref = { uri: "rna://state/conversation/export/data", revision: "r1" };
  const column = { key: "value", label: "Value", type: "number" };
  const table = (offset, rows, done) => ({ resource: ref, view: "table", offset,
    nextOffset: offset + rows.length, done, rows: rows.map(value => ({ value, note: "kept" })),
    columns: [column, { key: "note", type: "string" }],
    coverage: { kind: "record-range", start: offset, end: offset + rows.length, path: "$", fields: ["value", "note"] } });
  const textValue = "</script><!--<script>Данные 😀";
  const textBatch = { resource: ref, view: "text", text: textValue, offset: 0, nextOffset: textValue.length,
    done: true, coverage: { kind: "whole", fields: [] } };
  function binding(name, view, index, binary) {
    const id = String(index).repeat(64);
    return { name, lease: { leaseId: id, url: "https://rnassistant.local-resource/v1/" + id,
      descriptor: { reference: ref }, view, path: "$", maxBatchItems: 32000, maxBatchBytes: binary ? 4 : 4096, binary } };
  }
  const resourceExport = { generations: { "conversation:export": 3 }, bindings: [
    binding("bound", "table", 1), binding("text", "text", 2),
    binding("image", "image", 3, { payload: { byteLength: 4, contentType: "image/png" } }),
    binding("empty", "table", 4)
  ] };
  const reads = [];
  const captureOptions = {
    isCurrent: () => true,
    fetch: async url => {
      reads.push(url);
      if (url.includes("/" + "3".repeat(64))) return new Response(new Uint8Array([0, 1, 254, 255]));
      const batch = url.includes("/" + "2".repeat(64)) ? textBatch :
        url.includes("/" + "4".repeat(64)) ? table(0, [], true) :
          url.includes("offset=0") ? table(0, [12, 34], false) : table(2, [56], true);
      return new Response(JSON.stringify(batch));
    }
  };
  const snapshot = await context.RNAssistantHtmlResourceExport.capture(resourceExport, captureOptions);
  assert.equal(reads.length, 5);
  assert.ok(reads[1].includes("offset=2"), "export follows bounded sequential offsets");
  assert.equal(snapshot.resources[0].descriptor.reference.revision, "r1");
  assert.equal(snapshot.resources[0].url, undefined, "transient host capability is not exported");
  const offlineHtml = context.RNAssistantHtmlWorkspacePreview.build({
    files: [{ id: "index.html", path: "index.html", kind: "html", content: "<html><head><title>Snapshot</title></head><body><main>snapshot</main></body></html>" }],
    dataSources: resourceExport.bindings.map(item => ({ name: item.name })), resourceSnapshot: snapshot, hostBridge: false
  });
  assert.ok(!offlineHtml.includes(textValue), "script and comment delimiters stay inert");
  assert.match(offlineHtml, /<head>\s*<meta charset="utf-8">/, "exported part hashes survive full-document UTF-8 decoding");
  const elements = new Map(); let decodes = 0;
  const offline = vm.createContext({ addEventListener() {}, setTimeout, clearTimeout, atob, TextEncoder, crypto,
    document: { getElementById(id) { decodes++; return elements.get(id); } } });
  offline.window = offline; offline.parent = offline;
  for (const match of offlineHtml.matchAll(/<script([^>]*)>([\s\S]*?)<\/script>/gi)) {
    if (match[1].includes("application/vnd.rnassistant.resource-part"))
      elements.set(/id="([^"]+)"/.exec(match[1])[1], { textContent: match[2] });
    else vm.runInContext(match[2], offline);
  }
  assert.equal(decodes, 0, "offline boot does not hydrate resource bodies");
  const handle = await offline.RN.resources.open("bound");
  assert.equal(decodes, 0, "open is metadata-only for retained exported parts");
  await assert.rejects(handle.read({ limit: 0 }), /RESOURCE_BATCH_BOUNDS/);
  const firstRead = handle.read({ limit: 1, fields: ["value"] });
  await assert.rejects(handle.read({ limit: 1 }), /RESOURCE_BACKPRESSURE/);
  const firstBatch = await firstRead;
  assert.deepEqual(JSON.parse(JSON.stringify(firstBatch.rows)), [{ value: 12 }]);
  assert.equal(firstBatch.coverage.end, 1);
  assert.equal((await handle.read({ limit: 1 })).rows[0].value, 34);
  const lastBatch = await handle.read({ limit: 10 });
  assert.equal(lastBatch.rows[0].value, 56); assert.equal(lastBatch.done, true);
  await handle.close();
  const textHandle = await offline.RN.resources.open("text");
  let restored = "";
  for await (const batch of textHandle.stream({ limit: 3 })) restored += batch.text;
  assert.equal(restored, textValue);
  const imageHandle = await offline.RN.resources.open("image");
  const binary = await imageHandle.read();
  assert.deepEqual(Array.from(new Uint8Array(binary.bytes)), [0, 1, 254, 255]);
  await imageHandle.close();
  const emptyHandle = await offline.RN.resources.open("empty");
  const empty = await emptyHandle.read();
  assert.equal(empty.done, true); assert.equal(empty.rows.length, 0); await emptyHandle.close();
  const interrupted = await offline.RN.resources.open("bound");
  for await (const batch of interrupted.stream({ limit: 1 })) { assert.equal(batch.rows.length, 1); break; }
  await assert.rejects(interrupted.read(), /RESOURCE_LEASE_CLOSED/);
  const badFields = await offline.RN.resources.open("bound");
  await assert.rejects(badFields.read({ fields: ["absent"] }), /RESOURCE_FIELD_UNAVAILABLE/);
  await assert.rejects(badFields.read({ signal: { aborted: true } }), /RESOURCE_READ_CANCELLED/);
  await badFields.close();
  const closing = await offline.RN.resources.open("bound");
  const closingRead = closing.read();
  await closing.close();
  await assert.rejects(closingRead, /RESOURCE_LEASE_(CLOSED|EXPIRED)/);
  assert.match(offlineHtml, /connect-src 'none'/);
  console.log("PASS HTML export: exact table/text/binary snapshots use lazy bounded RN.resources offline");

  await assert.rejects(context.RNAssistantHtmlResourceExport.capture(resourceExport,
    { ...captureOptions, isCurrent: () => false }), /RESOURCE_EXPORT_CANCELLED/);
  await assert.rejects(context.RNAssistantHtmlResourceExport.capture(resourceExport, { ...captureOptions,
    fetch: async () => new Response("x".repeat(4097)) }), /RESOURCE_EXPORT_BOUNDS/);
  await assert.rejects(context.RNAssistantHtmlResourceExport.capture(resourceExport, { ...captureOptions,
    fetch: async () => new Response(JSON.stringify({ ...table(0, [12], true), resource: { ...ref, revision: "r2" } }))
  }), /RESOURCE_EXPORT_REVISION_MISMATCH/);
  const partId = snapshot.resources[0].parts[0].id;
  elements.get(partId).textContent = elements.get(partId).textContent.replace('"value":12', '"value":99');
  const tampered = await offline.RN.resources.open("bound");
  await assert.rejects(tampered.read(), /RESOURCE_SNAPSHOT_UNAVAILABLE/); await tampered.close();
  elements.delete(partId);
  const missing = await offline.RN.resources.open("bound");
  await assert.rejects(missing.read(), /RESOURCE_SNAPSHOT_UNAVAILABLE/); await missing.close();
  console.log("PASS HTML export: cancellation, size, missing snapshot and mixed revisions fail closed");

  const calls = [];
  const downloads = [];
  const logs = [];
  let invalidEvidence = false;
  let failDownload = false;
  const state = {
    activeChatId: "chat-export",
    activeHtmlArtifactId: "html-r3",
    bridgeUnavailable: false,
    htmlWorkspaceDirty: false,
    htmlWorkspace: {}
  };
  const actions = context.RNAssistantHtmlWorkspaceActions.create({
    state,
    send: async (method, payload) => {
      calls.push({ method, payload: JSON.parse(JSON.stringify(payload)) });
      if (method === "resourceDataClose") return { closed: true };
      return {
        activeChatId: "chat-export",
        activeHtmlArtifactId: "html-r4",
        exportRevisionArtifactId: "html-r4",
        exportResourceUri: "rna://chat/chat-export/artifact/html-r4/revision/4",
        exportContentSha256: invalidEvidence ? "bad" : "a".repeat(64),
        resourceExport,
        workspace: {
          activeFileId: "index.html",
          files: [{ id: "index.html", path: "index.html", kind: "html", content: "<main>exact</main>" }],
          dataSources: [{ id: "bound", name: "bound", binding: { resource: { uri: "rna://test/data", revision: "r1" }, policy: "exact", view: "table" } }]
        }
      };
    },
    applyWorkspaceResponse: response => {
      state.activeHtmlArtifactId = response.activeHtmlArtifactId;
      state.htmlWorkspace = response.workspace;
      return true;
    },
    downloadHtmlExport: async value => {
      if (failDownload) throw new Error("RESOURCE_EXPORT_READ_FAILED");
      downloads.push(value);
    },
    log: (message, level) => logs.push({ message, level }),
    render: () => {}
  });

  assert.equal(await actions.exportWorkspace(), true);
  assert.equal(calls[0].method, "prepareHtmlWorkspaceExport");
  assert.equal(calls[0].payload.chatId, "chat-export");
  assert.equal(calls[0].payload.expectedActiveHtmlArtifactId, "html-r3");
  assert.equal(downloads[0].revisionArtifactId, "html-r4");
  assert.equal(downloads[0].workspace.dataSources[0].binding.resource.revision, "r1");
  assert.equal(downloads[0].workspace.dataSources[0].json, undefined);
  assert.equal(calls.filter(item => item.method === "resourceDataClose").length, 4);
  console.log("PASS HTML export: download uses the guarded server checkpoint payload");

  state.htmlWorkspaceDirty = true;
  const callCount = calls.length;
  assert.equal(await actions.exportWorkspace(), false);
  assert.equal(calls.length, callCount);
  console.log("PASS HTML export: unsaved editor state blocks stale export");

  state.htmlWorkspaceDirty = false;
  invalidEvidence = true;
  const downloadCount = downloads.length;
  assert.equal(await actions.exportWorkspace(), false);
  assert.equal(downloads.length, downloadCount);
  assert.match(logs.at(-1).message, /incomplete revision evidence/);
  console.log("PASS HTML export: incomplete revision evidence fails closed before download");

  invalidEvidence = false; failDownload = true;
  const successLogs = logs.filter(item => !item.level).length;
  const closeCount = calls.filter(item => item.method === "resourceDataClose").length;
  assert.equal(await actions.exportWorkspace(), false);
  assert.equal(logs.filter(item => !item.level).length, successLogs, "async hydration failure is never reported as success");
  assert.equal(calls.filter(item => item.method === "resourceDataClose").length, closeCount + 4);
  assert.equal(state.htmlWorkspaceExportPending, false);
  console.log("PASS HTML export: asynchronous download failure closes every prepared capability");

  const lateCloses = [];
  const lateActions = context.RNAssistantHtmlWorkspaceActions.create({
    state,
    send: async (method, payload) => {
      if (method === "resourceDataClose") { lateCloses.push(payload); return { closed: true }; }
      state.activeChatId = "another-chat";
      return { exportRevisionArtifactId: "html-r4", resourceExport };
    }
  });
  assert.equal(await lateActions.exportWorkspace(), false);
  assert.equal(lateCloses.length, 4);
  assert.ok(lateCloses.every(item => item.chatId === "chat-export" && item.workspaceId === "html-r4"));
  console.log("PASS HTML export: a late response after chat change releases the original owner's leases");

  const refreshCalls = [];
  const refreshState = {
    activeChatId: "chat-refresh",
    bridgeUnavailable: false,
    htmlWorkspaceDirty: false,
    htmlWorkspace: {}
  };
  const refreshActions = context.RNAssistantHtmlWorkspaceActions.create({
    state: refreshState,
    send: async (method, payload) => {
      refreshCalls.push({ method, payload: JSON.parse(JSON.stringify(payload)) });
      return method === "runTool"
        ? { success: true, message: "Updated." }
        : { workspace: {}, activeChatId: "chat-refresh" };
    },
    hasRefreshableData: () => true,
    refreshableDataNames: () => ["sales", "costs"],
    applyWorkspaceResponse: () => true,
    log: () => {},
    render: () => {}
  });
  await refreshActions.refreshAuto();
  const toolRefreshes = refreshCalls.filter(call => call.method === "runTool");
  assert.deepEqual(toolRefreshes.map(call => call.payload.arguments),
    [{ name: "sales" }, { name: "costs" }]);
  assert.ok(toolRefreshes.every(call => call.payload.arguments.policy === undefined));
  console.log("PASS HTML refresh: UI keeps policy internal and sends semantic names only");

  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  ["app-html-resource-export.js", "app-html-workspace-preview.js", "app-html-workspace-actions.js", "app-html-workspace.js"]
    .forEach(asset => assert.ok(index.includes(asset + "?v=resource-export-20260905-1"), asset));
  assert.ok(index.indexOf("app-html-resource-export.js?v=") < index.indexOf("app-html-workspace-preview.js?v="));
  assert.ok(index.includes("app-html-workspace-editor.js?v=ui-lazy-20260903-1"));
  assert.ok(index.includes(
    "app-html-workspace-artifacts.js?v=artifact-chart-preview-20260903-1"));
  assert.ok(index.includes("app-html-workspace.css?v=html-export-20260831-1"));
  console.log("PASS HTML export: changed UI graph uses one cache key");

  console.log("OK 11/11");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
