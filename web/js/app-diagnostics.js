function modelDiagnosticValue(source, pascal, camel, fallback) {
  source = source || {};
  return source[pascal] !== undefined ? source[pascal] : (source[camel] !== undefined ? source[camel] : fallback);
}

function formatDiagnosticDuration(value) {
  var milliseconds = Number(value || 0);
  if (milliseconds < 1000) return Math.max(0, Math.round(milliseconds)) + " мс";
  return (milliseconds / 1000).toFixed(milliseconds < 10000 ? 1 : 0) + " с";
}

var performanceDiagnostics = (function () {
  var navigation = [], model = [], bridge = [], hostPosts = [], longTasks = [];
  var nextId = 0;
  function bounded(list, entry, limit) {
    list.unshift(entry);
    if (list.length > limit) list.length = limit;
  }
  function visible() {
    var settings = $("tab-settings"), page = document.querySelector('.settings-page[data-settings-page="service"]');
    var overview = document.querySelector('[data-diagnostics-panel="overview"]');
    return settings && settings.classList.contains("active") && page && page.classList.contains("active") &&
      overview && overview.classList.contains("active");
  }
  function duration(value) { return value !== null && value !== undefined && Number.isFinite(Number(value))
    ? formatDiagnosticDuration(value) : "—"; }
  var bridgeStageLabels = { lease: "доступ к плагинам", workspace: "проект",
    load: "хранилище", state: "состояние", messages: "сообщения",
    selection: "выбор", preview: "предпросмотр", currentDocument: "текущий документ",
    hostSwitch: "смена Office host" };
  function render() {
    if (!visible()) return;
    var root = $("performanceDiagnosticsResults");
    if (!root) return;
    var lines = [];
    navigation.slice(0, 4).forEach(function (entry) {
      var age = duration(Date.now() - entry.startedMs);
      var part = entry.phase === "waiting" ? "ожидание bridge " + age + " (с " + entry.at + ")" :
        entry.phase === "queued" ? "в очереди " + age + " (с " + entry.at + ")" :
        entry.phase === "skipped" ? "заменено новым переключением" :
        entry.phase === "failed" ? "ошибка после " + duration(entry.queueMs + entry.bridgeMs) :
        "очередь " + duration(entry.queueMs) + ", bridge " + duration(entry.bridgeMs) +
          ", рендер " + duration(entry.renderMs) + ", сообщений " + entry.messages;
      lines.push("Чат · " + entry.kind + ": " + part);
    });
    bridge.slice(0, 4).forEach(function (entry) {
      lines.push("Bridge · " + entry.operation + ": работа " + duration(entry.handleMs) +
        ", JSON " + duration(entry.serializeMs) + ", ответ " + Math.round(entry.responseChars / 1024) + " КБ");
      if (Array.isArray(entry.stages) && entry.stages.length) {
        var stages = entry.stages.slice(0, 8).map(function (stage) {
          var name = Object.prototype.hasOwnProperty.call(bridgeStageLabels, stage.name)
            ? bridgeStageLabels[stage.name] : String(stage.name || "этап");
          return name + " " + duration(stage.elapsedMs);
        });
        var covered = entry.stages.reduce(function (sum, stage) {
          var elapsed = Number(stage.elapsedMs);
          return sum + (Number.isFinite(elapsed) && elapsed >= 0 ? elapsed : 0);
        }, 0);
        if (entry.handleMs - covered >= 100) stages.push("прочее " + duration(entry.handleMs - covered));
        lines.push("  Этапы: " + stages.join(", "));
      }
    });
    hostPosts.slice(0, 2).forEach(function (entry) {
      lines.push("Office UI → WebView: " + duration(entry.elapsedMs) +
        ", сообщение " + Math.round(entry.responseChars / 1024) + " КБ");
    });
    model.slice(0, 4).forEach(function (entry) {
      var value = entry.value, phase = String(value.phase || "");
      var stage = phase === "preparing" ? "подготовка HTTP" : phase === "sending" ? "ожидание заголовков" :
        phase === "headers" ? "ожидание данных" : phase === "first_chunk" ? "получение ответа" :
        phase === "completed" ? "завершён" : phase === "cancelled" ? "отменён" : "ошибка";
      var summary = "подготовка " + duration(value.preparationMs) +
        ", заголовки " + (value.responseHeadersMs == null || value.preparationMs == null ? "—" :
          duration(value.responseHeadersMs - value.preparationMs)) +
        ", первые данные " + (value.firstChunkMs == null || value.responseHeadersMs == null ? "—" :
          duration(value.firstChunkMs - value.responseHeadersMs)) +
        ", всего " + duration(value.totalMs == null ? value.elapsedMs : value.totalMs);
      lines.push("Модель · " + String(value.requestId || "?").slice(0, 12) + ": " + stage +
        " (" + summary + ")" + (value.statusCode == null ? "" : ", HTTP " + value.statusCode) +
        (value.requestBytes == null ? "" : ", запрос " + Math.round(value.requestBytes / 1024) + " КБ") +
        (value.failureKind ? ", " + String(value.failureKind).slice(0, 40) : "") +
        (value.error ? ", " + String(value.error).replace(/[\r\n]+/g, " ").slice(0, 160) : "") +
        (phase === "completed" || phase === "failed" || phase === "cancelled" ? "" :
          ", в этапе " + duration(Date.now() - entry.updatedAt)));
      var context = value.contextPreparation;
      if (context) lines.push("  Формирование запроса: всего " + duration(context.totalMs) +
        ", ресурсы " + duration(context.authorityMs) + ", настройка " + duration(context.setupMs) +
        ", контекст " + duration(context.compileMs) + ", запись " + duration(context.traceMs));
    });
    longTasks.slice(0, 2).forEach(function (entry) {
      lines.push("Интерфейс · долгая задача: " + duration(entry.durationMs) + " (" + entry.at + ")");
    });
    root.textContent = lines.length ? lines.join("\n") : "Пока нет измерений.";
  }
  function beginNavigation(kind) {
    var entry = { id: ++nextId, kind: kind, phase: "queued",
      startedMs: Date.now(), at: new Date().toLocaleTimeString("ru") };
    bounded(navigation, entry, 12); render(); return entry.id;
  }
  function updateNavigation(id, values) {
    var entry = navigation.find(function (item) { return item.id === id; });
    if (!entry) return;
    Object.assign(entry, values); render();
  }
  function recordModel(value) {
    if (!value || !value.requestId) return;
    var index = model.findIndex(function (entry) { return entry.value.requestId === value.requestId; });
    if (index >= 0) value = Object.assign({}, model.splice(index, 1)[0].value, value);
    bounded(model, { value: value, updatedAt: Date.now() }, 12);
    render();
  }
  function recordBridge(value) { if (value) { bounded(bridge, value, 12); render(); } }
  function recordHostPost(value) { if (value) { bounded(hostPosts, value, 8); render(); } }
  function recordLongTask(value) {
    bounded(longTasks, { durationMs: value.duration, at: new Date().toLocaleTimeString("ru") }, 8);
    render();
  }
  function observeLongTasks() {
    if (typeof PerformanceObserver !== "function") return;
    try {
      var observer = new PerformanceObserver(function (list) {
        list.getEntries().forEach(function (entry) { if (entry.duration >= 100) recordLongTask(entry); });
      });
      observer.observe({ entryTypes: ["longtask"] });
    } catch (_) { /* Older WebView2 may not expose long tasks. */ }
  }
  return { beginNavigation: beginNavigation, updateNavigation: updateNavigation,
    recordModel: recordModel, recordBridge: recordBridge, recordHostPost: recordHostPost,
    observeLongTasks: observeLongTasks, render: render };
})();
window.RNAssistantPerformanceDiagnostics = performanceDiagnostics;

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
  name = name === "overview" || name === "trajectory" || name === "model-lab" ? name : "runtime-log";
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
  if (name === "overview") performanceDiagnostics.render();
  if (name === "model-lab" && typeof openModelDiagnosticsLab === "function") openModelDiagnosticsLab();
}

function bindDiagnosticsActions() {
  if (typeof bindModelDiagnosticsLab === "function") bindModelDiagnosticsLab();
  performanceDiagnostics.observeLongTasks();
  Array.prototype.slice.call(document.querySelectorAll(".diagnostics-tab-button")).forEach(function (tab) {
    tab.addEventListener("click", function () {
      setDiagnosticsTab(tab.getAttribute("data-diagnostics-tab"), true);
    });
  });
  setDiagnosticsTab(state.diagnosticsTab || "runtime-log", false);
  var button = $("testModelConnectionButton");
  var timingRefresh = $("refreshPerformanceDiagnosticsButton");
  if (timingRefresh) timingRefresh.addEventListener("click", performanceDiagnostics.render);
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
