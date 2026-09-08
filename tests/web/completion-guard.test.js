"use strict";

// No packages or browser required. Exercise the shipped typed projection/render
// functions with a minimal DOM; this is not WebView delivery/layout validation.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

class Element {
  constructor(tag) {
    this.tagName = tag; this.childNodes = []; this.attributes = {}; this.open = false;
    this._text = ""; this.innerHTML = ""; this.className = "";
  }
  appendChild(child) { child.parentNode = this; this.childNodes.push(child); return child; }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  addEventListener() {}
  querySelectorAll() { return []; }
  set textContent(value) { this._text = String(value); }
  get textContent() { return this._text + this.innerHTML + this.childNodes.map(child => child.textContent).join(""); }
}

const context = vm.createContext({
  state: { messages: [], activeRunViewState: null },
  document: { createElement: tag => new Element(tag) },
  RNAssistantAgentApproval: { create: () => ({ pendingActivity: () => null }) },
  currentActiveSend: () => null,
  hasActiveMessageEdit: () => false,
  markdown: text => text,
  enhanceMarkdown: () => {},
  appendAgentRunArtifacts: () => {},
  appendAgentDiagnosticMessage: () => {},
  agentDiagnosticText: item => item.message.Content || "",
  renderActivityNode: () => new Element("div"),
  activityPrimaryText: activity => activity.Title || "Tool"
});
context.window = context;
for (const file of ["app-utils.js", "app-run-view-state.js", "app-agent-model.js", "app-agent.js"]) {
  vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js", file), "utf8"), context, { filename: file });
}

function walk(node) { return [node].concat(node.childNodes.flatMap(walk)); }
function view(health, options = {}) {
  return {
    RunId: "run", TurnId: "turn", Narrative: "Все изменения применены.",
    Lifecycle: options.lifecycle || "completed", ExecutionHealth: health,
    SuccessfulReads: options.reads || 0, VerifiedWrites: options.verified || 0,
    NoChangeWrites: options.noChange || 0, UnverifiedWrites: options.unverified || 0,
    FailedCalls: options.failed || 0, UnknownEffects: options.unknown || 0,
    PendingConfirmation: options.pending || null, Reason: options.reason || null,
    CurrentAction: options.action || "", StartedUtc: "2026-08-30T10:00:00Z"
  };
}
function finalMessage(runViewState) {
  return { Role: "assistant", Content: "Все изменения применены.", RunId: "run", Id: "final", RunViewState: runViewState };
}
function renderFinal(runViewState) {
  context.state.messages = [finalMessage(runViewState)];
  assert.equal(context.canCollectAgentRunAt(0), true, "final-only replies enter the typed run projection");
  const run = context.collectAgentRun(0);
  assert.equal(run.items.length, 0);
  assert.equal(run.nextIndex, 1);
  return context.renderAgentRunArticle(run);
}
function assertVisibleEvidence(node, health) {
  const note = walk(node).find(item => item.attributes["data-runtime-health"]);
  assert.ok(note, "runtime evidence is rendered");
  assert.equal(note.attributes["data-runtime-health"], health);
  for (let parent = note.parentNode; parent; parent = parent.parentNode) {
    assert.notEqual(parent.tagName, "details", "runtime warning is outside collapsed trace");
  }
  const finalSection = note.parentNode.childNodes.find(item => item.className === "agent-final-step");
  if (finalSection) {
    assert.ok(note.parentNode.childNodes.indexOf(note) < note.parentNode.childNodes.indexOf(finalSection));
    assert.equal(finalSection.textContent, "Все изменения применены.", "narrative is preserved, not parsed or rewritten");
  }
  return note;
}

