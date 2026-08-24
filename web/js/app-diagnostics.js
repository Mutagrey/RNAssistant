function modelDiagnosticValue(source, pascal, camel, fallback) {
  source = source || {};
  return source[pascal] !== undefined ? source[pascal] : (source[camel] !== undefined ? source[camel] : fallback);
}

function modelDiagnosticPhase(update) {
  return String(modelDiagnosticValue(update, "Phase", "phase", "") || "").toLowerCase();
}

function modelDiagnosticIsActive(update) {
  var phase = modelDiagnosticPhase(update);
  return phase === "preparing" || phase === "sending" || phase === "headers" || phase === "first_chunk";
}

function formatDiagnosticDuration(value) {
  var milliseconds = Number(value || 0);
  if (milliseconds < 1000) return Math.max(0, Math.round(milliseconds)) + " мс";
  return (milliseconds / 1000).toFixed(milliseconds < 10000 ? 1 : 0) + " с";
}

function currentModelDiagnosticElapsed(update) {
  if (modelDiagnosticIsActive(update) && state.modelDiagnosticsLocalStart) {
    return Math.max(Number(modelDiagnosticValue(update, "ElapsedMs", "elapsedMs", 0) || 0), Date.now() - state.modelDiagnosticsLocalStart);
  }
  return Number(modelDiagnosticValue(update, "TotalMs", "totalMs",
    modelDiagnosticValue(update, "ElapsedMs", "elapsedMs", 0)) || 0);
}

function modelDiagnosticPhaseLabel(update) {
  var phase = modelDiagnosticPhase(update);
  if (phase === "preparing") return "Подготовка";
  if (phase === "sending") return "Ожидание API";
  if (phase === "headers") return "Ожидание данных";
  if (phase === "first_chunk") return modelDiagnosticValue(update, "StreamRequested", "streamRequested", false) ? "Поток данных" : "Получение ответа";
  if (phase === "completed") return "Связь в норме";
  if (phase === "cancelled") return "Запрос остановлен";
  if (phase === "failed") return "Ошибка связи";
  return "Связь не проверена";
}

function modelDiagnosticTooltip(update) {
  if (!update) return "Связь с моделью ещё не проверялась.";
  var lines = [modelDiagnosticPhaseLabel(update)];
  var model = modelDiagnosticValue(update, "Model", "model", "");
  var preparation = modelDiagnosticValue(update, "PreparationMs", "preparationMs", null);
  var headers = modelDiagnosticValue(update, "ResponseHeadersMs", "responseHeadersMs", null);
  var firstChunk = modelDiagnosticValue(update, "FirstChunkMs", "firstChunkMs", null);
  var total = modelDiagnosticValue(update, "TotalMs", "totalMs", null);
  var status = modelDiagnosticValue(update, "StatusCode", "statusCode", null);
  var error = modelDiagnosticValue(update, "Error", "error", "");
  if (model) lines.push("Модель: " + model);
  if (preparation !== null) lines.push("Подготовка: " + formatDiagnosticDuration(preparation));
  if (headers !== null) lines.push("API до заголовков: " + formatDiagnosticDuration(Math.max(0, headers - Number(preparation || 0))));
  if (firstChunk !== null) lines.push("Данные после заголовков: " + formatDiagnosticDuration(Math.max(0, firstChunk - Number(headers || 0))));
  if (total !== null) lines.push("Всего: " + formatDiagnosticDuration(total));
  if (status !== null) lines.push("HTTP: " + status);
  if (error) lines.push(error);
  return lines.join("\n");
}

function renderModelConnectionIndicator() {
  var root = $("modelConnectionIndicator");
  var text = $("modelConnectionIndicatorText");
  if (!root || !text) return;
  var update = state.modelDiagnostics;
  var phase = modelDiagnosticPhase(update);
  var elapsed = update ? currentModelDiagnosticElapsed(update) : 0;
  root.className = "model-connection-indicator";
  if (!update) {
    root.classList.add("is-idle");
    text.textContent = "—";
  } else if (phase === "completed") {
    root.classList.add("is-ok");
    text.textContent = formatDiagnosticDuration(elapsed);
  } else if (phase === "failed") {
    root.classList.add("is-failed");
    text.textContent = "Ошибка";
  } else if (phase === "cancelled") {
    root.classList.add("is-idle");
    text.textContent = "Стоп";
  } else {
    root.classList.add(elapsed >= 10000 ? "is-slow" : "is-active");
    text.textContent = formatDiagnosticDuration(elapsed);
  }
  root.title = modelDiagnosticTooltip(update);
  root.setAttribute("aria-label", root.title);
}

