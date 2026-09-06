"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { webcrypto, createHash } = require("node:crypto");
const read = file => fs.readFileSync(path.join(__dirname, "../../web", file), "utf8");
function deferred() { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; }

function fixture(text = "<main>before</main>") {
  const calls = [], chunks = [], logs = [], alerts = [], applied = [], cancelled = [];
  const state = { activeChatId: "chat-a", activeHtmlArtifactId: "html-r1", htmlWorkspace: {}, htmlWorkspaceDirty: true };
  const item = { content: text };
  const selected = { type: "file", item, path: "index.html", kind: "html" };
  const id = "a".repeat(64);
  const f = { state, item, selected, calls, chunks, logs, alerts, applied, cancelled };
  const context = vm.createContext({ Blob, TextEncoder, TextDecoder, AbortController, crypto: webcrypto, setTimeout, clearTimeout });
  context.window = context; context.alert = message => alerts.push(message); context.confirm = () => true;
  context.RNAssistantArtifactViewerActions = { create: () => ({}) };
  const send = (action, payload) => {
    calls.push({ action, payload });
    const pending = (async () => {
      if (action === "beginHtmlWorkspaceMutationUpload") {
        if (f.onOpen) await f.onOpen();
        return { leaseId: id, url: "https://rnassistant.local-resource/v1/upload/" + id,
          byteLength: payload.byteLength, maxChunkBytes: 256 * 1024 };
      }
      if (action.startsWith("saveHtmlWorkspace")) {
        if (f.onSave) await f.onSave();
        return f.response || { activeChatId: "chat-a", activeHtmlArtifactId: "html-r2", workspace: {} };
      }
      if (action === "cancelHtmlWorkspaceMutationUpload") { if (f.onClose) await f.onClose(); return { closed: true }; }
      throw new Error("Unexpected action " + action);
    })();
    pending.requestId = "request-" + calls.length;
    return pending;
  };
  context.fetch = async (url, options) => {
    if (f.onFetch) await f.onFetch(options);
    const params = new URL(url).searchParams;
    const bytes = Buffer.from(await options.body.arrayBuffer());
    assert.equal(Number(params.get("count")), bytes.length);
    assert.equal(Number(params.get("offset")), chunks.reduce((count, part) => count + part.length, 0));
    assert.ok(bytes.length <= 256 * 1024); assert.equal(options.method, "POST");
    chunks.push(bytes);
    return { ok: true, json: async () => ({ leaseId: f.wrongAck ? "b".repeat(64) : id, nextOffset: Number(params.get("offset")) + bytes.length }) };
  };
  vm.runInContext(read("js/app-resource-upload.js"), context);
  vm.runInContext(read("js/app-html-workspace-actions.js"), context);
  f.actions = context.RNAssistantHtmlWorkspaceActions.create({ state, send, log: message => logs.push(message),
    getSelection: () => Object.assign({}, selected, { content: item.content, json: item.json }), syncEditor: () => {},
    cancelRequest: async id => cancelled.push(id), hideCreate: () => {}, render: () => {},
    applyWorkspaceResponse: response => {
      applied.push(response); state.activeHtmlArtifactId = response.activeHtmlArtifactId;
      state.htmlWorkspace = response.workspace; state.htmlWorkspaceDirty = false; return true;
    } });
  f.saves = () => calls.filter(call => call.action.startsWith("saveHtmlWorkspace"));
  f.closes = () => calls.filter(call => call.action === "cancelHtmlWorkspaceMutationUpload");
  return f;
}

