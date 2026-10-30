// Host owns scenarios, controls and assertions; this UI schedules explicit runs and presents reports.
var modelDiagnosticsLab = { catalog: null, loading: false, running: false, cancelled: false,
  request: null, rows: [], selected: -1, run: null, scope: "A", expanded: { all: true, A: true, B: true, C: true } };
var modelDiagnosticTitles = {
  A1: "Обычный текст", A2: "Роль инструкции", "A2-system": "Контроль system",
  A3: "Вызов в V8", A4: "Завершение в V8", B1: "content: null",
  B2: "content: пустая строка", B3: "Без поля content", B4: "Маркер в результате",
  B5: "Связь вызова с результатом", B6: "Нет результата",
  B7: "Источник данных", B8: "Native tool call", B9: "Несколько user / run",
  C1: "Ответ JSON", C2: "Строгая схема V8", C3: "Ошибочная схема",
  "C3-valid": "Контроль валидной schema", "C3-text": "JSON без response_format",
  C4: "SSE и обычный JSON", "C4-control": "Контроль JSON для SSE",
  C5: "Fallback: user", C6: "Fallback: developer"
};
var modelDiagnosticVerdicts = { NotRun: "Не запускался", Pass: "Пройдено", Fail: "Не пройдено",
  Inconclusive: "Недостаточно данных", Unsupported: "Не поддерживается", Running: "Выполняется", Cancelled: "Отменено" };

function modelLabNode(tag, text, className) {
  var node = document.createElement(tag);
  if (text !== undefined && text !== null) node.textContent = String(text);
  if (className) node.className = className;
  return node;
}

function modelLabSelectedCases() {
  var lab = modelDiagnosticsLab;
  return lab.catalog ? lab.catalog.scenarios.filter(function (item) {
    return !item.isControl && (lab.scope === "all" || item.id === lab.scope || item.id[0] === lab.scope);
  }) : [];
}

function renderModelLabScenarios() {
  var lab = modelDiagnosticsLab;
  if (!lab.catalog) return;
  var groups = { A: "Ответы и инструкции", B: "История и инструменты", C: "Формат и поток" };
  var tree = window.RNAssistantNavigationTree;
  var nodes = Object.keys(groups).map(function (group) {
    var cases = lab.catalog.scenarios.filter(function (item) { return !item.isControl && item.id[0] === group; });
    return { key: "scope:" + group, scope: group, kind: "parent", selectable: true, title: groups[group],
      iconHtml: tree.icon(group === "B" ? "tool" : group === "C" ? "braces" : "chat"),
      expanded: lab.expanded[group], selected: lab.scope === group, disabled: lab.running,
      badge: String(cases.reduce(function (count, item) { return count + 1 + item.controls.length; }, 0)),
      children: cases.map(function (item) { return { key: "case:" + item.id, scope: item.id, kind: "leaf",
        title: modelDiagnosticTitles[item.id] || item.capability, badge: item.id,
        tooltip: item.id + " · " + (item.responseFormat || "text") + (item.controls.length ? " · контроль: " + item.controls.join(", ") : ""),
        selected: lab.scope === item.id, disabled: lab.running }; }) };
  });
  tree.render($("modelLabScenarioList"), [{ key: "scope:all", scope: "all", kind: "parent", selectable: true,
    title: "Все проверки", iconHtml: tree.icon("layers"), badge: String(lab.catalog.scenarios.length),
    expanded: lab.expanded.all, selected: lab.scope === "all", disabled: lab.running, children: nodes }], {
    label: "Сценарии диагностики", positionKey: "model-diagnostics",
    onToggle: function (node, expanded) { lab.expanded[node.scope] = expanded; },
    onSelect: function (node) {
      if (lab.running) return false;
      lab.scope = node.scope; renderModelLabScenarios(); return false;
    }
  });
  var selected = modelLabSelectedCases(), count = selected.reduce(function (sum, item) { return sum + 1 + item.controls.length; }, 0);
  $("modelLabSelectionTitle").textContent = groups[lab.scope] || modelDiagnosticTitles[lab.scope] || "Все проверки";
  $("modelLabSelectionInfo").textContent = "Запросов за прогон: " + count +
    (selected.some(function (item) { return item.controls.length; }) ? " · включая контрольные проверки" : "");
  $("modelLabRun").title = "Запустить: " + $("modelLabSelectionTitle").textContent;
}

