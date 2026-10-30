// Host owns scenarios, controls and assertions; this UI schedules explicit runs and presents reports.
var modelDiagnosticsLab = { catalog: null, catalogRevision: 0, loading: false, running: false, cancelled: false,
  request: null, rows: [], selected: null, followResults: true, run: null, scope: "response", expanded: { all: true } };
var modelDiagnosticTitles = {
  A1: "Обычный текст", A2: "Роль инструкции", "A2-system": "Контроль system",
  A3: "Вызов в V8", A4: "Завершение в V8", B1: "content: null",
  B2: "content: пустая строка", B3: "Без поля content", B4: "Решение execute и результат",
  B5: "Связь вызова с результатом", B6: "Нет результата",
  B7: "Источник данных", B8: "Генерация native-вызова", B9: "Несколько запросов пользователя",
  C1: "Ответ JSON", C2: "Строгая схема V8", C3: "Ошибочная схема",
  "C3-valid": "Контроль валидной schema", "C3-text": "JSON без response_format",
  C4: "SSE и обычный JSON", "C4-control": "Контроль JSON для SSE",
  C5: "Результат в role: user", C6: "Результат в role: developer"
};
var modelDiagnosticGroups = [
  { id: "response", title: "Ответ в content", icon: "braces", ids: ["A1", "A3", "A4", "C1", "C2", "C3", "C3-text"],
    description: "Обычный ответ, JSON и строгая схема V8. Проверяется новый ответ модели." },
  { id: "instruction", title: "Роль инструкции", icon: "chat", ids: ["A2"],
    description: "Сравнение developer и контрольной system-инструкции." },
  { id: "native-history", title: "История Native tools", icon: "tool", ids: ["B1", "B2", "B3", "B4", "B5", "B6", "B7", "B9"],
    description: "Чтение assistant.tool_calls → role: tool. Новый ответ приходит JSON в content; генерация native-вызовов не требуется." },
  { id: "json-history", title: "История JSON в content", icon: "chat", ids: ["C5", "C6"],
    description: "Чтение V8 JSON в assistant.content и TOOL_RESULT в роли user или developer." },
  { id: "native-generation", title: "Генерация Native tools", icon: "tool", ids: ["B8"],
    description: "Отдельная возможность: модель сама возвращает tool_calls. Её результат не определяет поддержку native-истории." },
  { id: "stream", title: "Потоковый ответ", icon: "layers", ids: ["C4"],
    description: "SSE сравнивается с обычным JSON-ответом на одинаковый запрос." }
];
var modelDiagnosticVerdicts = { NotRun: "Не запускался", Pass: "Пройдено", Fail: "Не пройдено",
  Inconclusive: "Недостаточно данных", Unsupported: "Не поддерживается", Running: "Выполняется", Cancelled: "Отменено" };
var modelDiagnosticReasons = {
  exact_text_marker: "Текст совпал с ожидаемым маркером.", text_marker_mismatch: "Модель изменила ожидаемый текст.",
  exact_json_answer: "Все поля ответа совпали с ожидаемыми.", invalid_answer_json: "Ответ не является допустимым JSON-объектом.",
  marker_mismatch: "Маркер из результата инструмента не совпал.", call_id_mismatch: "Ответ связан с другим вызовом инструмента.",
  target_mismatch: "Модель выбрала другой объект.", unexpected_answer_fields: "В ответе есть лишние или отличающиеся поля.",
  exact_v8_response: "Ответ прошёл V8 parser и совпал с ожидаемым.", v8_parser_rejected: "Ответ нарушает контракт V8.",
  v8_response_mismatch: "V8-ответ допустим, но его содержание отличается от ожидаемого.",
  exact_native_function_and_arguments: "Имя функции и аргументы native-вызова совпали.",
  native_call_missing_or_extra: "Модель должна вернуть ровно один native-вызов.",
  native_arguments_mismatch: "Аргументы native-вызова не совпали.", invalid_native_arguments: "Аргументы native-вызова не являются JSON.",
  unexpected_native_generation: "Получен native-вызов вместо запрошенного ответа в content.",
  invalid_schema_rejected: "Сервер корректно отклонил ошибочную схему.", invalid_schema_accepted: "Сервер принял заведомо ошибочную схему.",
  schema_rejection_unproven: "Не подтверждено, что сервер отклонил именно ошибочную схему.",
  request_timeout: "Время ожидания ответа истекло.", request_failed: "Запрос не завершился успешно. Подробности — в HTTP-ответе ниже.",
  incomplete_or_refused_answer: "Модель отказалась отвечать либо ответ не завершён.",
  json_response_instead_of_sse: "Вместо запрошенного потока SSE получен обычный JSON.",
  sse_control_unavailable: "Контрольный JSON-ответ не прошёл проверку; сравнение SSE недоступно.",
  sse_parity_mismatch: "Результаты SSE и обычного JSON не совпали.", missing_exact_exchange: "Недостаточно данных HTTP-обмена для подтверждения результата."
};

