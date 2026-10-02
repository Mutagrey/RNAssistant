var promptContextInspectorRequest = null;
var promptContextInspectorSnapshot = null;
var promptContextInspectorRawText = "";

function closePromptContextDownload(operation) {
  if (!operation || !operation.lease) return;
  var lease = operation.lease;
  operation.lease = null;
  try {
    Promise.resolve(send("closeResourceData", { chatId: operation.chatId,
      workspaceId: "context-inspector", leaseId: lease.leaseId })).catch(function () {});
  } catch (_) { /* Expiry also releases a lease if the bridge has gone away. */ }
}

function cancelPromptContextInspection() {
  var operation = promptContextInspectorRequest;
  promptContextInspectorRequest = null;
  if (operation) {
    operation.cancelled = true;
    operation.abort.abort();
    closePromptContextDownload(operation);
  }
  promptContextInspectorRawText = "";
  promptContextInspectorSnapshot = null;
}

function promptContextInspectorValue(item, camel, pascal, fallback) {
  item = item || {};
  if (item[camel] !== undefined) return item[camel];
  if (item[pascal] !== undefined) return item[pascal];
  return fallback;
}

function promptContextInspectorOpen() {
  var panel = $("promptContextInspector");
  return !!panel && !panel.classList.contains("hidden");
}

function setPromptContextInspectorOpen(open) {
  var panel = $("promptContextInspector");
  var trigger = $("contextMeter");
  if (!panel || !trigger) return;
  panel.classList.toggle("hidden", !open);
  panel.setAttribute("aria-hidden", open ? "false" : "true");
  trigger.setAttribute("aria-expanded", open ? "true" : "false");
  if (open) {
    loadPromptContextInspector(false);
  } else {
    cancelPromptContextInspection();
    clearPromptContextRawViewer();
  }
}

function closePromptContextInspector() {
  setPromptContextInspectorOpen(false);
}

function promptContextResourceDraftIds() {
  return (state.draftAttachments || []).map(function (item) {
    return typeof attachmentId === "function"
      ? attachmentId(item)
      : (item.Id || item.id || "");
  }).filter(Boolean);
}

async function loadPromptContextInspector(includeRaw) {
  if (promptContextInspectorRequest || !state.activeChatId || state.bridgeUnavailable) return;
  var chatId = state.activeChatId;
  var loading = $("promptContextInspectorLoading");
  var error = $("promptContextInspectorError");
  var body = $("promptContextInspectorBody");
  var refresh = $("refreshPromptContextInspectorButton");
  var rawButton = $("loadPromptContextRawButton");
  loading.classList.remove("hidden");
  loading.textContent = includeRaw ? "Готовлю JSON структуры запроса…" : "Собираю снимок контекста…";
  error.classList.add("hidden");
  error.textContent = "";
  body.classList.add("hidden");
  refresh.disabled = true;
  rawButton.disabled = true;

  promptContextInspectorRawText = "";
  clearPromptContextRawViewer();
  var operation = { chatId: chatId, abort: new AbortController(), cancelled: false, lease: null };
  function current() {
    return promptContextInspectorRequest === operation && !operation.cancelled &&
      promptContextInspectorOpen() && state.activeChatId === chatId;
  }
  promptContextInspectorRequest = operation;
  try {
    var response = await send("inspectPromptContext", {
      chatId: chatId, text: $("chatInput") ? $("chatInput").value : "",
      resourceDraftIds: promptContextResourceDraftIds(), includeRaw: !!includeRaw
    });
    operation.lease = response && response.rawData;
    if (!current()) return;
    if (includeRaw) {
      if (!operation.lease || !operation.lease.payload || !window.RNAssistantResourceDownload ||
          operation.lease.payload.contentType !== "text/plain; charset=utf-8")
        throw new Error("RESOURCE_DOWNLOAD_INVALID");
      var bytes = await window.RNAssistantResourceDownload.read(operation.lease, {
        maxBytes: 2 * 1024 * 1024, fetch: window.fetch.bind(window), signal: operation.abort.signal, isCurrent: current
      });
      if (!current()) return;
      promptContextInspectorRawText = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
    } else if (operation.lease) {
      throw new Error("RESOURCE_DOWNLOAD_INVALID");
    }
    promptContextInspectorSnapshot = response || {};
    if (includeRaw) $("promptContextInspectorEstimate").open = true;
    renderPromptContextInspector(promptContextInspectorSnapshot);
    syncPromptContextInspectorState();
  } catch (requestError) {
    if (!current()) return;
    promptContextInspectorRawText = "";
    error.textContent = requestError.detail || requestError.message || "Не удалось собрать контекст.";
    error.classList.remove("hidden");
  } finally {
    closePromptContextDownload(operation);
    if (promptContextInspectorRequest === operation) {
      promptContextInspectorRequest = null;
      if (promptContextInspectorOpen()) {
        loading.classList.add("hidden");
        refresh.disabled = false;
        rawButton.disabled = false;
      }
    }
  }
}

