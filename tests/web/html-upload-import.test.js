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
  let readUri;
  const text = "<main>escaped source</main>";
  const fetches = [];
  const leaseId = "a".repeat(64);
  const actions = context.RNAssistantHtmlWorkspaceActions.create({
    state,
    send: async (method, payload) => {
      calls.push({ method, payload: JSON.parse(JSON.stringify(payload)) });
      if (method === "readArtifactViewerPage") {
        readUri = payload.resourceUri;
        if (switchChatDuringPreview) state.activeChatId = "chat-other";
        return {
          resourceUri: mismatchedPreview ? uri + "-stale" : readUri,
          viewerKind: "text", mimeType: "text/html", contentSha256: "b".repeat(64), offset: 0,
          returnedCharacters: text.length, sourceComplete: true, fullReadAllowed: true,
          maximumDocumentCharacters: 512000, nextCursor: "exact-next",
          totalCharacters: 40000,
          complete: false,
          truncated: true,
          data: { leaseId, url: "https://rnassistant.local-resource/v1/" + leaseId,
            descriptor: { reference: { uri: readUri, revision: readUri.split("/").at(-1) } },
            view: "text", maxBatchItems: 32000, maxBatchBytes: 128000,
            expiresUtc: new Date(Date.now() + 600000).toISOString() }
        };
      }
      if (method === "resourceDataClose") return { closed: true };
      return {
        activeChatId: "chat-c",
        importedFromResourceUri: payload.sourceResourceUri,
        importedPath: payload.targetPath,
        workspace: { files: [{ id: payload.targetPath, path: payload.targetPath, kind: "html" }] }
      };
    },
    fetch: async (url, options) => {
      fetches.push({ url, options });
      return { ok: true, text: async () => JSON.stringify({
        resource: { uri: readUri, revision: readUri.split("/").at(-1) },
        view: "text", offset: 0, nextOffset: text.length, text
      }) };
    },
    applyWorkspaceResponse: () => true,
    log: (message, level) => logs.push({ message, level }),
    render: () => renders.push(true)
  });

  assert.equal(await actions.loadArtifactViewer({ resourceUri: uri }), true);
  assert.deepEqual(calls[0], {
    method: "readArtifactViewerPage",
    payload: { chatId: "chat-c", resourceUri: uri, cursor: null }
  });
  assert.equal(actions.artifactViewerState(uri).pages[0].text, text);
  assert.equal(actions.artifactViewerState(uri).pages[0].truncated, true);
  assert.equal(fetches[0].url, "https://rnassistant.local-resource/v1/" + leaseId + "?offset=0&limit=32000");
  assert.equal(calls[1].method, "resourceDataClose");
  assert.equal(calls[1].payload.chatId, "chat-c");
  assert.equal(state.uploadedHtmlSourcePreviews, undefined);
  assert.ok(renders.length >= 1);
  console.log("PASS HTML upload import: shared exact viewer pulls bounded data and closes its lease");

  mismatchedPreview = true;
  const staleUri = uri.replace("revision/1", "revision/2");
  assert.equal(await actions.loadArtifactViewer({ resourceUri: staleUri }), false);
  assert.equal(actions.artifactViewerState(staleUri).status, "error");
  assert.match(actions.artifactViewerState(staleUri).message, /inconsistent/);
  assert.equal(calls.at(-1).method, "resourceDataClose");
  console.log("PASS HTML upload import: mismatched preview provenance fails closed");

  switchChatDuringPreview = true;
  const switchedUri = uri.replace("revision/1", "revision/3");
  const fetchedBeforeSwitch = fetches.length;
  assert.equal(await actions.loadArtifactViewer({ resourceUri: switchedUri }), false);
  assert.equal(actions.artifactViewerState(switchedUri), null);
  assert.equal(fetches.length, fetchedBeforeSwitch);
  assert.equal(calls.at(-1).method, "resourceDataClose");
  assert.equal(calls.at(-1).payload.chatId, "chat-c", "late lease closes in its original owner");
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

  assert.doesNotMatch(source, /getUploadedHtmlSourcePreview|loadUploadedHtmlSource|uploadedHtmlSourcePreviews/);

  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  assert.ok(index.includes("app-artifact-viewer-actions.js?v=artifact-gallery-20260902-1"));
  assert.ok(index.includes("app-html-workspace-actions.js?v=html-source-resource-20260905-1"));
  assert.ok(index.includes("app-html-workspace-editor.js?v=ui-lazy-20260903-1"));
  assert.ok(index.includes(
    "app-html-workspace-artifacts.js?v=html-source-resource-20260905-1"));
  assert.ok(index.includes("app-html-workspace.js?v=html-source-resource-20260905-1"));
  assert.ok(index.includes("app-html-workspace.css?v=html-export-20260831-1"));
  console.log("PASS HTML upload import: changed UI graph uses one cache key");
  console.log("OK 5/5");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
