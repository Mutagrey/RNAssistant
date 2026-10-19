"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const source = fs.readFileSync(path.join(root, "web/js/app-artifacts.js"), "utf8");
const context = vm.createContext({});
context.window = context;
context.document = { getElementById() { return null; } };
context.$ = () => null;
context.switchTab = () => {};
context.state = {
  activeChatId: "c",
  activePlanDocumentArtifactId: "plan-r2",
  activeHtmlArtifactId: "html-left",
  artifacts: [
    { id: "plan-r1", kind: "plan_document", title: "Plan", revision: 1 },
    { id: "plan-r2", kind: "plan_document", title: "Plan", revision: 2 },
    { id: "upload", kind: "attachment", title: "notes.md", revision: 9, contentByteLength: 100 },
    { id: "chart-1", kind: "chart", title: "Chart 1", revision: 1 },
    { id: "chart-2", kind: "chart", title: "Chart 2", revision: 2, parentArtifactId: "chart-1" }
  ],
  artifactLibrary: {
    sessionRevision: 12,
    heads: [
      {
        artifactId: "plan-r2", logicalId: "plan", resourceClass: "versioned_document",
        group: "authored_documents", displayKind: "plan", versionLabel: "v2", status: "ready",
        history: [
          { artifactId: "plan-r2", revision: 2, relation: "head", resourceUri: "rna://chat/c/artifact/plan-r2/revision/2" },
          { artifactId: "plan-r1", revision: 1, relation: "ancestor", resourceUri: "rna://chat/c/artifact/plan-r1/revision/1" }
        ]
      },
      {
        artifactId: "upload", resourceClass: "immutable_original", group: "files_media",
        displayKind: "file", versionLabel: "Original",
        history: [{ artifactId: "upload", revision: 9, relation: "head", resourceUri: "rna://chat/c/artifact/upload/revision/9" }]
      },
      {
        artifactId: "chart-1", resourceClass: "immutable_snapshot", group: "generated_snapshots",
        displayKind: "chart", history: [{ artifactId: "chart-1", revision: 1 }]
      },
      {
        artifactId: "chart-2", resourceClass: "immutable_snapshot", group: "generated_snapshots",
        displayKind: "chart", history: [{ artifactId: "chart-2", revision: 2 }]
      }
    ]
  },
  htmlWorkspace: { files: [], dataSources: [], history: [], redoHistory: [], redoBranches: [], recovery: {} },
  htmlWorkspaceDirty: false,
  htmlWorkspaceSelection: { type: "plan", id: "plan-r1" }
};
context.state.artifacts[0].libraryHead = context.state.artifactLibrary.heads[0];
vm.runInContext(source, context, { filename: "app-artifacts.js" });

{
  const heads = Array.from(context.artifactResourceHeads());
  assert.deepEqual(heads.map(item => item.id), ["plan-r2", "upload", "chart-1", "chart-2"]);
  assert.equal(context.RNAssistantArtifactVisuals.category(heads[0]), "authored");
  assert.equal(context.RNAssistantArtifactVisuals.category(heads[1]), "files");
  assert.equal(context.RNAssistantArtifactVisuals.category(heads[2]), "generated");
  assert.equal(context.RNAssistantArtifactVisuals.meta(heads[0]), "Только этот чат · Готов · v2");
  assert.equal(context.RNAssistantArtifactVisuals.meta(heads[1]), "Только этот чат · Файл · Оригинал");
  assert.equal(context.RNAssistantArtifactVisuals.meta(heads[3]), "Только этот чат · Диаграмма");
  console.log("PASS artifact library: UI consumes server-owned classes, order and labels");
}

{
  const modelSource = fs.readFileSync(path.join(root, "web/js/app-html-workspace-model.js"), "utf8");
  vm.runInContext(modelSource, context, { filename: "app-html-workspace-model.js" });
  const model = context.RNAssistantHtmlWorkspaceModel.create(context.state);
  assert.equal(model.refreshLibraryHeadSelection(), true);
  assert.deepEqual(
    { type: context.state.htmlWorkspaceSelection.type, id: context.state.htmlWorkspaceSelection.id },
    { type: "plan", id: "plan-r2" },
    "entering the library rebases a stale selection to the projected head");
  context.state.htmlWorkspaceSelection = { type: "plan", id: "plan-r1" };
  context.state.htmlWorkspaceDirty = true;
  assert.equal(model.refreshLibraryHeadSelection(), false);
  assert.equal(context.state.htmlWorkspaceSelection.id, "plan-r1", "unsaved edits keep their exact selection");
  context.state.htmlWorkspaceDirty = false;
  console.log("PASS artifact library: tab refresh selects the current server-owned head");
}

{
  const exact = Array.from(context.artifactResourceHeads([context.state.artifacts[0], context.state.artifacts[1]]));
  assert.deepEqual(exact.map(item => item.id), ["plan-r1", "plan-r2"], "exact message/run resources are not redirected to a head");
  assert.equal(context.RNAssistantArtifactVisuals.versionLabel(exact[0]), "v1");
  assert.equal(context.RNAssistantArtifactVisuals.libraryHead(exact[0]).artifactId, "plan-r2");
  console.log("PASS artifact library: pinned revisions stay exact while sharing server history");
}