function renderPromptContextInspector(snapshot) {
  snapshot = snapshot || {};
  var used = Number(promptContextInspectorValue(snapshot, "usedTokens", "UsedTokens", 0) || 0);
  var admission = Number(promptContextInspectorValue(snapshot, "admissionTokens", "AdmissionTokens", used) || 0);
  var limit = Number(promptContextInspectorValue(snapshot, "inputLimitTokens", "InputLimitTokens", 0) || 0);
  var percent = Number(promptContextInspectorValue(snapshot, "percent", "Percent", limit ? Math.round(admission * 100 / limit) : 0) || 0);
  var windowTokens = Number(promptContextInspectorValue(snapshot, "contextWindowTokens", "ContextWindowTokens", 0) || 0);
  var outputTokens = Number(promptContextInspectorValue(snapshot, "reservedOutputTokens", "ReservedOutputTokens", 0) || 0);
  var safetyTokens = Number(promptContextInspectorValue(snapshot, "safetyTokens", "SafetyTokens", 0) || 0);
  var remaining = Number(promptContextInspectorValue(snapshot, "remainingInputTokens", "RemainingInputTokens", 0) || 0);
  var mode = promptContextInspectorValue(snapshot, "mode", "Mode", "agent");
  var model = promptContextInspectorValue(snapshot, "model", "Model", "");
  var overBudget = !!promptContextInspectorValue(snapshot, "overBudget", "OverBudget", false);
  var estimated = promptContextInspectorValue(snapshot, "estimated", "Estimated", true) !== false;
  var multiplier = Number(promptContextInspectorValue(snapshot, "estimateMultiplier", "EstimateMultiplier", 1) || 1);
  var intercept = Number(promptContextInspectorValue(snapshot, "estimateInterceptTokens", "EstimateInterceptTokens", 0) || 0);
  var calibrationSamples = Number(promptContextInspectorValue(snapshot, "calibrationSamples", "CalibrationSamples", 0) || 0);
  var generatedUtc = promptContextInspectorValue(snapshot, "generatedUtc", "GeneratedUtc", "");
  var subtitle = [mode === "chat" ? "Chat" : mode === "plan" ? "Plan" : "Agent", model,
    generatedUtc ? "снимок " + formatPromptContextTime(generatedUtc) : ""].filter(Boolean).join(" · ");

  percent = Math.max(0, Math.min(100, percent));
  $("promptContextInspectorSubtitle").textContent = "Последний API usage";
  $("promptContextInspectorEstimateSubtitle").textContent = subtitle;
  $("promptContextInspectorEstimateUsage").textContent = "Вход " + (estimated ? "≈ " : "") +
    formatNumber(used) + " / " + formatNumber(limit) + " токенов";
  $("promptContextInspectorPercent").textContent = percent + "%";
  $("promptContextInspectorAdmission").textContent = "С резервами ≈ " + formatNumber(admission) +
    " / " + formatNumber(limit) + " · резерв " + formatNumber(Math.max(0, admission - used));
  $("promptContextInspectorWindow").textContent = formatNumber(windowTokens);
  $("promptContextInspectorOutput").textContent = formatNumber(outputTokens);
  $("promptContextInspectorSafety").textContent = formatNumber(safetyTokens);
  $("promptContextInspectorRemaining").textContent = formatNumber(remaining);

  var track = $("promptContextInspectorTrack");
  var level = overBudget || percent >= 90 ? "danger" : (percent >= 70 ? "warn" : "ok");
  track.dataset.level = level;
  track.style.setProperty("--prompt-context-percent", percent + "%");
  track.setAttribute("aria-valuenow", String(percent));

  var notice = $("promptContextInspectorNotice");
  notice.textContent = promptContextInspectorValue(snapshot, "notice", "Notice", "≈ снимок контекста.") +
    (calibrationSamples
      ? " ×" + multiplier.toFixed(2).replace(".", ",") +
        (intercept > 0 ? " + " + formatNumber(Math.ceil(intercept)) : "")
      : "");
  notice.classList.toggle("is-over-budget", overBudget);

  renderPromptContextUsage(modelUsageFromContext(snapshot),
    promptContextInspectorValue(snapshot, "lastPromptUtc", "LastPromptUtc", ""));

  var sections = promptContextInspectorValue(snapshot, "sections", "Sections", []);
  renderPromptContextSummary(sections, used);
  renderPromptContextSections(sections, Math.max(1, used));
  renderPromptContextRaw(snapshot);
  $("promptContextInspectorLoading").classList.add("hidden");
  $("promptContextInspectorError").classList.add("hidden");
  $("promptContextInspectorBody").classList.remove("hidden");
}

