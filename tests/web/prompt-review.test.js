"use strict";
// Real prompt/settings actions + shared transfers, without WebView/Office QA.
const assert = require("node:assert/strict"), fs = require("node:fs"), path = require("node:path"), vm = require("node:vm"), crypto = require("node:crypto");
const read = file => fs.readFileSync(path.join(__dirname, "../../web", file), "utf8");
const keys = ["systemPrompt", "agentToolsPrompt", "agentSkillsPrompt", "chatSystemPrompt", "planSystemPrompt", "contextCompactionPrompt", "chatTitlePrompt", "attachmentAnalysisPrompt"];
const fields = keys.map(key => key[0].toUpperCase() + key.slice(1)), copy = value => JSON.parse(JSON.stringify(value));
const ids = new Set(Array.from(read("index.html").matchAll(/\bid="([^"]+)"/g), match => match[1]));
const sha = bytes => crypto.createHash("sha256").update(bytes).digest("hex");
function metadata(revision = "r1") {
  return { type: "rnassistant.promptLibrary", contractVersion: 1, publication: { uri: "rna://catalog/prompts", revision },
    items: keys.map(key => ({ key, resource: { uri: "rna://catalog/prompts/" + key, revision } })) };
}
class Element {
  constructor() { this.value = ""; this.checked = false; this.style = {}; this.handlers = {}; this.childNodes = []; this.classList = { toggle() {}, add() {}, remove() {} }; }
  addEventListener(name, handler) { this.handlers[name] = handler; }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  closest() { return this; }
  setAttribute() {} removeAttribute() {}
}
function fixture(loadPrompts = true) {
  const elements = new Map(), calls = [], errors = [], downloads = new Map(), uploads = new Map(), trees = []; let next = 1, revision = 1;
  const get = id => { if (!ids.has(id)) return null; if (!elements.has(id)) elements.set(id, new Element()); return elements.get(id); };
  const bodies = Object.fromEntries(keys.map(key => [key, " custom " + key + " "]));
  const context = vm.createContext({ AbortController, TextEncoder, TextDecoder, Uint8Array, Blob, Response, setTimeout, clearTimeout, crypto: crypto.webcrypto,
    state: { settings: { AgentPromptSchemaVersion: 0 }, prompts: metadata(), activeChatId: "chat", promptDrafts: {}, selectedInstructionKind: "prompt", selectedPromptIndex: 0, skills: [], tools: [] },
    $: get, document: { querySelectorAll: () => [], querySelector: () => null, addEventListener() {},
      documentElement: { getAttribute: () => "light", setAttribute() {}, style: { setProperty() {} } } },
    isPanelActive: () => !!context.libraryVisible, markdown: text => text,
    RNAssistantNavigationTree: { icon: name => `<svg data-icon="${name}"></svg>`, render: (root, nodes) => { trees.push(nodes); root.treeNodes = nodes; } },
    modelImageSupportOverrides: () => ({}), modelAudioSupportOverrides: () => ({}), modelCapabilitiesForSettings: () => ({}), attachmentModelPriorityForSettings: () => [], textToHeaders: () => ({}),
    renderContextMeter() {}, clearRuntimeData() {}, setControlBusy: (id, busy) => { get(id).disabled = busy; },
    log: (message, level) => { if (level === "error") errors.push(message); }, confirm: () => true, cancelBridgeRequest: async () => {},
    fetch: async (url, options) => {
      const uri = new URL(url), id = uri.pathname.split("/").pop(), offset = Number(uri.searchParams.get("offset")), count = Number(uri.searchParams.get("count"));
      if (options.method === "POST") { uploads.get(id).set(new Uint8Array(options.body), offset); return new Response(JSON.stringify({ leaseId: id, nextOffset: offset + count })); }
      return new Response(downloads.get(id).slice(offset, offset + count), { headers: { "Content-Type": "text/markdown; charset=utf-8" } });
    },
    send: async (type, payload) => {
      calls.push({ type, payload: copy(payload) });
      if (type === "readPromptSource") {
        if (context.holdRead) await context.holdRead();
        const text = bodies[payload.resource.uri.split("/").pop()], bytes = new TextEncoder().encode(text), id = String(next++).padStart(64, "0"); downloads.set(id, bytes);
        return { type: "rnassistant.promptSource", contractVersion: 1, chatId: payload.chatId, resource: payload.resource, totalCharacters: text.length + (context.badSource ? 1 : 0),
          data: { leaseId: id, url: "https://rnassistant.local-resource/v1/download/" + id, maxChunkBytes: 4096, payload: { sha256: sha(bytes), byteLength: bytes.length, contentType: "text/markdown; charset=utf-8" } } };
      }
      if (type === "resourceDataClose") { downloads.delete(payload.leaseId); return { closed: true }; }
      if (type === "beginPromptMutationUpload") {
        if (context.holdUpload) await context.holdUpload();
        const id = String(next++).padStart(64, "0"); uploads.set(id, new Uint8Array(payload.byteLength));
        return { leaseId: id, url: "https://rnassistant.local-resource/v1/upload/" + id, byteLength: payload.byteLength, maxChunkBytes: 4096 };
      }
      if (type === "cancelPromptMutationUpload") { uploads.delete(payload.leaseId); return { closed: true }; }
      if (type === "getSettings") { if (context.holdSettings) await context.holdSettings(); return { settings: { AgentPromptSchemaVersion: 0 }, prompts: metadata("r" + revision) }; }
      assert.equal(type, "saveSettings"); if (context.failSave) throw new Error("fixture save failure");
      fields.forEach(field => assert.equal(payload.settings[field], undefined));
      if (payload.uploadLeaseId) {
        const bytes = uploads.get(payload.uploadLeaseId); assert.equal(sha(bytes), payload.sha256); context.uploadedBody = JSON.parse(new TextDecoder().decode(bytes));
        context.uploadedBody.changes.forEach(change => { bodies[change.resource.uri.split("/").pop()] = change.value || "default reset"; }); revision++;
      }
      if (context.beforeSaveResponse) context.beforeSaveResponse();
      const saved = copy(payload.settings);
      return { settings: saved, prompts: metadata("r" + revision) };
    }
  });
  context.window = context;
  vm.runInContext(read("js/app-accent.js"), context, { filename: "app-accent.js" });
  (loadPrompts ? ["app-settings.js", "app-resource-download.js", "app-resource-upload.js", "app-prompts.js"] : ["app-settings.js"])
    .forEach(file => vm.runInContext(read("js/" + file), context, { filename: file }));
  context.renderSettings = () => { if (loadPrompts) context.renderPromptSettings(context.state.prompts); };
  if (loadPrompts) { context.renderSettings(); context.bindSettingsActions(); }
  return { context, get, calls, errors, bodies, downloads, uploads, trees, load: async index => { context.state.selectedPromptIndex = index; return context.renderPromptEditor(); }, saves: () => calls.filter(call => call.type === "saveSettings") };
}
(async () => {
  {
    const f = fixture();
    const extended = metadata();
    extended.items.push({ key: "systemPromptRole", resource: { uri: "rna://catalog/prompts/systemPromptRole", revision: "r1" } });
    assert.doesNotThrow(() => f.context.renderPromptSettings(extended));
    extended.items[8].resource.uri = "rna://catalog/prompts/systemPrompt";
    f.context.renderPromptSettings(extended);
    assert.match(f.errors.pop(), /metadata-only contract/);
    console.log("PASS prompt library tolerates only well-formed extra metadata");
  }
  {
    const f = fixture(); assert.equal(f.calls.length, 0); await f.context.persistSettingsFromForm();
    assert.equal(f.saves()[0].payload.uploadLeaseId, null); assert.equal(f.saves()[0].payload.reviewAgentPrompts, undefined); assert.equal(f.saves()[0].payload.settings.AgentPromptSchemaVersion, 0);
    console.log("PASS settings metadata does not prefetch or clear unloaded prompts");
  }
  {
    const f = fixture(false); keys.forEach(key => { f.context.state.settings[key] = "retired inline text"; });
    fields.forEach(field => assert.equal(f.context.readSettings()[field], undefined)); await assert.rejects(f.context.persistSettingsFromForm(), /saveSettingsWithPromptChanges/); assert.equal(f.calls.length, 0);
    console.log("PASS missing transport has no inline settings read/save fallback");
  }
  {
    const f = fixture(); f.bodies.systemPrompt = "\ufeff# Exact\r\n" + "ж".repeat(40000) + "😀"; await f.load(0);
    assert.equal(f.get("promptEditInput").value, f.bodies.systemPrompt); assert.equal(f.downloads.size, 0);
    f.get("promptEditInput").value += " changed"; f.context.markPromptEditorDirty(); await f.load(1); await f.load(2);
    assert.deepEqual(Object.keys(f.context.state.promptDrafts).sort(), ["agentSkillsPrompt", "systemPrompt"]);
    await f.context.persistSettingsFromForm(); assert.equal(f.context.uploadedBody.changes.length, 1); assert.ok(f.context.uploadedBody.changes[0].value.endsWith(" changed")); assert.equal(f.uploads.size, 0);
    console.log("PASS exact Unicode source, one clean cache and changed-only upload");
  }
  {
    const f = fixture(); f.bodies.systemPrompt = ""; await f.load(0);
    assert.equal(f.get("promptEditInput").readOnly, false);
    assert.equal(f.get("copyPromptButton").disabled, false);
    await f.context.persistSettingsFromForm();
    assert.equal(f.saves()[0].payload.uploadLeaseId, null);
    assert.equal(f.saves()[0].payload.reviewAgentPrompts, undefined);
    console.log("PASS empty source and ordinary metadata-only save");
  }
  {
    const f = fixture(); await f.load(0); f.get("promptEditInput").value = "unsaved";
    f.context.markPromptEditorDirty(); f.context.failSave = true;
    await f.get("savePromptButton").handlers.click();
    assert.equal(f.context.state.promptDrafts.systemPrompt, "unsaved");
    assert.equal(f.uploads.size, 0);
    assert.deepEqual(f.errors, ["fixture save failure"]);
    console.log("PASS failed save retains drafts and closes upload");
  }
  {
    const f = fixture(); await f.load(0); f.get("resetCurrentPromptButton").handlers.click();
    assert.equal(f.calls.filter(call => call.type === "saveSettings").length, 0);
    await f.context.persistSettingsFromForm();
    assert.equal(f.context.uploadedBody.changes.length, 1);
    assert.equal(f.context.uploadedBody.changes[0].value, "");
    console.log("PASS returning one prompt to built-in is an explicit draft");
  }
  {
    const f = fixture(); let release; f.context.holdRead = () => new Promise(resolve => { release = resolve; }); const pending = f.load(0);
    f.context.state.activeChatId = "other"; f.context.releasePromptEditorContext(); release(); await pending; assert.deepEqual(Object.keys(f.context.state.promptDrafts), []); assert.equal(f.downloads.size, 0);
    delete f.context.holdRead; f.context.badSource = true; await f.load(0); assert.equal(f.get("promptEditInput").readOnly, true); assert.equal(f.downloads.size, 0);
    console.log("PASS late chat reads and malformed source cannot hydrate editor");
  }
  {
    const f = fixture(); await f.load(0); f.get("promptEditInput").value = "keep my draft"; f.context.markPromptEditorDirty(); f.context.state.prompts = metadata("newer"); f.context.renderSettings();
    await assert.rejects(f.context.persistSettingsFromForm(), /устарел/); assert.equal(f.context.state.promptDrafts.systemPrompt, "keep my draft"); assert.equal(f.saves().length, 0);
    f.context.state.prompts = metadata(); f.context.renderSettings(); f.context.beforeSaveResponse = () => { f.get("promptEditInput").value = "typed during save"; f.context.markPromptEditorDirty(); };
    await f.context.persistSettingsFromForm(); assert.equal(f.context.state.promptDrafts.systemPrompt, "typed during save");
    console.log("PASS stale and concurrently edited drafts survive save");
  }
  {
    const f = fixture(); f.get("resetCurrentPromptButton").handlers.click(); let release; f.context.holdUpload = () => new Promise(resolve => { release = resolve; }); const pending = f.context.persistSettingsFromForm();
    while (!release) await new Promise(resolve => setImmediate(resolve)); f.context.state.activeChatId = "other"; f.context.releasePromptEditorContext(); release();
    await assert.rejects(pending, /отменено/); assert.equal(f.uploads.size, 0); assert.equal(f.saves().length, 0); assert.equal(f.context.state.promptDrafts.systemPrompt, "");
    console.log("PASS cancelled upload closes late lease without save dispatch");
  }
  {
    const f = fixture(); f.context.state.skills = [{ Id: "excel.audit", Host: "Excel", BuiltIn: false, Enabled: true }];
    f.context.state.tools = [{ Id: "excel.validate", Host: "Excel", BuiltIn: true, Enabled: true }];
    f.context.libraryVisible = true; f.context.renderInstructions();
    const groups = f.trees.at(-1);
    assert.deepEqual(Array.from(groups, group => group.title), ["Навыки", "Инструменты", "Инструкции"]);
    assert.match(groups[0].children[0].iconHtml, /skill/);
    assert.match(groups[1].children[0].iconHtml, /tool/);
    assert.equal(groups[0].children[0].title, "excel.audit");
    assert.equal(groups[1].children[0].title, "excel.validate");
    console.log("PASS library uses the shared navigation tree for all three sections");
  }
  {
    ["app-prompts.js", "app-settings.js", "app-navigation-tree.js", "app-tools.css"].forEach(file =>
      assert.match(read("index.html"), new RegExp(file.replace(/\./g, "\\.") + "\\?v=[^\"'\\s]+")));
    assert.ok(read("js/app-prompts.js").includes("fetch: window.fetch.bind(window)"));
    assert.ok(!read("js/app-settings.js").includes("readPromptSettings")); console.log("PASS direct-cutover delivery keys and retired form reader removal");
  }
  console.log("OK passed=11 failed=0 total=11");
})().catch(error => { console.error(error); process.exitCode = 1; });
