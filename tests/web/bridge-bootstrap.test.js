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
  const context = vm.createContext({
    console,
    setTimeout,
    clearTimeout
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
  return { context, posted, deliver: data => messageListener({ data }) };
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

  console.log("OK 10/10");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
