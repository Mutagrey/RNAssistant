"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const source = fs.readFileSync(path.join(root, "web/js/app-artifacts.js"), "utf8");
const context = vm.createContext({});
context.window = context;
context.state = {
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
  }
};
vm.runInContext(source, context, { filename: "app-artifacts.js" });

{
  const heads = Array.from(context.artifactResourceHeads());
  assert.deepEqual(heads.map(item => item.id), ["plan-r2", "upload", "chart-1", "chart-2"]);
  assert.equal(context.RNAssistantArtifactVisuals.category(heads[0]), "authored");
  assert.equal(context.RNAssistantArtifactVisuals.category(heads[1]), "files");
  assert.equal(context.RNAssistantArtifactVisuals.category(heads[2]), "generated");
  assert.equal(context.RNAssistantArtifactVisuals.meta(heads[0]), "Готов · v2");
  assert.equal(context.RNAssistantArtifactVisuals.meta(heads[1]), "Файл · Оригинал");
  assert.equal(context.RNAssistantArtifactVisuals.meta(heads[3]), "Диаграмма");
  console.log("PASS artifact library: UI consumes server-owned classes, order and labels");
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
  assert.match(htmlController, /ArtifactLibrary\s*=\s*ArtifactLibraryProjectionService\.Project\(session\)/);
  assert.match(htmlController, /Artifacts\s*=\s*ChatArtifactDto\.From\(session\)/);
  assert.match(htmlUi, /response\.artifactLibrary/);
  assert.match(htmlUi, /RNAssistantRunViewState\.accept/);
  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  assert.ok(index.includes("app-core.js?v=bridge-bootstrap-20260831-1"), "core bridge bootstrap has the bridge cache key");
  assert.ok(index.includes("app-chat-state.js?v=tool-contract-20260901-1"), "chat state has the typed Tool contract cache key");
  assert.ok(index.includes("app-chat-session.js?v=tool-contract-20260901-1"), "chat session has the typed Tool contract cache key");
  assert.ok(index.includes("app-artifacts.js?v=plan-tombstone-20260831-1"), "artifact cards have the removal cache key");
  assert.ok(index.includes("app-html-workspace.js?v=resource-intent-20260902-1"), "artifact actions have the resource-intent cache key");
  assert.ok(index.includes("app-html-workspace-actions.js?v=artifact-text-20260831-1"), "artifact tool calls have the current cache key");
  assert.ok(index.includes("app-artifact-viewer-actions.js?v=artifact-text-20260831-1"), "artifact paging owner has the current cache key");
  assert.ok(index.includes("app-html-workspace-artifacts.js?v=artifact-text-20260831-1"), "artifact detail has the current cache key");
  assert.ok(index.includes("app-html-workspace-editor.js?v=artifact-text-20260831-1"), "artifact action bridge has the current cache key");
  console.log("PASS artifact library: client lineage inference and Plan JSON label are removed");
}

console.log("OK 3/3");