function scheduleModelDiagnosticRender() {
  if (state.modelDiagnosticsTimer) {
    window.clearTimeout(state.modelDiagnosticsTimer);
    state.modelDiagnosticsTimer = null;
  }
  if (!modelDiagnosticIsActive(state.modelDiagnostics)) return;
  state.modelDiagnosticsTimer = window.setTimeout(function () {
    state.modelDiagnosticsTimer = null;
    renderModelConnectionIndicator();
    scheduleModelDiagnosticRender();
  }, 500);
}

function handleModelDiagnosticsUpdate(update) {
  var requestId = modelDiagnosticValue(update, "RequestId", "requestId", "");
  var currentId = modelDiagnosticValue(state.modelDiagnostics, "RequestId", "requestId", "");
  var phase = modelDiagnosticPhase(update);
  if (phase !== "preparing" && currentId && requestId && currentId !== requestId) return;
  state.modelDiagnostics = update || null;
  state.modelDiagnosticsLocalStart = Date.now() - Number(modelDiagnosticValue(update, "ElapsedMs", "elapsedMs", 0) || 0);
  renderModelConnectionIndicator();
  scheduleModelDiagnosticRender();
}

function appendConnectionMetric(root, label, value) {
  if (value === null || value === undefined || value === "") return;
  var item = document.createElement("div");
  var name = document.createElement("span");
  var metric = document.createElement("strong");
  name.textContent = label;
  metric.textContent = value;
  item.appendChild(name);
  item.appendChild(metric);
  root.appendChild(item);
}

function renderModelConnectionTestResult(result) {
  var root = $("modelConnectionResults");
  if (!root) return;
  root.replaceChildren();
  var success = !!modelDiagnosticValue(result, "Success", "success", false);
  var summary = document.createElement("div");
  summary.className = "model-compatibility-summary " + (success ? "passed" : "failed");
  summary.textContent = modelDiagnosticValue(result, "Summary", "summary", success ? "Модель ответила." : "Проверка не пройдена.");
  root.appendChild(summary);

  var diagnostics = modelDiagnosticValue(result, "Diagnostics", "diagnostics", null) || {};
  var metrics = document.createElement("div");
  metrics.className = "model-connection-metrics";
  appendConnectionMetric(metrics, "Подготовка", diagnosticMetric(diagnostics, "PreparationMs", "preparationMs"));
  appendConnectionMetric(metrics, "API", diagnosticStageMetric(diagnostics, "ResponseHeadersMs", "responseHeadersMs", "PreparationMs", "preparationMs"));
  appendConnectionMetric(metrics, "Первые данные", diagnosticStageMetric(diagnostics, "FirstChunkMs", "firstChunkMs", "ResponseHeadersMs", "responseHeadersMs"));
  appendConnectionMetric(metrics, "Всего", formatDiagnosticDuration(modelDiagnosticValue(result, "DurationMs", "durationMs", 0)));
  var status = modelDiagnosticValue(diagnostics, "StatusCode", "statusCode", null);
  appendConnectionMetric(metrics, "HTTP", status === null ? null : String(status));
  root.appendChild(metrics);

  var error = modelDiagnosticValue(result, "Error", "error", "");
  if (error) {
    var errorNode = document.createElement("div");
    errorNode.className = "model-connection-error";
    errorNode.textContent = error;
    root.appendChild(errorNode);
  }
}

function diagnosticMetric(source, pascal, camel) {
  var value = modelDiagnosticValue(source, pascal, camel, null);
  return value === null ? null : formatDiagnosticDuration(value);
}

