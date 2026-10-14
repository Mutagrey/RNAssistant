"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const source = fs.readFileSync(path.join(root, "web/js/app-core.js"), "utf8");
const chatSessionSource = fs.readFileSync(path.join(root, "web/js/app-chat-session.js"), "utf8");

function createContext() {
  const posted = [];
  let messageListener = null;
  let timerId = 0;
  const timers = new Map();
  const context = vm.createContext({
    console,
    setTimeout: (callback, delay) => { const id = ++timerId; timers.set(id, { callback, delay }); return id; },
    clearTimeout: id => timers.delete(id)
  });
  context.window = context;
  context.document = {
    getElementById: () => null,
    querySelector: () => null,
    querySelectorAll: () => []
  };
  context.chrome = {
    webview: {
      addEventListener: (type, listener) => { if (type === "message") messageListener = listener; },
      postMessage: message => posted.push(JSON.parse(JSON.stringify(message)))
    }
  };
  vm.runInContext(source, context, { filename: "app-core.js" });
  const contract = { version: 1, operations: ["init", "listChats", "getChatState", "runTool", "clearRuntimeData"].map(name =>
    ({ name, idleTimeoutMs: name === "init" ? 30000 : 120000, cancellable: name === "runTool" })) };
  context.acceptBridgeContract(contract);
  return { context, posted, contract, timers, deliver: data => messageListener({ data }),
    expire: id => { const timer = timers.get(id); assert.ok(timer); timers.delete(id); timer.callback(); } };
}

