"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const { createHash, webcrypto } = require("node:crypto");
const read = file => fs.readFileSync(path.join(__dirname, "../../web", file), "utf8");
function deferred() { let resolve; return { promise: new Promise(done => { resolve = done; }), resolve: value => resolve(value) }; }
function fixture(texts = ["\ufeff<main>\r\n" + "я".repeat(140000) + "😀</main>", "", "console.log('exact');"]) {
  const buffers = texts.map(text => Buffer.from(text));
  const files = texts.map((text, index) => ({ id: "file" + index, path: "file" + index, kind: index === 0 ? "html" : "script",
    source: { uri: "rna://chat/chat-a/artifact/html-r1/revision/1/member/file/" + String(index).repeat(64), revision: "1" },
    characters: text.length, byteLength: buffers[index].length, sha256: createHash("sha256").update(buffers[index]).digest("hex") }));
  const state = { activeChatId: "chat-a", activeHtmlArtifactId: "html-r1", htmlWorkspace: { revisionArtifactId: "html-r1", files } };
  const calls = [], fetched = [], cancelled = [], leases = new Map(); let change = deferred(), leaseIndex = 0;
  const f = { files, state, calls, fetched, cancelled, buffers, nextChange: () => change.promise };
  const context = vm.createContext({ Blob, TextEncoder, TextDecoder, AbortController, crypto: webcrypto, setTimeout, clearTimeout });
  context.window = context;
  const send = (action, payload) => {
    calls.push({ action, payload });
    const result = (async () => {
      if (action === "resourceDataClose") return { closed: true };
      assert.equal(action, "readHtmlWorkspaceSource");
      const index = files.findIndex(file => file.source.uri === payload.resource.uri);
      if (f.onOpen) await f.onOpen();
      const id = (++leaseIndex).toString(16).padStart(64, "0"); leases.set(id, buffers[index]);
      return { chatId: f.foreign ? "foreign" : "chat-a", resource: payload.resource, totalCharacters: files[index].characters,
        data: { leaseId: id, url: "https://rnassistant.local-resource/v1/download/" + id,
          payload: { sha256: files[index].sha256, byteLength: buffers[index].length, contentType: "text/plain; charset=utf-8" }, maxChunkBytes: 65536 } };
    })();
    result.requestId = "req-" + calls.length; return result;
  };
  context.fetch = async (target, options) => {
    fetched.push(target); if (f.onFetch) await f.onFetch(options);
    const url = new URL(target), bytes = leases.get(url.pathname.split("/").at(-1));
    const offset = Number(url.searchParams.get("offset")), count = Number(url.searchParams.get("count"));
    const chunk = Buffer.from(bytes.subarray(offset, offset + count));
    if (f.corrupt && chunk.length) chunk[0] ^= 1;
    return new Response(chunk, { headers: { "Content-Type": "text/plain; charset=utf-8" } });
  };
  vm.runInContext(read("js/app-resource-download.js"), context);
  vm.runInContext(read("js/app-html-workspace-source.js"), context);
  f.source = context.RNAssistantHtmlWorkspaceSource.create({ state, send, cancelRequest: async id => cancelled.push(id),
    changed: () => { const previous = change; change = deferred(); previous.resolve(); } });
  f.context = context;
  f.load = async wanted => { const done = f.nextChange(); assert.equal(f.source.ensure(wanted), false); await done; };
  f.opens = () => calls.filter(call => call.action === "readHtmlWorkspaceSource");
  f.closes = () => calls.filter(call => call.action === "resourceDataClose");
  return f;
}

