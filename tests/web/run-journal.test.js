"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

class ClassList {
  constructor(owner) { this.owner = owner; }
  values() { return new Set(String(this.owner.className || "").split(/\s+/).filter(Boolean)); }
  write(values) { this.owner.className = Array.from(values).join(" "); }
  add(...names) { const values = this.values(); names.forEach(name => values.add(name)); this.write(values); }
  remove(...names) { const values = this.values(); names.forEach(name => values.delete(name)); this.write(values); }
  toggle(name, force) { const values = this.values(); const next = force === undefined ? !values.has(name) : !!force; if (next) values.add(name); else values.delete(name); this.write(values); return next; }
  contains(name) { return this.values().has(name); }
}

class Element {
  constructor(tag) {
    this.tagName = String(tag).toLowerCase(); this.className = ""; this.classList = new ClassList(this);
    this.childNodes = []; this.parentNode = null; this.attributes = {}; this.handlers = {};
    this.open = false; this.disabled = false; this._text = "";
  }
  get children() { return this.childNodes; }
  get firstElementChild() { return this.childNodes[0] || null; }
  get childElementCount() { return this.childNodes.length; }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  replaceChildren(...children) { this.childNodes.forEach(child => { child.parentNode = null; }); this.childNodes = []; children.forEach(child => this.appendChild(child)); }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  getAttribute(name) { return this.attributes[name]; }
  addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); }
  dispatch(name, event = {}) { (this.handlers[name] || []).forEach(handler => handler(Object.assign({ preventDefault() {}, stopPropagation() {} }, event))); }
  click() { if (!this.disabled) this.dispatch("click"); }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  querySelectorAll(selector) {
    const matches = node => selector.startsWith(".")
      ? node.classList.contains(selector.slice(1))
      : node.tagName === selector.toLowerCase();
    const found = [];
    const walk = node => node.childNodes.forEach(child => { if (matches(child)) found.push(child); walk(child); });
    walk(this);
    return found;
  }
  set textContent(value) { this._text = String(value); this.replaceChildren(); }
  get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(""); }
}

const mounts = [];
let unmounts = 0;
const context = vm.createContext({
  document: { createElement: tag => new Element(tag) },
  RNAssistantViewerRegistry: {
    has(kind) { return kind === "json"; },
    mount(kind, host, options) {
      assert.equal(kind, "json");
      const rendered = new Element("div"); rendered.className = "rn-json-viewer"; rendered.textContent = options.text;
      host.replaceChildren(rendered); mounts.push({ host, options }); return {};
    },
    unmount(host) { unmounts += 1; host.replaceChildren(); }
  },
  copyTextResult() { return Promise.resolve(); }
});
context.window = context;
const source = fs.readFileSync(path.join(__dirname, "../../web/js/app-run-journal.js"), "utf8");
vm.runInContext(source, context, { filename: "app-run-journal.js" });

function row(sequence, kind, status, extra = {}) {
  return Object.assign({
    Id: "row-" + sequence + "-" + kind,
    Kind: kind,
    Title: kind,
    Status: status,
    CreatedUtc: "2026-08-29T10:00:" + String(sequence).padStart(2, "0") + "Z",
    FirstSequence: sequence,
    LastSequence: sequence,
    DataJson: JSON.stringify({ sequence, exact: "</script><img onerror=1>", huge: "9007199254740993123456789" }),
    DataTruncated: false,
    SourceEventSeqs: [sequence],
    SourceEventIds: ["evt-" + sequence],
    ResourceRefs: []
  }, extra);
}

const rows = [
  row(1, "turn.started", "running", { RunId: "run-1", TurnId: "turn-1" }),
  row(2, "model.request.prepared", "prepared", { RunId: "run-1", ModelAttemptId: "attempt-1" }),
  row(3, "llm.response", "received", { RunId: "run-1", ModelAttemptId: "attempt-1" }),
  row(4, "model.attempt.rejected", "rejected", { RunId: "run-1", ModelAttemptId: "attempt-1", FailureCount: 1, DataTruncated: true }),
  row(5, "tool.call.recorded", "accepted", { RunId: "run-1", ToolCallId: "call-1", ToolId: "common.html_workspace_upsert" }),
  row(6, "domain.effect.verified", "committed", { RunId: "run-1", ToolCallId: "call-1", MutationId: "mutation-1" }),
  row(7, "diagnostic.evidence.missing", "missing", { RunId: "run-1", ToolCallId: "call-2", FailureCount: 1, SourceEventSeqs: [5, 7], SourceEventIds: ["evt-5", "evt-7"] }),
  row(8, "turn.ended", "failed", { RunId: "run-1", TurnId: "turn-1" })
];