function renderPromptContextUsage(last, lastPromptUtc) {
  var lastUsage = $("promptContextInspectorLastUsage");
  if (last) {
    $("promptContextInspectorUsage").textContent = last.prompt === null
      ? "Вход: API не сообщил число токенов"
      : "Вход " + formatNumber(last.prompt) + " токенов";
    var parts = ["API usage"];
    if (last.completion !== null) parts.push("выход " + formatNumber(last.completion));
    if (last.total !== null) parts.push("всего " + formatNumber(last.total));
    lastUsage.textContent = parts.join(" · ") + (parts.length > 1 ? " токенов" : "") +
      (lastPromptUtc ? " · " + formatPromptContextTime(lastPromptUtc) : "");
  } else {
    $("promptContextInspectorUsage").textContent = "Нет данных usage";
    lastUsage.textContent = "Точное число токенов появится после ответа с usage.";
  }
}

function renderPromptContextSummary(sections, usedTokens) {
  var root = $("promptContextInspectorSummary");
  root.replaceChildren();
  var chatIds = { history: true, tool_history: true, current_request: true };
  var resourceIds = { document_context: true, attachments: true, artifacts: true };
  var chat = 0;
  var resources = 0;
  (sections || []).forEach(function (section) {
    if (promptContextInspectorValue(section, "included", "Included", true) === false) return;
    var id = promptContextInspectorValue(section, "id", "Id", "");
    var tokens = Number(promptContextInspectorValue(section, "tokens", "Tokens", 0) || 0);
    if (chatIds[id]) chat += tokens;
    else if (resourceIds[id]) resources += tokens;
  });
  [
    ["Диалог и текущий запрос", chat],
    ["Ресурсы в запросе", resources],
    ["Инструкции, tools и skills", Math.max(0, usedTokens - chat - resources)]
  ].forEach(function (entry) {
    var row = document.createElement("div");
    var label = document.createElement("span");
    var value = document.createElement("strong");
    label.textContent = entry[0];
    value.textContent = "≈" + formatNumber(entry[1]);
    row.appendChild(label);
    row.appendChild(value);
    root.appendChild(row);
  });
}

