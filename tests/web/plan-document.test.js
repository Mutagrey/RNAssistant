"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const source = fs.readFileSync(path.join(root, "web/js/app-html-workspace-artifacts.js"), "utf8");
const context = vm.createContext({});
context.window = context;
vm.runInContext(source, context, { filename: "app-html-workspace-artifacts.js" });

{
  const exact = "  \n# Exact plan\n\nKeep trailing spaces.  \n\n";
  const draft = context.RNAssistantHtmlWorkspaceArtifacts.validatePlanDraft({
    id: "plan-r2",
    title: "Plan",
    inlineText: exact,
    metadataJson: JSON.stringify({ planId: "plan" })
  });
  assert.equal(draft.markdown, exact);
  assert.equal(draft.expectedRevisionArtifactId, "plan-r2");
  console.log("PASS plan document: UI preserves the complete Markdown revision");
}

{
  assert.throws(() => context.RNAssistantHtmlWorkspaceArtifacts.validatePlanDraft({
    id: "plan-r2",
    inlineText: " \n\t "
  }), /1 до 32000/);
  console.log("PASS plan document: UI rejects whitespace-only Markdown without normalizing valid content");
}

{
  const artifactSource = fs.readFileSync(path.join(root, "web/js/app-artifacts.js"), "utf8");
  const removedContext = vm.createContext({});
  removedContext.window = removedContext;
  removedContext.state = {
    artifacts: [],
    artifactLibrary: {
      heads: [],
      removedResourceUris: ["rna://chat/c/artifact/plan-r1/revision/1"]
    }
  };
  vm.runInContext(artifactSource, removedContext, { filename: "app-artifacts.js" });
  const removed = {
    id: "plan-r1",
    kind: "plan_document",
    title: "Plan",
    revision: 1,
    resourceUri: "rna://chat/c/artifact/plan-r1/revision/1"
  };
  assert.equal(removedContext.RNAssistantArtifactVisuals.removed(removed), true);
  assert.equal(removedContext.RNAssistantArtifactVisuals.meta(removed), "Ресурс удалён");
  assert.match(artifactSource, /card\.disabled = true/);
  console.log("PASS plan document: pinned removed revision renders a stable disabled placeholder");
}

{
  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  const actions = fs.readFileSync(path.join(root, "web/js/app-html-workspace-actions.js"), "utf8");
  const workspace = fs.readFileSync(path.join(root, "web/js/app-html-workspace.js"), "utf8");
  ["app-artifacts.js", "app-html-workspace.js", "app-html-workspace-actions.js"].forEach(asset => {
    assert.ok(index.includes(asset + "?v=plan-tombstone-20260831-1"), asset + " has the Plan tombstone cache key");
  });
  assert.match(workspace, /result\.expectedRevisionArtifactId = artifactId\(selected\.item\)/);
  assert.match(actions, /expectedRevisionArtifactId: selected\.expectedRevisionArtifactId/);
  console.log("PASS plan document: removal projections and guarded UI calls are cache-busted together");
}

console.log("OK 4/4");
