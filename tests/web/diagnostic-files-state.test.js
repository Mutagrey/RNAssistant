"use strict";
// Host-neutral async/lifecycle checks; the companion browser fixture owns layout evidence.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

class Node {
  constructor(tag = "div") {
    this.tagName = tag; this.children = []; this.events = {}; this.dataset = {}; this.attributes = {};
    this.value = ""; this.disabled = false; this.scrollTop = 0; this.scrollHeight = 600; this.clientHeight = 300;
    this.classes = new Set();
    this.classList = { toggle: (name, enabled) => enabled ? this.classes.add(name) : this.classes.delete(name) };
  }
  set textContent(value) { this.children = []; this.content = String(value); }
  get textContent() { return (this.content || "") + this.children.map(node => node.textContent).join(""); }
  appendChild(node) { this.children.push(node); return node; }
  replaceChildren() { this.content = ""; this.children = []; }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener(type, action) { this.events[type] = action; }
  getBoundingClientRect() { return { top: 0 }; }
  trigger(type, values = {}) { return this.events[type] && this.events[type].call(this, Object.assign({ preventDefault() {} }, values)); }
}

(async () => {
  const ids = ["diagnosticsAppView", "fileLogPane", "fileLogSelect", "fileLogRefresh", "fileLogLive", "fileLogEarlier", "fileLogLatest",
    "fileLogActions", "fileLogSaveOptions", "fileLogPrune", "fileLogMaxFile", "fileLogMaxTotal", "fileLogDays", "fileLogMaxFiles",
    "fileLogText", "fileLogStatus", "fileLogSearch", "fileLogFindNext", "fileLogMatches", "fileLogReadRegion", "fileLogNotice", "fileLogIssues",
    "fileLogUsage", "fileLogOptionsForm", "fileLogRetention", "panelLogPane"];
  const nodes = Object.fromEntries(ids.map(id => [id, new Node()]));
  nodes.diagnosticsAppView.value = "log";
  const sources = ["files", "panel"].map(mode => { const node = new Node("button"); node.dataset.logSource = mode; return node; });
  const timers = new Map(), calls = []; let timerId = 0, shown = true, dialog = null, menu = null, holdId = null, held = null, reset = false, partial = false;
  const inventory = { files: [
    { id: "rnassistant.log", name: "rnassistant.log", source: "runtime", byteLength: 90000, lastWriteUtc: "2026-11-16T10:00:00Z", isActive: true },
    { id: "20261116.log", name: "20261116.log", source: "desktop", byteLength: 120, lastWriteUtc: "2026-11-16T10:01:00Z", isActive: true }
  ], options: { maxFileMiB: 5, maxTotalMiB: 50, retentionDays: 7, maxFiles: 20 }, totalBytes: 90120, overLimit: false, issues: [] };
  const makePage = (id, older) => ({ fileId: id, text: older ? "Earlier entry\n  stack frame\n" : id === "20261116.log" ? "Desktop entry\n" :
    "Runtime entry\n<img src=x onerror=fail()>\nException\n  stack frame\n", snapshotToken: "snapshot-1", startOffset: older ? 0 : 24500,
    endOffset: older ? 24500 : 90000, snapshotLength: 90000, hasEarlier: !older, resetRequired: false, partialFirstLine: !older });
  const context = { state: { diagnosticsTab: "application" }, $: id => nodes[id], formatStorageBytes: bytes => bytes + " B",
    document: { hidden: false, querySelectorAll: selector => selector === "[data-log-source]" ? sources : [], addEventListener() {},
      createElement: tag => new Node(tag), createTextNode: value => { const node = new Node("#text"); node.textContent = value; return node; } },
    RNAssistantDiagnostics: { isVisible: () => shown }, RNAssistantDialog: { open: options => { dialog = options; } },
    RNAssistantActionMenu: { open: (trigger, items) => { menu = items; } }, copyTextResult: () => Promise.resolve(),
    requestAnimationFrame: callback => callback(), addEventListener() {},
    setTimeout: callback => { timers.set(++timerId, callback); return timerId; }, clearTimeout: id => timers.delete(id),
    cancelBridgeRequest: id => { calls.push({ type: "cancelRequest", id }); return Promise.resolve(); },
    send: (type, payload) => {
      calls.push({ type, payload });
      if (type === "listDiagnosticLogs") return Promise.resolve(JSON.parse(JSON.stringify(inventory)));
      if (type === "readDiagnosticLog") {
        const result = makePage(payload.fileId, payload.beforeOffset != null);
        if (reset && payload.beforeOffset != null) { result.resetRequired = true; reset = false; }
        if (holdId === payload.fileId) {
          holdId = null; const promise = new Promise(resolve => { held = () => resolve(result); }); promise.requestId = "held-log"; return promise;
        }
        return Promise.resolve(result);
      }
      if (type === "saveDiagnosticLogOptions") inventory.options = payload;
      return Promise.resolve({ affectedFiles: 1, reclaimedBytes: 100,
        issues: partial ? [{ fileId: "rnassistant.log", operation: "clear", message: "Locked file" }] : [], inventory });
    }
  };
  context.window = context;
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(path.resolve(__dirname, "../../web/js/app-logs.js"), "utf8"), context);
  const ui = context.RNAssistantFileLogs;
  const settle = async () => { for (let index = 0; index < 15; index++) await Promise.resolve(); };
  ui.bind(); ui.sync(); await settle();
  assert.equal(nodes.fileLogSelect.value, "rnassistant.log");
  assert.match(nodes.fileLogText.textContent, /<img src=x/);
  assert.equal(nodes.fileLogText.children.length, 0, "plain text source creates no HTML nodes");
  assert.equal(calls.find(call => call.type === "readDiagnosticLog").payload.maxBytes, 65536);
  assert.equal(timers.size, 1, "one live timer after first page");

  nodes.fileLogSearch.value = "Exception"; nodes.fileLogSearch.trigger("input");
  assert.equal(nodes.fileLogText.children.filter(node => node.tagName === "mark").length, 1);
  assert.equal(nodes.fileLogLive.attributes["aria-pressed"], "false"); assert.equal(timers.size, 0);
  nodes.fileLogSearch.value = ""; nodes.fileLogSearch.trigger("input");
  await nodes.fileLogEarlier.trigger("click"); await settle();
  assert.match(nodes.fileLogText.textContent, /^Earlier entry/);
  assert.equal(nodes.fileLogEarlier.disabled, true);
  assert.equal(calls.filter(call => call.type === "readDiagnosticLog").at(-1).payload.snapshotToken, "snapshot-1");
  shown = false; ui.sync(); inventory.files[0].byteLength++; shown = true; ui.sync(); await settle();
  assert.match(nodes.fileLogText.textContent, /^Earlier entry/, "paused page survives hidden append");

  nodes.fileLogLatest.trigger("click"); await settle(); reset = true;
  await nodes.fileLogEarlier.trigger("click"); await settle();
  assert.match(nodes.fileLogNotice.textContent, /ротации/);
  assert.match(nodes.fileLogText.textContent, /^Runtime entry/);
  assert.equal(nodes.fileLogLive.attributes["aria-pressed"], "false");

  holdId = "rnassistant.log"; nodes.fileLogRefresh.trigger("click"); await settle();
  assert.ok(held); nodes.fileLogSelect.value = "20261116.log"; nodes.fileLogSelect.trigger("change"); await settle();
  held(); held = null; await settle();
  assert.match(nodes.fileLogText.textContent, /^Desktop entry/, "late source cannot replace current page");
  assert.ok(calls.some(call => call.type === "cancelRequest" && call.id === "held-log"));

  // Turning follow off must also invalidate a response already on its way.
  nodes.fileLogLatest.trigger("click"); await settle(); holdId = "20261116.log";
  nodes.fileLogRefresh.trigger("click"); await settle();
  nodes.fileLogLive.trigger("click"); held(); held = null; await settle();
  assert.equal(nodes.fileLogLive.attributes["aria-pressed"], "false"); assert.equal(timers.size, 0);

  partial = true; nodes.fileLogActions.trigger("click");
  menu.find(item => item.label === "Очистить все диагностические логи").onSelect();
  assert.match(dialog.message, /История чатов и документы сохранятся/);
  await dialog.onSubmit(); await settle();
  assert.match(nodes.fileLogNotice.textContent, /частично/); assert.match(nodes.fileLogNotice.textContent, /Locked file/);
  nodes.fileLogMaxTotal.value = 3; dialog = null; nodes.fileLogOptionsForm.trigger("submit");
  assert.equal(dialog, null); assert.match(nodes.fileLogNotice.textContent, /не меньше двух файлов/);
  nodes.fileLogMaxTotal.value = 60; nodes.fileLogOptionsForm.trigger("submit");
  await dialog.onSubmit(); await settle();
  assert.equal(calls.find(call => call.type === "saveDiagnosticLogOptions").payload.maxTotalMiB, 60);

  shown = false; ui.sync(); const hiddenCalls = calls.length;
  assert.equal(timers.size, 0); await settle(); assert.equal(calls.length, hiddenCalls);
  ui.dispose(); assert.equal(nodes.fileLogText.textContent, "");
  shown = true; ui.sync(); await settle(); assert.match(nodes.fileLogText.textContent, /^Desktop entry/);
  sources[1].trigger("click"); assert.equal(timers.size, 0); assert.ok(!nodes.panelLogPane.classes.has("hidden"));
  console.log("PASS diagnostic file state: bounded text, paging, reset, paused view, stale/cancelled reads, partial mutation, option validation and cleanup");
})().catch(error => { console.error(error); process.exitCode = 1; });
