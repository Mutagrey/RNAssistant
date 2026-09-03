"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
const app = fs.readFileSync(path.join(root, "web/js/app.js"), "utf8");
const session = fs.readFileSync(path.join(root, "web/js/app-chat-session.js"), "utf8");

assert.equal(index.includes('<script src="js/vendor/echarts.min.js"></script>'), false,
  "the 1 MiB chart vendor must not block WebView startup");
assert.match(index, /app-echarts-sandbox-runtime\.js\?v=ui-lazy-20260903-1/);
["app-chat-state.js", "app-messages.js", "app-context.js", "app-model-render.js",
  "app-html-workspace.js", "app-html-workspace-editor.js"].forEach(asset => {
  assert.ok(index.includes(asset + "?v=ui-lazy-20260903-1"), asset + " uses the lazy UI cache key");
});
assert.doesNotMatch(app, /initializeCodeEditors\(\);/,
  "hidden CodeMirror editors must not be created during DOMContentLoaded");
assert.doesNotMatch(session, /loadModelCatalog\(false\)/,
  "opening the add-in must not start an unsolicited model-catalog request");
console.log("PASS lazy UI: startup omits chart parsing, hidden editors and model discovery");

const editorIds = [
  "toolSchemaInput", "toolRunArgsInput", "toolCodeInput", "toolReadmeInput",
  "skillBodyInput", "promptEditInput", "vbaCodeInput", "htmlWorkspaceEditorInput"
];
const nodes = Object.fromEntries(editorIds.map(id => [id, { id, value: "" }]));
const created = [];
const editorContext = vm.createContext({
  console,
  document: { getElementById: id => nodes[id] || null },
  setTimeout: callback => { callback(); return 1; }
});
editorContext.window = editorContext;
editorContext.CodeMirror = {
  fromTextArea(node) {
    created.push(node.id);
    return {
      getWrapperElement: () => ({ className: "", style: {}, classList: { toggle() {} } }),
      getInputField: () => ({ setAttribute() {} }),
      on() {},
      refresh() {},
      save() {},
      getValue: () => node.value,
      setValue: value => { node.value = value; },
      setOption() {}
    };
  }
};
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-editors.js"), "utf8"), editorContext,
  { filename: "app-editors.js" });

editorContext.activateCodeEditorsForTab("chat");
assert.deepEqual(created, []);
editorContext.activateCodeEditorsForTab("artifacts");
assert.deepEqual(created, ["htmlWorkspaceEditorInput"]);
editorContext.activateCodeEditorsForTab("vba");
assert.deepEqual(created, ["htmlWorkspaceEditorInput", "vbaCodeInput"]);
editorContext.activateCodeEditorsForTab("instructions");
assert.equal(created.length, 8);
assert.equal(new Set(created).size, 8, "each editor is initialized at most once");
console.log("PASS lazy UI: CodeMirror editors initialize only for the opened section");

const messagesContext = vm.createContext({ console });
messagesContext.window = messagesContext;
messagesContext.isPanelActive = () => false;
messagesContext.$ = () => { throw new Error("hidden chat touched the DOM"); };
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-messages.js"), "utf8"), messagesContext,
  { filename: "app-messages.js" });
assert.doesNotThrow(() => messagesContext.renderMessages());
console.log("PASS lazy UI: hidden chat state updates do not rebuild transcript DOM");

(async function () {
  const chartContext = vm.createContext({ Deno: {}, console, Promise });
  chartContext.window = chartContext;
  let appended = 0;
  chartContext.document = {
    createElement: () => ({}),
    head: {
      appendChild(script) {
        appended++;
        setImmediate(() => {
          vm.runInContext(fs.readFileSync(path.join(root, "web/js/vendor/echarts.min.js"), "utf8"), chartContext,
            { filename: "echarts.min.js", timeout: 5000 });
          script.onload();
        });
      }
    }
  };
  vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-echarts-sandbox-runtime.js"), "utf8"), chartContext,
    { filename: "app-echarts-sandbox-runtime.js" });
  assert.equal(chartContext.echarts, undefined);
  const first = chartContext.RNAssistantEChartsSandboxRuntime.load();
  const second = chartContext.RNAssistantEChartsSandboxRuntime.load();
  assert.equal(first, second, "parallel chart requests share one vendor load");
  const loaded = await first;
  assert.equal(appended, 1);
  assert.equal(loaded.version, "5.6.0");
  assert.equal(typeof chartContext.RNAssistantEChartsFactory, "function");
  console.log("PASS lazy UI: first chart use loads and captures ECharts exactly once");
  console.log("OK 4/4");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