function renderPromptContextSections(sections, usedTokens) {
  var roots = {
    request: $("promptContextInspectorSections"),
    reserve: $("promptContextInspectorReserves"),
    local: $("promptContextInspectorLocal")
  };
  Object.keys(roots).forEach(function (key) { roots[key].replaceChildren(); });
  var reserveTokens = 0;
  (sections || []).forEach(function (section) {
    var id = promptContextInspectorValue(section, "id", "Id", "");
    if (id === "format_repair_reserve" || id === "continuation_reserve") {
      reserveTokens += Number(promptContextInspectorValue(section, "tokens", "Tokens", 0) || 0);
    }
  });
  (sections || []).forEach(function (section) {
    var included = promptContextInspectorValue(section, "included", "Included", true) !== false;
    var id = promptContextInspectorValue(section, "id", "Id", "");
    var category = !included ? "local" :
      (id === "format_repair_reserve" || id === "continuation_reserve" ? "reserve" : "request");
    var estimated = category === "request";
    var root = roots[category];
    var tokens = Number(promptContextInspectorValue(section, "tokens", "Tokens", 0) || 0);
    var count = Number(promptContextInspectorValue(section, "count", "Count", 0) || 0);
    var details = document.createElement("details");
    details.className = "prompt-context-section" + (included ? "" : " is-excluded");
    details.open = category === "request" && root.childElementCount === 0;

    var summary = document.createElement("summary");
    var title = document.createElement("span");
    title.className = "prompt-context-section-title";
    title.textContent = promptContextInspectorValue(section, "title", "Title", "Раздел");
    summary.appendChild(title);
    var meta = document.createElement("span");
    meta.className = "prompt-context-section-meta";
    meta.textContent = included
      ? (estimated ? "≈" : "") + formatNumber(tokens) + " ток. · " + formatNumber(count)
      : formatNumber(count) + " элементов";
    summary.appendChild(meta);
    var detailText = promptContextInspectorValue(section, "detail", "Detail", "");
    if (detailText) {
      var detail = document.createElement("span");
      detail.className = "prompt-context-section-detail";
      detail.textContent = detailText;
      summary.appendChild(detail);
    }
    if (included) {
      var track = document.createElement("span");
      track.className = "prompt-context-section-track";
      var fill = document.createElement("i");
      var denominator = category === "reserve" ? Math.max(1, reserveTokens) : usedTokens;
      fill.style.setProperty("--prompt-context-section-percent", Math.min(100, Math.round(tokens * 100 / denominator)) + "%");
      track.style.setProperty("--prompt-context-section-percent", Math.min(100, Math.round(tokens * 100 / denominator)) + "%");
      track.appendChild(fill);
      summary.appendChild(track);
    }
    details.appendChild(summary);

    var items = document.createElement("div");
    items.className = "prompt-context-items";
    var values = promptContextInspectorValue(section, "items", "Items", []);
    if (!values || !values.length) {
      var empty = document.createElement("div");
      empty.className = "prompt-context-item-static";
      empty.textContent = "Нет элементов.";
      items.appendChild(empty);
    } else {
      values.forEach(function (item) { items.appendChild(renderPromptContextItem(item, included, estimated)); });
    }
    details.appendChild(items);
    root.appendChild(details);
  });
  $("promptContextInspectorReserveGroup").classList.toggle("hidden", !roots.reserve.childElementCount);
  $("promptContextInspectorLocalGroup").classList.toggle("hidden", !roots.local.childElementCount);
}

