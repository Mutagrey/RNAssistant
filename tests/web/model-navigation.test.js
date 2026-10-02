"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const read = name => fs.readFileSync(path.join(__dirname, "../../web/js", name), "utf8");
const tick = () => new Promise(resolve => setImmediate(resolve));
const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };

function fixture() {
  let creations = 0;
  class Node {
    constructor() { this.children = []; this.attributes = {}; this.listeners = {}; this.value = ""; this.open = false; this.replacements = 0;
      const classes = new Set(); this.classList = { contains: value => classes.has(value), toggle: (value, enabled) => enabled ? classes.add(value) : classes.delete(value) }; }
    get childNodes() { return this.children; }
    appendChild(node) { this.children.push(node); }
    replaceChildren() { this.children = []; this.replacements++; }
    setAttribute(key, value) { this.attributes[key] = value; }
    addEventListener(name, fn) { this.listeners[name] = fn; }
    querySelector() { return this.summary || null; }
    querySelectorAll() { return this.children.filter(node => (node.className || "").split(/\s+/).includes("composer-model-item")); }
  }
  const nodes = Object.fromEntries(["chatModelPicker", "chatModelMenu", "chatModelButtonLabel", "chatReasoningToggle", "modelSelect", "modelInput", "loadModelsButton", "apiKeyInput"].map(id => [id, new Node()]));
  nodes.chatModelPicker.summary = new Node();
  nodes.apiKeyInput.value = "unsaved-key";
  const context = vm.createContext({ console, Promise, Date }); context.window = context;
  context.state = { settings: { Model: "default-model" }, bridgeToken: "excel-token", activeChatId: "excel-chat",
    modelCatalog: { models: [], loaded: false, loading: false, error: "" }, chatNavigationPending: 0, chatNavigationVersion: 1 };
  context.$ = id => nodes[id] || null;
  context.document = { createElement: () => { creations++; const node = new Node(); node.dataset = {}; return node; } };
  context.isPanelActive = name => name === "chat";
  context.currentActiveSend = () => null;
  context.hasActiveMessageEdit = () => false;
  context.effectiveModelSupportsReasoning = context.effectiveModelSupportsImages = context.effectiveModelSupportsAudio = () => null;
  context.effectiveModelCapabilityValue = () => null;
  context.log = () => {};
  context.readSettings = () => ({ Model: "form-model" });
  context.renderSendControls = () => {};
  context.applyChatState = value => { context.state.activeChatId = value.activeChatId; return true; };
  ["app-model-data.js", "app-model-picker.js", "app-model-render.js", "app-models.js", "app-chat-session.js"].forEach(file => vm.runInContext(read(file), context, { filename: file }));
  return { context, nodes, creations: () => creations };
}

