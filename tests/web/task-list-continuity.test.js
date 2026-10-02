"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

function element() {
  const classes = new Set();
  return {
    children: [], textContent: "", className: "",
    classList: { add: name => classes.add(name), remove: name => classes.delete(name), contains: name => classes.has(name) },
    appendChild(child) { this.children.push(child); },
    replaceChildren(...children) { this.children = children; },
    setAttribute() {}, addEventListener() {}
  };
}
const dock = element();
const context = vm.createContext({
  state: { activeTaskListArtifactId: "r1", artifacts: [], liveAgentRun: [], agentPlanExpanded: {} },
  document: { createElement: element, addEventListener() {} },
  $: id => id === "agentPlanDock" ? dock : null,
  activityToolId: a => a.toolId, activityStatus: a => a.status,
  activityDataJson: a => a.dataJson, activityChildren: () => []
});
context.window = context;
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-task-list.js"), "utf8"), context);
const task = { goal: "CSS and JS", status: "active", reason: "План уточнён после чтения", steps: [{ text: "CSS", status: "completed" }, { text: "JS", status: "pending", note: "Проверить, нужна ли правка" }] };
context.state.artifacts = [{ id: "r1", kind: "task_list", inlineText: JSON.stringify(task) }];
const text = node => node.textContent + node.children.map(text).join(" ");
context.renderAgentPlanDock();
assert.match(text(dock), /1\/2/);
assert.match(text(dock), /План уточнён после чтения/);
assert.match(text(dock), /Проверить, нужна ли правка/);
const blocked = { ...task, status: "blocked", blocker: "Нужны данные для JS" };
context.state.liveAgentRun = [{ toolId: "common.task_list_set", status: "completed", dataJson: JSON.stringify({ taskList: blocked }) }];
context.renderAgentPlanDock();
assert.match(text(dock), /1\/2 · заблокирован/);
assert.match(text(dock), /Нужны данные для JS/);
assert.equal(dock.classList.contains("hidden"), false);
context.state.liveAgentRun = [];
context.state.artifacts[0].inlineText = JSON.stringify(blocked);
context.renderAgentPlanDock();
assert.match(text(dock), /Нужны данные для JS/, "reloaded blocked task preserves unfinished work");
context.state.liveAgentRun = [{ toolId: "common.task_list_set", status: "completed", dataJson: JSON.stringify({ taskList: task }) }];
context.renderAgentPlanDock();
assert.doesNotMatch(text(dock), /Нужны данные|заблокирован/, "resuming the list removes the stale blocker");
console.log("PASS task continuity: live, saved and resumed blocker keep remaining steps visible");