async function openModelDiagnosticsLab(force) {
  var lab = modelDiagnosticsLab;
  if (lab.loading || lab.running || lab.catalog && !force) return;
  lab.loading = true; updateModelLabControls();
  $("modelLabStatus").textContent = "Загружаю сценарии…";
  try {
    var catalog = await send("getModelDiagnosticScenarios", {});
    lab.catalog = catalog;
    $("modelLabModel").value = catalog.model || "";
    renderModelLabScenarios();
    $("modelLabStatus").textContent = "Выберите раздел или отдельную проверку.";
  } catch (error) { $("modelLabStatus").textContent = "Сценарии недоступны: " + error.message; }
  finally { lab.loading = false; updateModelLabControls(); }
}

function updateModelLabControls() {
  var lab = modelDiagnosticsLab, locked = lab.running || lab.loading;
  $("modelLabRun").disabled = locked || !lab.catalog;
  $("modelLabStop").disabled = !lab.running || lab.cancelled;
  $("modelLabRefresh").disabled = locked;
  $("modelLabRun").classList.toggle("hidden", lab.running);
  $("modelLabStop").classList.toggle("hidden", !lab.running);
  ["modelLabModel", "modelLabTimeout", "modelLabRepeat", "modelLabSeed"].forEach(function (id) { $(id).disabled = locked; });
  $("modelLabExport").disabled = lab.running || !lab.rows.some(function (row) { return row.exchange; });
  $("modelLabExport").classList.toggle("hidden", $("modelLabExport").disabled);
  renderModelLabScenarios();
}

function renderModelLabResults() {
  var lab = modelDiagnosticsLab, root = $("modelLabResults"); root.replaceChildren();
  if (!lab.rows.length) return;
  var table = modelLabNode("table", undefined, "rna-table"), head = modelLabNode("thead"), header = modelLabNode("tr");
  ["Сценарий", "Результат", "HTTP", "Время"].forEach(function (name) { header.appendChild(modelLabNode("th", name)); });
  head.appendChild(header); table.appendChild(head); var body = modelLabNode("tbody");
  lab.rows.forEach(function (row, index) {
    var result = row.exchange && row.exchange.result, verdict = result ? result.Verdict : row.phase;
    var tr = modelLabNode("tr"); tr.setAttribute("aria-selected", index === lab.selected ? "true" : "false");
    var first = modelLabNode("td"), button = modelLabNode("button", row.id + (lab.run.repeat > 1 ? " · #" + row.repetition : ""), "rna-table-link");
    button.type = "button"; button.title = modelDiagnosticTitles[row.id] || row.id;
    button.disabled = !row.exchange;
    button.setAttribute("aria-pressed", index === lab.selected ? "true" : "false");
    button.addEventListener("click", function () { lab.selected = index; renderModelLabResults(); renderModelLabInspector(); });
    first.appendChild(button); tr.appendChild(first);
    var status = modelLabNode("td");
    var badge = modelLabNode("span", modelDiagnosticVerdicts[verdict] || verdict,
      verdict === "Pass" ? "model-compatibility-summary passed" : verdict === "Fail" ? "model-compatibility-summary failed" :
        verdict === "Inconclusive" || verdict === "Unsupported" ? "model-compatibility-summary warning" : "muted");
    status.appendChild(badge);
    status.title = verdict;
    status.setAttribute("data-verdict", verdict); tr.appendChild(status);
    tr.appendChild(modelLabNode("td", result && result.HttpStatusCode != null ? result.HttpStatusCode : "—"));
    tr.appendChild(modelLabNode("td", result ? formatDiagnosticDuration(result.DurationMs) : "—")); body.appendChild(tr);
  });
  table.appendChild(body); root.appendChild(table);
}

function modelLabAssertion(root, name, value) {
  var row = modelLabNode("tr");
  row.appendChild(modelLabNode("td", name));
  row.appendChild(modelLabNode("td", value === null || value === undefined ? "Не проверялось" :
    typeof value === "boolean" ? value ? "Да" : "Нет" : modelDiagnosticVerdicts[value] || value)); root.appendChild(row);
}

function modelLabRaw(root, title, data, open) {
  if (!data) { root.appendChild(modelLabNode("p", title + ": не получено.")); return; }
  var details = modelLabNode("details", undefined, "product-settings-advanced"); details.open = !!open;
  details.appendChild(modelLabNode("summary", title + (data.truncated ? " · сокращённый просмотр" : "") +
    (data.redacted ? " · секреты скрыты" : "")));
  var body = modelLabNode("div"); body.appendChild(modelLabNode("pre", data.text)); details.appendChild(body); root.appendChild(details);
}

