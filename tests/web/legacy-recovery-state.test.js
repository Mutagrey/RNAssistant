"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

class Node {
  constructor(tag = "div") {
    this.tagName = tag; this.children = []; this.events = {}; this.dataset = {}; this.attributes = {};
    this.value = ""; this.disabled = false; this.scrollTop = 0;
  }
  set textContent(value) { this.children = []; this.content = String(value); }
  get textContent() { return (this.content || "") + this.children.map(node => node.textContent).join(""); }
  set innerHTML(value) { assert.equal(value, "", "archive content must never reach an HTML parser"); this.replaceChildren(); }
  appendChild(node) { this.children.push(node); return node; }
  replaceChildren() { this.content = ""; this.children = []; }
  setAttribute(name, value) { this.attributes[name] = value; }
  getAttribute(name) { return this.attributes[name]; }
  addEventListener(type, action) { this.events[type] = action; }
  trigger(type) { if (type !== "click" || !this.disabled) return this.events[type] && this.events[type](); }
}

const settle = async () => { for (let index = 0; index < 20; index++) await Promise.resolve(); };
const attack = '<img src=x onerror="window.archiveExecuted=true">';
const chat = (id = "archive-source", revision = 42) => ({ sourceId: id, chatId: "chat-old", title: attack,
  documentTitle: "Old document", documentPath: "C:/old/" + attack, host: "Word", revision, messageCount: 7,
  status: "readable", error: null });
const plugin = (id = "legacy-plugin") => ({ pluginId: id, name: attack, sourceRoot: "C:/plugins/" + attack,
  sourceWorkspaceId: "old-workspace", status: "readable", canConnect: true, canImport: true, registrationRevision: "registration-42" });
const page = (items, nextOffset = null) => ({ rootPath: "C:/archive/" + attack, total: items.length, nextOffset, items });
const transcript = (extra = {}) => Object.assign({ sourceId: "archive-source", revision: 42, messageStartIndex: 0,
  totalMessages: 7, nextMessageIndex: 3, nextCharacterOffset: 9, text: "Archived text\n" + attack }, extra);
const chatReceipt = (item = chat(), extra = {}) => Object.assign({ status: "imported", sourceId: item.sourceId,
  sourceRevision: item.revision, chatId: item.chatId, host: item.host, documentKey: "exact-document", documentAuthorityId: "exact-authority",
  workspaceId: item.host === "Workspace" ? "exact-workspace" : null, messageCount: item.messageCount, revision: 10, targetDeleted: false }, extra);
const pluginReceipt = (item = plugin(), extra = {}) => Object.assign({ item: { pluginId: item.pluginId, name: "Recovered", icon: "puzzle", enabled: true },
  registrationRevision: item.registrationRevision, alreadyImported: false, savedVersions: 2, releases: 1 }, extra);
const pluginButton = (f, index, action) => f.recovery("PluginList").children[index].children[3].children[action];

