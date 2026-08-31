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

console.log("OK 2/2");