function diagnosticStageMetric(source, endPascal, endCamel, startPascal, startCamel) {
  var end = modelDiagnosticValue(source, endPascal, endCamel, null);
  if (end === null) return null;
  var start = Number(modelDiagnosticValue(source, startPascal, startCamel, 0) || 0);
  return formatDiagnosticDuration(Math.max(0, Number(end) - start));
}

var lastCasHealthReport = null;

function formatStorageBytes(value) {
  var bytes = Number(value || 0);
  if (bytes < 1024) return Math.max(0, Math.round(bytes)) + " B";
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + " KB";
  if (bytes < 1024 * 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(1) + " MB";
  return (bytes / (1024 * 1024 * 1024)).toFixed(2) + " GB";
}

function shortCasHash(value) {
  value = String(value || "");
  return value.length > 16 ? value.slice(0, 12) + "…" + value.slice(-4) : value;
}

function renderCasHealthReport(report, actionMessage) {
  var root = $("casHealthResults");
  var collect = $("collectCasButton");
  if (!root) return;
  lastCasHealthReport = report || null;
  root.replaceChildren();

  var complete = !!modelDiagnosticValue(report, "ReachabilityComplete", "reachabilityComplete", false);
  var missing = Number(modelDiagnosticValue(report, "MissingBlobCount", "missingBlobCount", 0) || 0);
  var corrupt = Number(modelDiagnosticValue(report, "CorruptBlobCount", "corruptBlobCount", 0) || 0);
  var orphan = Number(modelDiagnosticValue(report, "OrphanBlobCount", "orphanBlobCount", 0) || 0);
  var summary = document.createElement("div");
  if (!complete) {
    summary.className = "model-compatibility-summary failed";
    summary.textContent = "Reachability неполный — удаление заблокировано.";
  } else if (missing || corrupt) {
    summary.className = "model-compatibility-summary failed";
    summary.textContent = "Найдены отсутствующие или повреждённые referenced blobs.";
  } else if (orphan) {
    summary.className = "model-compatibility-summary warning";
    summary.textContent = "Найдены безопасные кандидаты на очистку: " + orphan + ".";
  } else {
    summary.className = "model-compatibility-summary passed";
    summary.textContent = "CAS и все ссылки целы; orphan blobs нет.";
  }
  if (actionMessage) summary.textContent = actionMessage + " " + summary.textContent;
  root.appendChild(summary);

  var metrics = document.createElement("div");
  metrics.className = "model-connection-metrics";
  appendConnectionMetric(metrics, "Chat streams", String(modelDiagnosticValue(report, "ChatStreamCount", "chatStreamCount", 0)));
  appendConnectionMetric(metrics, "VBA journals", String(modelDiagnosticValue(report, "VbaJournalCount", "vbaJournalCount", 0)));
  appendConnectionMetric(metrics, "Referenced", String(modelDiagnosticValue(report, "ReferencedBlobCount", "referencedBlobCount", 0)));
  appendConnectionMetric(metrics, "CAS", String(modelDiagnosticValue(report, "StoredBlobCount", "storedBlobCount", 0)) + " · " +
    formatStorageBytes(modelDiagnosticValue(report, "StoredByteLength", "storedByteLength", 0)));
  appendConnectionMetric(metrics, "Missing / corrupt", missing + " / " + corrupt);
  appendConnectionMetric(metrics, "Orphan", orphan + " · " +
    formatStorageBytes(modelDiagnosticValue(report, "OrphanStoredByteLength", "orphanStoredByteLength", 0)));
  root.appendChild(metrics);

  var issues = modelDiagnosticValue(report, "Issues", "issues", []) || [];
  var orphans = modelDiagnosticValue(report, "OrphanBlobs", "orphanBlobs", []) || [];
  var lines = issues.slice(0, 12).map(function (issue) {
    var kind = modelDiagnosticValue(issue, "Kind", "kind", "issue");
    var source = modelDiagnosticValue(issue, "SourceId", "sourceId", "");
    var hash = modelDiagnosticValue(issue, "Sha256", "sha256", "");
    var message = modelDiagnosticValue(issue, "Message", "message", "");
    return "[" + kind + "] " + [source, hash ? shortCasHash(hash) : "", message].filter(Boolean).join(" · ");
  });
  if (!issues.length && orphans.length) {
    lines = orphans.slice(0, 12).map(function (item) {
      return "[orphan] " + shortCasHash(modelDiagnosticValue(item, "Sha256", "sha256", "")) + " · " +
        formatStorageBytes(modelDiagnosticValue(item, "StoredByteLength", "storedByteLength", 0));
    });
  }
  if (modelDiagnosticValue(report, "DetailsTruncated", "detailsTruncated", false)) lines.push("…детали ограничены bridge preview");
  if (lines.length) {
    var details = document.createElement("pre");
    details.className = "cas-health-details";
    details.textContent = lines.join("\n");
    root.appendChild(details);
  }
  if (collect) collect.disabled = !modelDiagnosticValue(report, "CanGarbageCollect", "canGarbageCollect", false) || orphan === 0;
}

async function auditCasHealth() {
  var audit = $("auditCasButton");
  var collect = $("collectCasButton");
  var root = $("casHealthResults");
  try {
    audit.disabled = true;
    collect.disabled = true;
    audit.textContent = "Проверяю…";
    if (root) root.textContent = "Проверяю event streams, VBA journals и CAS…";
    renderCasHealthReport(await send("getCasHealth", {}));
  } catch (error) {
    lastCasHealthReport = null;
    if (root) root.textContent = "Проверка CAS не выполнена: " + error.message;
    log(error.message, "error");
  } finally {
    audit.disabled = false;
    audit.textContent = "Проверить CAS";
  }
}

async function collectCasGarbage() {
  var count = Number(modelDiagnosticValue(lastCasHealthReport, "OrphanBlobCount", "orphanBlobCount", 0) || 0);
  var bytes = modelDiagnosticValue(lastCasHealthReport, "OrphanStoredByteLength", "orphanStoredByteLength", 0);
  if (!count || !window.confirm("Удалить " + count + " orphan blobs (" + formatStorageBytes(bytes) + ")? Отмена невозможна.")) return;
  var audit = $("auditCasButton");
  var collect = $("collectCasButton");
  try {
    audit.disabled = true;
    collect.disabled = true;
    collect.textContent = "Удаляю…";
    var result = await send("collectCasGarbage", {});
    var deleted = Number(modelDiagnosticValue(result, "DeletedBlobCount", "deletedBlobCount", 0) || 0);
    var deletedBytes = modelDiagnosticValue(result, "DeletedStoredByteLength", "deletedStoredByteLength", 0);
    renderCasHealthReport(modelDiagnosticValue(result, "Health", "health", {}),
      "Удалено: " + deleted + " (" + formatStorageBytes(deletedBytes) + ").");
    log("CAS GC удалил orphan blobs: " + deleted + ".", "success");
  } catch (error) {
    log(error.message, "error");
    var root = $("casHealthResults");
    if (root) root.textContent = "Очистка CAS не выполнена: " + error.message;
  } finally {
    audit.disabled = false;
    collect.textContent = "Удалить orphan";
    if (lastCasHealthReport) {
      collect.disabled = !modelDiagnosticValue(lastCasHealthReport, "CanGarbageCollect", "canGarbageCollect", false) ||
        !modelDiagnosticValue(lastCasHealthReport, "OrphanBlobCount", "orphanBlobCount", 0);
    }
  }
}

function bindDiagnosticsActions() {
  renderModelConnectionIndicator();
  var button = $("testModelConnectionButton");
  if (button) button.addEventListener("click", async function () {
    var root = $("modelConnectionResults");
    try {
      button.disabled = true;
      button.textContent = "Проверяю…";
      if (root) root.textContent = "Отправляю короткий запрос текущей модели…";
      await persistSettingsFromForm();
      var result = await send("testModelConnection", {});
      renderModelConnectionTestResult(result);
      log("Проверка связи с моделью завершена.");
    } catch (error) {
      if (root) root.textContent = "Тест не выполнен: " + error.message;
      log(error.message, "error");
    } finally {
      button.disabled = false;
      button.textContent = "Проверить модель";
    }
  });
  var auditCas = $("auditCasButton");
  var collectCas = $("collectCasButton");
  if (auditCas) auditCas.addEventListener("click", auditCasHealth);
  if (collectCas) collectCas.addEventListener("click", collectCasGarbage);
}
