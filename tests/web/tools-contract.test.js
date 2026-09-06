"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const source = fs.readFileSync(path.join(root,
  "web/js/app-tools.js"), "utf8");
const documentationSource = fs.readFileSync(path.join(root,
  "web/js/app-tools-documentation.js"), "utf8");
const chatStateSource = fs.readFileSync(path.join(root,
  "web/js/app-chat-state.js"), "utf8");
const chatSessionSource = fs.readFileSync(path.join(root,
  "web/js/app-chat-session.js"), "utf8");
const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
const context = vm.createContext({ console });
context.window = context;
context.state = {
  tools: [], selectedToolIndex: -1,
  toolLibraryBaselineItems: [], toolLibraryBaseline: ""
};
context.$ = () => null;
context.send = () => Promise.resolve();
context.setControlBusy = () => {};
context.log = () => {};
context.logToolResult = () => {};
context.RNAssistantToolStructuredEditor = {
  create: () => ({
    readRunArguments: () => ({}),
    syncSchemaDraft: () => true
  })
};
context.RNAssistantToolActions = { create: () => ({}) };
vm.runInContext(fs.readFileSync(path.join(root,
  "web/js/app-tools-documentation.js"), "utf8"), context,
{ filename: "app-tools-documentation.js" });
vm.runInContext(source, context, { filename: "app-tools.js" });

function component() {
  return {
    name: "RNA_Echo", type: "StdModule", fileName: "RNA_Echo.bas",
    code: "Option Explicit\n", codeSha256: "a".repeat(64)
  };
}

function item(id, revision, description = "Description") {
  return {
    revision, id, host: "Excel", name: id, description,
    source: { sha256: "a".repeat(64), byteLength: 100 }, executor: "vba",
    requiresConfirmation: true, mutatesDocument: true,
    mutatesLocalState: false, canSourceHtmlData: false,
    agentCanRun: false,
    enabled: true, builtIn: false, riskLevel: 1,
    useWhen: "", doNotUseWhen: "", capabilityStatus: "available",
    limitations: "", packageVersion: "1.0.0",
    entryPoint: "RNA_Echo.Run", argumentOrder: [],
    scope: "global",
    installationStatus: "not_installed"
  };
}

function library(tools) {
  return {
    type: "rnassistant.toolLibrary",
    contractVersion: 1,
    tools
  };
}

{
  const tools = context.toolLibraryItemsFromContract(library([
    item("excel.one", "1".repeat(64))
  ]));
  assert.equal(tools[0].Id, "excel.one");
  assert.equal(tools[0]._baseRevision, "1".repeat(64));
  assert.throws(() => context.toolLibraryItemsFromContract([
    item("excel.legacy", "2".repeat(64))
  ]), /typed Tool Library/);
  assert.throws(() => context.toolLibraryItemsFromContract({
    Type: "rnassistant.toolLibrary", ContractVersion: 1, Tools: []
  }), /typed Tool Library/);
  console.log("PASS tool contract: lowercase versioned library is the only accepted source");
}

{
  context.state.tools = context.toolLibraryItemsFromContract(library([
    item("excel.update", "3".repeat(64), "Before"),
    item("excel.delete", "4".repeat(64))
  ]));
  context.setToolLibraryBaseline(context.state.tools);
  const body = { argumentSchemaJson: '{"type":"object"}', code: "Option Explicit\n", readme: "", components: [component()] };
  context.applyToolSource(context.state.tools[0], body);
  context.state.tools[0].Description = "After";
  context.state.tools.splice(1, 1);
  const created = context.toolFromContract(
    item("excel.new", "5".repeat(64)));
  created.Revision = "";
  created._baseId = "";
  created._baseRevision = "";
  context.applyToolSource(created, body);
  delete created.Source; delete created._sourceBaseline;
  context.state.tools.push(created);
  const mutations = context.toolLibraryMutations();
  assert.deepEqual(Array.from(mutations, mutation => mutation.kind),
    ["upsert", "upsert", "delete"]);
  assert.equal(mutations[0].baseId, "excel.update");
  assert.equal(mutations[0].expectedRevision, "3".repeat(64));
  assert.equal(mutations[1].baseId, "");
  assert.equal(mutations[1].expectedRevision, "");
  assert.equal(mutations[2].baseId, "excel.delete");
  assert.equal(mutations[2].expectedRevision, "4".repeat(64));
  assert.equal(Object.prototype.hasOwnProperty.call(
    mutations[0], "Tools"), false);
  console.log("PASS tool contract: editor emits explicit revision-guarded mutations");
}

{
  const response = {
    type: "rnassistant.toolLibraryMutationResult",
    contractVersion: 1,
    results: [{
      type: "rnassistant.toolMutationResult", contractVersion: 1,
      status: "ok", message: "saved", dispatch: "may_have_dispatched",
      effect: "verified_change", id: "excel.one", operation: "update",
      previousRevision: "1".repeat(64), revision: "2".repeat(64)
    }],
    library: library([item("excel.one", "2".repeat(64))])
  };
  const parsed = context.toolLibraryMutationFromContract(response);
  assert.equal(parsed.tools[0]._baseRevision, "2".repeat(64));
  assert.equal(parsed.failure, null);
  assert.throws(() => context.toolLibraryMutationFromContract(
    Object.assign({}, response, { contractVersion: 0 })), /typed/);
  console.log("PASS tool contract: mutation result and refreshed catalog are exact v1");
}

{
  const operation = { toolId: "excel.inspect", revision: "d".repeat(64), chatId: "chat", host: "excel" };
  const response = {
    type: "rnassistant.toolLibraryDocumentation",
    contractVersion: 1,
    toolId: operation.toolId,
    revision: operation.revision, chatId: "chat",
    resource: { uri: "rna://catalog/builtin-tools-excel/excel.inspect/documentation", revision: "exact" },
    data: { payload: { contentType: "text/markdown; charset=utf-8" } }
  };
  assert.equal(context.RNAssistantToolDocumentation.fromContract(response, operation), response);
  assert.throws(() => context.RNAssistantToolDocumentation.fromContract({
    ...response, revision: "stale"
  }, operation), /typed contract/);
  assert.throws(() => context.RNAssistantToolDocumentation.fromContract({ ...response, markdown: "inline" }, operation), /typed contract/);
  assert.match(documentationSource, /getToolDocumentation/);
  assert.match(documentationSource, /expectedRevision:\s*operation\.revision/);
  assert.ok(index.includes("id=\"toolDocumentationMarkdown\""));
  console.log("PASS tool contract: built-in documentation uses exact UI-only id/revision boundary");
}

{
  assert.ok(index.includes(
    "app-tools.js?v=tool-docs-20260906-1"));
  assert.equal(/StoragePath|storagePath/.test(source), false);
  assert.match(source, /expectedRevision/);
  assert.match(source, /toolLibraryMutationRequestType/);
  assert.match(chatSessionSource,
    /state\.tools\s*=\s*toolLibraryItemsFromContract\(init\.tools\)/);
  assert.match(chatStateSource,
    /toolLibraryItemsFromContract\(response\.tools\)/);
  assert.doesNotMatch(chatStateSource, /response\.Tools/);
  console.log("PASS tool contract: shipped UI has no path identity or unversioned response fallback");
}

console.log("OK 5/5");
