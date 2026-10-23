"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const source = fs.readFileSync(path.join(root, "web/js/app-core.js"), "utf8");
const chatSessionSource = fs.readFileSync(path.join(root, "web/js/app-chat-session.js"), "utf8");
const messagesSource = fs.readFileSync(path.join(root, "web/js/app-messages.js"), "utf8");

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
  const contract = { version: 2, operations: ["init", "listChats", "getChatState", "runTool", "clearRuntimeData", "selectChat", "createOfficeHostChat"].map(name =>
    ({ name, idleTimeoutMs: name === "init" ? 30000 : 120000, cancellable: name === "runTool" })) };
  context.acceptBridgeContract(contract);
  return { context, posted, contract, timers, deliver: data => messageListener({ data }),
    expire: id => { const timer = timers.get(id); assert.ok(timer); timers.delete(id); timer.callback(); } };
}

function installChatProjection(context) {
  for (const file of ["app-run-view-state.js", "app-chat-state.js", "app-chat-session.js"]) {
    vm.runInContext(fs.readFileSync(path.join(root, "web/js", file), "utf8"), context, { filename: file });
  }
  context.document.body = { classList: { add() {}, remove() {} } };
  context.$ = () => ({ textContent: "", classList: { contains: () => false } });
  for (const name of ["captureChatDraft", "restoreChatDraft", "resetMessageEditState", "resetLiveReasoning",
    "acceptToolLibraryState", "acceptSkillLibraryState", "renderSettings",
    "renderChatSessions", "renderMessages", "renderContext", "renderContextMeter", "renderModelControls",
    "renderSendControls", "syncTokenEstimateCalibrationFromUsage", "log"]) context[name] = () => {};
  context.toolLibraryItemsFromContract = contract => contract.items;
  context.skillLibraryItemsFromContract = contract => contract.items;
}

function appInit(host, chatId) {
  return { host, activeChatId: chatId, sessionRevision: 1, activeChatMode: "agent",
    activeChatModel: "model", activeChatReasoning: true, mutationPolicy: "Ask", chatReadOnly: false,
    chats: [], documents: [], messages: [], artifacts: [], context: {}, contextUsage: {},
    htmlWorkspace: { files: [], dataSources: [] }, tools: { items: [] }, skills: { items: [] },
    inbox: { chatId, inputs: [] } };
}

