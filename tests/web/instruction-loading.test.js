"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), vm = require("node:vm");
const tick = () => new Promise(resolve => setImmediate(resolve));
class Element {
  constructor() { this.children = []; this.style = {}; this.value = "no matching library row"; this.classList = { toggle() {} }; }
  replaceChildren() { this.children = []; }
  appendChild(child) { this.children.push(child); return child; }
  setAttribute() {}
}
function fixture() {
  const nodes = new Map(), calls = [], pending = [];
  const context = vm.createContext({
    state: { tools: [], skills: [], promptDrafts: {} },
    document: { createElement: () => new Element() },
    $: id => { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); },
    isPanelActive: () => !!context.visible,
    toolLibraryItemsFromContract: value => value.items,
    skillLibraryItemsFromContract: value => value.items,
    RNAssistantNavigationTree: { render() {} },
    send(type) { calls.push(type); return new Promise((resolve, reject) => pending.push({ resolve, reject, type })); }
  });
  context.window = context;
  for (const file of ["app-chat-state.js", "app-prompts.js"])
    vm.runInContext(fs.readFileSync("web/js/" + file, "utf8"), context, { filename: file });
  return { context, nodes, calls, pending };
}
(async () => {
  {
    const { context, calls, pending } = fixture();
    context.resetInstructionLibraries(); context.renderInstructions();
    assert.equal(calls.length, 0, "hidden library never starts reads");
    context.visible = true; context.renderInstructions(); context.renderInstructions();
    assert.deepEqual(calls, ["getTools", "getSkills"], "one pair of metadata reads on opening");
    assert.equal(context.$("addToolButton").disabled, true, "cannot overwrite an unloaded catalog");
    pending.forEach(request => request.resolve({ items: [] })); await tick();
    context.renderInstructions();
    assert.equal(calls.length, 2, "re-render and reopening reuse the loaded catalogs");
    assert.equal(context.$("addSkillButton").disabled, false);
  }
  {
    const { context, calls, pending } = fixture();
    context.visible = true; context.renderInstructions();
    pending[0].resolve({ items: [] }); pending[1].reject(new Error("skills unavailable")); await tick();
    context.renderInstructions();
    assert.equal(calls.length, 2, "failure does not create a render/retry loop");
    const retry = context.$("instructionsList").children.find(node => node.textContent === "Повторить загрузку");
    assert.ok(retry, "failed read exposes an explicit retry");
    retry.onclick();
    assert.deepEqual(calls, ["getTools", "getSkills", "getSkills"], "retry only reads the failed catalog");
    pending[2].resolve({ items: [] }); await tick();
    assert.equal(context.state.skillLibraryLoaded, true);
  }
  {
    const { context, calls, pending } = fixture();
    context.visible = true; context.renderInstructions();
    context.resetInstructionLibraries(); context.renderInstructions();
    pending[0].resolve({ items: [{ Id: "old-host" }] }); pending[1].resolve({ items: [] }); await tick();
    assert.equal(context.state.tools.length, 0, "late previous-host catalogs are discarded");
    pending[2].resolve({ items: [] }); pending[3].resolve({ items: [] }); await tick();
    assert.equal(context.state.toolLibraryLoaded, true);
    assert.equal(calls.length, 4, "host change starts only its own pair of reads");
  }
  console.log("PASS library demand loading: hidden, coalesced, retry, stale-host isolation");
})().catch(error => { console.error(error); process.exitCode = 1; });
