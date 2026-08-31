"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const context = vm.createContext({});
context.window = context;
context.alert = () => {};
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-html-workspace-preview.js"), "utf8"), context,
  { filename: "app-html-workspace-preview.js" });
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-artifact-viewer-actions.js"), "utf8"), context,
  { filename: "app-artifact-viewer-actions.js" });
vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-html-workspace-actions.js"), "utf8"), context,
  { filename: "app-html-workspace-actions.js" });

(async function () {
  const exactJson = "{\"duplicate\":1,\"duplicate\":2,\"large\":9007199254740993,\"html\":\"</script>\"}";
  const assembled = context.RNAssistantHtmlWorkspacePreview.build({
    activeFileId: "index.html",
    files: [{ id: "index.html", path: "index.html", kind: "html", content: "<main>export</main>" }],
    dataSources: [{
      id: "bound", name: "bound", json: exactJson,
      binding: {
        toolId: "excel.inspect",
        status: "ready",
        payloadCompleteness: "truncated",
        contentSha256: "b".repeat(64)
      }
    }],
    hostBridge: false
  });
  const rawMatch = assembled.match(/var raw=(.*?),data=Object\.create\(null\),meta=/);
  assert.ok(rawMatch, "raw payload map is embedded");
  const raw = JSON.parse(rawMatch[1]);
  assert.equal(raw.bound, exactJson);
  assert.ok(!assembled.includes("9007199254740992"), "large integer lexeme is not rounded during assembly");
  console.log("PASS HTML export: standalone assembly preserves exact raw JSON lexemes");

  const metaMatch = assembled.match(/,meta=(.*?);Object\.keys\(raw\)/);
  assert.ok(metaMatch, "binding metadata is embedded");
  const metadata = JSON.parse(metaMatch[1]);
  assert.equal(metadata.bound.payloadCompleteness, "truncated");
  assert.equal(metadata.bound.jsonCharacters, exactJson.length);
  assert.equal(metadata.bound.contentSha256, "b".repeat(64));
  assert.match(assembled, /RNAssistantDataRaw=raw/);
  console.log("PASS HTML export: binding completeness and exact raw access remain explicit");

  const calls = [];
  const downloads = [];
  const logs = [];
  let invalidEvidence = false;
  const state = {
    activeChatId: "chat-export",
    activeHtmlArtifactId: "html-r3",
    bridgeUnavailable: false,
    htmlWorkspaceDirty: false,
    htmlWorkspace: {}
  };
  const actions = context.RNAssistantHtmlWorkspaceActions.create({
    state,
    send: async (method, payload) => {
      calls.push({ method, payload: JSON.parse(JSON.stringify(payload)) });
      return {
        activeChatId: "chat-export",
        activeHtmlArtifactId: "html-r4",
        exportRevisionArtifactId: "html-r4",
        exportResourceUri: "rna://chat/chat-export/artifact/html-r4/revision/4",
        exportContentSha256: invalidEvidence ? "bad" : "a".repeat(64),
        workspace: {
          activeFileId: "index.html",
          files: [{ id: "index.html", path: "index.html", kind: "html", content: "<main>exact</main>" }],
          dataSources: [{ id: "bound", name: "bound", json: exactJson }]
        }
      };
    },
    applyWorkspaceResponse: response => {
      state.activeHtmlArtifactId = response.activeHtmlArtifactId;
      state.htmlWorkspace = response.workspace;
      return true;
    },
    downloadHtmlExport: value => downloads.push(value),
    log: (message, level) => logs.push({ message, level }),
    render: () => {}
  });

  assert.equal(await actions.exportWorkspace(), true);
  assert.equal(calls[0].method, "prepareHtmlWorkspaceExport");
  assert.equal(calls[0].payload.chatId, "chat-export");
  assert.equal(calls[0].payload.expectedActiveHtmlArtifactId, "html-r3");
  assert.equal(downloads[0].revisionArtifactId, "html-r4");
  assert.equal(downloads[0].workspace.dataSources[0].json, exactJson);
  console.log("PASS HTML export: download uses the guarded server checkpoint payload");

  state.htmlWorkspaceDirty = true;
  const callCount = calls.length;
  assert.equal(await actions.exportWorkspace(), false);
  assert.equal(calls.length, callCount);
  console.log("PASS HTML export: unsaved editor state blocks stale export");

  state.htmlWorkspaceDirty = false;
  invalidEvidence = true;
  const downloadCount = downloads.length;
  assert.equal(await actions.exportWorkspace(), false);
  assert.equal(downloads.length, downloadCount);
  assert.match(logs.at(-1).message, /incomplete revision evidence/);
  console.log("PASS HTML export: incomplete revision evidence fails closed before download");

  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  ["app-html-workspace-preview.js", "app-html-workspace.js", "app-artifact-viewer-actions.js", "app-html-workspace-actions.js",
    "app-html-workspace-artifacts.js", "app-html-workspace-editor.js"]
    .forEach(asset => assert.ok(index.includes(asset + "?v=" +
      (asset === "app-html-workspace-preview.js" ? "html-export-20260831-1" : "artifact-text-20260831-1")), asset));
  assert.ok(index.includes("app-html-workspace.css?v=html-export-20260831-1"));
  console.log("PASS HTML export: changed UI graph uses one cache key");

  console.log("OK 6/6");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