{
  const editor = fs.readFileSync(path.join(root, "web/js/app-html-workspace-editor.js"), "utf8");
  const detail = fs.readFileSync(path.join(root, "web/js/app-html-workspace-artifacts.js"), "utf8");
  assert.doesNotMatch(source, /artifactLineageRoot|activePlanArtifactId/);
  assert.doesNotMatch(editor, /План · JSON|textContent = isPlan \? "JSON"/);
  assert.match(editor, /План · Markdown/);
  assert.match(editor, /isPlan \? "Источник"/);
  assert.match(detail, /История · /);
  assert.match(detail, /ParentResourceUri/);
  const htmlController = fs.readFileSync(path.join(root, "src/RNAssistant.Office/Controller/AssistantController.HtmlWorkspace.cs"), "utf8");
  const htmlUi = fs.readFileSync(path.join(root, "web/js/app-html-workspace.js"), "utf8");
  assert.match(htmlController, /ArtifactLibrary\s*=\s*artifactPresentation\.Library/);
  assert.match(htmlController, /Artifacts\s*=\s*artifactPresentation\.Artifacts/);
  assert.match(htmlUi, /response\.artifactLibrary/);
  assert.match(htmlUi, /RNAssistantRunViewState\.accept/);
  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  assert.match(index, /app-core\.js\?v=[^"\s]+/, "core has the current cache key");
  assert.match(index, /app-chat-state\.js\?v=[^"\s]+/, "chat state has the current cache key");
  assert.match(index, /app-chat-session\.js\?v=[^"\s]+/, "chat session has the current cache key");
  assert.match(index, /app-artifacts\.js\?v=[^"\s]+/, "artifact cards have the lazy library cache key");
  assert.match(index, /app-html-workspace-model\.js\?v=[^"\s]+/, "artifact selection model has the gallery cache key");
  assert.match(index, /app-html-workspace\.js\?v=[^"\s]+/, "artifact actions have the current resource cache key");
  assert.match(index, /app-html-workspace-actions\.js\?v=[^"\s]+/, "artifact tool calls have the current resource cache key");
  assert.match(index, /app-artifact-viewer-actions\.js\?v=[^"\s]+/, "artifact paging owner has the current resource cache key");
  assert.match(index, /app-html-workspace-artifacts\.js\?v=[^"\s]+/, "artifact detail has the lazy history cache key");
  assert.match(index, /app-chart-artifacts\.js\?v=[^"\s]+/, "chart artifact renderer has the current UI cache key");
  assert.match(index, /app-html-workspace-editor\.js\?v=[^"\s]+/, "artifact action bridge has the lazy UI cache key");
  assert.match(source, /function artifactProjection\(\)/, "artifact UI builds a transient projection index");
  assert.doesNotMatch(source, /artifactLibraryHeads\(\)\.filter/, "head lookup does not scan all heads per card");
  assert.doesNotMatch(source, /filtered\.filter/, "resource navigation groups filtered rows in one pass");
  console.log("PASS artifact library: client lineage inference and Plan JSON label are removed");
}

{
  const first = {
    id: "image-1", kind: "image", title: "One.png", revision: 1, mimeType: "image/png",
    resourceUri: "rna://chat/c/artifact/image-1/revision/1", metadataJson: "{\"attachmentId\":\"a-1\"}"
  };
  const second = {
    id: "image-2", kind: "image", title: "Two.png", revision: 1, mimeType: "image/png",
    resourceUri: "rna://chat/c/artifact/image-2/revision/1", metadataJson: "{\"attachmentId\":\"a-2\"}"
  };
  context.state.artifacts.push(first, second);
  context.state.artifactLibrary.heads.push(
    { artifactId: "image-1", resourceClass: "immutable_original", group: "files_media", displayKind: "image",
      history: [{ artifactId: "image-1", revision: 1, resourceUri: first.resourceUri }] },
    { artifactId: "image-2", resourceClass: "immutable_original", group: "files_media", displayKind: "image",
      history: [{ artifactId: "image-2", revision: 1, resourceUri: second.resourceUri }] }
  );
  const files = Array.from(context.artifactCollectionItems("artifact-files"));
  assert.deepEqual(files.map(item => item.id), ["image-1", "upload", "image-2"].sort((left, right) => {
    const titles = { "image-1": "One.png", upload: "notes.md", "image-2": "Two.png" };
    return titles[left].localeCompare(titles[right]);
  }));
  context.openArtifactResource(first, [first, second]);
  assert.equal(context.state.artifactImageGalleryContext.items.length, 2);
  assert.equal(context.state.htmlWorkspaceSelection.id, "image-1");
  assert.equal(context.selectArtifactImageGalleryItem(1), true);
  assert.equal(context.state.htmlWorkspaceSelection.id, "image-2");
  const attachmentIds = context.messageImageAttachmentIds({
    ArtifactIds: [first.id, second.id]
  });
  assert.equal(attachmentIds["a-1"], true);
  assert.equal(attachmentIds["a-2"], true);
  console.log("PASS artifact library: collection and chat image contexts stay exact and ephemeral");
}

console.log("OK 5/5");