(async function () {
  {
    const { context, nodes, creations } = fixture();
    context.normalizeModelCatalog({ models: Array.from({ length: 500 }, (_, i) => ({ id: "model-" + i })) });
    for (let i = 0; i < 100; i++) context.renderModelControls();
    assert.equal(creations(), 0, "closed picker and hidden settings create no catalog DOM nodes");
    nodes.chatModelPicker.open = true;
    context.renderChatModelPicker();
    const rendered = creations();
    assert.ok(rendered > 500);
    for (let i = 0; i < 100; i++) context.renderChatModelPicker();
    assert.equal(creations(), rendered, "composer updates retain the open menu when its data is unchanged");
    let chosen;
    context.saveChatModelSelection = value => { chosen = value; };
    nodes.chatModelMenu.children.find(node => node.dataset && node.dataset.value === "model-499").listeners.click();
    assert.equal(chosen, "model-499", "retained menu item dispatches its own model identity");
    nodes.chatModelPicker.open = true;
    context.state.modelCatalog.loading = true;
    context.renderChatModelPicker();
    assert.equal(nodes.chatModelPicker.open, true, "opening discovery does not close its own picker");
    context.state.chatNavigationPending = 1;
    context.renderModelControls();
    assert.equal(nodes.chatModelPicker.open, false);
    assert.equal(nodes.chatModelPicker.summary.attributes["aria-disabled"], "true");
    assert.equal(nodes.chatReasoningToggle.disabled, true, "late model render cannot reenable navigation controls");
    console.log("PASS models: 500-model hidden catalog does no DOM work; open menu is retained and navigation stays disabled");
  }
  {
    const { context } = fixture(), catalog = deferred(), cancellation = deferred(), calls = [];
    catalog.promise.requestId = "catalog";
    context.send = (type, payload) => {
      calls.push({ type, payload });
      return type === "getModelCatalog" ? catalog.promise : Promise.resolve({ activeChatId: "outlook-chat" });
    };
    context.cancelBridgeRequest = id => { calls.push({ type: "cancel", id }); return cancellation.promise; };
    const loading = context.loadModelCatalog(false);
    assert.equal(context.loadModelCatalog(false), loading, "discovery shares one pending request");
    assert.equal(calls[0].payload.apiKey, null, "chat picker must not use an unsaved settings-form key");
    const switching = context.navigateChat("selectChat", { chatId: "outlook-chat" });
    assert.equal(context.state.chatNavigationTargetId, "outlook-chat", "the pending selection has a visible loading target");
    await tick();
    assert.deepEqual(calls.map(call => call.type), ["getModelCatalog", "cancel"]);
    assert.equal(calls[1].id, "catalog");
    catalog.resolve({ models: ["old-excel-model"] }); // Success racing cancellation must still be discarded.
    await loading; await tick();
    assert.equal(context.state.modelCatalog.loaded, false);
    assert.equal(calls.length, 2, "host switch also waits for cancellation acknowledgement");
    cancellation.resolve({ cancelled: true });
    await switching;
    assert.equal(calls[2].type, "selectChat");
    assert.equal(context.state.activeChatId, "outlook-chat");
    assert.equal(context.state.chatNavigationPending, 0);
    assert.equal(context.state.chatNavigationTargetId, "", "the loading target clears after selection");
    assert.equal(context.state.modelCatalogRequest, null);
    console.log("PASS models: navigation cancels and drains discovery, ignores racing success and never uses an unsaved API key");
  }
  {
    const { context } = fixture(), old = deferred();
    old.promise.requestId = "old";
    context.send = () => old.promise;
    context.cancelBridgeRequest = () => Promise.resolve();
    const loading = context.loadModelCatalog(true);
    context.state.bridgeToken = "outlook-token";
    context.state.settings = { Model: "new-model" };
    context.resetModelCatalog();
    old.resolve({ models: ["stale-model"] });
    assert.equal(await loading, false);
    assert.equal(context.state.modelCatalog.models.length, 0);
    await context.cancelModelCatalogLoad();
    const fresh = deferred();
    context.send = () => fresh.promise;
    const reloaded = context.loadModelCatalog(false);
    fresh.resolve({ models: ["new-model"] });
    assert.equal(await reloaded, true);
    assert.equal(context.state.modelCatalog.models[0].value, "new-model");
    console.log("PASS models: old binding cannot publish its catalog; the new binding can reload");
  }
  {
    const { context } = fixture(), saving = deferred(), calls = [];
    context.send = (type, payload) => { calls.push({ type, payload }); return type === "setChatModel" ? saving.promise : Promise.resolve({ activeChatId: "next" }); };
    context.sendChatPreference("setChatModel", { chatId: "excel-chat", model: "chosen" });
    const switching = context.navigateChat("selectChat", { chatId: "next" });
    await tick(); assert.equal(calls.length, 1);
    assert.equal(await context.saveChatModelSelection("late-model"), false);
    assert.equal(await context.saveChatReasoningSelection(true), false);
    saving.resolve({ activeChatId: "excel-chat" });
    await switching;
    assert.deepEqual(calls.map(call => call.type), ["setChatModel", "selectChat"]);
    console.log("PASS models: accepted model preference drains before rebinding; new preferences wait for navigation");
  }
  {
    const { context } = fixture(), preview = deferred(), cancelled = deferred(), calls = [];
    preview.promise.requestId = "preview";
    context.send = (type, payload) => {
      calls.push(payload);
      return calls.length === 1 ? preview.promise : Promise.resolve({ models: ["saved-server-model"] });
    };
    context.cancelBridgeRequest = () => cancelled.promise;
    const loading = context.loadModelCatalog(true);
    context.document.querySelector = () => ({ id: "tab-settings" });
    context.document.querySelectorAll = () => [];
    context.document.addEventListener = () => {};
    context.renderChatSessions = context.renderMessages = context.renderContext = context.renderContextMeter = () => {};
    vm.runInContext(read("app.js"), context);
    context.switchTab("chat");
    const reload = context.loadModelCatalog(false);
    assert.equal(calls.length, 1, "new picker request drains the cancelled settings preview first");
    preview.resolve({ models: ["unsaved-server-model"] });
    await loading;
    cancelled.resolve({ cancelled: true });
    assert.equal(await reload, true, "the open picker reloads after cancellation without reopening");
    assert.equal(calls[0].apiKey, "unsaved-key");
    assert.equal(calls[1].apiKey, null);
    assert.equal(context.state.modelCatalog.models[0].value, "saved-server-model");
    context.send = () => Promise.resolve({ models: ["another-preview"] });
    await context.loadModelCatalog(true);
    context.switchTab("chat");
    assert.equal(context.state.modelCatalog.loaded, false, "a completed settings preview is also discarded on leaving settings");
    console.log("PASS models: leaving settings discards previews; the chat picker reloads saved settings after cancellation");
  }
  {
    const { context } = fixture(), catalog = deferred(), cancellation = deferred();
    catalog.promise.requestId = "failed";
    context.send = () => catalog.promise;
    context.cancelBridgeRequest = () => cancellation.promise;
    const loading = context.loadModelCatalog(false);
    const drained = context.cancelModelCatalogLoad();
    cancellation.reject(new Error("transport stopped"));
    let finished = false; drained.then(() => { finished = true; });
    await tick(); assert.equal(finished, false, "a failed cancel still waits for the original request to terminate");
    catalog.reject(Object.assign(new Error("cancelled"), { cancelled: true }));
    assert.equal(await loading, false);
    await drained;
    assert.equal(context.state.modelCatalogRequest, null);
    assert.equal(context.state.modelCatalog.loading, false);
    assert.equal(context.state.modelCatalog.error, "", "intentional cancellation is not shown as a model failure");
    console.log("PASS models: cancellation failure drains the request and resets the loading state");
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