function renderModelLabInspector() {
  var lab = modelDiagnosticsLab, row = lab.rows[lab.selected], root = $("modelLabInspector"); root.replaceChildren();
  if (!row || !row.exchange) { root.appendChild(modelLabNode("p", "После запуска здесь появятся результаты и детали ответов.", "tree-empty")); return; }
  var exchange = row.exchange, result = exchange.result;
  root.appendChild(modelLabNode("h4", row.id + " · " + (modelDiagnosticTitles[row.id] || result.Capability) + " · #" + row.repetition));
  root.appendChild(modelLabNode("p", result.Model + " · " + formatDiagnosticDuration(result.DurationMs), "muted"));
  root.appendChild(modelLabNode("p", (modelDiagnosticVerdicts[result.Verdict] || result.Verdict) + " · " + result.Reason));
  var assertions = modelLabNode("table", undefined, "rna-table");
  [["HTTP принят", result.HttpAccepted], ["Маркер совпал", result.MarkerMatches], ["Call ID совпал", result.CallIdMatches],
    ["Target совпал", result.TargetMatches], ["V8 parser", result.ParserVerdict], ["SSE parity", result.ParityVerdict],
    ["Получен SSE", result.StreamingRequested ? result.StreamingObserved : null], ["Вход / выход токенов", (result.PromptTokens == null ? "—" : result.PromptTokens) +
      " / " + (result.CompletionTokens == null ? "—" : result.CompletionTokens)]].filter(function (item) {
        return item[1] !== null && item[1] !== undefined && item[1] !== "NotRun";
      }).forEach(function (item) { modelLabAssertion(assertions, item[0], item[1]); });
  root.appendChild(assertions);
  if (row.id === "C3") root.appendChild(modelLabNode("p", "Pass здесь означает корректный отказ на ошибочную schema.", "muted"));
  if (result.StreamingRequested) root.appendChild(modelLabNode("p", result.ResponseCaptureTruncated ? "Лимит захвата: сохранена часть ответа." :
    result.ResponseBodyComplete ? "Тело HTTP получено полностью." : "Сохранён полученный префикс HTTP; полнота не подтверждена.", "muted"));
  if (exchange.resultTruncated || exchange.resultRedacted) root.appendChild(modelLabNode("p",
    (exchange.resultTruncated ? "Просмотр результата сокращён. " : "") + (exchange.resultRedacted ? "Секреты скрыты." : ""), "muted"));
  modelLabRaw(root, "Ожидаемый ответ", { text: result.ExpectedJson || result.ExpectedText || "—" }, false);
  modelLabRaw(root, "Фактический ответ", { text: result.ActualNativeCallsJson || result.ActualContent || "—" }, result.Verdict !== "Pass");
  if (result.ParserErrorCode || result.FailureKind) root.appendChild(modelLabNode("p", "Ошибка: " + (result.ParserErrorCode || result.FailureKind)));
  var raw = exchange.request;
  if (raw && !raw.truncated) {
    try {
      var request = JSON.parse(raw.text), messages = modelLabNode("details");
      messages.appendChild(modelLabNode("summary", "Сообщения запроса · " + (request.messages || []).length));
      (request.messages || []).forEach(function (message, index) {
        modelLabRaw(messages, (index + 1) + ". " + message.role + (message.tool_call_id ? " · " + message.tool_call_id : ""),
          { text: JSON.stringify(message, null, 2) }, false);
      }); root.appendChild(messages);
    } catch (_) { /* A redacted payload may no longer be a JSON object; the text remains below. */ }
  }
  modelLabRaw(root, "Метаданные обмена", { text: JSON.stringify({ verdict: result.Verdict, reason: result.Reason,
    requestSha256: result.RequestSha256, endpoint: result.Endpoint, seed: row.seed }, null, 2) }, false);
  modelLabRaw(root, "HTTP request · UTF-8", raw, false);
  modelLabRaw(root, "HTTP response · текст", exchange.response, false);
}

