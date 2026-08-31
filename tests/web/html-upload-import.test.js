"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const source = fs.readFileSync(path.join(root, "web/js/app-html-workspace-actions.js"), "utf8");
const viewerActions = fs.readFileSync(path.join(root, "web/js/app-artifact-viewer-actions.js"), "utf8");
const context = vm.createContext({});
context.window = context;
const confirmations = [];
context.confirm = message => { confirmations.push(message); return true; };
context.prompt = () => "pages/imported.html";
context.alert = () => {};
vm.runInContext(viewerActions, context, { filename: "app-artifact-viewer-actions.js" });
vm.runInContext(source, context, { filename: "app-html-workspace-actions.js" });

(async function () {
  const uri = "rna://chat/chat-c/artifact/upload-html/revision/1";
  const calls = [];
  const renders = [];
  const logs = [];
  const state = {
    activeChatId: "chat-c",
    activeHtmlArtifactId: "html-r3",
    bridgeUnavailable: false,
    htmlWorkspaceDirty: false
  };
  let mismatchedPreview = false;
  let switchChatDuringPreview = false;
  const actions = context.RNAssistantHtmlWorkspaceActions.create({
    state,
    send: async (method, payload) => {
      calls.push({ method, payload: JSON.parse(JSON.stringify(payload)) });
      if (method === "getUploadedHtmlSourcePreview") {
        if (switchChatDuringPreview) state.activeChatId = "chat-other";
        return {
          sourceResourceUri: mismatchedPreview ? uri + "-stale" : payload.sourceResourceUri,
          text: "<main>escaped source</main>",
          returnedCharacters: 27,
          totalCharacters: 40000,
          complete: false,
          truncated: true
        };
      }
      return {
        activeChatId: "chat-c",
        importedFromResourceUri: payload.sourceResourceUri,
        importedPath: payload.targetPath,
        workspace: { files: [{ id: payload.targetPath, path: payload.targetPath, kind: "html" }] }
      };
    },
    applyWorkspaceResponse: () => true,
    log: (message, level) => logs.push({ message, level }),
    render: () => renders.push(true)
  });

  assert.equal(await actions.loadUploadedHtmlSource({ sourceResourceUri: uri }), true);
  assert.deepEqual(calls[0], {
    method: "getUploadedHtmlSourcePreview",
    payload: { chatId: "chat-c", sourceResourceUri: uri }
  });
  assert.equal(actions.uploadedHtmlPreview(uri).text, "<main>escaped source</main>");
  assert.equal(actions.uploadedHtmlPreview(uri).truncated, true);
  assert.ok(renders.length >= 2);
  console.log("PASS HTML upload import: source preview is exact, bounded and cached by URI");

  mismatchedPreview = true;
  const staleUri = uri.replace("revision/1", "revision/2");
  assert.equal(await actions.loadUploadedHtmlSource({ sourceResourceUri: staleUri }), false);
  assert.equal(actions.uploadedHtmlPreview(staleUri).status, "error");
  assert.match(actions.uploadedHtmlPreview(staleUri).message, /изменился/);
  console.log("PASS HTML upload import: mismatched preview provenance fails closed");

  switchChatDuringPreview = true;
  const switchedUri = uri.replace("revision/1", "revision/3");
  assert.equal(await actions.loadUploadedHtmlSource({ sourceResourceUri: switchedUri }), false);
  assert.equal(actions.uploadedHtmlPreview(switchedUri), null);
  state.activeChatId = "chat-c";
  switchChatDuringPreview = false;
  console.log("PASS HTML upload import: chat switch discards an unfinished preview");

  calls.length = 0;
  assert.equal(await actions.importUploadedHtml({ sourceResourceUri: uri, targetPath: "landing.html" }), true);
  assert.deepEqual(calls[0], {
    method: "importUploadedHtmlToWorkspace",
    payload: {
      chatId: "chat-c",
      sourceResourceUri: uri,
      expectedActiveHtmlArtifactId: "html-r3",
      targetPath: "pages/imported.html"
    }
  });
  assert.equal(state.htmlWorkspaceSelection.type, "file");
  assert.equal(state.htmlWorkspaceSelection.id, "pages/imported.html");
  assert.match(confirmations[0], /Оригинал останется неизменным и инертным/);
  console.log("PASS HTML upload import: explicit import sends source, head guard and target path");

  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  ["app-html-workspace.js", "app-artifact-viewer-actions.js", "app-html-workspace-actions.js",
    "app-html-workspace-artifacts.js", "app-html-workspace-editor.js"]
    .forEach(asset => assert.ok(index.includes(asset + "?v=artifact-text-20260831-1"), asset));
  assert.ok(index.includes("app-html-workspace.css?v=html-export-20260831-1"));
  console.log("PASS HTML upload import: changed UI graph uses one cache key");
  console.log("OK 5/5");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
