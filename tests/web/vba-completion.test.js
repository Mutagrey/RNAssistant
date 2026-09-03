"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "../..");
const elements = new Map([
  ["vbaModuleSelect", { value: "Module2" }],
  ["toolComponentNameInput", { value: "RNA_Current" }]
]);
const helpers = {};
const context = vm.createContext({
  state: {
    vba: {
      selectedModule: "Module2",
      modules: [
        { name: "Module1", code: "Public Sub Alpha(ByVal value As Long)\nEnd Sub\nPrivate Function Secret() As Long\nEnd Function" },
        { name: "Module2", code: "Public Sub StaleSource()\nEnd Sub" },
        { name: "EmptyModule" }
      ]
    },
    tools: [],
    selectedToolIndex: -1,
    selectedToolComponentIndex: 0
  },
  document: { getElementById: id => elements.get(id) || null },
  CodeMirror: {
    Pos: (line, ch) => ({ line, ch }),
    registerHelper(type, name, helper) {
      helpers[type + ":" + name] = helper;
    }
  }
});
context.window = context;
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-vba-completion.js"), "utf8"), context, { filename: "app-vba-completion.js" });

assert.equal(typeof helpers["hint:vb"], "function");
const hint = helpers["hint:vb"];
function editor(id, line, source, tokenType) {
  return {
    _rnEditorId: id,
    getCursor: () => ({ line: 0, ch: line.length }),
    getLine: () => line,
    getTokenAt: () => ({ type: tokenType || "variable", string: line }),
    getValue: () => source,
    getHelper: (_cursor, type) => type === "hintWords" ? ["sub", "function", "option", "with"] : []
  };
}
function texts(result) {
  return result ? Array.from(result.list, item => item.text) : [];
}

const currentSource = "Option Explicit\nPrivate Sub LocalOnly()\nEnd Sub\nPublic Function CurrentFn(ByVal x As Long) As Long\nEnd Function";
let result = hint(editor("vbaCodeInput", "Cu", currentSource));
assert.ok(texts(result).includes("CurrentFn"), "current unsaved source participates in completion");
assert.equal(texts(result).includes("StaleSource"), false, "stored selected-module source is replaced by the editor draft");

result = hint(editor("vbaCodeInput", "Mo", currentSource));
assert.deepEqual(texts(result).filter(text => /^Module/.test(text)), ["Module1", "Module2"]);

result = hint(editor("vbaCodeInput", "Module1.", currentSource));
assert.ok(texts(result).includes("Alpha"));
assert.equal(texts(result).includes("Secret"), false, "private procedures stay private across modules");
assert.deepEqual(result.from, { line: 0, ch: 8 });

result = hint(editor("vbaCodeInput", "Module2.L", currentSource));
assert.deepEqual(texts(result), ["LocalOnly"], "current-module qualification includes private procedures");

assert.equal(hint(editor("vbaCodeInput", "Cur", currentSource, "comment")), null);
assert.equal(hint(editor("vbaCodeInput", "Cur", currentSource, "string")), null);

context.state.tools = [{ Components: [
  { Name: "RNA_Current", Code: "Public Sub OldDraft()\nEnd Sub" },
  { Name: "RNA_Helper", Code: "Public Function HelperValue() As Long\nEnd Function\nPrivate Sub HiddenHelper()\nEnd Sub" }
] }];
context.state.selectedToolIndex = 0;
result = hint(editor("toolCodeInput", "Hel", "Public Sub CurrentDraft()\nEnd Sub"));
assert.ok(texts(result).includes("HelperValue"));
assert.equal(texts(result).includes("HiddenHelper"), false);

const manyProcedures = Array.from({ length: 120 }, (_, index) => "Public Sub Proc" + index + "()\nEnd Sub").join("\n");
result = hint(editor("toolCodeInput", "", manyProcedures));
assert.equal(result.list.length, 80, "completion DOM is bounded");

const page = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
assert.ok(page.includes("css/vendor/codemirror/addon/hint/show-hint.min.css"));
assert.ok(page.indexOf("js/vendor/codemirror/addon/hint/show-hint.min.js") < page.indexOf("js/app-vba-completion.js"));
assert.ok(page.indexOf("js/app-vba-completion.js") < page.indexOf("js/app-editors.js"));
const createdEditors = {};
["vbaCodeInput", "toolCodeInput", "toolSchemaInput"].forEach(id => {
  if (!elements.has(id)) elements.set(id, { id, value: "" });
  elements.get(id).id = id;
});
context.CodeMirror.fromTextArea = function (node, options) {
  const attributes = {};
  const handlers = {};
  const wrapper = { className: "", style: {}, title: "", classList: { toggle() {} } };
  const instance = {
    options,
    handlers,
    attributes,
    hintCalls: 0,
    save() {},
    getWrapperElement: () => wrapper,
    getInputField: () => ({ setAttribute: (name, entry) => { attributes[name] = entry; } }),
    on: (name, handler) => { handlers[name] = handler; },
    showHint() { this.hintCalls += 1; }
  };
  createdEditors[node.id] = instance;
  return instance;
};
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-editors.js"), "utf8"), context, { filename: "app-editors.js" });
context.initializeCodeEditors();
const vbaEditor = createdEditors.vbaCodeInput;
assert.equal(typeof vbaEditor.options.extraKeys["Ctrl-Space"], "function");
assert.equal(createdEditors.toolSchemaInput.options.extraKeys["Ctrl-Space"], undefined, "non-VBA editors do not publish VBA completion");
vbaEditor.options.extraKeys["Ctrl-Space"](vbaEditor);
assert.equal(vbaEditor.hintCalls, 1);
vbaEditor.handlers.inputRead(vbaEditor, { origin: "+input", text: ["."] });
assert.equal(vbaEditor.hintCalls, 2, "member completion opens after a typed dot");
assert.equal(vbaEditor.attributes["aria-keyshortcuts"], "Control+Space");
console.log("PASS VBA completion: bounded local keywords, modules, procedures and editor wiring");
