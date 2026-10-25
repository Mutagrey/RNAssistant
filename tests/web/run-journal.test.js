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
  AbortController,
  document: { createElement: tag => new Element(tag) },
  RNAssistantViewerRegistry: {
    has(kind) { return kind === "json"; }, register() {},
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
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-json-viewer.js"), "utf8"), context);
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
  row(5, "tool.call.recorded", "accepted", { RunId: "run-1", ToolCallId: "call-1", ToolId: "common.html_workspace_write_file" }),
  row(6, "domain.effect.verified", "committed", { RunId: "run-1", ToolCallId: "call-1", MutationId: "mutation-1" }),
  row(7, "diagnostic.evidence.missing", "missing", { RunId: "run-1", ToolCallId: "call-2", FailureCount: 1, SourceEventSeqs: [5, 7], SourceEventIds: ["evt-5", "evt-7"] }),
  row(8, "turn.ended", "failed", { RunId: "run-1", TurnId: "turn-1" })
];

function findButton(root, prefix) {
  return root.querySelectorAll("button").find(button => button.textContent.startsWith(prefix));
}

function openParents(element) {
  for (let parent = element.parentNode; parent; parent = parent.parentNode) {
    if (parent.classList.contains("rn-run-journal-branch")) { parent.open = true; parent.dispatch("toggle"); }
  }
}