(async function () {
  {
    const f = fixture();
    assert.equal(f.calls.length, 0, "metadata does not prefetch");
    await f.load([f.files[0]]);
    assert.equal(f.files[0].content, f.buffers[0].toString("utf8")); assert.equal(f.source.ready(f.files[0]), true);
    assert.equal(f.files[1].content, undefined); assert.equal(f.opens().length, 1); assert.equal(f.closes().length, 1);
    assert.equal(f.source.ensure([f.files[0]]), true); assert.equal(f.opens().length, 1, "exact selected source cache is reused");
    await f.load(f.files);
    assert.equal(f.opens().length, 3); assert.equal(f.closes().length, 3); assert.equal(f.source.ready(f.files[1]), true);
    assert.equal(f.files[1].content, "");
    f.files[0].content = "local draft"; assert.equal(f.source.ensure(f.files), true); assert.equal(f.files[0].content, "local draft");
    console.log("PASS HTML source: selected-only hydration, bounded exact bytes, empty file and one workspace cache");
  }
  {
    for (const type of ["corrupt", "foreign", "oversized", "inline"]) {
      const f = fixture(["source"]);
      if (type === "corrupt") f.corrupt = true;
      if (type === "foreign") f.foreign = true;
      if (type === "oversized") f.files[0].characters = 300001;
      if (type === "inline") { f.files[0].content = "old inline"; delete f.files[0].source; }
      await f.load(f.files);
      assert.equal(f.source.ready(f.files[0]), false); assert.ok(f.files[0].sourceError);
      const count = f.opens().length; assert.equal(f.source.ensure(f.files), false); assert.equal(f.opens().length, count, "no automatic retry");
      assert.equal(f.closes().length, count);
    }
    console.log("PASS HTML source: corrupt/foreign/unbounded/inline sources stay unreadable without fallback");
  }
  {
    const f = fixture(["source"]), opened = deferred(), release = deferred();
    f.onOpen = async () => { opened.resolve(); await release.promise; };
    f.source.ensure(f.files); await opened.promise;
    f.source.release(); release.resolve();
    for (let i = 0; i < 10 && !f.closes().length; i++) await new Promise(resolve => setImmediate(resolve));
    assert.equal(f.cancelled.length, 1); assert.equal(f.fetched.length, 0); assert.equal(f.closes().length, 1);
    assert.equal(f.closes()[0].payload.chatId, "chat-a"); assert.equal(f.source.ready(f.files[0]), false);
    console.log("PASS HTML source: cancelled pending open closes its late lease in the original owner");
  }
  {
    for (const change of [f => { f.state.activeChatId = "other"; }, f => { f.state.activeHtmlArtifactId = "new"; },
      f => { f.state.htmlWorkspace = { ...f.state.htmlWorkspace }; }]) {
      const f = fixture(["source"]); f.onFetch = async () => change(f); await f.load(f.files);
      assert.equal(f.source.ready(f.files[0]), false); assert.equal(f.closes().length, 1); assert.equal(f.opens().length, 1);
    }
    console.log("PASS HTML source: late chat/revision/projection responses cannot hydrate the current editor");
  }
  {
    const f = fixture(["a", "b"]), opened = deferred(), release = deferred();
    f.onOpen = async () => { opened.resolve(); await release.promise; };
    const done = f.nextChange(); f.source.ensure([f.files[0]]); await opened.promise;
    for (let i = 0; i < 20; i++) assert.equal(f.source.ensure([f.files[1]]), false);
    assert.equal(f.opens().length, 1, "selection changes coalesce behind one outstanding open");
    assert.equal(f.cancelled.length, 1, "repeated renders do not queue repeated cancellation controls");
    release.resolve(); await done; f.onOpen = null;
    await f.load([f.files[1]]); assert.equal(f.source.ready(f.files[1]), true); assert.equal(f.source.ready(f.files[0]), false);
    console.log("PASS HTML source: selection churn has bounded producer and cancellation backpressure");
  }
  {
    const f = fixture(["<main>export</main>", ""]); f.state.htmlWorkspaceExportPending = true;
    await f.source.exportSources(f.state.htmlWorkspace, () => true);
    assert.ok(f.files.every(f.source.ready)); assert.equal(f.closes().length, 2);
    const bad = fixture(["source"]); bad.state.htmlWorkspaceExportPending = true; bad.corrupt = true;
    await assert.rejects(bad.source.exportSources(bad.state.htmlWorkspace, () => true));
    vm.runInContext(read("js/app-html-workspace-preview.js"), f.context);
    assert.throws(() => f.context.RNAssistantHtmlWorkspacePreview.build({ files: [{ kind: "html" }] }), /RESOURCE_SOURCE_REQUIRED/);
    console.log("PASS HTML source: export shares the reader and assembly refuses missing file bodies");
  }
  {
    const f = fixture(["source"]); let writes = 0;
    const node = { value: "stale editor contents", classList: { toggle() {}, add() {}, remove() {} }, replaceChildren() {}, removeAttribute() {} };
    f.context.$ = () => node;
    f.context.document = { querySelector: () => node, querySelectorAll: () => [] };
    f.state.htmlWorkspaceMode = "edit";
    vm.runInContext(read("js/app-html-workspace-editor.js"), f.context);
    const editor = f.context.RNAssistantHtmlWorkspaceEditor.create({ state: f.state, source: f.source, preview: {}, artifacts: {},
      model: { selectedItem: () => ({ type: "file", item: f.files[0] }), setFileContent: (_, value) => { writes++; f.files[0].content = value; },
        recoveryBlocked: () => false, workspace: () => f.state.htmlWorkspace, files: () => f.files, filePath: file => file.path,
        fileKind: file => file.kind, fileContent: file => file.content } });
    editor.sync(); editor.markDirty(); assert.equal(writes, 0, "unloaded source cannot become an empty/stale draft");
    await f.load(f.files); editor.sync(); assert.equal(writes, 0, "cache hydration cannot sync the still-displayed placeholder into source");
    editor.render(); assert.equal(node.value, "source"); node.value = "edited"; editor.sync(); assert.equal(writes, 1);
    assert.equal(f.files[0].content, "edited");
    console.log("PASS HTML source: editor sync requires verified source actually rendered, never its old placeholder");
  }
  const index = read("index.html");
  ["source", "model", "editor", "preview", "actions"].map(part => "app-html-workspace-" + part + ".js")
    .concat(["app-html-workspace.js", "app-chat-state.js", "app-chat-session.js"])
    .forEach(file => assert.ok(index.includes(file + "?v=html-read-20260906-1"), file));
  assert.ok(index.includes('id="reloadHtmlWorkspaceSourceButton"'));
  assert.ok(index.indexOf("app-resource-download.js?v=") < index.indexOf("app-html-workspace-source.js?v="));
  console.log("PASS HTML source: source/editor/preview/export and lifecycle delivery graph is switched together");
  console.log("OK 8/8");
}()).catch(error => { console.error(error.stack || error); process.exitCode = 1; });
