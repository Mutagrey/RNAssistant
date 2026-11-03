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
    this.dataset = {}; this.style = { setProperty() {} }; this.hidden = false;
    this.open = false; this.disabled = false; this._text = "";
  }
  get children() { return this.childNodes; }
  get firstElementChild() { return this.childNodes[0] || null; }
  get childElementCount() { return this.childNodes.length; }
  appendChild(child) { child.remove(); child.parentNode = this; this.childNodes.push(child); return child; }
  remove() { if (this.parentNode) { this.parentNode.childNodes = this.parentNode.childNodes.filter(item => item !== this); this.parentNode = null; } }
  insertBefore(child, reference) { child.remove(); child.parentNode = this; this.childNodes.splice(this.childNodes.indexOf(reference), 0, child); return child; }
  replaceChildren(...children) { this.childNodes.forEach(child => { child.parentNode = null; }); this.childNodes = []; children.forEach(child => this.appendChild(child)); }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  removeAttribute(name) { delete this.attributes[name]; }
  getAttribute(name) { return this.attributes[name]; }
  addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); }
  dispatch(name, event = {}) { (this.handlers[name] || []).forEach(handler => handler(Object.assign({ preventDefault() {}, stopPropagation() {} }, event))); }
  click() { if (!this.disabled) this.dispatch("click"); }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
  querySelectorAll(selector) {
    const matches = node => selector.split(", ").some(part => part.startsWith(".") ? node.classList.contains(part.slice(1)) : node.tagName === part.toLowerCase());
    const found = [];
    const walk = node => node.childNodes.forEach(child => { if (matches(child)) found.push(child); walk(child); });
    walk(this);
    return found;
  }
  set textContent(value) { this._text = String(value); this.replaceChildren(); }
  get textContent() { return this._text + this.childNodes.map(child => child.textContent).join(""); }
}