(async function run() {
  const root = new Element("div");
  let filter = "";
  let navigation = null;
  const payloadEventIds = [];
  const expanded = {};
  const options = {
    filter: "all", expanded, activeRunId: "run-1",
    onFilterChange(value) { filter = value; },
    onExpandedChange(id, open) { expanded[id] = open; },
    onNavigate(field, value, view) { navigation = { field, value, view }; },
    onLoadPayload(eventId) {
      payloadEventIds.push(eventId);
      const text = eventId === "evt-2"
        ? "{\"model\":\"test\",\"messages\":[]}"
        : "{\"message\":\"done\",\"tool_calls\":[]}";
      return Promise.resolve({ Text: text, ContentType: "application/json", TextTruncated: false });
    },
    onExpandedSet(ids, open) { ids.forEach(id => { expanded[id] = open; }); }
  };

  const result = context.RNAssistantRunJournal.render(root, rows, options);
  assert.equal(result.displayed, 8);
  assert.equal(result.problems, 2);
  assert.equal(root.querySelectorAll(".rn-run-journal-row").length, 8);
  assert.match(root.textContent, /Получен исходный ответ модели/);
  assert.match(root.textContent, /Эффект подтверждён/);
  assert.equal(root.querySelectorAll(".rn-run-journal-api-body").length, 0);
  assert.match(root.textContent, /Запрос к модели/);
  assert.match(root.textContent, /Ответ модели/);
  assert.equal(root.querySelectorAll(".rn-run-journal-metric").length, 0, "no duplicate counters above the tree");
  console.log("PASS run journal: chronological typed rows and run view evidence render without inference");

  const toolSelection = rows.filter(item => item.ToolCallId === "call-1");
  context.RNAssistantRunJournal.render(root, toolSelection, options);
  assert.match(root.textContent, /Итог не найден в выборке/);
  assert.doesNotMatch(root.textContent, /Нет terminal/);
  console.log("PASS run journal: a correlation-filtered selection does not claim the run has no terminal");
  context.RNAssistantRunJournal.render(root, rows, options);

  const requestRow = root.querySelectorAll(".rn-run-journal-row")
    .find(item => item.getAttribute("data-row-id") === rows[1].Id);
  openParents(requestRow);
  requestRow.open = true;
  requestRow.dispatch("toggle");
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(payloadEventIds, [], "expanding an explanation does not fetch model bodies");
  const requestTechnical = requestRow.querySelector(".rn-run-journal-technical");
  requestTechnical.open = true; requestTechnical.dispatch("toggle");
  findButton(requestRow, "Показать запрос к модели").click();
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(payloadEventIds, ["evt-2"]);
  assert.match(requestRow.textContent, /messages/);
  requestRow.open = false;
  requestRow.dispatch("toggle");

  const responseRow = root.querySelectorAll(".rn-run-journal-row")
    .find(item => item.getAttribute("data-row-id") === rows[2].Id);
  openParents(responseRow);
  responseRow.open = true;
  responseRow.dispatch("toggle");
  const responseTechnical = responseRow.querySelector(".rn-run-journal-technical");
  responseTechnical.open = true; responseTechnical.dispatch("toggle");
  findButton(responseRow, "Показать ответ модели").click();
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(payloadEventIds, ["evt-2", "evt-3"]);
  assert.match(responseRow.textContent, /Ответ модели/);
  assert.match(root.textContent, /tool_calls/);
  assert.ok(findButton(responseRow, "Показать ответ модели"));
  assert.doesNotMatch(root.textContent, /attempt attempt-1/);
  responseRow.open = false;
  responseRow.dispatch("toggle");
  console.log("PASS run journal: explicit technical actions load persisted API request/response bodies while correlation IDs stay collapsed");

  root.querySelector(".rn-run-journal-filters").value = "api";
  root.querySelector(".rn-run-journal-filters").dispatch("change");
  assert.equal(filter, "api");
  const apiFiltered = context.RNAssistantRunJournal.render(root, rows,
    Object.assign({}, options, { filter }));
  assert.equal(apiFiltered.displayed, 2);
  assert.equal(root.querySelectorAll(".rn-run-journal-row").length, 2);
  assert.match(root.textContent, /Запрос к модели/);
  assert.match(root.textContent, /Ответ модели/);
  console.log("PASS run journal: dedicated API filter isolates exact request and response body rows");

  context.RNAssistantRunJournal.render(root, rows, options);

  root.querySelector(".rn-run-journal-filters").value = "problems";
  root.querySelector(".rn-run-journal-filters").dispatch("change");
  assert.equal(filter, "problems");
  const filtered = context.RNAssistantRunJournal.render(root, rows, Object.assign({}, options, { filter }));
  assert.equal(filtered.displayed, 2);
  assert.equal(root.querySelectorAll(".rn-run-journal-row").length, 2);
  console.log("PASS run journal: problems/model/tools/effects filters remain UI-only over loaded rows");

  context.RNAssistantRunJournal.render(root, rows, options);
  const rejected = root.querySelectorAll(".rn-run-journal-row").find(item => item.getAttribute("data-row-id") === rows[3].Id);
  openParents(rejected);
  rejected.open = true; rejected.dispatch("toggle");
  const rejectedTechnical = rejected.querySelector(".rn-run-journal-technical");
  rejectedTechnical.open = true; rejectedTechnical.dispatch("toggle");
  assert.equal(mounts.at(-1).options.text, rows[3].DataJson);
  assert.equal(mounts.at(-1).options.completeness, "preview");
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
  assert.ok(expanded[rows[6].Id] && expanded[rows[7].Id]);
  assert.ok(Object.keys(expanded).some(id => id.startsWith("group:") && expanded[id]), "problem expansion includes ancestors");
  findButton(root, "Свернуть всё").click();
  assert.equal(Object.keys(expanded).filter(id => expanded[id] && !id.endsWith(":technical")).length, 0);

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
  assert.equal(bulkExpanded.filter(id => !id.startsWith("group:")).length, context.RNAssistantRunJournal.maxBulkExpandedRows);
  assert.match(context.RNAssistantRunJournal.render(root, null, options).error, /array/i);
  console.log("PASS run journal: expand/collapse state is owner-controlled and malformed projection fails closed");

  for (const action of ["collapse", "parent-collapse", "unmount"]) {
    let resolveLate, signal;
    context.RNAssistantRunJournal.render(root, [rows[1]], {
      onLoadPayload(eventId, cancellation) {
        assert.equal(eventId, "evt-2");
        signal = cancellation;
        return new Promise(resolve => { resolveLate = resolve; });
      }
    });
    const pendingRow = root.querySelector(".rn-run-journal-row");
    openParents(pendingRow);
  pendingRow.open = true; pendingRow.dispatch("toggle");
    const pendingTechnical = pendingRow.querySelector(".rn-run-journal-technical");
    pendingTechnical.open = true; pendingTechnical.dispatch("toggle");
    findButton(pendingRow, "Показать запрос к модели").click();
    await new Promise(resolve => setImmediate(resolve));
    if (action === "collapse") { pendingRow.open = false; pendingRow.dispatch("toggle"); }
    else if (action === "parent-collapse") {
      const branch = root.querySelector(".branch-run"); branch.open = false; branch.dispatch("toggle");
    } else context.RNAssistantRunJournal.unmount(root);
    assert.equal(signal.aborted, true);
    resolveLate({ text: "LATE BODY", contentType: "text/plain" });
    await new Promise(resolve => setImmediate(resolve));
    assert.doesNotMatch(pendingRow.textContent, /LATE BODY/);
    assert.doesNotMatch(root.textContent, /LATE BODY/);
  }
  console.log("PASS run journal: collapse/unmount cancels payload and suppresses late render");

  const causeRows = [
    row(1, "tool.execution.finished", "failed", { RunId: "a", ToolCallId: "call", Summary: "Earlier failure" }),
    row(2, "tool.result.recorded", "cancelled", { RunId: "a", ToolCallId: "call", Target: "Продажи", Summary: "Read-back unavailable", ExecutionEvidence: { Dispatch: "MayHaveDispatched", Effect: "Unknown" } }),
    row(3, "tool.execution.finished", "failed", { RunId: "b", ToolCallId: "call", Target: "Итоги", ErrorCode: "excel_sheet_already_exists", ExecutionEvidence: { Dispatch: "NotDispatched", Effect: "None" } })
  ];
  const beforeCauseMounts = mounts.length;
  context.RNAssistantRunJournal.render(root, causeRows, {});
  const causes = root.querySelectorAll(".branch-action");
  assert.equal(causes.length, 2, "same call in separate runs is not merged");

  assert.match(causes[0].firstElementChild.textContent, /Не подтверждено/);
  assert.match(causes[0].textContent, /Read-back unavailable.*Продажи/);
  assert.match(causes[0].textContent, /Результат не подтверждён/);
  assert.match(causes[1].textContent, /Итоги.*Лист уже существует.*не отправлено/);
  assert.equal(context.RNAssistantRunJournal.isProblem(causeRows[1]), true, "cancellation never hides an unknown effect");
  assert.equal(mounts.length, beforeCauseMounts, "causes are readable without mounting technical JSON");
  console.log("PASS run journal: typed causes retain unknown effects, deduplicate exact calls and isolate runs");

  const correlated = [
    row(1, "turn.started", "running", { RunId: "a" }),
    row(2, "llm.request", "prepared", { RunId: "a", StepId: "step-a", ModelAttemptId: "attempt" }),
    row(3, "llm.response", "received", { RunId: "a", ModelAttemptId: "attempt" }),
    row(4, "tool.call.recorded", "accepted", { RunId: "a", StepId: "step-a", ToolCallId: "call" }),
    row(5, "tool.execution.finished", "completed", { RunId: "a", ToolCallId: "call" }),
    row(6, "ui.projected", "projected", { RunId: "a", StepId: "step-a" }),
    row(7, "tool.execution.finished", "failed", { RunId: "b", StepId: "step-b", ToolCallId: "call" }),
    row(8, "llm.response", "received", { RunId: "b", ModelAttemptId: "attempt" }),
    row(9, "tool.execution.started", "running", { RunId: "a", StepId: "step-conflict", ToolCallId: "call" }),
    row(10, "llm.response", "received", { ModelAttemptId: "attempt" }),
    row(11, "domain.effect.verified", "unknown", { RunId: "a", StepId: "step-a", ToolCallId: "call", ExecutionEvidence: { Effect: "Unknown" } }),
    row(12, "step.ended", "completed", { RunId: "a", StepId: "step-a" }),
    row(13, "turn.ended", "completed", { RunId: "a" })
  ];
  const treeResult = context.RNAssistantRunJournal.render(root, correlated, { activeRunId: "a" });
  assert.equal(root.querySelectorAll(".branch-run").length, 3, "orphan run stays separate");
  assert.equal(root.querySelectorAll(".rn-run-journal-row").length, correlated.length, "each source row appears once");
  const findRow = seq => root.querySelectorAll(".rn-run-journal-row").find(element => element.getAttribute("data-row-id") === correlated[seq - 1].Id);
  const ancestor = (element, cls) => { for (let item = element.parentNode; item; item = item.parentNode) if (item.classList.contains(cls)) return item; };
  assert.equal(ancestor(findRow(3), "branch-step").getAttribute("data-step-id"), "step-a", "unique same-run attempt link resolves step");
  assert.equal(ancestor(findRow(5), "branch-step").getAttribute("data-step-id"), undefined, "conflicting call links never pick a step");
  assert.equal(ancestor(findRow(8), "branch-step").getAttribute("data-step-id"), undefined, "attempt IDs in other runs cannot infer a step");
  assert.equal(ancestor(findRow(6), "branch-step"), undefined, "run bookkeeping does not invent a step");
  assert.equal(ancestor(findRow(7), "branch-run").getAttribute("data-run-id"), "b");
  assert.equal(ancestor(findRow(10), "branch-run").getAttribute("data-run-id"), undefined);
  const branch = root.querySelector(".branch-run"), branchId = branch.getAttribute("data-group-id");
  const step = root.querySelector(".branch-step");
  assert.equal(step.open, false, "steps start as a readable overview");
  assert.equal(branch.open, true);
  assert.match(branch.firstElementChild.textContent, /Завершено.*эффект не подтверждён/);
  assert.match(step.firstElementChild.textContent, /Завершено.*эффект не подтверждён/);
  assert.ok(treeResult.validExpansionIds.includes(branchId));
  context.RNAssistantRunJournal.render(root, correlated, { activeRunId: "a", expanded: { [branchId]: false } });
  assert.equal(root.querySelector(".branch-run").open, false, "explicit collapse overrides default-open on refresh");
  const beforeHidden = mounts.length;
  context.RNAssistantRunJournal.render(root, correlated, { expanded: { [correlated[1].Id]: true, [correlated[1].Id + ":technical"]: true } });
  assert.equal(mounts.length, beforeHidden, "hidden descendants do not mount readers");
  context.RNAssistantRunJournal.render(root, correlated, { activeToolCallId: "call", filter: "tools" });
  assert.ok(root.querySelectorAll(".branch-action").every(element => element.open), "exact action routes and filters reveal matching paths");
  console.log("PASS run tree: correlation isolation, ambiguous links, default/collapsed state and hidden readers");

  const parts = [row(1, "tool.call.recorded", "accepted", { RunId: "parts", StepId: "s", ToolCallId: "c",
    DataJson: '{"arguments":{"id":9007199254740993123456789}}' }),
    row(2, "tool.execution.finished", "completed", { RunId: "parts", StepId: "s", ToolCallId: "c",
      DataJson: '{"resultData":{"count":9007199254740993123456789,"count":3}}' })];
  context.RNAssistantRunJournal.render(root, parts, { activeToolCallId: "c" });
  const partChoice = root.querySelector(".rn-run-journal-part-controls").querySelector("select");
  assert.equal(partChoice.value, "result");
  assert.equal(mounts.at(-1).options.text, '{"count":9007199254740993123456789,"count":3}', "result keeps exact numbers and duplicate inner keys");
  partChoice.value = "arguments"; partChoice.dispatch("change");
  assert.equal(mounts.at(-1).options.text, '{"id":9007199254740993123456789}');
  partChoice.value = "events"; partChoice.dispatch("change");
  assert.equal(root.querySelector(".rn-run-journal-part").classList.contains("hidden"), true);
  const beforeInvalid = mounts.length;
  parts[1].DataJson = '{"resultData":1,"resultData":2}';
  context.RNAssistantRunJournal.render(root, parts, { activeToolCallId: "c" });
  assert.match(root.querySelector(".rn-run-journal-part").textContent, /Поле повторяется/);
  assert.equal(mounts.length, beforeInvalid, "duplicate wrapper never picks an arbitrary result");
  parts[1].DataTruncated = true;
  context.RNAssistantRunJournal.render(root, parts, { activeToolCallId: "c" });
  assert.match(root.querySelector(".rn-run-journal-part").textContent, /усечены/);
  console.log("PASS action content: direct result/arguments preserve source tokens, hide event noise and reject ambiguous/truncated wrappers");

  const page = fs.readFileSync(path.join(__dirname, "../../web/index.html"), "utf8");
  const trajectory = fs.readFileSync(path.join(__dirname, "../../web/js/app-trajectory.js"), "utf8");
  const activity = fs.readFileSync(path.join(__dirname, "../../web/js/app-agent-activity.js"), "utf8");
  const agent = fs.readFileSync(path.join(__dirname, "../../web/js/app-agent.js"), "utf8");
  assert.ok(page.indexOf("app-run-journal.js") < page.indexOf("app-trajectory.js"));
  ["app-agent.js", "app-run-journal.css", "app-run-journal.js", "app-trajectory-payload.js", "app-trajectory.js"].forEach(asset => {
    assert.equal(page.split(asset + "?v=").length - 1, 1, asset + " is loaded once with a versioned URL");
  });
  assert.match(page, /option value="run-causal">Ход запуска/);
  assert.match(trajectory, /pageSize:\s*view === "run-causal" \? 200 : 100/);
  assert.match(trajectory, /combined\.slice\(0, journalLimit\)/);
  assert.match(trajectory, /!hasMore \|\| !!loadedLimitReached/);
  assert.match(trajectory, /refreshTrajectory\(false, true\)/);
  assert.match(trajectory, /window\.openRunJournal = openRunJournal/);
  assert.match(activity, /Причина и детали/);
  assert.match(agent, /appendAgentRunViewState\(body, runViewState, agentRunId\(items, finalMessage\)\)/);
  assert.equal(/JSON\.parse|fetch\(|XMLHttpRequest|WebSocket|EventSource/.test(source), false);
  console.log("PASS run journal: integration defaults to bounded run-causal and exposes direct failed-activity navigation");
  console.log("OK run journal");
}()).catch(error => {
  console.error(error);
  process.exitCode = 1;
});