function fixture() {
  const nodes = new Map(), calls = [], copied = [], events = {}, windowEvents = {}, refreshed = [], found = [];
  let shown = true;
  const node = id => { if (!nodes.has(id)) nodes.set(id, new Node()); return nodes.get(id); };
  const recovery = id => node("legacyRecovery" + id);
  node("diagnosticsAppView").value = "health";
  const context = { state: { diagnosticsTab: "application", bridgeToken: "", initializePromise: null, activeChatId: "unchanged-active-chat" }, $: node,
    document: { hidden: false, getElementById: node, createElement: tag => new Node(tag),
      addEventListener: (name, handler) => { events[name] = handler; } },
    addEventListener: (name, handler) => { windowEvents[name] = handler; },
    RNAssistantDiagnostics: { isVisible: () => shown },
    RNAssistantIcons: { renderPlugin: () => "" }, RNAssistantDockWorkspace: { refreshPresentation() {} },
    RNAssistantWorkspaceChat: { node: name => node("plugin" + name) },
    RNAssistantProjectChats: { refreshCatalog: async () => { refreshed.push({ type: "workspaceCatalog" }); } },
    RNAssistantConversationPanel: { find: target => { found.push(JSON.parse(JSON.stringify(target)));
      return { refresh: async () => { refreshed.push({ type: "view", target: JSON.parse(JSON.stringify(target)) }); } }; } },
    RNAssistantBrowser: { pluginImported: pluginId => { refreshed.push({ type: "browser", pluginId }); } },
    synchronizeChatState: async force => { assert.equal(force, true); refreshed.push({ type: "officeCatalog" }); },
    copyTextResult: value => { copied.push(value); return Promise.resolve(); },
    cancelBridgeRequest: requestId => { calls.push({ type: "cancelRequest", requestId }); return Promise.resolve(); },
    send: (type, payload) => {
      const call = { type, payload: JSON.parse(JSON.stringify(payload)), requestId: "request-" + calls.length };
      const promise = new Promise((resolve, reject) => {
        call.resolve = value => { call.done = true; resolve(value); };
        call.reject = error => { call.done = true; reject(error); };
      });
      promise.requestId = call.requestId; calls.push(call); return promise;
    }
  };
  context.window = context; vm.createContext(context);
  for (const file of ["app-plugins.js", "app-legacy-recovery.js"])
    vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), context, { filename: file });
  const ui = context.RNAssistantLegacyRecovery;
  const requests = type => calls.filter(call => call.type === type);
  const pending = type => requests(type).find(call => !call.done);
  const reply = (type, value) => { const call = pending(type); assert.ok(call, "expected pending " + type); call.resolve(value); return call; };
  const setVisible = value => { shown = value; ui.sync(); };
  ui.bind(); ui.sync();
  return { context, ui, node, recovery, calls, copied, events, windowEvents, refreshed, found, requests, pending, reply, setVisible };
}

async function load(f, chats = page([chat()]), plugins = page([plugin()])) {
  f.recovery("Refresh").trigger("click");
  f.reply("listLegacyChats", chats); f.reply("listLegacyPlugins", plugins); await settle();
}