const mounts = [];
let unmounts = 0, treeNodes = [], treeOptions;
const context = vm.createContext({
  AbortController,
  RNAssistantIcons: { render: () => "" },
  RNAssistantNavigationTree: { render(root, nodes, options) { treeNodes = nodes; treeOptions = options; }, unmount() {} },
  RNAssistantActionMenu: { createTrigger() { return new Element("button"); }, closeWithin() {} },
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
  row(5, "tool.call.recorded", "accepted", { RunId: "run-1", ToolCallId: "call-1", ToolId: "html.source.write" }),
  row(6, "domain.effect.verified", "committed", { RunId: "run-1", ToolCallId: "call-1", MutationId: "mutation-1" }),
  row(7, "diagnostic.evidence.missing", "missing", { RunId: "run-1", ToolCallId: "call-2", FailureCount: 1, SourceEventSeqs: [5, 7], SourceEventIds: ["evt-5", "evt-7"] }),
  row(8, "turn.ended", "failed", { RunId: "run-1", TurnId: "turn-1" })
];

function findButton(root, prefix) {
  return root.querySelectorAll("button").find(button => button.textContent.startsWith(prefix));
}

function treeEntries(nodes = treeNodes, parents = []) {
  return nodes.flatMap(node => [{ node, parents }, ...treeEntries(node.children || [], parents.concat(node.key))]);
}
function selectTree(key) {
  const entry = treeEntries().find(item => item.node.key === key);
  assert.ok(entry, "selected source exists: " + key);
  treeOptions.onSelect(entry.node);
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
  assert.equal(result.displayed, 8); assert.equal(result.problems, 2);
  assert.equal(treeEntries().filter(item => !item.node.key.startsWith("group:")).length, rows.length, "each source event appears once in the shared tree");
  assert.equal(root.querySelectorAll("details").length, 0, "the detail pane has no second disclosure tree");
  assert.equal(mounts.length, 0, "a group overview mounts no body reader");
  treeOptions.onToggle(treeNodes[0], false);
  assert.equal(expanded[treeNodes[0].key], false);
  selectTree(rows[1].Id);
  assert.equal(mounts.at(-1).options.text, rows[1].DataJson);
  assert.deepEqual(payloadEventIds, [], "selecting an event does not load its remote body");
  const choice = root.querySelector(".rn-run-journal-data-kind");
  choice.value = "payload"; choice.dispatch("change");
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(payloadEventIds, ["evt-2"]);
  assert.match(root.querySelector(".rn-run-journal-detail").textContent, /messages/);
  selectTree(rows[3].Id);
  assert.equal(mounts.at(-1).options.completeness, "preview");
  const rejectionChoice = root.querySelector(".rn-run-journal-data-kind");
  rejectionChoice.value = "payload"; rejectionChoice.dispatch("change");
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(payloadEventIds.at(-1), "evt-4");
  assert.ok(Array.from(mounts.at(-1).options.expandedPaths).includes('$["tool_calls"][0]'));
  selectTree(rows[6].Id);
  findButton(root, "Диапазон событий").click();
  assert.deepEqual(JSON.parse(JSON.stringify(navigation)), { field: "sourceRange", value: { min: 5, max: 7 }, view: "raw" });
  assert.match(root.textContent, /не доказывает ни успех, ни ошибку/);
  console.log("PASS journal: one shared tree, selected exact data, lazy payload, source navigation");

  root.querySelector(".rn-run-journal-filters").value = "api";
  root.querySelector(".rn-run-journal-filters").dispatch("change");
  assert.equal(filter, "api");
  assert.equal(context.RNAssistantRunJournal.render(root, rows, Object.assign({}, options, { filter })).displayed, 2);
  assert.equal(treeEntries().filter(item => !item.node.key.startsWith("group:")).length, 2);
  assert.match(context.RNAssistantRunJournal.render(root, [rows[0], rows[0]], options).error, /unique/i);
  assert.match(context.RNAssistantRunJournal.render(root, null, options).error, /array/i);
  assert.match(context.RNAssistantRunJournal.render(root, [row(9, "tool.execution.finished", "completed", { SourceEventSeqs: "9" })], options).error, /source evidence/i);
  const many = Array.from({ length: 1001 }, (_, i) => row(i + 1, "tool.execution.finished", "completed"));
  const bounded = context.RNAssistantRunJournal.render(root, many, options);
  assert.equal(bounded.loaded, 1000); assert.equal(bounded.truncated, true);

  for (const action of ["pause", "selection", "unmount"]) {
    let release, signal;
    context.RNAssistantRunJournal.render(root, rows, { onLoadPayload(id, cancellation) {
      signal = cancellation; return new Promise(resolve => { release = resolve; });
    } });
    selectTree(rows[1].Id);
    const part = root.querySelector(".rn-run-journal-data-kind"); part.value = "payload"; part.dispatch("change");
    await new Promise(resolve => setImmediate(resolve));
    if (action === "selection") selectTree(rows[0].Id); else context.RNAssistantRunJournal[action](root);
    assert.equal(signal.aborted, true);
    release({ text: "LATE BODY", contentType: "text/plain" });
    await new Promise(resolve => setImmediate(resolve));
    assert.doesNotMatch(root.textContent, /LATE BODY/);
  }
  console.log("PASS journal: bounded filters, invalid evidence, cancellation and late replies");

  const correlated = [
    row(1, "llm.request", "prepared", { RunId: "a", StepId: "s", ModelAttemptId: "attempt" }),
    row(2, "llm.response", "received", { RunId: "a", ModelAttemptId: "attempt" }),
    row(3, "tool.call.recorded", "accepted", { RunId: "a", StepId: "s", ToolCallId: "call" }),
    row(4, "tool.execution.finished", "completed", { RunId: "a", ToolCallId: "call" }),
    row(5, "tool.execution.started", "running", { RunId: "a", StepId: "other", ToolCallId: "call" }),
    row(6, "llm.response", "received", { RunId: "b", ModelAttemptId: "attempt" }),
    row(7, "ui.projected", "projected", { RunId: "a", StepId: "s" }),
    row(8, "llm.response", "received", { ModelAttemptId: "attempt" })
  ];
  context.RNAssistantRunJournal.render(root, correlated, options);
  const ancestors = i => treeEntries().find(item => item.node.key === correlated[i].Id).parents.map(key => JSON.parse(key.slice(6)));
  assert.deepEqual(ancestors(1).find(key => key[0] === "exchange"), ancestors(0).find(key => key[0] === "exchange"));
  assert.equal(ancestors(3).some(key => key[0] === "exchange"), false, "ambiguous call never picks an adjacent request");
  assert.notDeepEqual(ancestors(5).find(key => key[0] === "exchange"), ancestors(0).find(key => key[0] === "exchange"), "attempt correlation stays inside its exact run");
  assert.equal(ancestors(6).some(key => key[0] === "exchange"), false, "run bookkeeping stays in the run group");
  assert.equal(treeNodes.length, 3, "uncorrelated events remain explicitly separate");

  const exchanges = [
    row(1, "step.started", "running", { RunId: "trace", StepId: "transport-1", DataJson: '{"requestId":"q1"}' }),
    row(2, "model.request.prepared", "prepared", { RunId: "trace", StepId: "logical", ModelAttemptId: "a1", DataJson: '{"requestId":"q1"}' }),
    row(3, "llm.response", "received", { RunId: "trace", StepId: "logical", ModelAttemptId: "a1", DataJson: '{"requestId":"q1"}' }),
    row(4, "step.ended", "completed", { RunId: "trace", StepId: "transport-1", DataJson: '{"requestId":"q1"}' }),
    row(5, "model.attempt.rejected", "rejected", { RunId: "trace", StepId: "logical", ModelAttemptId: "a1", Summary: "Некорректные аргументы" }),
    row(6, "step.started", "running", { RunId: "trace", StepId: "transport-2", DataJson: '{"requestId":"q2"}' }),
    row(7, "llm.request", "prepared", { RunId: "trace", StepId: "logical", ModelAttemptId: "a2", DataJson: '{"requestId":"q2"}' }),
    row(8, "llm.response", "received", { RunId: "trace", StepId: "logical", ModelAttemptId: "a2", DataJson: '{"requestId":"q2"}' }),
    row(9, "step.ended", "completed", { RunId: "trace", StepId: "transport-2", DataJson: '{"requestId":"q2"}' }),
    row(10, "tool.call.recorded", "accepted", { RunId: "trace", StepId: "logical", ModelAttemptId: "a2", ToolCallId: "read", ToolId: "core.read" }),
    row(11, "tool.execution.finished", "completed", { RunId: "trace", ToolCallId: "read", Summary: "Прочитано 12 строк" }),
    row(12, "step.started", "running", { RunId: "trace", StepId: "bookkeeping-only" }),
    row(13, "step.ended", "completed", { RunId: "trace", StepId: "bookkeeping-only" })
  ];
  mounts.length = 0;
  let openedRequest;
  options.onOpenModelContext = target => { openedRequest = target; };
  context.RNAssistantRunJournal.render(root, exchanges, options);
  assert.deepEqual(Array.from(treeNodes[0].children, node => node.title), ["Запрос 1", "Запрос 2", "Служебные события"], "lifecycle steps do not create groups; retries remain separate exchanges");
  assert.equal(treeEntries().filter(item => !item.node.key.startsWith("group:")).length, exchanges.length);
  assert.match(root.querySelector(".rn-run-journal-detail").textContent, /Некорректные аргументы.*Прочитано 12 строк/);
  assert.equal(mounts.length, 0, "overview is metadata only");
  selectTree(treeNodes[0].children[0].key);
  findButton(root, "Открыть запрос").click();
  assert.equal(openedRequest.requestEventId, "evt-2", "prepared-request groups open their exact saved request");
  const secondRequest = treeNodes[0].children[1];
  selectTree(secondRequest.key);
  assert.match(root.querySelector(".rn-run-journal-overview").textContent, /Запрос.*Ответ.*Прочитано 12 строк/);
  const actionId = treeEntries().find(entry => entry.node.title === "Чтение источника").node.key;
  findButton(root.querySelector(".rn-run-journal-overview"), "Чтение источника").click();
  assert.equal(root.querySelector(".rn-run-journal-detail").dataset.selectionId, actionId, "overview links select the existing tree node");
  assert.equal(expanded[secondRequest.key], true, "overview navigation reveals the selected path");
  const manyRequests = Array.from({ length: 70 }, (_, i) => row(i + 1, "llm.request", "prepared", { RunId: "many", ModelAttemptId: "attempt-" + i }));
  context.RNAssistantRunJournal.render(root, manyRequests, options);
  assert.equal(root.querySelector(".rn-run-journal-overview").querySelectorAll("tr").length, 31, "overview has a bounded first page");
  findButton(root.querySelector(".rn-run-journal-overview"), "Ещё").click();
  assert.equal(root.querySelector(".rn-run-journal-overview").querySelectorAll("tr").length, 61);

  const parts = [row(1, "tool.call.recorded", "accepted", { RunId: "parts", StepId: "s", ToolCallId: "c", DataJson: '{"arguments":{"id":9007199254740993123456789}}' }),
    row(2, "tool.execution.finished", "completed", { RunId: "parts", StepId: "s", ToolCallId: "c", DataJson: '{"resultData":{"count":9007199254740993123456789,"count":3}}' })];
  context.RNAssistantRunJournal.render(root, parts, { activeToolCallId: "c" });
  assert.equal(mounts.at(-1).options.text, '{"count":9007199254740993123456789,"count":3}');
  const part = root.querySelector(".rn-run-journal-data-kind"); part.value = "arguments"; part.dispatch("change");
  assert.equal(mounts.at(-1).options.text, '{"id":9007199254740993123456789}');
  parts[1].DataJson = '{"resultData":1,"resultData":2}';
  context.RNAssistantRunJournal.render(root, parts, { activeToolCallId: "c" });
  assert.match(root.querySelector(".rn-run-journal-part").textContent, /Поле повторяется/);
  parts[1].DataTruncated = true;
  context.RNAssistantRunJournal.render(root, parts, { activeToolCallId: "c" });
  assert.match(root.querySelector(".rn-run-journal-part").textContent, /усечены/);
  console.log("PASS journal: exact grouping, unknown scope, lossless arguments and results");

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
  assert.match(agent, /appendAgentRunViewState/);
  assert.equal(/JSON\.parse|fetch\(|XMLHttpRequest|WebSocket|EventSource/.test(source), false);
  console.log("PASS run journal: integration defaults to bounded run-causal and exposes direct failed-activity navigation");
  console.log("OK run journal");
}()).catch(error => {
  console.error(error);
  process.exitCode = 1;
});