const tests = [
  ["failed attempts remain visible without replacing completed lifecycle", () => {
    const node = renderFinal(view("errors", { verified: 1, failed: 1 }));
    const note = assertVisibleEvidence(node, "errors");
    assert.equal(note.attributes.role, "status");
    assert.match(note.textContent, /Неудачных попыток в ходе работы: 1/);
    assert.match(node.className, /status-completed/);
    assert.doesNotMatch(note.textContent, /устранен|устранён|применены/);
  }],
  ["historical unverified write is explained once without duplicate unknown count", () => {
    const node = renderFinal(view("unknown", { unverified: 1, failed: 1, unknown: 1 }));
    const text = assertVisibleEvidence(node, "unknown").textContent;
    assert.match(text, /неподтверждённым результатом: 1/);
    assert.match(text, /перед повторной записью/);
    assert.equal(assertVisibleEvidence(node, "unknown").attributes.role, "alert");

  }],
  ["no write is an ordinary response", () => {
    const node = renderFinal(view("clean", { reads: 1 }));
    assert.equal(walk(node).some(item => item.attributes["data-runtime-health"]), false);
    assert.equal(walk(node).find(item => item.className === "agent-final-step").textContent, "Все изменения применены.");
  }],
  ["verified and no-change writes remain distinct", () => {
    const source = view("clean", { verified: 1, noChange: 2 });
    const normalized = context.RNAssistantRunViewState.normalize(source);
    assert.equal(normalized.verifiedWrites, 1);
    assert.equal(normalized.noChangeWrites, 2);
    assert.equal(walk(renderFinal(source)).some(item => item.attributes["data-runtime-health"]), false);

  }],
  ["legacy flat or malformed projection is never promoted", () => {
    assert.equal(context.RNAssistantRunViewState.fromMessage({ ExecutionSummary: { ExecutionHealth: "clean", WriteOk: 9 } }), null);
    assert.equal(context.RNAssistantRunViewState.normalize(view("completed")), null);
    assert.equal(context.RNAssistantRunViewState.normalize(view("clean", { verified: -1 })), null);
  }],
  ["cancelled boundary retains unknown health", () => {
    const message = { Role: "assistant", RunId: "run", RunViewState: view("unknown", { lifecycle: "cancelled", unknown: 1 }),
      Activity: { Kind: "diagnostic", Status: "cancelled", Title: "Cancelled", ResultMessage: "Запрос отменён." } };
    const items = [{ message, index: 0, activity: message.Activity }];
    const runViewState = context.agentRunViewState(items, null);
    const stats = context.agentRunStats(items, false, runViewState);
    assert.equal(stats.lifecycleStatus, "cancelled");
    assert.equal(stats.status, "cancelled");
    assertVisibleEvidence(context.renderAgentRunArticle({ items }), "unknown");
  }],
  ["camelCase bridge shape projects the same state", () => {
    const camel = Object.fromEntries(Object.entries(view("errors", { reads: 2, failed: 1 }))
      .map(([key, item]) => [key.charAt(0).toLowerCase() + key.slice(1), item]));
    const normalized = context.RNAssistantRunViewState.normalize(camel);
    assert.equal(normalized.executionHealth, "errors");
    assert.equal(normalized.successfulReads, 2);
    const stats = context.agentRunStats([], true, normalized);
    assert.equal(stats.lifecycleStatus, "completed");
    assert.equal(stats.status, "completed");
  }],
  ["recovery without typed state cannot inherit earlier clean state", () => {
    const items = [
      { message: { RunViewState: view("clean", { verified: 1 }) } },
      { message: { Activity: { Kind: "diagnostic", Status: "interrupted_unknown" } } }
    ];
    assert.equal(context.agentRunViewState(items, null), null);
  }]
];

tests.push(["adjacent runs and confirmation segments retain their own results", () => {
  const tool = run => ({ Role: "assistant", RunId: run, Activity: { Kind: "tool", Status: "completed", ToolCallId: run + "-call" } });
  context.state.messages = [tool("a"), tool("b"), { Role: "assistant", RunId: "b", Content: "B final" }];
  const first = context.collectAgentRun(0);
  assert.equal(first.items.length, 1);
  assert.equal(first.finalMessage, null);
  assert.equal(first.nextIndex, 1);
  assert.equal(context.collectAgentRun(1).finalMessage.message.Content, "B final");
  assert.equal(context.agentRunStats([{ activity: { Status: "completed" } }], true, null).status, "completed");
}]);
tests.push(["waiting and running are not replaced by previous failed attempts", () => {
  const rv = context.RNAssistantRunViewState;
  assert.equal(rv.displayStatus(rv.normalize(view("errors", { lifecycle: "running", failed: 1 }))), "running");
  const pending = { PendingId: "p", ToolCallId: "c", ToolName: "excel.write_range" };
  assert.equal(rv.displayStatus(rv.normalize(view("errors", { lifecycle: "awaiting_confirmation", failed: 1, pending }))), "waiting");
}]);
vm.runInContext(fs.readFileSync(path.join(__dirname, "../../web/js/app-agent-activity.js"), "utf8"), context);
tests.push(["semantic target is visible and unknown effect takes precedence over conflict wording", () => {
  const activity = { Kind: "tool", ToolId: "common.resources_read", Display: { action: "Чтение ресурса", runningAction: "Читаю ресурс", operation: "Read" }, Title: "Resource read", ProgressTitle: "Working", Subtitle: "Продажи!A1:D120", Status: "running" };
  const row = context.renderActivityRow(activity, true, false, null);
  assert.match(row.textContent, /Читаю ресурс/);
  assert.match(row.textContent, /Продажи!A1:D120/);
  const conflict = { Status: "failed", ErrorCode: "excel_sheet_already_exists", ResultMessage: "raw", ExecutionEvidence: { Dispatch: "NotDispatched", Effect: "None" } };
  assert.equal(context.activityDisplayResult(conflict), "Лист уже существует. Создание не выполнено.");
  conflict.ExecutionEvidence = { Dispatch: "MayHaveDispatched", Effect: "Unknown" };
  assert.match(context.activityDisplayResult(conflict), /не подтверждён/);
}]);