function findButton(root, prefix) {
  return root.querySelectorAll("button").find(button => button.textContent.startsWith(prefix));
}

(async function run() {
  const root = new Element("div");
  let filter = "";
  let navigation = null;
  let payloadEventId = null;
  const expanded = {};
  const options = {
    filter: "all", expanded, activeRunId: "run-1",
    onFilterChange(value) { filter = value; },
    onExpandedChange(id, open) { expanded[id] = open; },
    onNavigate(field, value, view) { navigation = { field, value, view }; },
    onLoadPayload(eventId) {
      payloadEventId = eventId;
      return Promise.resolve({ Text: "{\"message\":\"done\",\"tool_calls\":[]}", ContentType: "application/json", TextTruncated: false });
    },
    onExpandedSet(ids, open) { ids.forEach(id => { expanded[id] = open; }); }
  };

  const result = context.RNAssistantRunJournal.render(root, rows, options);
  assert.equal(result.displayed, 8);
  assert.equal(result.problems, 3);
  assert.equal(root.querySelectorAll(".rn-run-journal-row").length, 8);
  assert.match(root.textContent, /Получен исходный ответ модели/);
  assert.match(root.textContent, /Эффект подтверждён/);
  const metrics = root.querySelectorAll(".rn-run-journal-metric");
  assert.equal(metrics[1].textContent, "3Проблемы");
  assert.equal(metrics[2].textContent, "2Уникальные tool calls");
  console.log("PASS run journal: chronological typed rows and run view evidence render without inference");

  const toolSelection = rows.filter(item => item.ToolCallId === "call-1");
  context.RNAssistantRunJournal.render(root, toolSelection, options);
  assert.match(root.textContent, /Не найден в выборке/);
  assert.doesNotMatch(root.textContent, /Нет terminal/);
  console.log("PASS run journal: a correlation-filtered selection does not claim the run has no terminal");
  context.RNAssistantRunJournal.render(root, rows, options);

  findButton(root, "Показать ответ модели").click();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(payloadEventId, "evt-3");
  assert.match(root.textContent, /Фактический ответ модели/);
  assert.match(root.textContent, /tool_calls/);
  assert.doesNotMatch(root.textContent, /attempt attempt-1/);
  console.log("PASS run journal: model request/response payload is available directly while correlation IDs stay collapsed");

  findButton(root, "Проблемы").click();
  assert.equal(filter, "problems");
  const filtered = context.RNAssistantRunJournal.render(root, rows, Object.assign({}, options, { filter }));
  assert.equal(filtered.displayed, 3);
  assert.equal(root.querySelectorAll(".rn-run-journal-row").length, 3);
  console.log("PASS run journal: problems/model/tools/effects filters remain UI-only over loaded rows");

  const rejected = root.querySelectorAll(".rn-run-journal-row").find(item => item.getAttribute("data-row-id") === rows[3].Id);
  rejected.open = true; rejected.dispatch("toggle");
  assert.equal(mounts.at(-2).options.text, rows[3].DataJson);
  assert.equal(mounts.at(-2).options.completeness, "preview");
  assert.match(rejected.textContent, /9007199254740993123456789/);
  assert.match(rejected.textContent, /попытка отклонена/i);
  rejected.open = false; rejected.dispatch("toggle");
  assert.ok(unmounts >= 2);
  console.log("PASS run journal: inline expansion lazily mounts exact JSON/evidence and unmounts on collapse");

  const missing = root.querySelectorAll(".rn-run-journal-row").find(item => item.getAttribute("data-row-id") === rows[6].Id);
  missing.open = true; missing.dispatch("toggle");
  findButton(missing, "Диапазон событий").click();
  assert.equal(navigation.field, "sourceRange");
  assert.equal(navigation.value.min, 5);
  assert.equal(navigation.value.max, 7);
  assert.equal(navigation.view, "raw");
  assert.match(missing.textContent, /не доказывает ни успех, ни ошибку/i);
  console.log("PASS run journal: evidence gaps stay explicit and navigate to exact source range");

  context.RNAssistantRunJournal.render(root, rows, options);
  findButton(root, "Развернуть проблемы").click();
  assert.equal(Object.keys(expanded).filter(id => expanded[id]).length, 3);
  findButton(root, "Свернуть всё").click();
  assert.equal(Object.keys(expanded).filter(id => expanded[id]).length, 0);

  const duplicate = context.RNAssistantRunJournal.render(root, [rows[0], Object.assign({}, rows[0])], options);
  assert.match(duplicate.error, /unique/i);
  assert.match(root.textContent, /Журнал не отображён/);
  assert.equal(context.RNAssistantRunJournal.isProblem(row(9, "tool.execution.finished", "partial_failure")), true);
  const malformedEvidence = context.RNAssistantRunJournal.render(root,
    [row(9, "tool.execution.finished", "completed", { SourceEventSeqs: "9" })], options);
  assert.match(malformedEvidence.error, /source evidence/i);
  const uncorrelatedEvidence = context.RNAssistantRunJournal.render(root,
    [row(9, "tool.execution.finished", "completed", { SourceEventIds: [] })], options);
  assert.match(uncorrelatedEvidence.error, /source evidence/i);
  const missingStatus = context.RNAssistantRunJournal.render(root,
    [row(9, "tool.execution.finished", "")], options);
  assert.match(missingStatus.error, /kind and status/i);
  let bulkExpanded = [];
  const manyProblems = Array.from({ length: 55 }, (_, index) =>
    row(index + 10, "tool.execution.finished", "failed", { FailureCount: 1 }));
  context.RNAssistantRunJournal.render(root, manyProblems,
    Object.assign({}, options, { onExpandedSet(ids) { bulkExpanded = ids; } }));
  findButton(root, "Развернуть первые 50 проблем").click();
  assert.equal(bulkExpanded.length, context.RNAssistantRunJournal.maxBulkExpandedRows);
  assert.match(context.RNAssistantRunJournal.render(root, null, options).error, /array/i);
  console.log("PASS run journal: expand/collapse state is owner-controlled and malformed projection fails closed");

  const page = fs.readFileSync(path.join(__dirname, "../../web/index.html"), "utf8");
  const trajectory = fs.readFileSync(path.join(__dirname, "../../web/js/app-trajectory.js"), "utf8");
  const activity = fs.readFileSync(path.join(__dirname, "../../web/js/app-agent-activity.js"), "utf8");
  const agent = fs.readFileSync(path.join(__dirname, "../../web/js/app-agent.js"), "utf8");
  assert.ok(page.indexOf("app-run-journal.js") < page.indexOf("app-trajectory.js"));
  ["app-run-journal.css", "app-trajectory.js", "app-agent.js"].forEach(asset => {
    assert.ok(page.includes(asset + "?v=runtime-diagnostics-20260831-1"), asset + " uses the diagnostics cache key");
  });
  assert.ok(page.includes("app-run-journal.js?v=runtime-diagnostics-20260831-2"),
    "changed run-journal renderer uses a fresh cache key");
  assert.match(page, /option value="run-causal">Журнал запуска/);
  assert.match(trajectory, /pageSize:\s*view === "run-causal" \? 200 : 100/);
  assert.match(trajectory, /combined\.slice\(0, journalLimit\)/);
  assert.match(trajectory, /!hasMore \|\| !!loadedLimitReached/);
  assert.match(trajectory, /refreshTrajectory\(false, true\)/);
  assert.match(trajectory, /window\.openRunJournal = openRunJournal/);
  assert.match(activity, /Открыть журнал запуска/);
  assert.match(agent, /appendAgentRunViewState\(body, runViewState, agentRunId\(items, finalMessage\)\)/);
  assert.equal(/JSON\.parse|fetch\(|XMLHttpRequest|WebSocket|EventSource/.test(source), false);
  console.log("PASS run journal: integration defaults to bounded run-causal and exposes direct failed-activity navigation");
  console.log("OK 8/8");
}()).catch(error => {
  console.error(error);
  process.exitCode = 1;
});
