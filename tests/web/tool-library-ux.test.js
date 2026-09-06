"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "../..");

class Element {
  constructor(tagName = "div") {
    this.tagName = tagName.toUpperCase();
    this.type = "";
    this.value = "";
    this.checked = false;
    this.disabled = false;
    this.hidden = false;
    this.children = [];
    this.handlers = {};
    this.className = "";
    this.textContent = "";
    this.attributes = {};
    this.classList = { toggle() {}, add() {}, remove() {} };
  }
  appendChild(child) { this.children.push(child); child.parentNode = this; return child; }
  addEventListener(name, handler) { this.handlers[name] = handler; }
  setAttribute(name, value) { this.attributes[name] = value; }
  getAttribute(name) { return this.attributes[name] || null; }
  querySelectorAll() { return []; }
}

const elements = new Map();
function element(id, tag) {
  if (!elements.has(id)) elements.set(id, new Element(tag));
  return elements.get(id);
}
[
  ["toolSchemaInput", "textarea"], ["toolRunArgsInput", "textarea"],
  ["toolSchemaVisual", "div"], ["toolRunArgsVisual", "div"],
  ["toolSchemaError", "div"], ["toolRunArgsError", "div"],
  ["toolRunAdvancedJson", "details"],
  ["formatToolSchemaButton", "button"], ["applyToolRunJsonButton", "button"]
].forEach(entry => element(entry[0], entry[1]));

const state = { toolLibraryRendering: false };
const context = vm.createContext({
  window: null,
  state,
  $: id => elements.get(id) || null,
  document: {
    createElement: tag => new Element(tag),
    querySelectorAll: () => []
  },
  createResourceEmptyState: text => {
    const node = new Element(); node.textContent = text; return node;
  }
});
context.window = context;
vm.runInContext(fs.readFileSync(path.join(root,
  "web/js/app-tools-structured.js"), "utf8"), context,
{ filename: "app-tools-structured.js" });

const schema = {
  type: "object",
  properties: {
    enabled: { type: "boolean", description: "Enable the operation." },
    count: { type: "integer", minimum: 1, maximum: 10, default: 2, description: "Number of rows." },
    mode: { type: "string", enum: ["fast", "safe"], default: "safe", description: "Execution mode." },
    limit: { type: "integer", minimum: 1, maximum: 5, description: "Optional limit." },
    note: { type: ["string", "null"], maxLength: 500, description: "Optional multiline note that wraps." },
    tags: { type: "array", items: { type: "string" }, maxItems: 2, description: "Bounded tags." },
    options: {
      type: "object", description: "Bounded strict object.",
      properties: { name: { type: "string", maxLength: 20, description: "Name." } },
      required: ["name"], additionalProperties: false
    }
  },
  required: ["enabled", "count"],
  additionalProperties: false
};
element("toolSchemaInput").value = JSON.stringify(schema);
element("toolRunArgsInput").value = JSON.stringify({ enabled: true, count: 2, mode: "fast", note: null });
const editor = context.RNAssistantToolStructuredEditor.create({ state });
assert.equal(editor.syncSchemaDraft(), true);
editor.renderRunArguments();

function row(name) {
  return element("toolRunArgsVisual").children.find(item =>
    item.children[0] && item.children[0].children[0] &&
    item.children[0].children[0].textContent === name);
}
function valueControl(name) {
  const controls = row(name).children[1].children;
  return controls[controls.length - 1];
}

assert.equal(valueControl("enabled").type, "checkbox");
assert.equal(valueControl("count").type, "number");
assert.equal(valueControl("count").step, "1");
assert.equal(valueControl("count").min, "1");
assert.equal(valueControl("count").max, "10");
assert.equal(valueControl("mode").tagName, "SELECT");
const limitMode = row("limit").children[1].children[0];
const limitControl = valueControl("limit");
limitMode.value = "value";
limitMode.handlers.change();
assert.equal(limitControl.disabled, false, "optional numeric control enables before value entry");
assert.equal(valueControl("note").tagName, "TEXTAREA");
assert.equal(row("note").children[1].children[0].value, "null");
assert.equal(valueControl("tags").tagName, "TEXTAREA");
assert.equal(valueControl("tags").maxLength, 1000000);
assert.equal(row("tags").children[1].children[0].value, "omit");
assert.equal(row("note").children[2].textContent,
  "Optional multiline note that wraps.");
