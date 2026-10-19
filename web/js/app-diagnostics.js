function modelDiagnosticValue(source, pascal, camel, fallback) {
  source = source || {};
  return source[pascal] !== undefined ? source[pascal] : (source[camel] !== undefined ? source[camel] : fallback);
}

function formatDiagnosticDuration(value) {
  var milliseconds = Number(value || 0);
  if (milliseconds < 1000) return Math.max(0, Math.round(milliseconds)) + " мс";
  return (milliseconds / 1000).toFixed(milliseconds < 10000 ? 1 : 0) + " с";
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

function setDiagnosticsTab(name, refreshRecords) {
  name = name === "overview" || name === "trajectory" ? name : "runtime-log";
  state.diagnosticsTab = name;
  Array.prototype.slice.call(document.querySelectorAll(".diagnostics-tab-button")).forEach(function (button) {
    var active = button.getAttribute("data-diagnostics-tab") === name;
    button.classList.toggle("active", active);
    button.setAttribute("aria-selected", active ? "true" : "false");
  });
  Array.prototype.slice.call(document.querySelectorAll(".diagnostics-tab-panel")).forEach(function (panel) {
    var target = panel.getAttribute("data-diagnostics-panel");
    var active = target === name || target === "records" && name === "trajectory";
    panel.classList.toggle("active", active);
  });
  if (name === "trajectory" && typeof setTrajectoryDiagnosticsMode === "function") {
    setTrajectoryDiagnosticsMode("trajectory", refreshRecords !== false);
  }
  if (name === "runtime-log" && typeof updateLogScrollButton === "function") {
    requestAnimationFrame(updateLogScrollButton);
  }
}

function bindDiagnosticsActions() {
  Array.prototype.slice.call(document.querySelectorAll(".diagnostics-tab-button")).forEach(function (tab) {
    tab.addEventListener("click", function () {
      setDiagnosticsTab(tab.getAttribute("data-diagnostics-tab"), true);
    });
  });
  setDiagnosticsTab(state.diagnosticsTab || "runtime-log", false);
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

window.setDiagnosticsTab = setDiagnosticsTab;
