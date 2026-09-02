"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");

function createContext(stageFails) {
  let resolveStage;
  let rejectStage;
  const sent = [];
  const logs = [];
  const input = { value: "", focus() {} };
  const context = vm.createContext({ Promise, console, setImmediate });
  context.window = context;
  context.state = {
    activeChatId: "chat-a", draftAttachments: [], activeSends: {}, messages: [],
    modelSaving: false, modeSaving: false, reasoningSaving: false, bridgeUnavailable: false
  };
  context.$ = id => id === "chatInput" ? input : null;
  context.FileReader = class {
    readAsDataURL() {
      this.result = "data:application/pdf;base64,JVBERg==";
      this.onload();
    }
  };
  context.URL = { createObjectURL: () => "blob:test", revokeObjectURL() {} };
  context.chatDraftStore = () => (context.state.chatDrafts = context.state.chatDrafts || {});
  context.log = (message, level) => logs.push({ message, level });
  context.updateComposerInputState = () => {};
  context.renderAttachmentDrafts = () => {};
  context.renderSendControls = () => {};
  context.renderMessages = () => {};
  context.renderChatSessions = () => {};
  context.renderContextMeter = () => {};
  context.updateEstimatedContextUsage = () => {};
  context.clearSendError = () => {};
  context.hasActiveMessageEdit = () => false;
  context.pendingAgentApprovalActivity = () => null;
  context.setChatInputText = value => { input.value = value; };
  context.clearDraftAttachments = () => { context.state.draftAttachments = []; };
  context.send = (type, payload) => {
    assert.equal(type, "stageChatResource");
    assert.equal(payload.chatId, "chat-a");
    return new Promise((resolve, reject) => {
      resolveStage = resolve;
      rejectStage = reject;
    });
  };

  for (const file of ["app-attachments.js", "app-chat-run.js"]) {
    vm.runInContext(fs.readFileSync(path.join(root, "web/js", file), "utf8"), context, { filename: file });
  }
  context.sendChat = (text, attachments) => sent.push({ text, attachments });

  return {
    context,
    input,
    sent,
    logs,
    finishStage() {
      if (stageFails) rejectStage(new Error("PDF staging failed"));
      else resolveStage({ resource: { Id: "draft-pdf", FileName: "sample.pdf", Kind: "pdf", Size: 7 } });
    }
  };
}

async function settle() {
  await new Promise(resolve => setImmediate(resolve));
}

(async function () {
  {
    const fixture = createContext(false);
    const ingestion = fixture.context.ingestChatResourceFiles([
      { name: "sample.pdf", type: "application/pdf", size: 7 }
    ]);
    const submission = fixture.context.submitChatInput();
    await settle();

    assert.equal(fixture.sent.length, 0, "model dispatch waits while the PDF draft is staging");
    assert.equal(fixture.context.state.pendingChatSubmitId, "chat-a");
    fixture.finishStage();
    assert.equal(await ingestion, true);
    await submission;

    assert.equal(fixture.sent.length, 1);
    assert.equal(fixture.sent[0].text, "");
    assert.deepEqual(fixture.sent[0].attachments.map(item => item.Id), ["draft-pdf"]);
    console.log("PASS attachment staging: immediate send waits and includes the committed draft id");
  }

  {
    const fixture = createContext(true);
    fixture.input.value = "Прочитай PDF";
    const ingestion = fixture.context.ingestChatResourceFiles([
      { name: "broken.pdf", type: "application/pdf", size: 7 }
    ]);
    const submission = fixture.context.submitChatInput();
    await settle();
    fixture.finishStage();

    assert.equal(await ingestion, false);
    await submission;
    assert.equal(fixture.sent.length, 0, "failed staging never falls through to a text-only model dispatch");
    assert.equal(fixture.input.value, "Прочитай PDF", "composer text remains available for retry");
    assert.match(fixture.logs[0].message, /staging failed/);
    console.log("PASS attachment staging: failure preserves the composer and prevents partial dispatch");
  }

  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  ["app-attachments.js", "app-chat-composer.js", "app-chat-run.js"].forEach(asset => {
    assert.ok(index.includes(asset + "?v=attachment-stage-20260902-1"), asset + " has the staging barrier cache key");
  });
  console.log("PASS attachment staging: changed UI modules use one cache key");
  console.log("OK 3/3");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
