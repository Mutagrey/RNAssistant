"use strict";

// Exercise real form serialization, prompt actions and save dispatch. Rendering
// helpers/transport are stubbed; this is not WebView layout or controller QA.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

class Element {
  constructor() {
    this.value = "";
    this.checked = false;
    this.style = {};
    this.handlers = {};
    this.childNodes = [];
    this.classList = { toggle() {}, add() {}, remove() {} };
  }
  addEventListener(name, handler) { this.handlers[name] = handler; }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  setAttribute() {}
  removeAttribute() {}
}

const promptFields = ["SystemPrompt", "AgentToolsPrompt", "AgentSkillsPrompt", "ChatSystemPrompt", "PlanSystemPrompt",
  "ContextCompactionPrompt", "ChatTitlePrompt", "AttachmentAnalysisPrompt"];
const elementIds = new Set(Array.from(fs.readFileSync(path.join(__dirname, "../../web/index.html"), "utf8")
  .matchAll(/\bid="([^"]+)"/g), match => match[1]));
const reviewedSchemaVersion = 37; // A server-supplied value, not a UI version constant.

function fixture(loadPrompts = true) {
  const elements = new Map();
  const get = id => {
    if (!elementIds.has(id)) return null;
    if (!elements.has(id)) elements.set(id, new Element());
    return elements.get(id);
  };
  const settings = { AgentPromptSchemaVersion: 0 };
  for (const field of promptFields) settings[field] = " custom " + field + " ";
  const calls = [];
  const errors = [];
  const context = vm.createContext({
    state: { settings, promptDrafts: {}, selectedInstructionKind: "prompt", selectedPromptIndex: 0, skills: [], tools: [] },
    $: get,
    document: { querySelectorAll: () => [], querySelector: () => null, addEventListener() {} },
    markdown: text => text,
    createResourceGroup: () => { const group = new Element(); group.treeChildren = new Element(); return group; },
    createResourceListItem: () => new Element(),
    modelImageSupportOverrides: () => ({}),
    modelAudioSupportOverrides: () => ({}),
    modelCapabilitiesForSettings: () => ({}),
    attachmentModelPriorityForSettings: () => [],
    textToHeaders: () => ({}),
    updateEstimatedContextUsage() {},
    renderContextMeter() {},
    clearRuntimeData() {},
    setControlBusy: (id, busy) => { get(id).disabled = busy; },
    log: (message, level) => { if (level === "error") errors.push(message); },
    confirm: () => true,
    send: async (type, payload) => {
      calls.push({ type, payload: JSON.parse(JSON.stringify(payload)) });
      if (context.failSave) throw new Error("fixture save failure");
      const saved = JSON.parse(JSON.stringify(payload.settings));
      if (payload.reviewAgentPrompts) saved.AgentPromptSchemaVersion = reviewedSchemaVersion;
      return { settings: saved };
    }
  });
  context.window = context;
  for (const file of loadPrompts ? ["app-settings.js", "app-prompts.js"] : ["app-settings.js"]) {
    vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), context, { filename: file });
  }
  // Keep real prompt drafts/editor synchronization; unrelated settings rendering
  // is outside this test's contract.
  context.renderSettings = () => { if (loadPrompts) context.renderPromptSettings(context.state.settings); };
  if (loadPrompts) {
    context.renderPromptSettings(settings);
    context.bindSettingsActions();
  }
  return { context, get, calls, errors };
}

(async () => {
  {
    const { context, calls } = fixture();
    await context.persistSettingsFromForm();
    assert.equal(calls[0].payload.reviewAgentPrompts, false, "ordinary save never approves prompts");
    assert.equal(calls[0].payload.settings.AgentPromptSchemaVersion, 0, "missing/legacy marker stays unreviewed");
    for (const field of promptFields) assert.equal(calls[0].payload.settings[field], " custom " + field + " ");
    console.log("PASS ordinary save preserves all prompt fields, including Plan, and the old marker");
  }
  {
    const { context } = fixture(false);
    const camel = {};
    for (const field of promptFields) camel[field[0].toLowerCase() + field.slice(1)] = "kept " + field;
    context.state.settings = camel;
    const payload = context.readSettings();
    assert.equal(payload.AgentPromptSchemaVersion, 0, "missing marker is not invented by the UI");
    for (const field of promptFields) assert.equal(payload[field], "kept " + field, "missing editor does not clear saved prompts");
    console.log("PASS unavailable prompt editor preserves saved text in either property casing");
  }
  {
    const { context, get, calls } = fixture();
    const before = JSON.stringify(context.state.promptDrafts);
    context.confirm = () => false;
    await get("reviewAgentPromptsButton").handlers.click();
    assert.equal(calls.length, 0, "cancelled review never saves");
    assert.equal(JSON.stringify(context.state.promptDrafts), before);
    context.confirm = () => true;
    await get("reviewAgentPromptsButton").handlers.click();
    assert.equal(calls[0].payload.reviewAgentPrompts, true);
    assert.equal(calls[0].payload.settings.AgentPromptSchemaVersion, 0, "only the server applies the reviewed marker");
    assert.equal(context.state.settings.AgentPromptSchemaVersion, reviewedSchemaVersion, "UI accepts the server's reviewed marker");
    for (const field of promptFields) assert.equal(calls[0].payload.settings[field], " custom " + field + " ");
    await context.persistSettingsFromForm();
    assert.equal(calls[1].payload.reviewAgentPrompts, false, "review does not leak into the next save");
    console.log("PASS review requires confirmation and is request-local");
  }
  {
    const { context, get, calls, errors } = fixture();
    const before = JSON.stringify(context.state.promptDrafts);
    context.failSave = true;
    await get("reviewAgentPromptsButton").handlers.click();
    assert.equal(calls.length, 1);
    assert.equal(context.state.settings.AgentPromptSchemaVersion, 0);
    assert.equal(JSON.stringify(context.state.promptDrafts), before, "failed review preserves editable drafts");
    assert.equal(get("reviewAgentPromptsButton").disabled, false);
    assert.deepEqual(errors, ["fixture save failure"]);
    console.log("PASS failed review retains drafts and reports the save failure");
  }
  {
    const { get, calls } = fixture();
    get("resetAllPromptsButton").handlers.click();
    assert.equal(calls.length, 0, "reset remains a draft until explicit save");
    await get("reviewAgentPromptsButton").handlers.click();
    assert.equal(calls[0].payload.reviewAgentPrompts, true);
    for (const field of promptFields) assert.equal(calls[0].payload.settings[field], "", "explicit reset includes " + field);
    console.log("PASS reset-all plus explicit review submits cleared prompts, including Plan");
  }
  console.log("OK passed=5 failed=0 total=5");
})().catch(error => { console.error(error); process.exitCode = 1; });