(async function () {
  {
    const { context, posted } = createContext();
    await assert.rejects(() => context.send("listChats", {}), /not initialized/);
    assert.equal(posted.length, 0);
    console.log("PASS bridge bootstrap: host calls fail closed before init starts");
  }

  {
    const { context, posted } = createContext();
    let resolveInit;
    context.state.initializePromise = new Promise(resolve => { resolveInit = resolve; });
    const promise = context.send("listChats", { marker: true });
    assert.equal(posted.length, 0);
    context.state.bridgeToken = "token-1";
    resolveInit();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(posted.length, 1);
    assert.equal(posted[0].type, "listChats");
    assert.equal(posted[0].bridgeToken, "token-1");
    assert.equal(posted[0].contractVersion, 1);
    assert.deepEqual(posted[0].payload, { marker: true });
    assert.equal(promise.requestId, posted[0].id);
    console.log("PASS bridge bootstrap: host calls wait for init token before posting");
  }

  {
    const { context, posted } = createContext();
    context.send("init", {});
    assert.equal(posted.length, 1);
    assert.equal(posted[0].type, "init");
    assert.equal(posted[0].bridgeToken, null);
    console.log("PASS bridge bootstrap: init remains the only tokenless bridge request");
  }

  {
    const { context, posted } = createContext();
    context.document.body = { classList: { add: () => {} } };
    context.captureChatDraft = () => {};
    context.resetMessageEditState = () => {};
    context.resetLiveReasoning = () => {};
    context.acceptToolLibraryState = () => {};
    context.acceptSkillLibraryState = () => {};
    context.restoreChatDraft = () => {};
    context.renderSettings = () => {};
    context.renderTools = () => {};
    context.renderSkills = () => {};
    context.renderContext = () => {};
    context.renderChatSessions = () => {};
    context.renderMessages = () => {};
    context.renderContextMeter = () => {};
    context.renderHtmlWorkspace = () => {};
    context.renderModelControls = () => {};
    context.renderSendControls = () => {};
    context.renderVbaProject = () => {};
    context.updateVbaMacroRunState = () => {};
    context.log = () => {};
    context.$ = () => ({ textContent: "", classList: { contains: () => false } });
    vm.runInContext(chatSessionSource, context, { filename: "app-chat-session.js" });
    context.state.bridgeToken = "token-from-partial-init";
    context.applyBridgeUnavailableState(new Error("initial projection failed"));
    context.state.initializePromise = Promise.resolve();
    await assert.rejects(() => context.send("listChats", {}), /not initialized/);
    assert.equal(posted.length, 0);
    console.log("PASS bridge bootstrap: failed init projection revokes token and queued calls");
  }

  {
    const { context, posted, deliver } = createContext();
    context.state.bridgeToken = "token";
    const first = context.send("listChats", {});
    const second = context.send("getChatState", { chatId: "chat" });
    deliver({ id: posted[0].id, ok: false, error: "Transport failed.", errorDetail: "line one\nline two", errorCode: "bridge_transport_failed", transportFailure: true });
    await assert.rejects(first, error => error.code === "bridge_transport_failed" && error.transportFailure && error.detail.includes("line two"));
    assert.equal(Object.keys(context.state.pending).length, 1);
    deliver({ id: posted[1].id, ok: true, payload: { ok: true } });
    await second;
    assert.equal(context.state.bridgeUnavailable, false);
    console.log("PASS bridge bootstrap: correlated transport failure settles only its request");
  }

  {
    const { context, deliver } = createContext();
    context.state.bridgeToken = "token";
    const first = context.send("listChats", {});
    const second = context.send("getChatState", { chatId: "chat" });
    deliver({ ok: false, error: "Transport failed.", errorCode: "bridge_transport_failed", transportFailure: true });
    const settled = await Promise.allSettled([first, second]);
    assert.ok(settled.every(result => result.status === "rejected" && result.reason.transportFailure));
    assert.equal(Object.keys(context.state.pending).length, 0);
    assert.equal(context.state.bridgeUnavailable, true);
    console.log("PASS bridge bootstrap: uncorrelated transport failure settles all waits");
  }

  {
    const { context, deliver } = createContext();
    context.state.bridgeToken = "token";
    const pending = context.send("listChats", {});
    deliver(null);
    await assert.rejects(pending, error => error.transportFailure && error.code === "bridge_transport_failed");
    assert.equal(Object.keys(context.state.pending).length, 0);
    assert.equal(context.state.bridgeUnavailable, true);
    console.log("PASS bridge bootstrap: invalid JSON values cannot strand pending requests");
  }

  for (const phase of ["ready", "initializing", "init"]) {
    const { context, posted, deliver } = createContext();
    context.state.bridgeToken = "token";
    const other = context.send("getChatState", { chatId: "other-chat" });
    let resolveInit;
    if (phase === "initializing") {
      context.state.bridgeToken = "";
      context.state.initializePromise = new Promise(resolve => { resolveInit = resolve; });
    }
    const failure = new Error("Injected postMessage failure");
    let attempts = 0;
    context.chrome.webview.postMessage = () => { attempts++; throw failure; };
    const promise = context.send(phase === "init" ? "init" : "runTool", { chatId: "source-chat" });
    let outcome = "pending";
    let rejection;
    promise.then(() => { outcome = "resolved"; }, error => { outcome = "rejected"; rejection = error; });
    if (resolveInit) {
      assert.equal(attempts, 0);
      context.state.bridgeToken = "token";
      resolveInit();
    }
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(outcome, "rejected", "the caller's promise must settle");
    assert.equal(rejection, failure, "the caller receives the original send error");
    assert.equal(attempts, 1, "failed sends are never automatically replayed");
    assert.equal(context.state.pending[promise.requestId], undefined);
    assert.deepEqual(Object.keys(context.state.pending), [other.requestId], "other requests remain pending");
    deliver({ id: posted[0].id, ok: true, payload: { chatId: "other-chat" } });
    await other;
    assert.equal(Object.keys(context.state.pending).length, 0);
    console.log("PASS bridge bootstrap: post failure settles and cleans only its request (" + phase + ")");
  }

  {
    const { context, posted, timers, expire, deliver } = createContext();
    context.state.bridgeToken = "token";
    const promise = context.send("runTool", {});
    const id = promise.requestId;
    const before = context.state.pending[id].timer;
    deliver({ id, type: "progress", payload: {} });
    assert.ok(!timers.has(before), "progress replaces the idle timer");
    const rejection = assert.rejects(promise, error => error.code === "bridge_response_timeout" && error.resultUncertain);
    expire(context.state.pending[id].timer);
    await rejection;
    assert.equal(Object.keys(context.state.pending).length, 0);
    assert.equal(timers.size, 0);
    deliver({ id, ok: true, payload: { changed: true } });
    assert.equal(posted.length, 1, "missing/late replies never replay an operation");
    assert.equal(context.state.bridgeToken, "token", "read-back remains available");
    const read = context.send("getChatState", {});
    deliver({ id: read.requestId, ok: true, payload: {} });
    await read;
    assert.equal(timers.size, 0, "successful replies release timers");
    console.log("PASS bridge bootstrap: lost and late replies settle without effect replay");
  }

  for (const version of [0, 2, undefined]) {
    const { context, posted, deliver, contract, timers } = createContext();
    const init = context.send("init", {});
    deliver({ id: posted[0].id, ok: true, payload: { bridgeToken: "token", bridgeContract: { ...contract, version } } });
    await assert.rejects(init, error => error.code === "bridge_contract_mismatch");
    assert.equal(context.state.bridgeToken, "");
    assert.equal(context.state.bridgeOperations, null);
    assert.equal(timers.size, 0);
  }
  {
    const { context, deliver, contract } = createContext();
    const init = context.send("init", {});
    deliver({ id: init.requestId, ok: true, payload: { bridgeToken: "token", bridgeContract: contract } });
    await init;
    context.state.bridgeToken = "token";
    await assert.rejects(context.send("retiredOperation", {}), /Unknown WebView bridge operation/);
    console.log("PASS bridge bootstrap: exact version and operation catalog required");
  }
  console.log("OK bridge bootstrap");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
