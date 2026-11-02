"use strict";
const assert = require("node:assert/strict"), fs = require("node:fs"), vm = require("node:vm");
const ui = vm.createContext({ state: { activeChatId: "a", messages: [{ Id: "saved" }] } });
ui.window = ui;
for (const file of ["app-conversation-state", "app-chat-composer", "app-workspace-chat", "app-chat-state", "app-chat-session"])
  vm.runInContext(fs.readFileSync("web/js/" + file + ".js", "utf8"), ui);
const office = ui.officeChatSource("a"), target = { workspaceId: "w", sessionId: "a" };
const workspace = ui.RNAssistantWorkspaceChat.snapshotSource(target);
target.workspaceId = "changed"; target.sessionId = "changed";
const officeValue = { activeChatId: "a", messages: [{ Id: "office" }] };
const workspaceValue = { project: { workspaceId: "w" }, sessionId: "a", messages: [{ Id: "workspace" }] };
assert.ok(Object.isFrozen(office.target)); assert.ok(Object.isFrozen(workspace.target));
assert.equal(workspace.target.workspaceId, "w", "source captures an immutable request address");
assert.equal(office.accept(officeValue), officeValue, "delivery borrows the original snapshot without cloning history");
assert.equal(workspace.accept(workspaceValue), workspaceValue);
assert.equal(office.matches({ ActiveChatId: "a" }), true);
assert.equal(workspace.matches({ Project: { WorkspaceId: "w" }, SessionId: "a" }), true);
for (const value of [null, {}, workspaceValue, { activeChatId: "other" }])
  assert.throws(() => office.accept(value), /другого чата/);
for (const value of [officeValue, { ...workspaceValue, sessionId: "other" },
  { ...workspaceValue, project: { workspaceId: "other" } }])
  assert.throws(() => workspace.accept(value), /другого чата/);
assert.throws(() => ui.officeChatSource(""), /адрес чата/);
assert.throws(() => ui.RNAssistantWorkspaceChat.snapshotSource({ sessionId: "a" }), /адрес чата/);

(async () => {
  let finishOffice, finishWorkspace;
  const one = office.read(address => new Promise(resolve => {
    assert.equal(address.sessionId, "a"); finishOffice = resolve;
  }));
  const two = workspace.read(address => new Promise(resolve => {
    assert.equal(address.workspaceId, "w"); finishWorkspace = resolve;
  }));
  finishWorkspace(workspaceValue); assert.equal(await two, workspaceValue);
  finishOffice(officeValue); assert.equal(await one, officeValue);
  await assert.rejects(office.read(async () => ({ activeChatId: "other" })), /другого чата/);
  await assert.rejects(workspace.read(async () => ({ ...workspaceValue, project: { workspaceId: "other" } })), /другого чата/);

  let catalog = 0, detail = 0;
  ui.applyChatState = value => { detail++; ui.state.activeChatId = value.activeChatId; ui.state.messages = value.messages; return true; };
  ui.applyChatCatalogState = () => { catalog++; };
  const previous = ui.state.messages;
  assert.throws(() => ui.applyChatStateForChat({ activeChatId: "other", messages: [] }, "a"), /другого чата/);
  assert.equal(ui.state.activeChatId, "a"); assert.equal(ui.state.messages, previous);
  assert.equal(detail, 0); assert.equal(catalog, 0, "mismatched reply cannot update even navigation metadata");
  ui.send = async (name, payload) => { assert.equal(name, "getChatState"); assert.equal(payload.chatId, "a"); return { activeChatId: "other" }; };
  await assert.rejects(ui.loadChatState("a"), /другого чата/);
  assert.equal(ui.applyChatStateForChat(officeValue, "a"), true);
  assert.equal(ui.applyChatStateForChat({ activeChatId: "background" }, "background"), false);
  assert.equal(ui.state.activeChatId, "a"); assert.equal(catalog, 1);
  assert.equal(ui.applyPushedChatState({ scope: "full", payload: { activeChatId: "background" } }), false);
  assert.equal(detail, 1, "a background full push does not select another chat");
  assert.equal(ui.applyPushedChatState({ scope: "full", payload: officeValue }), true);
  console.log("PASS snapshot sources: exact addresses, concurrent reads, borrowed history, wrong replies and full push isolation");
})().catch(error => { console.error(error); process.exitCode = 1; });