(async () => {
  {
    const f = fixture();
    assert.equal(f.calls.length, 0, "bind/sync during startup performs no archive reads");
    f.ui.bind(); f.ui.sync(); f.context.document.hidden = true; f.events.visibilitychange();
    f.context.document.hidden = false; f.events.visibilitychange();
    assert.equal(f.calls.length, 0, "visibility and repeated binding never start archive discovery");
    const broken = Object.assign(chat("broken", 0), { status: "unreadable", error: "Protection key unavailable " + attack });
    const missing = Object.assign(plugin("missing"), { status: "missingSource", canConnect: false, error: "Folder is missing" });
    await load(f, page([chat(), broken], 25), page([plugin(), missing], 25));
    assert.deepEqual(f.requests("listLegacyChats")[0].payload, { offset: 0, limit: 25 });
    assert.deepEqual(f.requests("listLegacyPlugins")[0].payload, { offset: 0, limit: 25 });
    assert.match(f.recovery("ChatSelect").children[0].textContent, /<img/);
    assert.equal(f.recovery("ChatSelect").children[0].children.length, 0);
    assert.match(f.recovery("PluginList").children[0].children[0].textContent, /<img/);
    assert.equal(pluginButton(f, 1, 2).disabled, true, "missing source cannot be reconnected");
    f.recovery("ChatSelect").value = "broken"; f.recovery("ChatSelect").trigger("change");
    assert.equal(f.recovery("Read").disabled, true);
    assert.match(f.recovery("ChatInfo").textContent, /Ошибка чтения.*Protection key unavailable/);
    f.recovery("Read").trigger("click"); assert.equal(f.requests("readLegacyTranscript").length, 0);
    f.recovery("ChatSelect").value = "archive-source"; f.recovery("ChatSelect").trigger("change");
    f.recovery("Read").trigger("click");
    assert.deepEqual(f.pending("readLegacyTranscript").payload, { sourceId: "archive-source", expectedRevision: 42,
      messageIndex: 0, characterOffset: 0, maxCharacters: 32000 });
    f.reply("readLegacyTranscript", transcript()); await settle();
    assert.equal(f.recovery("Text").textContent, transcript().text);
    assert.equal(f.recovery("Text").children.length, 0, "transcript is plain text rather than executable markup");
    f.recovery("Copy").trigger("click"); await settle(); assert.deepEqual(f.copied, [transcript().text]);
    f.recovery("TextNext").trigger("click");
    assert.deepEqual(f.pending("readLegacyTranscript").payload, { sourceId: "archive-source", expectedRevision: 42,
      messageIndex: 3, characterOffset: 9, maxCharacters: 32000 });
    f.reply("readLegacyTranscript", transcript({ revision: 43, text: "wrong revision" })); await settle();
    assert.equal(f.recovery("Text").textContent, transcript().text, "mismatched exact revision cannot replace the accepted page");
    f.recovery("TextNext").trigger("click");
    f.reply("readLegacyTranscript", transcript({ messageStartIndex: 3, text: "Last part", nextMessageIndex: null })); await settle();
    assert.equal(f.recovery("TextNext").disabled, true); assert.match(f.recovery("TextStatus").textContent, /конец текста/);
    f.recovery("TextFirst").trigger("click"); assert.equal(f.pending("readLegacyTranscript").payload.messageIndex, 0);
    f.reply("readLegacyTranscript", transcript()); await settle();
    f.recovery("ChatNext").trigger("click"); assert.equal(f.pending("listLegacyChats").payload.offset, 25);
    f.reply("listLegacyChats", page([chat("next-source", 7)])); await settle();
    assert.equal(f.recovery("Text").textContent, "", "new catalog selection clears the previous transcript");
    f.recovery("Read").trigger("click"); assert.equal(f.pending("readLegacyTranscript").payload.expectedRevision, 7);
    f.reply("readLegacyTranscript", transcript({ sourceId: "next-source", revision: 7 })); await settle();
    f.recovery("ChatFirst").trigger("click"); assert.equal(f.pending("listLegacyChats").payload.offset, 0);
    f.reply("listLegacyChats", page([chat()])); await settle();
    f.recovery("PluginNext").trigger("click"); assert.equal(f.pending("listLegacyPlugins").payload.offset, 25);
    f.reply("listLegacyPlugins", page([plugin("next-plugin")])); await settle();
    f.recovery("PluginFirst").trigger("click"); assert.equal(f.pending("listLegacyPlugins").payload.offset, 0);
    console.log("PASS legacy recovery: explicit bounded catalogs, exact transcript cursors, unreadable status and inert text");
  }

  {
    const f = fixture();
    f.recovery("Refresh").trigger("click");
    const oldChats = f.pending("listLegacyChats"), oldPlugins = f.pending("listLegacyPlugins");
    f.setVisible(false);
    assert.deepEqual(f.requests("cancelRequest").map(call => call.requestId), [oldChats.requestId, oldPlugins.requestId]);
    f.setVisible(true); assert.equal(f.requests("listLegacyChats").length, 1, "show does not restart hidden reads");
    f.recovery("Refresh").trigger("click");
    oldChats.resolve(page([chat("stale-source")])); oldPlugins.resolve(page([plugin("stale-plugin")])); await settle();
    assert.equal(f.recovery("ChatSelect").children.length, 0, "late catalog cannot replace a new generation");
    f.reply("listLegacyChats", page([chat()])); f.reply("listLegacyPlugins", page([plugin()])); await settle();
    f.recovery("Read").trigger("click"); const oldText = f.pending("readLegacyTranscript");
    f.context.document.hidden = true; f.events.visibilitychange();
    oldText.resolve(transcript({ text: "late hidden text" })); await settle();
    assert.equal(f.recovery("Text").textContent, "");
    assert.ok(f.requests("cancelRequest").some(call => call.requestId === oldText.requestId));
    f.context.document.hidden = false; f.events.visibilitychange();
    assert.equal(f.requests("readLegacyTranscript").length, 1);
    f.recovery("Read").trigger("click"); const finalText = f.pending("readLegacyTranscript");
    f.windowEvents.pagehide(); finalText.resolve(transcript()); await settle();
    assert.equal(f.recovery("Text").textContent, "", "page teardown rejects late transcript delivery");
    console.log("PASS legacy recovery: hide/pagehide cancel reads and ignore stale catalog/transcript responses");
  }

  {
    const f = fixture(); await load(f);
    const button = pluginButton(f, 0, 2);
    button.trigger("click"); button.trigger("click");
    assert.equal(f.requests("reconnectLegacyPlugin").length, 1, "double click cannot duplicate reconnect");
    assert.deepEqual(f.pending("reconnectLegacyPlugin").payload, { pluginId: "legacy-plugin" }, "only native plugin identity crosses the bridge");
    f.setVisible(false);
    assert.equal(f.requests("cancelRequest").length, 0, "accepted reconnect is not cancelled with presentation");
    f.reply("reconnectLegacyPlugin", { pluginId: "legacy-plugin", enabled: false }); await settle();
    f.reply("getPlugins", { items: [{ pluginId: "legacy-plugin", name: "Recovered", icon: "puzzle" }] }); await settle();
    assert.ok(f.context.RNAssistantPlugins.item("legacy-plugin"), "successful reconnect refreshes the canonical plugin UI owner");
    f.setVisible(true);
    const connected = f.recovery("PluginList").children[0];
    assert.equal(pluginButton(f, 0, 2).disabled, true); assert.equal(pluginButton(f, 0, 2).textContent, "Подключено");
    assert.match(connected.children[2].textContent, /Проверьте, сохраните и активируйте/);
    console.log("PASS legacy recovery: explicit reconnect is deduplicated, survives hide and refreshes plugins");
  }

  for (const rejectedOldRead of [false, true]) {
    const f = fixture();
    const initial = f.context.RNAssistantPlugins.refresh().catch(() => {});
    const ordinary = f.context.RNAssistantPlugins.refresh().catch(() => {});
    const stale = f.pending("getPlugins"); await load(f);
    pluginButton(f, 0, 2).trigger("click");
    f.reply("reconnectLegacyPlugin", { pluginId: "legacy-plugin", enabled: false }); await settle();
    const joined = f.context.RNAssistantPlugins.refresh({ afterPending: true });
    assert.equal(f.requests("getPlugins").length, 1, "fresh readers wait for the older in-flight catalog");
    if (rejectedOldRead) stale.reject(new Error("Old startup read failed")); else stale.resolve({ items: [] });
    await initial; await ordinary; await settle();
    assert.equal(f.requests("getPlugins").length, 2, "successful reconnect must read a catalog started after the mutation");
    f.reply("getPlugins", { items: [{ pluginId: "legacy-plugin", name: "Recovered", icon: "puzzle" }] });
    await joined; await settle();
    assert.ok(f.context.RNAssistantPlugins.item("legacy-plugin"));
    assert.equal(f.node("pluginRail").children[0].dataset.pluginId, "legacy-plugin");
    assert.equal(f.requests("getPlugins").length, 2, "concurrent fresh refresh requests share the same successor read");
    console.log("PASS legacy recovery: post-reconnect catalog follows the pending " + (rejectedOldRead ? "failed" : "stale") + " startup read");
  }

  {
    const f = fixture(), first = chat(), second = Object.assign(chat("other-source", 8), { chatId: "other-chat", title: "Other" });
    await load(f, page([first, second]));
    f.recovery("ImportChat").trigger("click"); f.recovery("ImportChat").trigger("click");
    assert.equal(f.requests("importLegacyChat").length, 1);
    assert.deepEqual(f.pending("importLegacyChat").payload, { sourceId: first.sourceId, expectedRevision: first.revision });
    f.recovery("ChatSelect").value = second.sourceId; f.recovery("ChatSelect").trigger("change");
    f.reply("importLegacyChat", chatReceipt(first)); await settle();
    assert.equal(f.recovery("ChatSelect").value, second.sourceId, "completion preserves the current archive selection");
    assert.equal(f.recovery("ImportStatus").textContent, "", "receipt belongs to the captured source, not the selected row");
    assert.deepEqual(f.found, [{ source: "office", sessionId: first.chatId }]);
    assert.deepEqual(f.refreshed.map(value => value.type).sort(), ["officeCatalog", "view"]);
    assert.equal(f.context.state.activeChatId, "unchanged-active-chat", "import never selects a global chat");
    f.recovery("ChatSelect").value = first.sourceId; f.recovery("ChatSelect").trigger("change");
    assert.match(f.recovery("ImportStatus").textContent, /Чат перенесён в историю.*7/);
    assert.equal(f.recovery("ImportChat").disabled, true); assert.equal(f.recovery("CheckChat").disabled, true);
    assert.equal(f.requests("listLegacyChats").length, 1, "receipt refresh does not rescan the archive");
    console.log("PASS legacy recovery: exact chat import receipt refreshes its existing view and catalog without changing selection");
  }

  {
    const f = fixture(), item = Object.assign(chat(), { host: "Workspace" }); await load(f, page([item]));
    f.recovery("ImportChat").trigger("click");
    f.pending("importLegacyChat").reject(Object.assign(new Error("Acknowledgement lost " + attack), { transportFailure: true })); await settle();
    assert.match(f.recovery("ImportStatus").textContent, /Результат переноса неизвестен/);
    assert.equal(f.recovery("ImportStatus").children.length, 0);
    assert.equal(f.recovery("ImportChat").disabled, true);
    f.recovery("CheckChat").trigger("click"); f.recovery("CheckChat").trigger("click");
    assert.equal(f.requests("getLegacyChatImport").length, 1);
    assert.deepEqual(f.pending("getLegacyChatImport").payload, { sourceId: item.sourceId, expectedRevision: item.revision, chatId: item.chatId });
    const late = f.pending("getLegacyChatImport"); f.setVisible(false); late.resolve(chatReceipt(item)); await settle();
    assert.deepEqual(f.refreshed, [], "hidden status read is ignored and cannot refresh a stale view");
    assert.ok(f.requests("cancelRequest").some(call => call.requestId === late.requestId));
    f.setVisible(true); assert.equal(f.requests("getLegacyChatImport").length, 1, "show does not automatically retry receipt reads");
    f.recovery("CheckChat").trigger("click"); f.reply("getLegacyChatImport", null); await settle();
    assert.equal(f.recovery("ImportChat").disabled, true, "a missing receipt does not authorize replay after lost acknowledgement");
    assert.match(f.recovery("ImportStatus").textContent, /остаётся неизвестным/);
    f.recovery("CheckChat").trigger("click"); f.reply("getLegacyChatImport", chatReceipt(item, { sourceRevision: 43 })); await settle();
    assert.deepEqual(f.refreshed, [], "a receipt for another revision is not import proof");
    f.recovery("CheckChat").trigger("click"); f.reply("getLegacyChatImport", chatReceipt(item, { status: "alreadyImported" })); await settle();
    assert.deepEqual(f.found, [{ source: "workspace", workspaceId: "exact-workspace", sessionId: item.chatId }]);
    assert.deepEqual(f.refreshed.map(value => value.type).sort(), ["view", "workspaceCatalog"]);
    assert.equal(f.requests("importLegacyChat").length, 1, "all recovery after uncertainty is read-only");
    console.log("PASS legacy recovery: lost chat acknowledgement uses exact read-only receipts, hidden reads cancel and unknown never replays");
  }

  {
    const f = fixture(); await load(f);
    f.recovery("CheckChat").trigger("click");
    assert.equal(f.recovery("ImportChat").disabled, true, "receipt read and import cannot race for the same source");
    f.reply("getLegacyChatImport", chatReceipt(chat(), { status: "alreadyImported", targetDeleted: true })); await settle();
    assert.match(f.recovery("ImportStatus").textContent, /перенесён ранее, затем удалён/);
    assert.equal(f.recovery("ImportChat").disabled, true);
    assert.deepEqual(f.found, [], "a deleted imported chat is never reopened or retried");
    assert.deepEqual(f.refreshed, [{ type: "officeCatalog" }]);
    assert.equal(f.requests("importLegacyChat").length, 0);
    console.log("PASS legacy recovery: durable receipt of a deleted chat does not recreate it or revive its view");
  }

  {
    const f = fixture(), item = Object.assign(plugin(), { canConnect: false, status: "missingSource", error: "Offline source" });
    const oldCatalog = f.context.RNAssistantPlugins.refresh(), stale = f.pending("getPlugins");
    await load(f, page([chat()]), page([item]));
    assert.equal(pluginButton(f, 0, 0).disabled, false, "offline source does not block saved registration import");
    assert.equal(pluginButton(f, 0, 2).disabled, true);
    const importing = pluginButton(f, 0, 0); importing.trigger("click"); importing.trigger("click");
    assert.deepEqual(f.pending("importLegacyPlugin").payload, { pluginId: item.pluginId, expectedRegistrationRevision: item.registrationRevision });
    assert.equal(f.requests("importLegacyPlugin").length, 1);
    f.setVisible(false); f.reply("importLegacyPlugin", pluginReceipt(item)); await settle();
    assert.deepEqual(f.refreshed, [{ type: "browser", pluginId: item.pluginId }, { type: "workspaceCatalog" }]);
    assert.equal(f.requests("getPlugins").length, 1, "fresh plugin read follows the older catalog");
    stale.resolve({ items: [] }); await oldCatalog; await settle();
    f.reply("getPlugins", { items: [pluginReceipt(item).item] }); await settle();
    assert.equal(f.context.RNAssistantPlugins.item(item.pluginId).enabled, true);
    f.setVisible(true);
    const row = f.recovery("PluginList").children[0];
    assert.match(row.children[2].textContent, /Offline source.*Регистрация и сохранённые версии восстановлены/);
    assert.equal(pluginButton(f, 0, 0).disabled, true); assert.equal(pluginButton(f, 0, 2).disabled, true);
    assert.equal(f.requests("reconnectLegacyPlugin").length, 0, "registration import never falls back to source reconnect");
    assert.equal(f.requests("cancelRequest").length, 0, "accepted mutation survives hide");
    console.log("PASS legacy recovery: offline plugin import preserves native registration proof, refreshes fresh catalogs and invalidates exact Browser targets");
  }

  {
    const f = fixture(); await load(f);
    pluginButton(f, 0, 0).trigger("click");
    f.pending("importLegacyPlugin").reject(Object.assign(new Error("Missing saved payload"), { code: "legacy_plugin_import_payload_unavailable" })); await settle();
    assert.match(f.recovery("PluginList").children[0].children[2].textContent, /Перенос отклонён.*Missing saved payload/);
    assert.equal(f.requests("importLegacyPlugin").length, 1, "typed failure never retries itself");
    pluginButton(f, 0, 0).trigger("click");
    f.pending("importLegacyPlugin").reject(Object.assign(new Error("Unknown durable result"), { code: "legacy_plugin_import_unknown" })); await settle();
    assert.equal(pluginButton(f, 0, 0).disabled, true); assert.equal(pluginButton(f, 0, 2).disabled, true);
    pluginButton(f, 0, 1).trigger("click");
    assert.deepEqual(f.pending("getLegacyPluginImport").payload, { pluginId: "legacy-plugin", expectedRegistrationRevision: "registration-42" });
    f.reply("getLegacyPluginImport", pluginReceipt(plugin(), { registrationRevision: "another-revision" })); await settle();
    assert.deepEqual(f.refreshed, []);
    pluginButton(f, 0, 1).trigger("click"); f.reply("getLegacyPluginImport", pluginReceipt(plugin(), { alreadyImported: true })); await settle();
    f.reply("getPlugins", { items: [pluginReceipt().item] }); await settle();
    assert.equal(f.requests("importLegacyPlugin").length, 2);
    assert.equal(f.requests("reconnectLegacyPlugin").length, 0);
    assert.equal(f.refreshed.filter(value => value.type === "browser").length, 1);
    console.log("PASS legacy recovery: typed plugin refusal remains separate from unknown receipt recovery and never silently reconnects");
  }

  {
    const f = fixture(), items = Array.from({ length: 30 }, (_, index) => Object.assign(chat("source-" + index, index + 1), { chatId: "chat-" + index }));
    await load(f, page(items, 30)); f.recovery("ImportPage").trigger("click"); f.recovery("ImportPage").trigger("click");
    for (let index = 0; index < 25; index++) {
      assert.equal(f.requests("importLegacyChat").length, index + 1, "batch sends one mutation at a time");
      assert.equal(f.pending("importLegacyChat").payload.sourceId, items[index].sourceId);
      f.reply("importLegacyChat", chatReceipt(items[index])); await settle();
    }
    assert.equal(f.requests("importLegacyChat").length, 25, "one user action imports at most 25 captured rows");
    assert.equal(f.requests("listLegacyChats").length, 1, "batch never scans another archive page");
    assert.match(f.recovery("BatchStatus").textContent, /25\/25; подтверждено: 25/);
    assert.equal(f.context.state.activeChatId, "unchanged-active-chat");
    console.log("PASS legacy recovery: page import is sequential, captured, bounded to 25 and does not scan or select another chat");
  }

  for (const stop of ["hide", "stop", "unknown"]) {
    const f = fixture(), items = [chat("one"), Object.assign(chat("two"), { chatId: "second-chat" })];
    await load(f, page(items)); f.recovery("ImportPage").trigger("click");
    if (stop === "hide") f.setVisible(false);
    if (stop === "stop") f.recovery("StopImport").trigger("click");
    if (stop === "unknown") f.pending("importLegacyChat").reject(new Error("Lost acknowledgement"));
    else f.reply("importLegacyChat", chatReceipt(items[0]));
    await settle(); f.setVisible(true); await settle();
    assert.equal(f.requests("importLegacyChat").length, 1, stop + " prevents the next batch mutation");
    assert.equal(f.requests("cancelRequest").length, 0, "a started import is never cancelled as a read");
    assert.match(f.recovery("BatchStatus").textContent, /1\/2.*не начато: 1/);
    if (stop === "hide") assert.equal(f.refreshed.filter(value => value.type === "officeCatalog").length, 1,
      "an accepted hidden mutation still refreshes the main list");
    console.log("PASS legacy recovery: " + stop + " stops the page batch between commands without replaying or cancelling its accepted mutation");
  }
  {
    const f = fixture(), items = [chat("unsupported"), Object.assign(chat("valid"), { chatId: "valid-chat" })];
    await load(f, page(items)); f.recovery("ImportPage").trigger("click");
    f.pending("importLegacyChat").reject(Object.assign(new Error("Unsupported historical payload " + attack), { code: "legacy_chat_import_unavailable" })); await settle();
    assert.match(f.recovery("ImportStatus").textContent, /Перенос отклонён.*Unsupported historical payload/);
    assert.equal(f.pending("importLegacyChat").payload.sourceId, "valid", "explicit batch may continue after a typed pre-write refusal");
    f.context.synchronizeChatState = async () => { throw new Error("Catalog unavailable"); };
    f.reply("importLegacyChat", chatReceipt(items[1])); await settle();
    assert.equal(f.requests("importLegacyChat").length, 2, "neither a refused import nor a refresh failure is automatically replayed");
    assert.match(f.recovery("BatchStatus").textContent, /подтверждено: 1; отказов: 1; неизвестно: 0/);
    f.recovery("ChatSelect").value = "valid"; f.recovery("ChatSelect").trigger("change");
    assert.match(f.recovery("ImportStatus").textContent, /Чат перенесён в историю.*список не обновлён: Catalog unavailable/);
    assert.equal(f.recovery("ImportChat").disabled, true, "read-side refresh failure cannot discard durable import proof");
    console.log("PASS legacy recovery: typed unsupported chat refusal and catalog refresh failure remain distinct from durable import success");
  }
  console.log("OK legacy recovery state");
})().catch(error => { console.error(error); process.exitCode = 1; });