assert.deepEqual(JSON.parse(JSON.stringify(editor.readRunArguments())), {
  enabled: true, count: 2, mode: "fast", note: null
});

element("toolRunArgsInput").value = JSON.stringify({ enabled: true });
assert.throws(() => editor.readRunArguments(), /count/,
  "a schema default does not make a missing required property valid");
editor.renderRunArguments();
assert.deepEqual(JSON.parse(JSON.stringify(editor.readRunArguments())), {
  enabled: true, count: 2
}, "required defaults are materialized into the submitted object");

element("toolRunArgsInput").value = JSON.stringify({ enabled: true, count: 11 });
assert.throws(() => editor.readRunArguments(), /maximum/);
assert.match(element("toolRunArgsError").textContent, /maximum/);
element("toolRunArgsInput").value = JSON.stringify({ enabled: true, count: 2, uri: "rna://runtime" });
assert.throws(() => editor.readRunArguments(), /uri/);

const variantSchema = {
  type: "object",
  properties: {
    action: { type: "string" },
    name: { type: "string", minLength: 2 }
  },
  required: ["action"],
  additionalProperties: false,
  anyOf: [
    { properties: { action: { const: "list" } }, required: ["action"] },
    { properties: { action: { const: "get" } }, required: ["action", "name"] }
  ]
};
state.toolSchemaVisualDraft = variantSchema;
element("toolRunArgsInput").value = JSON.stringify({ action: "list", cursor: "opaque" });
assert.throws(() => editor.readRunArguments(), /cursor/,
  "root strictness still applies when anyOf selects a variant");
element("toolRunArgsInput").value = JSON.stringify({ action: "get", name: "" });
assert.throws(() => editor.readRunArguments(), /minLength/,
  "root property constraints still apply when anyOf selects a variant");

const referenceSchema = {
  type: "object",
  properties: {
    id: { type: "string", description: "Exact skill id." },
    referencePath: { type: "string", description: "Semantic reference path." },
    action: { type: "string", enum: ["read", "next"], default: "read", description: "Semantic continuation." }
  },
  required: ["id", "referencePath"], additionalProperties: false
};
state.toolSchemaVisualDraft = referenceSchema;
element("toolRunArgsInput").value = JSON.stringify({
  id: "common.sample", referencePath: "references/details.md", action: "read"
});
const nextArgs = editor.readNextArguments();
assert.deepEqual(JSON.parse(JSON.stringify(nextArgs)), {
  id: "common.sample", referencePath: "references/details.md", action: "next"
});
assert.equal(Object.prototype.hasOwnProperty.call(nextArgs, "cursor"), false);
console.log("PASS Tool Library form: typed controls, omit/null, bounds and semantic next");