function renderPromptContextItem(item, included, estimated) {
  var preview = promptContextInspectorValue(item, "preview", "Preview", "");
  var reason = promptContextInspectorValue(item, "reason", "Reason", "");
  var subtitle = promptContextInspectorValue(item, "subtitle", "Subtitle", "");
  var tokens = Number(promptContextInspectorValue(item, "tokens", "Tokens", 0) || 0);
  var size = Number(promptContextInspectorValue(item, "sizeBytes", "SizeBytes", 0) || 0);
  var container = document.createElement(preview ? "details" : "div");
  container.className = "prompt-context-item";
  var row = document.createElement(preview ? "summary" : "div");
  if (!preview) row.className = "prompt-context-item-static";
  var title = document.createElement("span");
  title.className = "prompt-context-item-title";
  title.textContent = promptContextInspectorValue(item, "title", "Title", "Элемент");
  row.appendChild(title);
  var value = document.createElement("span");
  value.className = "prompt-context-item-value";
  var values = [];
  if (included && tokens) values.push((estimated ? "≈" : "") + formatNumber(tokens) + " ток.");
  if (size) values.push(formatPromptContextSize(size));
  value.textContent = values.join(" · ") || (included ? (estimated ? "≈" : "") + "0 ток." : "");
  row.appendChild(value);
  if (subtitle) {
    var subtitleNode = document.createElement("span");
    subtitleNode.className = "prompt-context-item-subtitle";
    subtitleNode.textContent = subtitle;
    row.appendChild(subtitleNode);
  }
  if (reason) {
    var reasonNode = document.createElement("span");
    reasonNode.className = "prompt-context-item-reason";
    reasonNode.textContent = reason;
    row.appendChild(reasonNode);
  }
  if (preview) {
    container.appendChild(row);
    var pre = document.createElement("pre");
    pre.textContent = preview;
    container.appendChild(pre);
  } else {
    container.appendChild(row);
  }
  return container;
}

function renderPromptContextRaw(snapshot) {
  var raw = promptContextInspectorRawText;
  var details = $("promptContextInspectorRaw");
  var button = $("loadPromptContextRawButton");
  clearPromptContextRawViewer();
  if (!raw) {
    details.classList.add("hidden");
    details.open = false;
    button.textContent = "Показать JSON";
    return;
  }
  details.classList.remove("hidden");
  details.open = true;
  mountPromptContextRawViewer(snapshot);
  updatePromptContextRawButton(snapshot);
}

function clearPromptContextRawViewer() {
  var target = $("promptContextInspectorRawText");
  if (target && window.RNAssistantViewerRegistry) {
    window.RNAssistantViewerRegistry.unmount(target);
  }
}

function mountPromptContextRawViewer(snapshot) {
  snapshot = snapshot || promptContextInspectorSnapshot || {};
  var raw = promptContextInspectorRawText;
  var target = $("promptContextInspectorRawText");
  var details = $("promptContextInspectorRaw");
  if (!raw || !target || !details || !details.open || target.firstElementChild) return;
  if (!window.RNAssistantViewerRegistry || !window.RNAssistantViewerRegistry.has("json")) {
    throw new Error("JSON viewer is unavailable.");
  }
  window.RNAssistantViewerRegistry.mount("json", target, {
    text: String(raw),
    completeness: promptContextInspectorValue(snapshot, "rawTruncated", "RawTruncated", false) ? "preview" : "full",
    mode: "tree",
    onCopy: window.copyTextResult
  });
}

function updatePromptContextRawButton(snapshot) {
  snapshot = snapshot || promptContextInspectorSnapshot || {};
  var raw = promptContextInspectorRawText;
  var truncated = !!promptContextInspectorValue(snapshot, "rawTruncated", "RawTruncated", false);
  var details = $("promptContextInspectorRaw");
  var button = $("loadPromptContextRawButton");
  if (!raw) button.textContent = "Показать JSON";
  else if (details.open) button.textContent = truncated ? "JSON сокращён · скрыть" : "Скрыть JSON";
  else button.textContent = truncated ? "Показать сокращённый JSON" : "Показать JSON";
}

