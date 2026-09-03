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
  const detail = fs.readFileSync(path.join(root, "web/js/app-html-workspace-artifacts.js"), "utf8");
  const editor = fs.readFileSync(path.join(root, "web/js/app-html-workspace-editor.js"), "utf8");
  const questions = fs.readFileSync(path.join(root, "web/js/app-agent-activity.js"), "utf8");
  const taskList = fs.readFileSync(path.join(root, "web/js/app-task-list.js"), "utf8");
  assert.ok(index.includes("app-artifacts.js?v=chart-resource-card-20260903-1"), "artifact cards use the chart resource cache key");
  assert.ok(index.includes("app-artifact-viewer-actions.js?v=artifact-gallery-20260902-1"),
    "artifact actions have the current artifact cache key");
  assert.ok(index.includes("app-html-workspace-artifacts.js?v=artifact-chart-preview-20260903-1"),
    "artifact detail has the current artifact cache key");
  assert.ok(index.includes("app-html-workspace-editor.js?v=artifact-gallery-20260902-1"));
  ["app-task-list.js", "app-agent-activity.js"].forEach(asset => {
    assert.ok(index.includes(asset + "?v=planning-intents-20260902-1"), asset + " has the current planning-intent cache key");
  });
  assert.ok(index.includes("app-html-workspace-actions.js?v=artifact-gallery-20260902-1"),
    "app-html-workspace-actions.js has the current preview cache key");
  assert.ok(index.includes("app-html-workspace.js?v=html-echarts-20260903-1"),
    "app-html-workspace.js has the current preview cache key");
  assert.ok(index.includes("app-html-workspace.css?v=html-export-20260831-1"), "Plan/HTML actions have the matching CSS cache key");
  assert.match(workspace, /switchChatMode:\s*function\s*\(mode\)/);
  assert.doesNotMatch(workspace, /switchChatMode:\s*saveChatMode/);
  assert.match(workspace, /result\.expectedRevisionArtifactId = artifactId\(selected\.item\)/);
  assert.match(actions, /toolId: "common\.plan_doc_save"/);
  assert.doesNotMatch(actions, /arguments:\s*\{[^}]*expectedRevisionArtifactId/);
  assert.match(detail, /expectedRevisionArtifactId: headArtifactId/);
  assert.match(detail, /sourceRevisionArtifactId: revisionArtifactId/);
  assert.match(editor, /renderDetail\(detail, selected, selectedEditorValue\(selected\), options\.artifactActions\)/);
  assert.match(workspace, /Выполни утверждённый активный план/);
  assert.doesNotMatch(workspace, /input\.value\s*=.*revisionUri/);
  assert.match(questions, /return \{ question: question\.prompt.*selections: selected/);
  assert.doesNotMatch(questions, /questionId|optionIds|JSON\.stringify\(\{ questionSetId/);
  assert.match(taskList, /toolId === "common\.task_list_set"/);
  assert.doesNotMatch(taskList, /common\.task_list_(?:create|update|close)/);
  console.log("PASS plan document: removal projections and guarded UI calls are cache-busted together");
}

{
  const index = fs.readFileSync(path.join(root, "web/index.html"), "utf8");
  const vba = fs.readFileSync(path.join(root, "web/js/app-vba.js"), "utf8");
  assert.ok(index.includes("app-vba.js?v=resource-intent-20260902-1"),
    "VBA chat handoff has the resource-intent cache key");
  assert.match(vba, /common\.resources_find со scope=vba/);
  assert.doesNotMatch(vba, /common\.resources_(?:list|resolve|search)|provider=vba|kind=vba-component/);
  console.log("PASS plan document: VBA review handoff uses semantic resource intent");
}

(async function () {
  const actionsSource = fs.readFileSync(path.join(root, "web/js/app-html-workspace-actions.js"), "utf8");
  const actionContext = vm.createContext({});
  actionContext.window = actionContext;
  const confirmations = [];
  actionContext.confirm = message => { confirmations.push(message); return true; };
  actionContext.alert = () => {};
  vm.runInContext(fs.readFileSync(path.join(root, "web/js/app-artifact-viewer-actions.js"), "utf8"), actionContext,
    { filename: "app-artifact-viewer-actions.js" });
  vm.runInContext(actionsSource, actionContext, { filename: "app-html-workspace-actions.js" });

  const uri = "rna://chat/c/artifact/plan-r2/revision/2";
  const calls = [];
  const logs = [];
  const handoffs = [];
  const modes = [];
  const state = {
    activeChatId: "chat-c",
    activePlanDocumentArtifactId: "plan-r2",
    activeTaskListArtifactId: "",
    bridgeUnavailable: false,
    htmlWorkspaceDirty: false,
    artifacts: [{
      id: "plan-r2",
      kind: "plan_document",
      resourceUri: uri,
      metadataJson: JSON.stringify({ planId: "plan", status: "ready" })
    }]
  };
  const send = async (method, payload) => {
    calls.push({ method, payload: JSON.parse(JSON.stringify(payload || {})) });
    if (method === "selectChat") return { activeChatId: "chat-c" };
    if (payload.dryRun) {
      return { success: true, dataJson: JSON.stringify({ removedRevisions: 3, referencingMessageIds: ["message-a", "message-b"] }) };
    }
    return { success: true, dataJson: "{}" };
  };
  const actions = actionContext.RNAssistantHtmlWorkspaceActions.create({
    state,
    send,
    log: message => logs.push(message),
    applyPlanRefresh: () => true,
    switchChatMode: async mode => { modes.push(mode); return true; },
    submitPlanHandoff: exactUri => { handoffs.push(exactUri); return true; }
  });

  calls.length = 0;
  confirmations.length = 0;
  assert.equal(await actions.restorePlanRevision({
    planId: "plan",
    expectedRevisionArtifactId: "plan-r2",
    sourceRevisionArtifactId: "plan-r1",
    revision: 1
  }), true);
  assert.equal(calls[0].payload.toolId, "common.plan_doc_restore");
  assert.deepEqual(calls[0].payload.arguments, { version: 1 });
  assert.equal(calls[0].payload.dryRun, false);
  assert.match(confirmations[0], /v1.*новую версию/);
  console.log("PASS plan document: history restore sends only the semantic version");

  calls.length = 0;
  confirmations.length = 0;
  assert.equal(await actions.deleteSelection({
    type: "plan",
    label: "Plan",
    planId: "plan",
    expectedRevisionArtifactId: "plan-r2"
  }), true);
  assert.equal(calls[0].payload.dryRun, true);
  assert.equal(calls[1].payload.dryRun, false);
  assert.deepEqual(calls[0].payload.arguments, {});
  assert.deepEqual(calls[1].payload.arguments, calls[0].payload.arguments);
  assert.match(confirmations[0], /ревизия удаления/);
  assert.match(confirmations[0], /message-a/);
  assert.match(confirmations[0], /message-b/);
  console.log("PASS plan document: removal keeps runtime guards out of UI tool arguments");

  calls.length = 0;
  modes.length = 0;
  handoffs.length = 0;
  assert.equal(await actions.handoffPlan({
    planId: "plan",
    expectedRevisionArtifactId: "plan-r2",
    revisionUri: uri
  }), true);
  assert.deepEqual(modes, ["agent"]);
  assert.deepEqual(handoffs, [uri]);
  assert.equal(await actions.handoffPlan({
    planId: "plan",
    expectedRevisionArtifactId: "plan-r2",
    revisionUri: uri.toUpperCase()
  }), false);
  assert.deepEqual(handoffs, [uri]);
  console.log("PASS plan document: ready handoff accepts only the exact active pinned URI");

  console.log("OK 8/8");
}()).catch(error => {
  console.error(error.stack || error);
  process.exitCode = 1;
});
