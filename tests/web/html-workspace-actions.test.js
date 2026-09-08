"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

function fixture() {
  const f = { calls: [], applied: [], alerts: [], logs: [], confirm: () => true };
  f.state = { activeChatId: "chat-a", activeHtmlArtifactId: "html-r3", chatNavigationVersion: 1,
    chatProjectionRevisions: { "chat-a": 7 }, htmlWorkspaceEditVersion: 0,
    htmlWorkspaceSelection: { type: "file", id: "index.html" } };
  const context = vm.createContext({});
  context.window = context;
  context.confirm = () => f.confirm();
  context.prompt = () => { if (f.prompt) f.prompt(); return "import.html"; };
  context.alert = value => f.alerts.push(value);
  context.RNAssistantArtifactViewerActions = { create: () => ({}) };
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-html-workspace-actions.js"), "utf8"), context);
  f.actions = context.RNAssistantHtmlWorkspaceActions.create({ state: f.state,
    getSelection: () => ({ type: "file", path: "index.html", label: "index.html" }),
    getActionState: () => ({ chatId: f.state.activeChatId, dirty: f.state.htmlWorkspaceDirty,
      undoSnapshotId: "parent", redoSnapshotId: "child", recoverySnapshotId: "healthy" }),
    send: async (method, payload) => {
      f.calls.push({ method, payload: JSON.parse(JSON.stringify(payload)) });
      if (method === "resourceDataClose") return {};
      if (f.wait) await f.wait;
      return f.response || { importedFromResourceUri: payload.sourceResourceUri, importedPath: "import.html" };
    },
    applyWorkspaceResponse: response => { f.applied.push(response); f.state.htmlWorkspaceDirty = false; return true; },
    log: text => f.logs.push(text), render: () => {} });
  return f;
}

const cases = [
  ["delete file", f => f.actions.deleteSelection(), "deleteHtmlWorkspaceFile", { path: "index.html" }],
  ["delete data", f => f.actions.deleteSelection({ type: "data", name: "sales", label: "sales" }), "deleteHtmlWorkspaceData", { name: "sales" }],
  ["undo", f => f.actions.undo(), "restoreHtmlWorkspaceSnapshot", { snapshotId: "parent" }],
  ["redo", f => f.actions.redo(), "redoHtmlWorkspaceSnapshot", { snapshotId: "child" }],
  ["recover", f => f.actions.recoverRevision(), "restoreHtmlWorkspaceSnapshot", { snapshotId: "healthy" }],
  ["import", f => f.actions.importUploadedHtml({ sourceResourceUri: "rna://exact/original" }), "importUploadedHtmlToWorkspace",
    { sourceResourceUri: "rna://exact/original", targetPath: "import.html" }]
];

(async () => {
  for (const [name, run, method, payload] of cases) {
    const f = fixture();
    await run(f);
    assert.deepEqual(f.calls, [{ method, payload: { ...payload, chatId: "chat-a", expectedSessionRevision: 7,
      expectedActiveHtmlArtifactId: "html-r3" } }]);
    assert.equal(f.applied.length, 1, name);
    for (const drift of [g => { g.state.chatNavigationVersion += 2; }, g => {
      g.state.htmlWorkspaceEditVersion++; g.state.htmlWorkspaceDirty = true;
    }, g => { g.state.activeHtmlArtifactId = "new"; }, g => { g.state.chatProjectionRevisions["chat-a"]++; }]) {
      const g = fixture();
      let resolve;
      g.wait = new Promise(r => { resolve = r; });
      const pending = run(g);
      await run(g);
      await g.actions.undo();
      assert.equal(g.calls.length, 1, name + " suppresses duplicate and competing action");
      drift(g);
      const selection = g.state.htmlWorkspaceSelection;
      const dirty = g.state.htmlWorkspaceDirty;
      resolve(); await pending;
      assert.equal(g.applied.length, 0, name + " ignores delayed response");
      assert.equal(g.state.htmlWorkspaceSelection, selection);
      assert.equal(g.state.htmlWorkspaceDirty, dirty);
      assert.equal(g.alerts.length, 0, "a completed action is not reported as a failed mutation");
      assert.equal(g.calls.length, 1, "no automatic mutation retry");
    }
    const h = fixture();
    h.state.htmlWorkspaceDirty = true;
    h.confirm = () => { h.state.chatNavigationVersion++; return true; };
    await run(h);
    assert.equal(h.calls.length, 0, name + " pins state before confirmation");
    h.confirm = () => true;
    await run(h);
    assert.equal(h.calls.length, 1, "guard refusal releases pending state");
    const missing = fixture();
    delete missing.state.chatProjectionRevisions;
    await run(missing);
    assert.equal(missing.calls.length, 0, name + " requires a known session revision");
    console.log("PASS HTML actions: " + name + " exact controls and races");
  }
  const f = fixture();
  f.prompt = () => { f.state.activeChatId = "chat-b"; };
  await f.actions.importUploadedHtml({ sourceResourceUri: "rna://exact/original" });
  assert.equal(f.calls.length, 0, "import pins source chat before the path prompt");

  const e = fixture();
  let release;
  e.wait = new Promise(r => { release = r; });
  e.response = { exportRevisionArtifactId: "checkpoint", resourceExport: { bindings: [{ lease: { leaseId: "lease" } }] } };
  const pending = e.actions.exportWorkspace();
  e.state.chatNavigationVersion += 2;
  release();
  assert.equal(await pending, false);
  assert.equal(e.applied.length, 0);
  assert.deepEqual(e.calls[1], { method: "resourceDataClose", payload: { chatId: "chat-a", workspaceId: "checkpoint", leaseId: "lease" } });
  assert.equal(e.state.htmlWorkspaceExportPending, false);
  console.log("PASS HTML actions: stale export closes exact leases without applying its projection");
})().catch(error => { console.error(error); process.exitCode = 1; });