async function runModelDiagnosticsLab() {
  var lab = modelDiagnosticsLab;
  if (lab.running || lab.loading || !lab.catalog) return;
  var ids = modelLabSelectedCases().map(function (item) { return item.id; });
  var repeat = Number($("modelLabRepeat").value), timeout = Number($("modelLabTimeout").value), model = $("modelLabModel").value.trim();
  if (!ids.length || !model || !Number.isInteger(repeat) || repeat < 1 || repeat > 3 || !Number.isInteger(timeout) || timeout < 10 || timeout > 60) {
    $("modelLabStatus").textContent = "Выберите проверку, модель, 1–3 повтора и таймаут 10–60 секунд.";
    $("modelLabOptions").open = true; return;
  }
  var seed = $("modelLabSeed").value.trim();
  if (!seed) { var bytes = new Uint32Array(4); window.crypto.getRandomValues(bytes); seed = Array.from(bytes).map(function (n) { return n.toString(16); }).join("-"); }
  $("modelLabSeed").value = seed;
  lab.running = true; lab.cancelled = false; lab.selected = -1; lab.rows = [];
  lab.run = { scenarioRevision: lab.catalog.scenarioRevision, endpoint: lab.catalog.endpoint, model: model,
    seed: seed, repeat: repeat, timeoutSeconds: timeout, startedUtc: new Date().toISOString() };
  for (var iteration = 1; iteration <= repeat; iteration++) ids.forEach(function (id) {
    lab.rows.push({ id: id, repetition: iteration, phase: "NotRun", exchange: null });
  });
  var scheduled = lab.rows.slice(); updateModelLabControls(); renderModelLabResults(); renderModelLabInspector();
  try {
    for (var i = 0; i < scheduled.length && !lab.cancelled; i++) {
      var row = scheduled[i]; row.phase = "Running";
      $("modelLabStatus").textContent = "Выполняется " + row.id + " · повтор " + row.repetition + " · " + (i + 1) + "/" + scheduled.length;
      renderModelLabResults();
      lab.request = send("runModelDiagnostic", { scenarioId: row.id, seed: seed + "/" + row.repetition,
        endpoint: lab.run.endpoint, model: model, timeoutSeconds: timeout });
      var response = await lab.request; lab.request = null;
      if (response.scenarioRevision !== lab.run.scenarioRevision || !Array.isArray(response.exchanges) ||
          !response.exchanges.some(function (exchange) { return exchange.result && exchange.result.ScenarioId === row.id; }))
        throw new Error("Ответ диагностики не соответствует выбранному сценарию. Обновите список.");
      row.seed = response.seed;
      response.exchanges.forEach(function (exchange) {
        if (exchange.result.ScenarioId === row.id) { row.exchange = exchange; row.phase = "Complete"; }
        else lab.rows.splice(lab.rows.indexOf(row), 0, { id: exchange.result.ScenarioId, repetition: row.repetition, phase: "Complete", exchange: exchange });
      });
      lab.selected = lab.rows.indexOf(row); renderModelLabResults(); renderModelLabInspector();
    }
    var counts = {};
    lab.rows.forEach(function (item) { if (item.exchange) {
      var verdict = item.exchange.result.Verdict; counts[verdict] = (counts[verdict] || 0) + 1;
    } });
    $("modelLabStatus").textContent = lab.cancelled ? "Остановлено. Полученные результаты сохранены в панели." :
      "Завершено · " + Object.keys(counts).map(function (verdict) { return modelDiagnosticVerdicts[verdict] + ": " + counts[verdict]; }).join(" · ");
  } catch (error) {
    if (row) { row.phase = lab.cancelled ? "Cancelled" : "Inconclusive"; row.error = error.message; }
    $("modelLabStatus").textContent = lab.cancelled ? "Остановлено. Активная проверка отменена." : "Серия остановлена: " + error.message;
  } finally { lab.request = null; lab.running = false; renderModelLabResults(); updateModelLabControls(); }
}

function stopModelDiagnosticsLab() {
  var lab = modelDiagnosticsLab;
  if (!lab.running || lab.cancelled) return;
  lab.cancelled = true; updateModelLabControls(); $("modelLabStatus").textContent = "Отменяю активный запрос…";
  if (lab.request && lab.request.requestId) cancelBridgeRequest(lab.request.requestId).catch(function (error) {
    $("modelLabStatus").textContent = "Отмена не подтверждена: " + error.message + ". Следующие сценарии не запустятся.";
  });
}

function exportModelDiagnosticsLab() {
  var lab = modelDiagnosticsLab;
  if (lab.running || !lab.rows.some(function (row) { return row.exchange; })) return;
  var report = { reportVersion: 1, source: "diagnostics-ui", run: { scenarioRevision: lab.run.scenarioRevision,
    repeat: lab.run.repeat, timeoutSeconds: lab.run.timeoutSeconds, startedUtc: lab.run.startedUtc }, rows: lab.rows,
    note: "Текстовые previews ограничены 65536 символами; redacted/truncated отмечены отдельно. Не квалификация Office." };
  var blob = new Blob([JSON.stringify(report, null, 2)], { type: "application/json" });
  var url = URL.createObjectURL(blob), link = document.createElement("a");
  link.href = url; link.download = "rna-diagnostics.json"; document.body.appendChild(link); link.click(); link.remove();
  setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
}

function bindModelDiagnosticsLab() {
  $("modelLabRefresh").addEventListener("click", function () { openModelDiagnosticsLab(true); });
  $("modelLabRun").addEventListener("click", runModelDiagnosticsLab);
  $("modelLabStop").addEventListener("click", stopModelDiagnosticsLab);
  $("modelLabExport").addEventListener("click", exportModelDiagnosticsLab);
}
