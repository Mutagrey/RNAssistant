"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

async function main() {
  const elements = new Map(), calls = [];
  function element(id) {
    if (!elements.has(id)) elements.set(id, { id, dataset: {}, listeners: {}, value: "", hidden: false,
      setAttribute() {}, addEventListener(type, callback) { this.listeners[type] = callback; } });
    return elements.get(id);
  }
  const file = "draft-1700000000000-12345678123412341234123456789abc-report.draft";
  const item = { file, savedAt: 1700000000000, label: "report", byteLength: 1 };
  let state = { type: "rnassistant.workspaceState", contractVersion: 1, revision: 2,
    workspace: { bindingId: "binding-a", rootPath: "/workspace", readOnly: false },
    project: { name: "Reports", manifestPath: "reports/project.json", formatVersion: 2, sources: [] } };
  let uncertain = false;
  const scope = { document: { getElementById: element }, TextEncoder, Set, Number,
    send: async (type, payload) => {
      calls.push({ type, payload });
      if (type === "getWorkspaceState") return state;
      if (type === "listTabularProjectDrafts") return { state, items: [item] };
      if (type === "openTabularProjectDraft") return { state, draft: item, input: "{" };
      if (type === "saveTabularProjectDraft") {
        if (uncertain) throw Object.assign(new Error("write receipt missing"), { code: "effect_unknown" });
        return { state: { ...state, revision: state.revision + 1 }, draft: item };
      }
      if (type === "reconcileWorkspaceFile") return { state: { ...state, project: null }, exists: true,
        outcome: "UnknownAfterDispatch" };
      throw new Error(type);
    }, RNAssistantWorkspaceSources: { hasUnfinished: () => false },
    RNAssistantDataTemplates: { setContext() {} } };
  scope.window = scope;
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-workspace-projects.js"), "utf8"), scope);
  scope.RNAssistantWorkspace.bind();
  await scope.RNAssistantWorkspace.show();
  assert.equal((await scope.RNAssistantWorkspace.projectDraft("list")).items[0].file, file);
  assert.equal((await scope.RNAssistantWorkspace.projectDraft("open", { file })).input, "{");
  uncertain = true;
  await assert.rejects(scope.RNAssistantWorkspace.projectDraft("save", { file, input: "{" }),
    error => error.code === "effect_unknown");
  assert.equal(element("workspaceProjectResult").hidden, true, "unknown write revokes the selected project");
  assert.equal(element("workspaceReconcile").hidden, false, "exact-file recovery stays available");
  await element("workspaceReconcile").listeners.click();
  assert.equal(calls.at(-1).payload.manifestPath, "reports/drafts/" + file,
    "recovery inspects the draft target, never the project manifest");
  assert.equal(calls.filter(call => call.type === "saveTabularProjectDraft").length, 1,
    "uncertain save is not replayed");
  console.log("PASS workspace draft commands: typed list/open and exact-file recovery without save replay");
}
main().catch(error => { console.error(error); process.exitCode = 1; });