function formatPromptContextSize(bytes) {
  if (typeof formatAttachmentSize === "function") return formatAttachmentSize(bytes);
  if (bytes < 1024) return bytes + " Б";
  if (bytes < 1024 * 1024) return Math.ceil(bytes / 1024) + " КБ";
  return (bytes / (1024 * 1024)).toFixed(1) + " МБ";
}

function formatPromptContextTime(value) {
  var date = new Date(value);
  if (isNaN(date.getTime())) return "";
  return date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

function togglePromptContextRaw() {
  var raw = promptContextInspectorSnapshot && promptContextInspectorRawText;
  if (!raw) {
    loadPromptContextInspector(true);
    return;
  }
  var details = $("promptContextInspectorRaw");
  details.open = !details.open;
  if (details.open) mountPromptContextRawViewer(promptContextInspectorSnapshot);
  else clearPromptContextRawViewer();
  updatePromptContextRawButton(promptContextInspectorSnapshot);
}

function renderPromptContextInspectorAvailability() {
  var trigger = $("contextMeter");
  if (!trigger) return;
  var disabled = hasActiveMessageEdit() || state.bridgeUnavailable || !state.activeChatId;
  trigger.disabled = disabled;
  if (disabled && promptContextInspectorOpen()) closePromptContextInspector();
}

function syncPromptContextInspectorState() {
  if (promptContextInspectorRequest && promptContextInspectorRequest.chatId !== state.activeChatId) {
    closePromptContextInspector();
    return;
  }
  if (!promptContextInspectorOpen() || !promptContextInspectorSnapshot) return;
  var snapshotChatId = promptContextInspectorValue(promptContextInspectorSnapshot, "chatId", "ChatId", "");
  if (snapshotChatId && snapshotChatId !== state.activeChatId) {
    closePromptContextInspector();
    return;
  }
  var active = typeof activeChatSummary === "function" ? activeChatSummary() : null;
  var activeRevision = Number(promptContextInspectorValue(active, "revision", "Revision", 0) || 0);
  var snapshotRevision = Number(promptContextInspectorValue(promptContextInspectorSnapshot, "sessionRevision", "SessionRevision", 0) || 0);
  if (activeRevision && snapshotRevision && activeRevision !== snapshotRevision) {
    if (activeRevision > snapshotRevision) renderPromptContextUsage(lastModelUsage(), "");
    var notice = $("promptContextInspectorNotice");
    if (notice && notice.textContent.indexOf("Состояние чата изменилось") < 0) {
      notice.textContent += " Состояние чата изменилось — нажмите «Обновить».";
    }
  }
}

function bindContextInspectorActions() {
  $("contextMeter").addEventListener("click", function () {
    if ($("contextMeter").disabled) return;
    setPromptContextInspectorOpen(!promptContextInspectorOpen());
  });
  $("closePromptContextInspectorButton").addEventListener("click", closePromptContextInspector);
  $("refreshPromptContextInspectorButton").addEventListener("click", function () {
    loadPromptContextInspector(false);
  });
  $("loadPromptContextRawButton").addEventListener("click", togglePromptContextRaw);
  $("promptContextInspectorRaw").addEventListener("toggle", function () {
    if ($("promptContextInspectorRaw").open) mountPromptContextRawViewer(promptContextInspectorSnapshot);
    else clearPromptContextRawViewer();
    updatePromptContextRawButton(promptContextInspectorSnapshot);
  });
  $("managePromptContextButton").addEventListener("click", function () {
    closePromptContextInspector();
    if (typeof setContextManagerOpen === "function") setContextManagerOpen(true);
    if ($("contextManager")) $("contextManager").scrollIntoView({ block: "nearest" });
  });
  $("openPromptContextArtifactsButton").addEventListener("click", function () {
    closePromptContextInspector();
    switchTab("artifacts");
  });
  document.addEventListener("keydown", function (event) {
    if (event.key === "Escape" && promptContextInspectorOpen()) {
      event.preventDefault();
      closePromptContextInspector();
      $("contextMeter").focus();
    }
  });
}
