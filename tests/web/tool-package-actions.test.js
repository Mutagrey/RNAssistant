"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

(async function () {
  const logs = [];
  const outputs = [];
  const calls = [];
  const state = {
    tools: [{ Id: "excel.echo_vba", Executor: "vba" }],
    selectedToolIndex: 0,
    selectedToolComponentIndex: 0
  };
  const context = vm.createContext({ window: null });
  context.window = context;
  vm.runInContext(fs.readFileSync(path.join(__dirname,
    "../../web/js/app-tools-actions.js"), "utf8"), context,
    { filename: "app-tools-actions.js" });
  const actions = context.RNAssistantToolActions.create({
    state,
    syncSelected() {},
    readTools() { return state.tools; },
    mutationRequest() {
      return {
        type: "rnassistant.toolLibraryMutationRequest",
        contractVersion: 1,
        mutations: []
      };
    },
    parseMutation(response) {
      assert.equal(response.type, "rnassistant.toolLibraryMutationResult");
      return { tools: state.tools, results: response.results, failure: null };
    },
    parseLibrary(response) {
      assert.equal(response.type, "rnassistant.toolLibrary");
      return state.tools;
    },
    acceptSaved() {},
    renderTools() {},
    renderEditor() {},
    setBusy() {},
    setJsonOutput(value) { outputs.push(value); },
    setTextOutput(value) { outputs.push(value); },
    log(message, level) { logs.push({ message, level }); },
    async send(action, payload) {
      calls.push({ action, payload });
      if (action === "saveTools") return {
        type: "rnassistant.toolLibraryMutationResult",
        contractVersion: 1,
        results: [],
        library: {
          type: "rnassistant.toolLibrary",
          contractVersion: 1,
          tools: []
        }
      };
      return {
        result: {
          contractVersion: 1,
          sourceRevision: "source-revision",
          status: "ok",
          success: true,
          message: "installed",
          mayHaveDispatched: true,
          effect: "verified_change"
        },
        tools: {
          type: "rnassistant.toolLibrary",
          contractVersion: 1,
          tools: []
        },
        Result: { Message: "legacy must not win" },
        Tools: []
      };
    }
  });

  await actions.installVba();

  assert.deepEqual(calls.map(item => item.action),
    ["saveTools", "installVbaTool"]);
  assert.equal(calls[0].payload.type,
    "rnassistant.toolLibraryMutationRequest");
  assert.equal(outputs.at(-1).contractVersion, 1);
  assert.equal(outputs.at(-1).effect, "verified_change");
  assert.equal(logs.at(-1).message, "installed");
  assert.equal(state.tools.length, 1,
    "PascalCase compatibility response is ignored");
  console.log("PASS tool package actions: typed result v1 only");
}()).catch(error => {
  console.error(error);
  process.exitCode = 1;
});
