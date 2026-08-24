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

function bindDiagnosticsActions() {
  renderModelConnectionIndicator();
  var button = $("testModelConnectionButton");
  if (!button) return;
  button.addEventListener("click", async function () {
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
}
