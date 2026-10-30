"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const context = vm.createContext({ state: {}, RNAssistantAgentApproval: { create: () => ({}) } });
context.window = context;
for (const name of ["app-agent-model.js", "app-agent.js"])
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", name), "utf8"),
    context, { filename: name });

const activities = [];
const notice = { Kind: "notice", RunId: "run", Status: "warning",
  Title: "Check source", ResultMessage: "Unknown" };
context.recordActivityTimeline(activities, notice);
context.recordActivityTimeline(activities, { Kind: "tool", RunId: "run", StepId: "step-2",
  ToolCallId: "call-2", ToolId: "core.read", Status: "completed" });
context.recordActivityTimeline(activities, notice);
assert.equal(activities[0].Kind, "notice", "refreshing a notice preserves its original position");

const steps = context.groupAgentRunSteps([
  { activity: { Kind: "tool", StepId: "step-1", ToolCallId: "call-1",
    ToolId: "core.read", Status: "completed" } },
  { activity: { Kind: "step", StepId: "step-1", StepMessage: "Read the current source." } }
]);
assert.equal(steps.length, 1);
assert.equal(steps[0].message, "Read the current source.");
assert.ok(steps[0].marker, "late marker stays attached to its step");
console.log("PASS agent timeline: notices and late markers retain chronological steps");

const live = [];
const thinking = { Kind: "notice", RunId: "run", Status: "running", Title: "Думаю…" };
context.recordActivityTimeline(live, thinking);
context.recordActivityTimeline(live, { Kind: "step", RunId: "run", StepId: "read", Status: "running", StepMessage: "Читаю модули" });
context.recordActivityTimeline(live, { Kind: "tool", RunId: "run", StepId: "read", ToolCallId: "read-1", ToolId: "core.read", Status: "completed" });
context.recordActivityTimeline(live, thinking);
context.recordActivityTimeline(live, { Kind: "step", RunId: "run", StepId: "reflection", Status: "completed", Title: "Промежуточное решение", StepMessage: "Нашёл ошибки. Исправляю." });
context.recordActivityTimeline(live, thinking);
const groupedLive = context.groupAgentRunUpdates(context.groupAgentRunSteps(
  context.collectVisibleAgentTimelineItems(live.map(activity => ({ activity })))));
assert.equal(live[live.length - 1].Title, "Думаю…");
assert.equal(live.filter(item => item.Kind === "notice").length, 1);
assert.equal(groupedLive[groupedLive.length - 1].ambient.activity.Title, "Думаю…",
  "a new model request remains visible after a read batch and a reflection");
const replayed = context.collapseAgentTimelineItems([{ activity: thinking },
  { activity: live[1] }, { activity: thinking }]);
assert.equal(replayed[replayed.length - 1].activity.Title, "Думаю…",
  "merging saved and live activity keeps the latest wait at the tail");
console.log("PASS agent timeline: repeated model waits follow the latest accepted step");