(async function () {
  {
    const text = "\ufeff<main>\r\n" + "я".repeat(270000) + "😀</main>";
    const f = fixture(text); await f.actions.saveSelection();
    assert.equal(Buffer.concat(f.chunks).toString("utf8"), text);
    assert.equal(f.chunks.length, 3); assert.equal(f.saves().length, 1);
    const payload = f.saves()[0].payload;
    assert.equal(payload.sha256, createHash("sha256").update(text).digest("hex"));
    assert.equal(payload.expectedActiveHtmlArtifactId, "html-r1");
    assert.equal(payload.content, undefined); assert.equal(payload.json, undefined);
    assert.ok(JSON.stringify(payload).length < 500); assert.equal(f.applied.length, 1); assert.equal(f.closes().length, 1);
    console.log("PASS HTML write: exact bounded raw bytes and body-free guarded controls");
  }
  {
    for (const kind of ["file", "data", "empty"]) {
      const f = fixture(kind === "empty" ? "" : "unused"); f.state.htmlWorkspaceDirty = false;
      if (kind === "file") await f.actions.createFile("css", "style.css");
      else if (kind === "data") await f.actions.createData("items");
      else await f.actions.saveSelection();
      assert.equal(f.saves().length, 1); assert.equal(f.applied.length, 1); assert.equal(f.closes().length, 1);
      assert.equal(f.saves()[0].payload.content, undefined); assert.equal(f.saves()[0].payload.json, undefined);
      if (kind === "empty") assert.equal(f.chunks.length, 0, "zero-byte replacement still uses a complete upload lease");
    }
    const f = fixture(); f.selected.type = "data"; f.selected.name = "items"; f.item.json = "{\"items\":[]}";
    await f.actions.saveSelection(); assert.equal(f.saves()[0].action, "saveHtmlWorkspaceData");
    assert.equal(Buffer.concat(f.chunks).toString("utf8"), f.item.json);
    console.log("PASS HTML write: Save/create file and JSON use one writer, including empty file");
  }
  {
    for (const text of ["x".repeat(300001), "\ud800"]) {
      const f = fixture(text); await f.actions.saveSelection(); assert.equal(f.calls.length, 0); assert.equal(f.applied.length, 0);
    }
    const f = fixture(); await f.actions.createData("items");
    assert.equal(f.calls.length, 0, "creating a resource never discards an existing dirty draft");
    console.log("PASS HTML write: invalid/oversized text and dirty create fail before upload");
  }
  {
    const f = fixture(); f.wrongAck = true; await f.actions.saveSelection();
    assert.equal(f.saves().length, 0); assert.equal(f.applied.length, 0); assert.equal(f.closes().length, 1);
    assert.equal(f.state.htmlWorkspaceDirty, true);
    console.log("PASS HTML write: invalid chunk acknowledgement closes without dispatch or replay");
  }
  {
    const f = fixture(); const opened = deferred(), release = deferred();
    f.onOpen = async () => { opened.resolve(); await release.promise; };
    const saving = f.actions.saveSelection(); await opened.promise;
    await f.actions.saveSelection(); assert.equal(f.calls.length, 1, "one in-flight producer");
    f.actions.cancelWrite(); release.resolve(); await saving;
    assert.equal(f.cancelled.length, 1); assert.equal(f.saves().length, 0); assert.equal(f.chunks.length, 0);
    assert.equal(f.closes().length, 1, "late lease closes in its original chat");
    assert.equal(f.closes()[0].payload.chatId, "chat-a");
    console.log("PASS HTML write: single-flight, cancellation and late lease cleanup");
  }
  {
    for (const change of [f => { f.state.activeChatId = "chat-b"; }, f => { f.state.activeHtmlArtifactId = "html-new"; },
      f => { f.state.htmlWorkspace = {}; }, f => { f.item.content = "new typing"; }]) {
      const f = fixture(); f.onFetch = async () => change(f); await f.actions.saveSelection();
      assert.equal(f.saves().length, 0); assert.equal(f.applied.length, 0); assert.equal(f.closes().length, 1);
    }
    console.log("PASS HTML write: chat/revision/projection/typing changes prevent stale dispatch");
  }
  {
    const f = fixture(); f.onSave = async () => { f.item.content = "late typing"; }; await f.actions.saveSelection();
    assert.equal(f.saves().length, 1); assert.equal(f.applied.length, 0); assert.equal(f.state.htmlWorkspaceDirty, true);
    assert.equal(f.item.content, "late typing"); assert.equal(f.state.activeHtmlArtifactId, "html-r1", "dirty draft is never silently rebased");
    assert.ok(f.logs.some(message => message.includes("Отправленная версия сохранена")));
    const creating = fixture(); creating.state.htmlWorkspaceDirty = false;
    creating.onClose = async () => { creating.item.content = "typing during close"; creating.state.htmlWorkspaceDirty = true; };
    await creating.actions.createFile("css", "new.css");
    assert.equal(creating.applied.length, 0); assert.equal(creating.item.content, "typing during close");
    assert.equal(creating.state.htmlWorkspaceSelection, undefined, "create acknowledgement cannot switch away from a newer draft");
    console.log("PASS HTML write: acknowledgement retains late edits and requires explicit refresh");
  }
  {
    for (const failure of ["lost", "cancelled", "foreign"]) {
      const f = fixture();
      f.onSave = async () => {
        if (failure === "lost") throw new Error("lost response");
        if (failure === "cancelled") f.actions.cancelWrite();
        if (failure === "foreign") { f.item.content = "late"; f.response = { activeChatId: "foreign", activeHtmlArtifactId: "other" }; }
      };
      await f.actions.saveSelection();
      assert.equal(f.saves().length, 1); assert.equal(f.applied.length, 0); assert.equal(f.state.htmlWorkspaceDirty, true);
      assert.ok(f.alerts.some(message => message.includes("Запись могла завершиться")));
      assert.ok(!f.logs.some(message => message.includes("Отправленная версия сохранена")));
      assert.equal(f.closes().length, 1);
    }
    console.log("PASS HTML write: lost/cancelled/foreign save response has no false success or automatic retry");
  }
  const index = read("index.html");
  ["app-html-workspace-actions.js", "app-html-workspace.js", "app-chat-state.js", "app-chat-session.js"]
    .forEach(file => assert.ok(index.includes(file + "?v=html-write-20260906-1")));
  assert.ok(index.indexOf("app-resource-upload.js?v=") < index.indexOf("app-html-workspace-actions.js?v="));
  assert.match(read("js/app-html-workspace.js"), /addEventListener\("pagehide", workspaceActions.cancelWrite\)/);
  ["app-chat-state.js", "app-chat-session.js"].forEach(file => assert.match(read("js/" + file), /window.cancelHtmlWorkspaceWrite\(\)/));
  console.log("PASS HTML write: cache graph and chat/page lifecycle are wired");
  console.log("OK 9/9");
}()).catch(error => { console.error(error.stack || error); process.exitCode = 1; });
