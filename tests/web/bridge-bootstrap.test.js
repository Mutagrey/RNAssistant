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
      addEventListener: () => {},
      postMessage: message => posted.push(JSON.parse(JSON.stringify(message)))
    }
  };
  vm.runInContext(source, context, { filename: "app-core.js" });
  return { context, posted };
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
    context.$ = () => ({ textContent: "" });
    vm.runInContext(chatSessionSource, context, { filename: "app-chat-session.js" });
    context.state.bridgeToken = "token-from-partial-init";
    context.applyBridgeUnavailableState(new Error("initial projection failed"));
    context.state.initializePromise = Promise.resolve();
    await assert.rejects(() => context.send("listChats", {}), /not initialized/);
    assert.equal(posted.length, 0);
    console.log("PASS bridge bootstrap: failed init projection revokes token and queued calls");
  }

  console.log("OK 4/4");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
