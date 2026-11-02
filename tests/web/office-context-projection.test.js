"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), vm = require("node:vm");
const ui = vm.createContext({ console }); ui.window = ui;
for (const file of ["app-core", "app-conversation-state", "app-run-view-state", "app-chat-state", "app-context-data", "app-context"])
  vm.runInContext(fs.readFileSync("web/js/" + file + ".js", "utf8"), ui, { filename: file });
for (const name of ["captureChatDraft", "restoreChatDraft", "renderChatSessions", "renderMessages", "renderContext",
  "renderContextMeter", "renderModelControls", "syncTokenEstimateCalibrationFromUsage", "setControlBusy", "reportFocusState"])
  ui[name] = () => {};
const handlers = new Map(), logs = []; let syncs = 0, closes = 0;
ui.$ = id => ({ addEventListener: (event, callback) => handlers.set(id + ":" + event, callback) });
ui.document = { addEventListener() {}, activeElement: null };
ui.log = value => logs.push(value); ui.currentActiveSend = () => null;
ui.setContextManagerOpen = () => closes++;
ui.syncActiveChatState = async () => { syncs++; };
ui.bindContextActions();
function select(id, label) {
  const response = { activeChatId: id, sessionRevision: 5, messages: [], context: { Notes: [{ Id: label }] },
    contextUsage: { usedTokens: 10 }, artifacts: [{ id: label }], artifactLibrary: { sessionRevision: 5, heads: [{ resourceUri: label }] },
    activeContextCheckpointId: label, activeHtmlArtifactId: label, activeTaskListArtifactId: label, activePlanDocumentArtifactId: label };
  ui.applyChatState(response); return response;
}
const commands = [
  ["addTextContext", () => ui.addTextContext("user", "text", "title", "ref", "body", {})],
  ["addSelectionContext", () => ui.addSelectionContext("full")],
  ["removeContextItem", () => ui.removeContextItem("note")],
  ["clearContext", () => handlers.get("clearContextButton:click")()]
];
(async () => {
  for (const [name, invoke] of commands) {
    select("a", "old"); const old = ui.officeChatProjection();
    let resolve;
    ui.send = (type, payload) => {
      assert.equal(type, name); assert.equal(payload.chatId, "a");
      return new Promise(done => { resolve = done; });
    };
    const pending = invoke();
    select("b", "other"); const fresh = select("a", "fresh");
    const before = [syncs, closes, logs.length];
    resolve({ context: { Notes: [{ Id: "late" }] } }); await pending;
    assert.equal(ui.state.context, fresh.context, name + " cannot overwrite a reopened A context");
    assert.deepEqual([syncs, closes, logs.length], before, "obsolete context delivery has no selected-view follow-up");
    assert.equal(old.snapshot(), null);
  }

  let resolve;
  ui.send = () => new Promise(done => { resolve = done; });
  const pending = ui.addTextContext("user", "text", "title", "ref", "body", {});
  ui.clearOfficeChatProjection(); const reset = select("a", "reset");
  resolve({ context: { Notes: [] } });
  assert.equal(await pending, false); assert.equal(ui.state.context, reset.context, "same-ID reconnect retires the context request");
  const accepted = { Notes: [{ Id: "accepted" }] };
  ui.send = async () => ({ context: accepted });
  const before = syncs;
  assert.equal(await ui.addTextContext("user", "text", "title", "ref", "body", {}), true);
  assert.equal(ui.state.context, accepted); assert.equal(syncs, before + 1);

  const fields = ["context", "contextUsage", "artifacts", "artifactLibrary", "activeContextCheckpointId",
    "activeHtmlArtifactId", "activeTaskListArtifactId", "activePlanDocumentArtifactId"];
  const owner = ui.officeChatProjection();
  for (const key of fields) {
    assert.equal(typeof Object.getOwnPropertyDescriptor(ui.state, key).get, "function", key + " has no second retained value");
    assert.equal(ui.state[key], owner.snapshot()[key]);
  }
  ui.applyChatState({ activeChatId: "b", sessionRevision: 5, messages: [] });
  assert.equal(owner.snapshot(), null);
  assert.equal(ui.state.artifacts.length, 0); assert.equal(ui.state.artifactLibrary.heads.length, 0);
  assert.equal(ui.state.activeContextCheckpointId, ""); assert.equal(ui.state.activeHtmlArtifactId, "");
  assert.equal(Object.keys(ui.state.context).length, 0, "a partial new selection cannot inherit another chat's context");
  ui.applyChatState({ activeChatId: "", messages: [], context: {}, contextUsage: {}, artifacts: [], artifactLibrary: { heads: [] },
    activeContextCheckpointId: "", activeHtmlArtifactId: "", activeTaskListArtifactId: "", activePlanDocumentArtifactId: "" });
  assert.equal(ui.officeChatProjection(), null); assert.equal(ui.state.artifacts.length, 0);
  assert.equal(await ui.addTextContext("user", "text", "title", "ref", "body", {}), false);
  console.log("PASS Office context: all four commands reject obsolete delivery; one context/material projection, reset and empty selection");
})().catch(error => { console.error(error); process.exitCode = 1; });