tests.push(["tool rows preserve action target and result without rewriting model data", () => {
  const activity = { Kind: "tool", ToolId: "common.capabilities_read", Display: { action: "Изучение", runningAction: "Изучаю", operation: "Learn" }, Subtitle: "excel.write_range", Status: "completed",
    ArgumentsJson: '{"id":"excel.write_range"}', DataJson: '{"kind":"tool-schema","catalogRevision":"private-revision","complete":true}',
    ResultMessage: "Capability loaded with catalogRevision=private-revision" };
  const before = JSON.stringify(activity);
  const row = context.renderActivityRow(activity, false, true, null);
  assert.match(row.textContent, /Изучение/);
  assert.match(row.textContent, /excel.write_range/);
  assert.match(row.textContent, /Загружено/);
  assert.doesNotMatch(row.textContent, /catalogRevision|private-revision|Capability loaded/);
  assert.equal(JSON.stringify(activity), before, "presentation leaves exact arguments and results unchanged");
  const longTarget = "Документ / " + "Раздел с длинным названием / ".repeat(8) + "Конец";
  const write = { Kind: "tool", ToolId: "word.replace_text", Display: { action: "Замена текста", operation: "Write" }, Subtitle: longTarget, Status: "completed",
    ExecutionEvidence: { Effect: "VerifiedChange", Dispatch: "MayHaveDispatched" } };
  const changed = context.renderActivityRow(write, false, false, null);
  assert.match(changed.textContent, /Замена текста/);
  assert.ok(changed.textContent.includes(longTarget), "long semantic target wraps, it is not cropped");
  assert.match(changed.textContent, /Изменения подтверждены/);
  write.ExecutionEvidence = { Effect: "VerifiedNoChange", Dispatch: "MayHaveDispatched" };
  assert.equal(context.activityDisplayResult(write), "Без изменений");
  write.Status = "failed";
  assert.notEqual(context.activityDisplayResult(write), "Без изменений", "failed no-op remains a failure");
}]);
tests.push(["search and read captions distinguish complete empty and partial data", () => {
  const search = { Kind: "tool", ToolId: "common.resources_find", Status: "completed",
    DataJson: '{"items":[],"complete":false,"partial":true}' };
  assert.equal(context.activityDisplayResult(search), "Получено элементов: 0 · неполный список");
  assert.equal(context.activityPresentationState(search), "partial");
  search.DataJson = '{"items":[{},{}],"complete":true}';
  assert.equal(context.activityDisplayResult(search), "Получено элементов: 2", "cached caption follows replaced payload");
  assert.equal(context.activityPresentationState(search), "completed");
  search.DataJson = '{"items":[],"complete":true}';
  assert.equal(context.activityDisplayResult(search), "Получено элементов: 0");
  const read = { Kind: "tool", ToolId: "common.resources_read", Status: "completed",
    DataJson: '{"kind":"resource-read","table":{"rows":[{},{}]},"complete":false}' };
  assert.equal(context.activityDisplayResult(read), "Получено строк: 2 · часть данных");
  read.DataJson = '{"truncated":true,"preview":"not the model result"}';
  assert.equal(context.activityDisplayResult(read), "В журнале показана часть результата");
}]);
tests.push(["result representations work for new tools without identity-specific renderers", () => {
  const activity = { Kind: "tool", ToolId: "custom.read_table", Title: "Пересчёт отчёта", Status: "completed", Subtitle: '`[Отчёт](https://example.com)` <b>Лист</b>' };
  assert.equal(context.activityOperation(activity), "command", "custom name does not establish read-only behavior");
  assert.equal(context.activityPrimaryText(activity), "Пересчёт отчёта");
  for (const [data, expected] of [
    [[1, 2], "Получен список · элементов: 2"], ["Готово", "Получен текстовый ответ"],
    [{ answer: 42 }, "Получены данные JSON"], [null, "Получены данные JSON"],
    [{ items: [], complete: false }, "Получены данные JSON"],
    [{ kind: "resource-read", representation: "media", hydratedForNextModelStep: true }, "Получены данные JSON"],
    [{ kind: "tool-schema", loaded: true, complete: true }, "Получены данные JSON"],
    [{ externalized: true }, "Результат сохранён отдельно"]
  ]) {
    activity.DataJson = JSON.stringify(data);
    const before = JSON.stringify(activity);
    assert.equal(context.activityDisplayResult(activity), expected);
    assert.equal(JSON.stringify(activity), before);
  }
  activity.ToolId = "common.resources_read";
  for (const [data, expected] of [
    [{ representation: "text", returnedCharacters: 125, complete: true }, "Получен текст · символов: 125"],
    [{ representation: "media", hydratedForNextModelStep: true }, "Медиа подготовлено для следующего запроса модели"],
    [{ representation: "media", hydratedForNextModelStep: false }, "Получены сведения о медиа"],
    [{ representation: "metadata" }, "Получены сведения · содержимое не загружено"]
  ]) {
    activity.DataJson = JSON.stringify(Object.assign({ kind: "resource-read" }, data));
    assert.equal(context.activityDisplayResult(activity), expected);
  }
  activity.ToolId = "common.capabilities_read";
  activity.DataJson = '{"kind":"skill","complete":false,"truncated":true}';
  assert.equal(context.activityDisplayResult(activity), "Загружена часть описания");
  activity.ToolId = "custom.read_table";
  const row = context.renderActivityRow(activity, false, true, null);
  const target = walk(row).find(node => node.tagName === "code");
  assert.equal(target.textContent, activity.Subtitle);
  assert.equal(target.innerHTML, "", "targets are literal text, never Markdown or HTML");
  activity.ExecutionEvidence = { Effect: "Unknown" };
  assert.match(context.activityDisplayResult(activity), /не подтверждён/);
  activity.ExecutionEvidence.Effect = "VerifiedChange";
  assert.equal(context.activityDisplayResult(activity), "Изменения подтверждены");
}]);
tests.push(["catalog display works for arbitrary ids while zero-action history stays hidden", () => {
  const item = { Kind: "tool", ToolId: "new.opaque_id", Status: "running", Subtitle: "Отчёт за май",
    Display: { action: "Пересчёт отчёта", runningAction: "Пересчитываю отчёт", operation: "Write" } };
  assert.equal(context.activityPrimaryText(item), "Пересчитываю отчёт");
  assert.equal(context.activityOperation(item), "write");
  item.Status = "completed";
  assert.equal(context.activityPrimaryText(item), "Пересчёт отчёта");
  const notice = context.normalizeProgressActivity({ phase: "thinking" });
  const row = context.renderActivityRow(notice, true, false, { hideIcon: true });
  assert.match(row.textContent, /Думаю/);
  assert.doesNotMatch(row.textContent, /thinking|working/);
  const noCalls = renderFinal(view("clean"));
  assert.equal(walk(noCalls).filter(node => /agent-run-overview/.test(node.className)).length, 0);
  const uncertain = renderFinal(view("unknown", { unknown: 1 }));
  assert.equal(walk(uncertain).filter(node => /agent-run-overview/.test(node.className)).length, 0);
  assertVisibleEvidence(uncertain, "unknown");
}]);
tests.push(["localized failures and operation icons remain independent of model wording", () => {
  const failure = { Kind: "tool", ToolId: "common.capabilities_read", Status: "failed", ErrorCode: "capability_not_found",
    ResultMessage: "Missing tool. RUNTIME_CONTEXT.capabilities catalogRevision=abc" };
  assert.equal(context.activityDisplayResult(failure), "Инструмент или навык не найден");
  failure.ErrorCode = "unexpected_vendor_failure";
  assert.equal(context.activityDisplayResult(failure), "Действие завершилось с ошибкой");
  failure.ExecutionEvidence = { Effect: "Unknown", Dispatch: "MayHaveDispatched" };
  assert.match(context.activityDisplayResult(failure), /не подтверждён/);
  assert.equal(context.activityPresentationState(failure), "unknown");
  const types = ["common.resources_find", "common.resources_read", "excel.write_range", "common.capabilities_read", "common.questions_ask", "common.vba_delete"];
  assert.deepEqual(types.map((ToolId, index) => context.activityOperation({ Kind: "tool", ToolId, Display: { operation: ["Search", "Read", "Write", "Learn", "Question", "Delete"][index] } })), ["search", "read", "write", "learn", "question", "delete"]);
  assert.equal(new Set(types.map((ToolId, index) => context.activityOperationIcon({ Kind: "tool", ToolId, Display: { operation: ["Search", "Read", "Write", "Learn", "Question", "Delete"][index] } }))).size, types.length);
}]);

