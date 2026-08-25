var promptContextInspectorRequest = null;
var promptContextInspectorSnapshot = null;

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
  }
}

function closePromptContextInspector() {
  setPromptContextInspectorOpen(false);
}

function promptContextAttachmentIds() {
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

  var request = send("inspectPromptContext", {
    chatId: chatId,
    text: $("chatInput") ? $("chatInput").value : "",
    attachmentIds: promptContextAttachmentIds(),
    includeRaw: !!includeRaw
  });
  promptContextInspectorRequest = request;
  try {
    var response = await request;
    if (!promptContextInspectorOpen() || state.activeChatId !== chatId) return;
    promptContextInspectorSnapshot = response || {};
    renderPromptContextInspector(promptContextInspectorSnapshot);
  } catch (requestError) {
    if (!promptContextInspectorOpen() || state.activeChatId !== chatId) return;
    error.textContent = requestError.detail || requestError.message || "Не удалось собрать контекст.";
    error.classList.remove("hidden");
  } finally {
    if (promptContextInspectorRequest === request) promptContextInspectorRequest = null;
    if (promptContextInspectorOpen()) {
      loading.classList.add("hidden");
      refresh.disabled = false;
      rawButton.disabled = false;
    }
  }
}

function renderPromptContextInspector(snapshot) {
  snapshot = snapshot || {};
  var used = Number(promptContextInspectorValue(snapshot, "usedTokens", "UsedTokens", 0) || 0);
  var limit = Number(promptContextInspectorValue(snapshot, "inputLimitTokens", "InputLimitTokens", 0) || 0);
  var percent = Number(promptContextInspectorValue(snapshot, "percent", "Percent", limit ? Math.round(used * 100 / limit) : 0) || 0);
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
  var subtitle = ["Следующий запрос", mode === "chat" ? "Chat" : "Agent", model,
    generatedUtc ? "снимок " + formatPromptContextTime(generatedUtc) : ""].filter(Boolean).join(" · ");

  percent = Math.max(0, Math.min(100, percent));
  $("promptContextInspectorSubtitle").textContent = subtitle;
  $("promptContextInspectorUsage").textContent = (estimated ? "≈ " : "") + formatNumber(used) + " / " + formatNumber(limit) + " токенов";
  $("promptContextInspectorPercent").textContent = percent + "%";
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

  var lastPrompt = promptContextInspectorValue(snapshot, "lastPromptTokens", "LastPromptTokens", null);
  var lastPromptUtc = promptContextInspectorValue(snapshot, "lastPromptUtc", "LastPromptUtc", "");
  var lastUsage = $("promptContextInspectorLastUsage");
  if (lastPrompt !== null && lastPrompt !== undefined) {
    lastUsage.textContent = "Последний API prompt: " + formatNumber(lastPrompt) + " токенов" +
      (lastPromptUtc ? " · " + formatPromptContextTime(lastPromptUtc) : "");
    lastUsage.classList.remove("hidden");
  } else {
    lastUsage.classList.add("hidden");
  }

  renderPromptContextSections(
    promptContextInspectorValue(snapshot, "sections", "Sections", []),
    Math.max(1, used));
  renderPromptContextRaw(snapshot);
  $("promptContextInspectorLoading").classList.add("hidden");
  $("promptContextInspectorError").classList.add("hidden");
  $("promptContextInspectorBody").classList.remove("hidden");
}

function renderPromptContextSections(sections, usedTokens) {
  var root = $("promptContextInspectorSections");
  root.replaceChildren();
  (sections || []).forEach(function (section, index) {
    var included = promptContextInspectorValue(section, "included", "Included", true) !== false;
    var tokens = Number(promptContextInspectorValue(section, "tokens", "Tokens", 0) || 0);
    var count = Number(promptContextInspectorValue(section, "count", "Count", 0) || 0);
    var details = document.createElement("details");
    details.className = "prompt-context-section" + (included ? "" : " is-excluded");
    details.open = included && index === 0;

    var summary = document.createElement("summary");
    var title = document.createElement("span");
    title.className = "prompt-context-section-title";
    title.textContent = promptContextInspectorValue(section, "title", "Title", "Раздел");
    summary.appendChild(title);
    var meta = document.createElement("span");
    meta.className = "prompt-context-section-meta";
    meta.textContent = included
      ? "≈" + formatNumber(tokens) + " ток. · " + formatNumber(count)
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
      fill.style.setProperty("--prompt-context-section-percent", Math.min(100, Math.round(tokens * 100 / usedTokens)) + "%");
      track.style.setProperty("--prompt-context-section-percent", Math.min(100, Math.round(tokens * 100 / usedTokens)) + "%");
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
      values.forEach(function (item) { items.appendChild(renderPromptContextItem(item, included)); });
    }
    details.appendChild(items);
    root.appendChild(details);
  });
}

function renderPromptContextItem(item, included) {
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
  if (included && tokens) values.push("≈" + formatNumber(tokens) + " ток.");
  if (size) values.push(formatPromptContextSize(size));
  value.textContent = values.join(" · ") || (included ? "≈0 ток." : "");
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
  var raw = promptContextInspectorValue(snapshot, "rawRequestJson", "RawRequestJson", "");
  var truncated = !!promptContextInspectorValue(snapshot, "rawTruncated", "RawTruncated", false);
  var details = $("promptContextInspectorRaw");
  var button = $("loadPromptContextRawButton");
  if (!raw) {
    details.classList.add("hidden");
    details.open = false;
    $("promptContextInspectorRawText").textContent = "";
    button.textContent = "Показать JSON";
    return;
  }
  details.classList.remove("hidden");
  details.open = true;
  $("promptContextInspectorRawText").textContent = raw;
  button.textContent = truncated ? "JSON сокращён" : "Скрыть JSON";
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
  var raw = promptContextInspectorSnapshot && promptContextInspectorValue(
    promptContextInspectorSnapshot, "rawRequestJson", "RawRequestJson", "");
  if (!raw) {
    loadPromptContextInspector(true);
    return;
  }
  var details = $("promptContextInspectorRaw");
  details.open = !details.open;
  $("loadPromptContextRawButton").textContent = details.open ? "Скрыть JSON" : "Показать JSON";
}

function renderPromptContextInspectorAvailability() {
  var trigger = $("contextMeter");
  if (!trigger) return;
  var disabled = hasActiveMessageEdit() || state.bridgeUnavailable || !state.activeChatId;
  trigger.disabled = disabled;
  if (disabled && promptContextInspectorOpen()) closePromptContextInspector();
}

function syncPromptContextInspectorState() {
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