function modelLabNode(tag, text, className) {
  var node = document.createElement(tag);
  if (text !== undefined && text !== null) node.textContent = String(text);
  if (className) node.className = className;
  return node;
}

function modelLabSelectedCases() {
  var lab = modelDiagnosticsLab;
  var group = modelDiagnosticGroups.find(function (item) { return item.id === lab.scope; });
  return lab.catalog ? lab.catalog.scenarios.filter(function (item) {
    return !item.isControl && (lab.scope === "all" || item.id === lab.scope || group && group.ids.indexOf(item.id) >= 0);
  }) : [];
}

function renderModelLabScenarios() {
  var lab = modelDiagnosticsLab;
  if (!lab.catalog) return;
  var tree = window.RNAssistantNavigationTree;
  var nodes = modelDiagnosticGroups.map(function (group) {
    var cases = lab.catalog.scenarios.filter(function (item) { return !item.isControl && group.ids.indexOf(item.id) >= 0; });
    return { key: "scope:" + group.id, scope: group.id, kind: "parent", selectable: true, title: group.title,
      iconHtml: tree.icon(group.icon), tooltip: group.description,
      expanded: !!lab.expanded[group.id], selected: lab.scope === group.id, disabled: lab.running,
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
  var group = modelDiagnosticGroups.find(function (item) { return item.id === lab.scope || item.ids.indexOf(lab.scope) >= 0; });
  $("modelLabSelectionTitle").textContent = modelDiagnosticTitles[lab.scope] || group && group.title || "Все проверки";
  $("modelLabSelectionDescription").textContent = group ? group.description : "Каждая возможность оценивается отдельно. Контрольные запросы включаются автоматически.";
  $("modelLabSelectionInfo").textContent = "Запросов за прогон: " + count +
    (selected.some(function (item) { return item.controls.length; }) ? " · включая контрольные проверки" : "");
  $("modelLabRun").title = "Запустить: " + $("modelLabSelectionTitle").textContent;
}

async function openModelDiagnosticsLab(force, resetModel) {
  var lab = modelDiagnosticsLab;
  if (lab.loading || lab.running || lab.catalog && !force) return;
  var revision = lab.catalogRevision, previousModel = lab.catalog && lab.catalog.model;
  lab.loading = true; updateModelLabControls();
  $("modelLabStatus").textContent = "Загружаю сценарии…";
  try {
    var catalog = await send("getModelDiagnosticScenarios", {});
    if (revision !== lab.catalogRevision) return;
    if (!catalog || !Array.isArray(catalog.scenarios) || !catalog.scenarios.length ||
        catalog.scenarios.some(function (item) { return !item.id || !Array.isArray(item.controls); }))
      throw new Error("Получен некорректный каталог. Перезагрузите панель.");
    lab.catalog = catalog;
    if (resetModel || !$("modelLabModel").value || $("modelLabModel").value === previousModel)
      $("modelLabModel").value = catalog.model || "";
    $("modelLabConnection").textContent = "Сохранённое подключение: " + (catalog.endpoint || "не задано");
    renderModelLabScenarios();
    $("modelLabStatus").textContent = "Выберите раздел или отдельную проверку.";
  } catch (error) { lab.catalog = null; $("modelLabStatus").textContent = "Сценарии недоступны: " + error.message; }
  finally {
    lab.loading = false; updateModelLabControls();
    if (revision !== lab.catalogRevision && state.diagnosticsTab === "model-lab") openModelDiagnosticsLab(true);
  }
}

function refreshModelDiagnosticsSettings() {
  if (modelDiagnosticsLab.catalog && $("modelLabModel").value === modelDiagnosticsLab.catalog.model)
    $("modelLabModel").value = "";
  modelDiagnosticsLab.catalogRevision++;
  modelDiagnosticsLab.catalog = null;
  if (state.diagnosticsTab === "model-lab") openModelDiagnosticsLab(true);
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

function modelLabBadge(verdict) {
  return modelLabNode("span", modelDiagnosticVerdicts[verdict] || verdict,
    verdict === "Pass" ? "model-compatibility-summary passed" : verdict === "Fail" ? "model-compatibility-summary failed" :
      verdict === "Inconclusive" || verdict === "Unsupported" ? "model-compatibility-summary warning" : "muted");
}

function selectModelLabRow(row) {
  var lab = modelDiagnosticsLab;
  lab.selected = row; lab.followResults = false;
  $("modelLabResults").querySelectorAll("tbody tr").forEach(function (tr, index) {
    var selected = lab.rows[index] === row;
    tr.setAttribute("aria-selected", String(selected));
    tr.querySelector("button").setAttribute("aria-pressed", String(selected));
  });
  renderModelLabInspector();
  var inspector = $("modelLabInspector"), panel = inspector.closest(".model-diagnostics-lab");
  if (inspector.getBoundingClientRect().top > panel.getBoundingClientRect().bottom - 240)
    inspector.scrollIntoView({ block: "start" });
}

function renderModelLabResults() {
  var lab = modelDiagnosticsLab, root = $("modelLabResults"), scroll = root.scrollTop;
  var focused = root.contains(document.activeElement) && document.activeElement.closest("tr");
  var focusKey = focused && focused.getAttribute("data-row-key");
  root.replaceChildren();
  $("modelLabRunSummary").classList.toggle("hidden", !lab.run);
  var synthetic = lab.rows.some(function (row) { return row.exchange && row.exchange.result.ExecutionKind === "Synthetic"; });
  $("modelLabRunSummary").textContent = lab.run ? (synthetic ? "Синтетический прогон: " : "Результаты прогона: ") + lab.run.model + " · " + lab.run.endpoint : "";
  if (!lab.rows.length) return;
  var table = modelLabNode("table", undefined, "rna-table"), head = modelLabNode("thead"), header = modelLabNode("tr");
  ["Проверка", "Результат"].forEach(function (name) { header.appendChild(modelLabNode("th", name)); });
  head.appendChild(header); table.appendChild(head); var body = modelLabNode("tbody");
  lab.rows.forEach(function (row) {
    var result = row.exchange && row.exchange.result, verdict = result ? result.Verdict : row.phase;
    var tr = modelLabNode("tr"), selected = row === lab.selected, key = row.id + "/" + row.repetition;
    tr.setAttribute("aria-selected", String(selected)); tr.setAttribute("data-row-key", key);
    tr.setAttribute("data-row-action", "inspect");
    var first = modelLabNode("td"), button = modelLabNode("button", modelDiagnosticTitles[row.id] || row.id, "rna-table-link");
    button.type = "button"; button.setAttribute("aria-pressed", String(selected));
    button.setAttribute("aria-controls", "modelLabInspector");
    tr.addEventListener("click", function () { selectModelLabRow(row); });
    first.appendChild(button);
    first.appendChild(modelLabNode("small", row.id + (row.control ? " · контроль" : "") +
      (lab.run.repeat > 1 ? " · повтор " + row.repetition : "")));
    tr.appendChild(first);
    var status = modelLabNode("td"); status.setAttribute("data-verdict", verdict);
    status.appendChild(modelLabBadge(verdict));
    if (result) status.appendChild(modelLabNode("small", (result.HttpStatusCode != null ? "HTTP " + result.HttpStatusCode + " · " : "") +
      formatDiagnosticDuration(result.DurationMs)));
    tr.appendChild(status);
    body.appendChild(tr);
  });
  table.appendChild(body); root.appendChild(table); root.scrollTop = scroll;
  if (focusKey) Array.from(body.rows).some(function (tr) {
    if (tr.getAttribute("data-row-key") !== focusKey) return false;
    tr.querySelector("button").focus({ preventScroll: true }); return true;
  });
}

function modelLabAssertion(root, name, value) {
  if (value === null || value === undefined || value === "NotRun") return;
  var row = modelLabNode("tr");
  row.appendChild(modelLabNode("th", name));
  row.lastChild.setAttribute("scope", "row");
  row.appendChild(modelLabNode("td", typeof value === "boolean" ? value ? "Да" : "Нет" : modelDiagnosticVerdicts[value] || value));
  root.appendChild(row);
}

function modelLabDisplayJson(text) {
  try { return JSON.stringify(JSON.parse(text), null, 2); }
  catch (_) { return text || "—"; }
}

function modelLabRaw(root, title, data, open) {
  var details = modelLabNode("details", undefined, "product-settings-advanced"); details.open = !!open;
  details.appendChild(modelLabNode("summary", title + (data && data.truncated ? " · сокращённый просмотр" : "") +
    (data && data.redacted ? " · секреты скрыты" : "")));
  var body = modelLabNode("div"); body.appendChild(modelLabNode("pre", data ? data.text : "Не получено."));
  details.appendChild(body); root.appendChild(details); return details;
}

function renderModelLabInspector() {
  var lab = modelDiagnosticsLab, row = lab.selected, root = $("modelLabInspector"); root.replaceChildren();
  if (!row) { root.appendChild(modelLabNode("p", "Нажмите на строку результата, чтобы увидеть детали.", "tree-empty")); return; }
  root.appendChild(modelLabNode("h4", (modelDiagnosticTitles[row.id] || row.id) + " · " + row.id +
    (lab.run.repeat > 1 ? " · повтор " + row.repetition : "")));
  var exchange = row.exchange, result = exchange && exchange.result;
  root.appendChild(modelLabBadge(result ? result.Verdict : row.phase));
  if (!result) {
    root.appendChild(modelLabNode("p", row.error || (row.phase === "Running" ? "Ожидается ответ модели…" : "Проверка ещё не выполнена.")));
    if (row.error) root.appendChild(modelLabNode("p", "Ошибка запуска не является результатом проверки модели.", "muted"));
    return;
  }
  root.appendChild(modelLabNode("p", modelDiagnosticReasons[result.Reason] || result.Reason || "Причина не указана."));
  var group = modelDiagnosticGroups.find(function (item) { return item.ids.indexOf(row.id) >= 0; });
  if (group) root.appendChild(modelLabNode("p", group.description, "muted"));
  if (row.id === "C3") root.appendChild(modelLabNode("p", "Ожидается отказ HTTP 400/422 на ошибочную схему. Такой отказ означает, что проверка пройдена.", "muted"));
  var assertions = modelLabNode("section"), checks = modelLabNode("table", undefined, "rna-table");
  assertions.appendChild(modelLabNode("h5", "Что проверено"));
  [["HTTP принят", result.HttpAccepted], ["JSON разобран", result.JsonObjectAccepted], ["Маркер совпал", result.MarkerMatches],
    ["ID вызова совпал", result.CallIdMatches], ["Объект совпал", result.TargetMatches], ["Контракт V8", result.ParserVerdict],
    ["SSE совпал с JSON", result.ParityVerdict], ["Получен SSE", result.StreamingRequested ? result.StreamingObserved : null]]
    .forEach(function (item) { modelLabAssertion(checks, item[0], item[1]); });
  if (checks.children.length) { assertions.appendChild(checks); root.appendChild(assertions); }
  if (result.ParserErrorCode || result.FailureKind) root.appendChild(modelLabNode("p", "Код ошибки: " + (result.ParserErrorCode || result.FailureKind)));
  var comparison = modelLabNode("section", undefined, "model-lab-comparison");
  [["Ожидалось", result.ExpectedJson || result.ExpectedText], ["Получено", result.ActualNativeCallsJson || result.ActualContent]]
    .forEach(function (item) {
      var panel = modelLabNode("div"); panel.appendChild(modelLabNode("h5", item[0]));
      panel.appendChild(modelLabNode("pre", modelLabDisplayJson(item[1]))); comparison.appendChild(panel);
    });
  root.appendChild(comparison);
  if (exchange.resultTruncated || exchange.resultRedacted) root.appendChild(modelLabNode("p",
    (exchange.resultTruncated ? "Просмотр результата сокращён. " : "") + (exchange.resultRedacted ? "Секреты скрыты." : ""), "muted"));
  if (result.StreamingRequested) root.appendChild(modelLabNode("p", result.ResponseCaptureTruncated ? "Лимит захвата: сохранена часть ответа." :
    result.ResponseBodyComplete ? "Тело HTTP получено полностью." : "Сохранён полученный префикс HTTP; полнота не подтверждена.", "muted"));
  var raw = exchange.request;
  if (raw && !raw.truncated) {
    try {
      var request = JSON.parse(raw.text), messages = modelLabNode("details", undefined, "product-settings-advanced");
      messages.appendChild(modelLabNode("summary", "Состав запроса · сообщений: " + (request.messages || []).length));
      var messageBody = modelLabNode("div");
      (request.messages || []).forEach(function (message, index) {
        modelLabRaw(messageBody, (index + 1) + ". " + message.role + (message.tool_calls ? " · tool_calls" : "") +
          (message.tool_call_id ? " · " + message.tool_call_id : ""), { text: JSON.stringify(message, null, 2) }, false);
      }); messages.appendChild(messageBody); root.appendChild(messages);
    } catch (_) { /* Redaction may make JSON unparsable; the raw text remains available. */ }
  }
  modelLabRaw(root, "HTTP-запрос", raw, false);
  modelLabRaw(root, "HTTP-ответ", exchange.response, result.Reason === "request_failed");
  var metadata = modelLabRaw(root, "Параметры и технические детали", { text: JSON.stringify({
    model: result.Model, endpoint: result.Endpoint, responseFormat: result.ResponseFormat,
    durationMs: result.DurationMs, promptTokens: result.PromptTokens, completionTokens: result.CompletionTokens,
    verdict: result.Verdict, reason: result.Reason, seed: row.seed, requestSha256: result.RequestSha256,
    executionKind: result.ExecutionKind }, null, 2) }, false);
  metadata.setAttribute("data-model-lab-metadata", "true");
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
  lab.running = true; lab.cancelled = false; lab.selected = null; lab.followResults = true; lab.rows = [];
  $("modelLabCases").open = false;
  lab.run = { scenarioRevision: lab.catalog.scenarioRevision, endpoint: lab.catalog.endpoint, model: model,
    seed: seed, repeat: repeat, timeoutSeconds: timeout, startedUtc: new Date().toISOString() };
  for (var iteration = 1; iteration <= repeat; iteration++) ids.forEach(function (id) {
    lab.rows.push({ id: id, repetition: iteration, phase: "NotRun", exchange: null });
  });
  var scheduled = lab.rows.slice(); updateModelLabControls(); renderModelLabResults(); renderModelLabInspector();
  try {
    for (var i = 0; i < scheduled.length && !lab.cancelled; i++) {
      var row = scheduled[i]; row.phase = "Running";
      row.seed = seed + "/" + row.repetition;
      if (lab.followResults) lab.selected = row;
      $("modelLabStatus").textContent = "Выполняется " + row.id + " · повтор " + row.repetition + " · " + (i + 1) + "/" + scheduled.length;
      renderModelLabResults(); renderModelLabInspector();
      lab.request = send("runModelDiagnostic", { scenarioId: row.id, seed: row.seed,
        endpoint: lab.run.endpoint, model: model, timeoutSeconds: timeout });
      var response = await lab.request; lab.request = null;
      if (!response || response.scenarioRevision !== lab.run.scenarioRevision || !Array.isArray(response.exchanges) ||
          response.exchanges.some(function (exchange) { return !exchange || !exchange.result; }) ||
          !response.exchanges.some(function (exchange) { return exchange.result && exchange.result.ScenarioId === row.id; }))
        throw new Error("Ответ диагностики не соответствует выбранному сценарию. Обновите список.");
      row.seed = response.seed;
      response.exchanges.forEach(function (exchange) {
        if (exchange.result.ScenarioId === row.id) { row.exchange = exchange; row.phase = "Complete"; }
        else lab.rows.splice(lab.rows.indexOf(row), 0, { id: exchange.result.ScenarioId, repetition: row.repetition,
          seed: response.seed, control: true, phase: "Complete", exchange: exchange });
      });
      renderModelLabResults(); renderModelLabInspector();
    }
    var counts = {};
    lab.rows.forEach(function (item) { if (item.exchange) {
      var verdict = item.exchange.result.Verdict; counts[verdict] = (counts[verdict] || 0) + 1;
    } });
    $("modelLabStatus").textContent = lab.cancelled ? "Остановлено. Полученные результаты сохранены в панели." :
      "Завершено · " + Object.keys(counts).map(function (verdict) { return modelDiagnosticVerdicts[verdict] + ": " + counts[verdict]; }).join(" · ");
  } catch (error) {
    if (row) { row.phase = lab.cancelled ? "Cancelled" : "Inconclusive"; row.error = error.message; lab.selected = row; }
    $("modelLabStatus").textContent = lab.cancelled ? "Остановлено. Активная проверка отменена." : "Серия остановлена: " + error.message;
  } finally {
    lab.request = null; lab.running = false;
    renderModelLabResults(); renderModelLabInspector(); updateModelLabControls();
    if (!lab.catalog && state.diagnosticsTab === "model-lab") openModelDiagnosticsLab(true);
  }
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
  $("modelLabRefresh").addEventListener("click", function () { openModelDiagnosticsLab(true, true); });
  $("modelLabRun").addEventListener("click", runModelDiagnosticsLab);
  $("modelLabStop").addEventListener("click", stopModelDiagnosticsLab);
  $("modelLabExport").addEventListener("click", exportModelDiagnosticsLab);
}