context.appendActivityArtifacts = () => {};
context.appendQuestionCards = () => {};
context.enhanceActivity = () => {};
tests.push(["live feed keeps previous steps and nested actions visible until the run ends", () => {
  const done = { Kind: "tool", ToolId: "common.resources_find", Status: "completed", ToolCallId: "one", StepId: "s1", StepMessage: "Ищу таблицу" };
  const child = { Kind: "tool", ToolId: "common.resources_read", Subtitle: "Продажи", Status: "running", ToolCallId: "two" };
  const parent = { Kind: "group", Status: "running", StepId: "s2", StepMessage: "Проверяю данные", Children: [child] };
  const items = [done, parent].map(activity => ({ activity }));
  const live = context.renderAgentRunArticle({ live: true, items });
  assert.match(live.textContent, /Ищу таблицу/);
  assert.match(live.textContent, /Проверяю данные/);
  assert.match(live.textContent, /Продажи/);
  assert.doesNotMatch(live.textContent, /Действия ·/);
  assert.equal(walk(live).filter(node => /agent-activity kind-tool/.test(node.className)).length, 2);
  assert.equal(walk(live).filter(node => /is-live-current/.test(node.className)).length, 1);
  assert.equal(walk(live).some(node => node.tagName === "details"), false, "children do not hide behind a group disclosure");
  const thinking = new Element("div");
  const ambient = { activity: { Kind: "notice", Status: "running", Title: "Думаю" } };
  context.appendLiveAgentStep(thinking, { items: [{ activity: done }], ambient }, true);
  assert.match(thinking.textContent, /Думаю/);
  assert.equal(walk(thinking).filter(node => /is-live-current/.test(node.className)).length, 1);
  const busy = new Element("div");
  context.appendLiveAgentStep(busy, { items: [{ activity: child }], ambient }, true);
  assert.doesNotMatch(busy.textContent, /Думаю/);
  const overview = new Element("div");
  const stats = context.agentRunStats(items, true, null);
  context.appendAgentRunOverview(overview, context.groupAgentRunSteps(items), items, stats);
  assert.equal(overview.childNodes[0].tagName, "details");
  assert.equal(overview.childNodes[0].open, false);
  assert.match(overview.childNodes[0].childNodes[0].textContent, /Действия/);
}]);

tests.push(["one step narration groups all calls and completion replaces its running row", () => {
  const call = (id, status) => ({ Kind: "tool", ToolId: "common.resources_read",
    RunId: "run", StepId: "step", StepMessage: "Проверю три ресурса.",
    ToolCallId: id, Subtitle: id, Status: status });
  const items = [call("a", "running"), call("a", "completed"),
    call("b", "completed"), call("c", "running")].map(activity => ({ activity }));
  const timeline = context.collectVisibleAgentTimelineItems(items);
  const steps = context.groupAgentRunSteps(timeline);
  assert.equal(steps.length, 1);
  assert.equal(steps[0].items.length, 3);
  const node = context.renderAgentRunArticle({ live: true, items });
  assert.equal(walk(node).filter(n => n.className === "agent-step-message markdown").length, 1);
  assert.equal(walk(node).filter(n => /agent-activity kind-tool/.test(n.className)).length, 3);
  assert.equal(walk(node).filter(n => /is-live-current/.test(n.className)).length, 1);
}]);

for (const [name, test] of tests) {
  test();
  process.stdout.write("PASS run view UI: " + name + "\n");
}
process.stdout.write("OK " + tests.length + "/" + tests.length + "\n");
