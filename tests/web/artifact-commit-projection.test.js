"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const ui = vm.createContext({});
ui.window = ui;
ui.state = {
  activeChatId: "chat-a", activeRunViewState: null, chatProjectionRevisions: {},
  chats: [], documents: [], messages: [], artifacts: [], tools: [], skills: [], chatStateApplyVersion: 0
};
ui.$ = () => null;
["renderMessages", "renderContext", "renderContextMeter", "renderModelControls", "renderChatSessions"].forEach(name => {
  ui[name] = () => {};
});
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-run-view-state.js"), "utf8"), ui,
  { filename: "app-run-view-state.js" });
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-chat-state.js"), "utf8"), ui,
  { filename: "app-chat-state.js" });

{
  const order = [];
  const applied = ui.applyPushedChatState({
    type: "chatState",
    scope: "full",
    payload: {
      activeChatId: "chat-a",
      sessionRevision: 4,
      messages: [{ Id: "message-1", Content: "committed" }],
      artifacts: [{ id: "artifact-1", resourceUri: "rna://chat/chat-a/artifact/artifact-1/revision/1" }],
      chats: [{ Id: "chat-a", Revision: 4 }]
    }
  });
  if (applied) order.push("projection");
  order.push("model-transport");
  assert.deepEqual(order, ["projection", "model-transport"]);
  assert.equal(ui.state.messages[0].Content, "committed");
  assert.equal(ui.state.artifacts[0].id, "artifact-1");
  console.log("PASS artifact commit: full pushed projection applies before fake model transport");
}

{
  ui.applyPushedChatState({
    type: "chatState",
    scope: "full",
    payload: {
      activeChatId: "chat-a", sessionRevision: 3,
      messages: [{ Content: "stale" }], artifacts: [], chats: [{ Id: "chat-a", Revision: 3 }]
    }
  });
  assert.equal(ui.state.messages[0].Content, "committed", "stale full projection is rejected");

  ui.applyPushedChatState({
    type: "chatState",
    scope: "full",
    payload: {
      activeChatId: "chat-b", sessionRevision: 8,
      messages: [{ Content: "other chat" }], artifacts: [], chats: [{ Id: "chat-b", Revision: 8 }]
    }
  });
  assert.equal(ui.state.activeChatId, "chat-a", "background projection cannot navigate the active chat");
  assert.equal(ui.state.messages[0].Content, "committed", "background projection cannot replace active transcript");
  console.log("PASS artifact commit: stale and background pushed state cannot replace active projection");
}

{
  const controller = fs.readFileSync(
    path.join(root, "src/RNAssistant.Office/Controller/AssistantController.ChatExecution.cs"), "utf8");
  const save = controller.indexOf("_conversationStore.Save(session);");
  const queue = controller.indexOf("chatStateChanged(ChatState(session));", save);
  const helper = controller.indexOf("_attachmentAnalysisService.EnsureAsync(", queue);
  const primary = controller.indexOf("_conversationRunService.ExecuteAsync(", helper);
  assert.ok(save >= 0 && queue > save && helper > queue && primary > helper,
    "production order is durable save -> UI queue -> helper transport -> primary transport");

  const attachments = fs.readFileSync(path.join(root, "web/js/app-attachments.js"), "utf8");
  assert.match(attachments, /draft:\s*"Не отправлено"/);
  assert.match(attachments, /preparing:\s*"Подготовка"/);
  assert.match(attachments, /committed:\s*"Оригинал"/);
  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  ["app-core.js", "app-chat-state.js", "app-messages.js", "app-attachments.js"].forEach(asset => {
    assert.ok(index.includes(asset + "?v=artifact-commit-20260831-1"), asset + " has the new cache key");
  });
  console.log("PASS artifact commit: production boundary and lifecycle labels are wired atomically");
}

console.log("OK 3/3");