(async function () {
  {
    const { context, posted, contract, deliver } = createContext();
    installChatProjection(context);
    const inboxes = [];
    context.applyChatInbox = inbox => inboxes.push(inbox.chatId);
    const initRequest = context.send("init", {});
    const initial = appInit("Excel", "excel-chat");
    deliver({ id: initRequest.requestId, ok: true,
      payload: { bridgeToken: "session-token", bridgeContract: contract, init: initial } });
    const init = await initRequest;
    assert.equal(init, initial, "transport resolves only application state");
    context.state.bridgeFailure = { code: "bridge_init_failed", message: "Earlier failure" };
    context.applyInitState(init);
    assert.equal(context.state.bridgeFailure, null, "successful init clears the previous startup failure");
    assert.equal(context.state.bridgeToken, "session-token", "transport accepts its token before resolving init");
    for (const operation of ["selectChat", "createOfficeHostChat"]) {
      const target = appInit("Word", operation + "-chat");
      const navigation = context.navigateChat(operation, { host: "Word", chatId: target.activeChatId });
      await new Promise(resolve => setImmediate(resolve));
      deliver({ id: posted.at(-1).id, ok: true, payload: { host: "Word", chatId: target.activeChatId, init: target } });
      await navigation;
      assert.equal(context.state.host, "Word");
      assert.equal(context.state.activeChatId, target.activeChatId);
      assert.equal(context.state.activeChatReasoning, true);
      assert.equal(context.state.bridgeToken, "session-token", "application navigation cannot revoke the token");
      const next = context.send("getChatState", { chatId: target.activeChatId });
      assert.equal(posted.at(-1).bridgeToken, "session-token", "next request remains authorized");
      deliver({ id: next.requestId, ok: true, payload: {} });
      await next;
    }
    assert.deepEqual(inboxes, ["excel-chat", "selectChat-chat", "createOfficeHostChat-chat"],
      "init and navigation both apply the shared inbox projection");
    const reset = context.send("clearRuntimeData", {});
    deliver({ id: reset.requestId, ok: true,
      payload: { bridgeToken: "reset-token", bridgeContract: contract, init: initial } });
    context.applyInitState(await reset);
    assert.equal(context.state.bridgeToken, "reset-token", "explicit bootstrap can replace transport authority");
    console.log("PASS bridge bootstrap: real chat projection preserves transport across host navigation and reset");
  }

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
    assert.equal(posted[0].contractVersion, 2);
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
    assert.equal(context.state.bridgeFailure.message, "initial projection failed");
    context.state.initializePromise = Promise.resolve();
    await assert.rejects(() => context.send("listChats", {}), /not initialized/);
    assert.equal(posted.length, 0);
    console.log("PASS bridge bootstrap: failed init projection revokes token and queued calls");
  }

  {
    const { context, posted, deliver } = createContext();
    installChatProjection(context);
    const element = tag => ({ tag, children: [], handlers: {},
      appendChild(child) { this.children.push(child); },
      addEventListener(type, handler) { this.handlers[type] = handler; } });
    context.document.createElement = element;
    context.RNAssistantIcons = { render: () => "" };
    let retries = 0;
    context.initialize = () => { retries += 1; };
    vm.runInContext(messagesSource, context, { filename: "app-messages.js" });
    const init = context.send("init", {});
    deliver({ id: posted[0].id, ok: false, error: "История чата не читается.",
      errorDetail: "Request failed. See the local runtime log for details.", errorCode: "bridge_request_failed" });
    await assert.rejects(init, error => {
      context.applyBridgeUnavailableState(error);
      return error.code === "bridge_request_failed";
    });
    const unavailable = context.renderChatEmptyState();
    assert.equal(unavailable.children[2].textContent, "История чата не читается.");
    const retry = unavailable.children.flatMap(child => child.children).find(child => child.tag === "button");
    assert.equal(retry.textContent, "Повторить загрузку");
    retry.handlers.click();
    assert.equal(retries, 1, "startup retry is an explicit user action");
    context.state.bridgeFailure.code = "bridge_contract_mismatch";
    assert.equal(context.renderChatEmptyState().children.some(child => child.className === "chat-empty-suggestions"), false,
      "incompatible bridge contract requires a matching panel build");
    context.chrome = null;
    assert.equal(context.renderChatEmptyState().children.some(child => child.className === "chat-empty-suggestions"), false,
      "static browser preview cannot retry without a host bridge");
    console.log("PASS bridge bootstrap: startup failure shows cause and manual retry only for a present bridge");
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

  for (const version of [0, 1, 3, undefined]) {
    const { context, posted, deliver, contract, timers } = createContext();
    const init = context.send("init", {});
    deliver({ id: posted[0].id, ok: true, payload: { bridgeToken: "token", bridgeContract: { ...contract, version }, init: {} } });
    await assert.rejects(init, error => error.code === "bridge_contract_mismatch");
    assert.equal(context.state.bridgeToken, "");
    assert.equal(context.state.bridgeOperations, null);
    assert.equal(timers.size, 0);
  }
  {
    const { context, deliver, contract } = createContext();
    const init = context.send("init", {});
    deliver({ id: init.requestId, ok: true, payload: { bridgeToken: "token", bridgeContract: contract, init: {} } });
    await init;
    assert.equal(context.state.bridgeToken, "token");
    await assert.rejects(context.send("retiredOperation", {}), /Unknown WebView bridge operation/);
    console.log("PASS bridge bootstrap: exact version and operation catalog required");
  }
  for (const invalid of [{ bridgeToken: "" }, { bridgeToken: null }, { init: null }, { init: [] }]) {
    const { context, deliver, contract } = createContext();
    const init = context.send("init", {});
    deliver({ id: init.requestId, ok: true,
      payload: { bridgeToken: "token", bridgeContract: contract, init: {}, ...invalid } });
    await assert.rejects(init, error => error.code === "bridge_contract_mismatch");
    assert.equal(context.state.bridgeToken, "");
    assert.equal(context.state.bridgeOperations, null);
  }
  console.log("OK bridge bootstrap");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
