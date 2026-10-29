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