(async function testActions() {
  const docElements = new Map([
    ["toolReadmeEditor", new Element()],
    ["toolBuiltInDocs", new Element()],
    ["toolDocumentationStatus", new Element()],
    ["toolDocumentationMarkdown", new Element()]
  ]);
  const docTool = {
    Id: "excel.inspect", Revision: "r".repeat(64), BuiltIn: true
  };
  const docState = {
    toolEditorPage: "main", tools: [docTool], selectedToolIndex: 0, selectedInstructionKind: "tool", activeChatId: "chat", host: "Excel"
  };
  const docCalls = [];
  const docContext = vm.createContext({
    window: null, AbortController, TextDecoder,
    fetch() {}, RNAssistantResourceDownload: { read: async () => new TextEncoder().encode("# exact docs") },
    $: id => docElements.get(id) || null
  });
  docContext.window = docContext;
  vm.runInContext(fs.readFileSync(path.join(root,
    "web/js/app-tools-documentation.js"), "utf8"), docContext,
  { filename: "app-tools-documentation.js" });
  const documentation = docContext.RNAssistantToolDocumentation.create({
    state: docState,
    log() {},
    cancelRequest: async () => {},
    async send(action, payload) {
      docCalls.push({ action, payload });
      if (action === "resourceDataClose") return { closed: true };
      return {
        type: "rnassistant.toolLibraryDocumentation", contractVersion: 1,
        chatId: "chat", toolId: docTool.Id, revision: docTool.Revision,
        resource: { uri: "rna://catalog/builtin-tools-excel/excel.inspect/documentation", revision: "exact" },
        data: { leaseId: "a".repeat(64), payload: { contentType: "text/markdown; charset=utf-8" } }
      };
    }
  });
  documentation.prepare(docTool);
  await documentation.ensure();
  assert.equal(docCalls.length, 0, "documentation is not fetched before its tab opens");
  docState.toolEditorPage = "docs";
  await documentation.ensure();
  assert.equal(docCalls.length, 2);
  assert.equal(docCalls[0].action, "getToolDocumentation");
  assert.equal(docCalls[0].payload.toolId, docTool.Id);
  assert.equal(docCalls[0].payload.expectedRevision, docTool.Revision);
  assert.equal(docElements.get("toolDocumentationMarkdown").textContent, "# exact docs");
  assert.equal(Object.prototype.hasOwnProperty.call(docTool, "Readme"), false,
    "UI documentation is not copied into package/model fields");
  console.log("PASS Tool Library docs: selected exact revision is fetched lazily and stays UI-only");

  const actionContext = vm.createContext({ window: null });
  actionContext.window = actionContext;
  vm.runInContext(fs.readFileSync(path.join(root,
    "web/js/app-tools-actions.js"), "utf8"), actionContext,
  { filename: "app-tools-actions.js" });
  const calls = [];
  const continuations = [];
  const outputs = [];
  let next = false;
  const actionState = {
    tools: [{ Id: "common.capabilities_read" }], selectedToolIndex: 0
  };
  const actions = actionContext.RNAssistantToolActions.create({
    state: actionState,
    validateSelected: () => true,
    syncSelected() {},
    readRunArguments: () => ({
      id: "common.sample", referencePath: "references/details.md", action: "read"
    }),
    readNextArguments: () => ({
      id: "common.sample", referencePath: "references/details.md", action: "next"
    }),
    setBusy() {},
    setTextOutput(value) { outputs.push(value); },
    setJsonOutput(value) { outputs.push(value); },
    setContinuation(value) { continuations.push(value); },
    log() {}, logToolResult() {},
    async send(action, payload) {
      calls.push({ action, payload });
      const complete = next;
      next = true;
      return {
        type: "rnassistant.toolRunResult", contractVersion: 1,
        success: true, status: "ok", code: null, retryable: null,
        pendingId: null, catalogRevision: null, message: "read",
        dataJson: JSON.stringify({
          kind: "reference", id: "common.sample",
          path: "references/details.md", hasMore: !complete,
          complete
        }),
        toolStepsConsumed: 1
      };
    }
  });
  await actions.run();
  assert.equal(continuations.at(-1).referencePath, "references/details.md");
  await actions.next();
  assert.equal(calls[1].payload.arguments.action, "next");
  assert.equal(Object.prototype.hasOwnProperty.call(calls[1].payload.arguments, "cursor"), false);
  assert.equal(continuations.at(-1), null);

  const invalid = actionContext.RNAssistantToolActions.create({
    state: actionState, validateSelected: () => true, syncSelected() {},
    readRunArguments: () => ({}), readNextArguments: () => ({}),
    setBusy() {}, setContinuation(value) { continuations.push(value); },
    setTextOutput(value) { outputs.push(value); }, setJsonOutput() {},
    log() {}, logToolResult() {},
    send: async () => ({ Success: true, Message: "legacy" })
  });
  await invalid.run();
  assert.match(String(outputs.at(-1)), /ToolRunResult v1/);
  console.log("PASS Tool Library actions: strict result v1 and cursor-free continuation");

  const css = fs.readFileSync(path.join(root, "web/css/app-tools.css"), "utf8");
  const responsive = fs.readFileSync(path.join(root, "web/css/app-responsive.css"), "utf8");
  assert.match(css, /\.tool-argument-control-row\s*\{/);
  assert.match(css, /\.component-toolbar\s*\{[^}]*display:\s*grid/s);
  assert.match(css, /\.library-editor-pane\s*\{[^}]*overflow:\s*hidden/s);
  assert.match(responsive, /#toolEditorPanel \.library-editor-scroll/);
  console.log("PASS Tool Library layout: right pane owns responsive overflow");
}()).catch(error => {
  console.error(error);
  process.exitCode = 1;
});
