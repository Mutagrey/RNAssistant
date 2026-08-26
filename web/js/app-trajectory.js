(function () {
  "use strict";

  var events = [];
  var selected = null;
  var nextCursor = null;
  var activeView = "raw";
  var correlationFilter = {};
  var trajectoryChatId = null;
  var trajectoryRequestId = 0;
  var detailRequestId = 0;
  var lastDerivedView = "model-replay";

  function value(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function prettyJson(text) {
    if (!text) return "{}";
    try { return JSON.stringify(typeof text === "string" ? JSON.parse(text) : text, null, 2); }
    catch (error) { return String(text); }
  }

  function bytesLabel(bytes) {
    var size = Number(bytes || 0);
    if (size < 1024) return size + " B";
    if (size < 1024 * 1024) return (size / 1024).toFixed(1) + " KB";
    return (size / (1024 * 1024)).toFixed(1) + " MB";
  }

  function eventId(item) { return value(item, "EventId", "eventId", ""); }
  function mutationId(item) { return value(item, "MutationId", "mutationId", ""); }
  function isVbaView() { return activeView === "vba-mutations"; }

  function itemId(item) {
    if (isVbaView()) return mutationId(item);
    return activeView === "raw" ? eventId(item) : value(item, "Id", "id", "");
  }

  function selectButton(button) {
    Array.prototype.slice.call($("trajectoryEvents").querySelectorAll(".trajectory-event")).forEach(function (node) {
      node.classList.toggle("active", node === button);
      node.setAttribute("aria-selected", node === button ? "true" : "false");
    });
  }

  function resetLazyDetail() {
    detailRequestId += 1;
    $("trajectoryEventPayload").textContent = "";
    $("trajectoryEventPayload").classList.add("hidden");
    $("trajectoryVbaDiff").replaceChildren();
    $("trajectoryVbaDiff").classList.add("hidden");
    $("loadTrajectoryPayloadButton").classList.add("hidden");
    $("loadVbaMutationButton").classList.add("hidden");
  }

  function setTrajectoryBusy(busy) {
    $("refreshTrajectoryButton").disabled = !!busy;
    $("loadMoreTrajectoryButton").disabled = !!busy;
  }

  function invalidateTrajectoryRequest() {
    trajectoryRequestId += 1;
    setTrajectoryBusy(false);
  }

  function unique(values) {
    var seen = {};
    return (values || []).filter(function (item) {
      var key = String(item || "").toLowerCase();
      if (!key || seen[key]) return false;
      seen[key] = true;
      return true;
    });
  }

  function navigateCorrelation(field, filterValue, targetView, sourceChatId) {
    if (typeof setDiagnosticsTab === "function") {
      setDiagnosticsTab(targetView === "raw" ? "events" : "trajectory", false);
    }
    correlationFilter = {};
    if (field === "sourceRange") {
      correlationFilter.minSequence = filterValue.min;
      correlationFilter.maxSequence = filterValue.max;
    } else {
      correlationFilter[field] = filterValue;
    }
    trajectoryChatId = sourceChatId || state.activeChatId;
    $("trajectoryViewInput").value = targetView;
    if (targetView !== "raw") lastDerivedView = targetView;
    nextCursor = null;
    updateViewControls();
    refreshTrajectory(false);
  }

  function correlationButton(root, label, field, filterValue, targetView, sourceChatId) {
    if (!filterValue || typeof filterValue === "object" && (!filterValue.min || !filterValue.max)) return;
    var button = document.createElement("button");
    button.type = "button";
    button.className = "secondary";
    button.textContent = label;
    button.addEventListener("click", function () {
      navigateCorrelation(field, filterValue, targetView, sourceChatId);
    });
    root.appendChild(button);
  }

  function activeFilterText() {
    var parts = Object.keys(correlationFilter).map(function (key) {
      return key + "=" + correlationFilter[key];
    });
    if (trajectoryChatId && trajectoryChatId !== state.activeChatId) parts.push("chat=" + trajectoryChatId);
    return parts.length ? "active: " + parts.join(" · ") : "";
  }

  function renderCorrelationActions(item) {
    var bar = $("trajectoryCorrelationBar");
    var root = $("trajectoryCorrelationActions");
    var clear = $("clearTrajectoryCorrelationButton");
    root.replaceChildren();
    var active = activeFilterText();
    if (active) {
      var chip = document.createElement("span");
      chip.className = "trajectory-correlation-chip";
      chip.textContent = active;
      root.appendChild(chip);
    }

    var sourceChatId = value(item, "SessionId", "sessionId", null);
    var runId = value(item, "RunId", "runId", "");
    var turnId = value(item, "TurnId", "turnId", "");
    var stepId = value(item, "StepId", "stepId", "");
    var directToolCallId = value(item, "ToolCallId", "toolCallId", "");
    unique(directToolCallId ? [directToolCallId] : value(item, "ToolCallIds", "toolCallIds", []) || []).forEach(function (id) {
      correlationButton(root, "tool " + id, "toolCallId", id, "tool-execution", sourceChatId);
    });
    if (runId) correlationButton(root, "run " + runId, "runId", runId, isVbaView() ? "raw" : activeView, sourceChatId);
    if (turnId) correlationButton(root, "turn " + turnId, "turnId", turnId, isVbaView() ? "raw" : activeView, sourceChatId);
    if (stepId) correlationButton(root, "step " + stepId, "stepId", stepId, isVbaView() ? "raw" : activeView, sourceChatId);

    var artifactIds = unique([value(item, "ArtifactId", "artifactId", "")]
      .concat(value(item, "ArtifactIds", "artifactIds", []) || []));
    artifactIds.forEach(function (id) {
      correlationButton(root, "artifact " + id, "artifactId", id, "artifact-lineage", sourceChatId);
    });
    var resourceRefs = value(item, "ResourceRefs", "resourceRefs", []) || [];
    unique(resourceRefs.map(function (reference) { return value(reference, "Uri", "uri", ""); })).forEach(function (uri) {
      correlationButton(root, "resource " + uri, "resourceUri", uri, "raw", sourceChatId);
    });
    var parentArtifactId = value(item, "ParentArtifactId", "parentArtifactId", "");
    if (parentArtifactId) {
      correlationButton(root, "parent " + parentArtifactId, "artifactId", parentArtifactId, "artifact-lineage", sourceChatId);
    }

    var sourceSeqs = value(item, "SourceEventSeqs", "sourceEventSeqs", []) || [];
    if (!isVbaView() && activeView !== "raw" && sourceSeqs.length) {
      var minimum = Math.min.apply(Math, sourceSeqs);
      var maximum = Math.max.apply(Math, sourceSeqs);
      correlationButton(root, "source events #" + minimum + (maximum === minimum ? "" : "…#" + maximum),
        "sourceRange", { min: minimum, max: maximum }, "raw", sourceChatId);
    }

    clear.classList.toggle("hidden", !active);
    bar.classList.toggle("hidden", root.childElementCount === 0);
  }

  function selectDerivedRow(item, button) {
    selected = item;
    selectButton(button);
    resetLazyDetail();
    var firstSequence = value(item, "FirstSequence", "firstSequence", 0);
    var lastSequence = value(item, "LastSequence", "lastSequence", 0);
    var duration = value(item, "DurationMs", "durationMs", null);
    var promptTokens = value(item, "PromptTokens", "promptTokens", null);
    var completionTokens = value(item, "CompletionTokens", "completionTokens", null);
    var totalTokens = value(item, "TotalTokens", "totalTokens", null);
    var costUsd = value(item, "CostUsd", "costUsd", null);
    var sourceSeqs = value(item, "SourceEventSeqs", "sourceEventSeqs", []) || [];
    var sourceIds = value(item, "SourceEventIds", "sourceEventIds", []) || [];
    $("trajectoryEventTitle").textContent = value(item, "Title", "title", value(item, "Kind", "kind", "row"));
    $("trajectoryEventMeta").textContent = [
      "seq=" + firstSequence + (lastSequence !== firstSequence ? "…" + lastSequence : ""),
      value(item, "Status", "status", ""),
      value(item, "RunId", "runId", "") ? "run=" + value(item, "RunId", "runId", "") : "",
      value(item, "TurnId", "turnId", "") ? "turn=" + value(item, "TurnId", "turnId", "") : "",
      value(item, "StepId", "stepId", "") ? "step=" + value(item, "StepId", "stepId", "") : "",
      value(item, "ToolId", "toolId", "") ? "tool=" + value(item, "ToolId", "toolId", "") : "",
      value(item, "ArtifactId", "artifactId", "") ? "artifact=" + value(item, "ArtifactId", "artifactId", "") : "",
      duration === null ? "" : "duration=" + duration + "ms",
      totalTokens === null ? "" : "tokens=" + totalTokens + " (" + (promptTokens || 0) + "+" + (completionTokens || 0) + ")",
      costUsd === null ? "" : "cost=$" + costUsd,
      "sources=" + sourceSeqs.length + "/" + sourceIds.length
    ].filter(Boolean).join(" · ");
    $("trajectoryEventData").textContent = prettyJson(value(item, "DataJson", "dataJson", "{}")) +
      (value(item, "DataTruncated", "dataTruncated", false) ? "\n\n[preview truncated]" : "") +
      "\n\nsourceEventSeqs: " + JSON.stringify(sourceSeqs) + "\nsourceEventIds: " + JSON.stringify(sourceIds);
    renderCorrelationActions(item);
  }

  function selectVbaMutation(item, button) {
    selected = item;
    selectButton(button);
    resetLazyDetail();
    var firstSequence = value(item, "FirstSequence", "firstSequence", 0);
    var lastSequence = value(item, "LastSequence", "lastSequence", firstSequence);
    var moduleName = value(item, "ModuleName", "moduleName", "");
    var packageId = value(item, "PackageId", "packageId", "");
    var operation = value(item, "Operation", "operation", "mutation");
    $("trajectoryEventTitle").textContent = operation + " · " + (moduleName || packageId || mutationId(item));
    $("trajectoryEventMeta").textContent = [
      "journal seq=" + firstSequence + (lastSequence !== firstSequence ? "…" + lastSequence : ""),
      value(item, "Kind", "kind", ""),
      value(item, "Status", "status", ""),
      "components=" + value(item, "ComponentCount", "componentCount", 0),
      value(item, "ErrorCode", "errorCode", "")
    ].filter(Boolean).join(" · ");
    $("trajectoryEventData").textContent = prettyJson(item);
    var detailButton = $("loadVbaMutationButton");
    detailButton.classList.remove("hidden");
    detailButton.disabled = false;
    detailButton.textContent = "Показать before / after";
    renderCorrelationActions(item);
  }

  function selectEvent(item, button) {
    if (isVbaView()) {
      selectVbaMutation(item, button);
      return;
    }
    if (activeView !== "raw") {
      selectDerivedRow(item, button);
      return;
    }
    selected = item;
    selectButton(button);
    resetLazyDetail();
    var sequence = value(item, "Sequence", "sequence", 0);
    var type = value(item, "Type", "type", "event");
    var hash = value(item, "Hash", "hash", "");
    var previousHash = value(item, "PreviousHash", "previousHash", "");
    var runId = value(item, "RunId", "runId", "");
    var turnId = value(item, "TurnId", "turnId", "");
    var stepId = value(item, "StepId", "stepId", "");
    var payloadSize = value(item, "PayloadByteLength", "payloadByteLength", null);
    var visibility = value(item, "Visibility", "visibility", "");
    var toolCallIds = value(item, "ToolCallIds", "toolCallIds", []) || [];
    var artifactIds = value(item, "ArtifactIds", "artifactIds", []) || [];
    var resourceRefs = value(item, "ResourceRefs", "resourceRefs", []) || [];
    var statuses = value(item, "Statuses", "statuses", []) || [];
    $("trajectoryEventTitle").textContent = "#" + sequence + "  " + type;
    $("trajectoryEventMeta").textContent = [
      runId ? "run=" + runId : "",
      turnId ? "turn=" + turnId : "",
      stepId ? "step=" + stepId : "",
      visibility || "",
      toolCallIds.length ? "tool=" + toolCallIds.join(",") : "",
      artifactIds.length ? "artifact=" + artifactIds.join(",") : "",
      resourceRefs.length ? "resource=" + resourceRefs.map(function (reference) {
        return value(reference, "Uri", "uri", "");
      }).filter(Boolean).join(",") : "",
      statuses.length ? "status=" + statuses.join(",") : "",
      previousHash ? "prev=" + previousHash : "root",
      hash ? "hash=" + hash : "",
      payloadSize === null ? "" : "payload=" + bytesLabel(payloadSize)
    ].filter(Boolean).join(" · ");
    var data = value(item, "DataJson", "dataJson", "");
    $("trajectoryEventData").textContent = prettyJson(data) +
      (value(item, "DataTruncated", "dataTruncated", false) ? "\n\n[preview truncated]" : "");
    var payloadButton = $("loadTrajectoryPayloadButton");
    payloadButton.classList.toggle("hidden", payloadSize === null);
    payloadButton.disabled = false;
    payloadButton.textContent = "Показать payload";
    renderCorrelationActions(item);
  }

  function rowTitle(item) {
    if (isVbaView()) {
      return value(item, "Operation", "operation", "mutation") + " · " +
        (value(item, "ModuleName", "moduleName", "") || value(item, "PackageId", "packageId", "") || mutationId(item));
    }
    return activeView === "raw"
      ? value(item, "Type", "type", "event")
      : value(item, "Title", "title", value(item, "Kind", "kind", "row"));
  }

  function renderEvents(response, append) {
    activeView = value(response, "View", "view", "raw") || "raw";
    var page = activeView === "raw"
      ? (value(response, "Events", "events", []) || [])
      : (value(response, "Rows", "rows", []) || []);
    events = append ? events.concat(page) : page;
    var root = $("trajectoryEvents");
    root.replaceChildren();
    var artifactMap = {};
    if (activeView === "artifact-lineage") {
      events.forEach(function (item) {
        var artifactId = value(item, "ArtifactId", "artifactId", "");
        if (artifactId) artifactMap[String(artifactId).toLowerCase()] = item;
      });
      root.setAttribute("role", "tree");
      root.setAttribute("aria-label", "Дерево версий артефактов");
    } else {
      root.setAttribute("role", "listbox");
      root.setAttribute("aria-label", activeView === "raw" ? "События JSONL" : "Строки траектории");
    }
    var renderedEvents = activeView === "artifact-lineage" ? orderArtifactTree(events, artifactMap) : events;
    renderedEvents.forEach(function (item) {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "trajectory-event";
      button.setAttribute("role", activeView === "artifact-lineage" ? "treeitem" : "option");
      var itemSelected = !!selected && itemId(selected) === itemId(item);
      button.classList.toggle("active", itemSelected);
      button.setAttribute("aria-selected", itemSelected ? "true" : "false");
      if (activeView === "artifact-lineage") {
        var depth = artifactTreeDepth(item, artifactMap, {});
        button.style.setProperty("--trajectory-depth", String(depth));
        button.setAttribute("aria-level", String(depth + 1));
      }
      var first = document.createElement("span");
      first.className = "trajectory-event-line";
      var sequence = document.createElement("span");
      sequence.className = "trajectory-event-sequence";
      var firstSequence = value(item, "FirstSequence", "firstSequence", value(item, "Sequence", "sequence", 0));
      var lastSequence = value(item, "LastSequence", "lastSequence", firstSequence);
      sequence.textContent = (isVbaView() ? "V#" : "#") + firstSequence + (lastSequence !== firstSequence ? "…" + lastSequence : "");
      var type = document.createElement("span");
      type.className = "trajectory-event-type";
      type.textContent = rowTitle(item);
      first.appendChild(sequence);
      first.appendChild(type);
      if (activeView === "raw" && value(item, "PayloadByteLength", "payloadByteLength", null) !== null) {
        var payload = document.createElement("span");
        payload.className = "trajectory-event-payload";
        payload.textContent = "payload";
        first.appendChild(payload);
      } else if (isVbaView()) {
        var status = document.createElement("span");
        status.className = "trajectory-event-payload";
        status.textContent = value(item, "Status", "status", "");
        first.appendChild(status);
      }
      var second = document.createElement("span");
      second.className = "trajectory-event-time";
      var created = value(item, "CreatedUtc", "createdUtc", "");
      second.textContent = created ? new Date(created).toLocaleString() : "";
      button.appendChild(first);
      button.appendChild(second);
      button.addEventListener("click", function () { selectEvent(item, button); });
      root.appendChild(button);
    });

    var total = activeView === "raw"
      ? value(response, "TotalEvents", "totalEvents", events.length)
      : value(response, "TotalRows", "totalRows", events.length);
    var matches = value(response, "TotalMatches", "totalMatches", events.length);
    nextCursor = value(response, "NextCursor", "nextCursor", null);
    var hasMore = !!value(response, "HasMore", "hasMore", false);
    var projectionNote = isVbaView()
      ? " · пересобрано из VBA journal; source из CAS только по запросу"
      : (activeView === "raw" ? " · payload из CAS только по запросу" : " · пересобрано из JSONL event stream");
    $("trajectoryStatus").textContent = "Совпадений: " + matches + " из " + total + " · загружено " + events.length + projectionNote;
    $("loadMoreTrajectoryButton").classList.toggle("hidden", !hasMore);
    $("trajectoryWorkspace").classList.toggle("hidden", events.length === 0);
    if (!append && renderedEvents.length) {
      selectEvent(renderedEvents[0], root.firstElementChild);
      root.scrollTop = 0;
    } else if (append && selected) {
      var selectedIndex = renderedEvents.map(itemId).indexOf(itemId(selected));
      if (selectedIndex >= 0) selectEvent(renderedEvents[selectedIndex], root.children[selectedIndex]);
    } else if (!events.length) {
      selected = null;
      $("trajectoryEventTitle").textContent = activeView === "raw" ? "Событие не выбрано" : "Строка не выбрана";
      $("trajectoryEventMeta").textContent = "";
      $("trajectoryEventData").textContent = "";
      resetLazyDetail();
      renderCorrelationActions({});
    }
  }

  function artifactTreeDepth(item, map, visited) {
    var id = String(value(item, "ArtifactId", "artifactId", "") || "").toLowerCase();
    var parentId = String(value(item, "ParentArtifactId", "parentArtifactId", "") || "").toLowerCase();
    if (!parentId || !map[parentId] || visited[id]) return 0;
    visited[id] = true;
    return Math.min(8, 1 + artifactTreeDepth(map[parentId], map, visited));
  }

  function artifactLastSequence(item) {
    return Number(value(item, "LastSequence", "lastSequence", 0) || 0);
  }

  function orderArtifactTree(rows, map) {
    var children = {};
    var roots = [];
    (rows || []).forEach(function (item) {
      var parentId = String(value(item, "ParentArtifactId", "parentArtifactId", "") || "").toLowerCase();
      if (parentId && map[parentId]) {
        children[parentId] = children[parentId] || [];
        children[parentId].push(item);
      } else {
        roots.push(item);
      }
    });
    Object.keys(children).forEach(function (parentId) {
      children[parentId].sort(function (left, right) {
        return artifactLastSequence(left) - artifactLastSequence(right);
      });
    });
    function branchLastSequence(item, visited) {
      var id = String(value(item, "ArtifactId", "artifactId", "") || "").toLowerCase();
      if (!id || visited[id]) return artifactLastSequence(item);
      visited[id] = true;
      return (children[id] || []).reduce(function (latest, child) {
        return Math.max(latest, branchLastSequence(child, visited));
      }, artifactLastSequence(item));
    }
    roots.sort(function (left, right) {
      return branchLastSequence(right, {}) - branchLastSequence(left, {});
    });
    var ordered = [];
    var appended = {};
    function append(item) {
      var id = String(value(item, "ArtifactId", "artifactId", "") || "").toLowerCase();
      if (id && appended[id]) return;
      if (id) appended[id] = true;
      ordered.push(item);
      (children[id] || []).forEach(append);
    }
    roots.forEach(append);
    (rows || []).forEach(append);
    return ordered;
  }

  function applyCorrelation(payload) {
    Object.keys(correlationFilter).forEach(function (key) { payload[key] = correlationFilter[key]; });
    return payload;
  }

  function queryPayload(chatId, cursor) {
    var view = $("trajectoryViewInput").value || "raw";
    if (view === "vba-mutations") {
      return applyCorrelation({
        cursor: cursor || null,
        pageSize: 100,
        search: $("trajectorySearchInput").value.trim(),
        kind: $("trajectoryVbaKindInput").value || null,
        status: $("trajectoryVbaStatusInput").value || null
      });
    }
    return applyCorrelation({
      chatId: chatId,
      view: view,
      cursor: cursor || null,
      pageSize: 100,
      search: $("trajectorySearchInput").value.trim(),
      eventTypes: view === "raw" ? $("trajectoryTypeInput").value.split(",").map(function (item) { return item.trim(); }).filter(Boolean) : [],
      visibility: view === "raw" ? ($("trajectoryVisibilityInput").value || null) : null
    });
  }

  function exportPayload(chatId) {
    var payload = queryPayload(chatId, null);
    delete payload.cursor;
    delete payload.pageSize;
    payload.redactionMode = $("trajectoryExportRedactionInput").value || "metadata";
    payload.includeCasPayloads = payload.redactionMode === "none" && $("trajectoryExportCasInput").checked;
    return payload;
  }

  function downloadBase64(response) {
    var encoded = value(response, "Base64", "base64", "");
    var binary = window.atob(encoded);
    var bytes = new Uint8Array(binary.length);
    for (var index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
    var blob = new Blob([bytes], { type: value(response, "ContentType", "contentType", "application/zip") });
    var url = URL.createObjectURL(blob);
    var link = document.createElement("a");
    link.href = url;
    link.download = value(response, "FileName", "fileName", "rnassistant-trajectory.zip");
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
  }

  async function exportTrajectory() {
    var view = $("trajectoryViewInput").value || "raw";
    var chatId = trajectoryChatId || state.activeChatId;
    if (view === "vba-mutations") {
      $("trajectoryStatus").textContent = "VBA journal пока не входит в chat trajectory export.";
      return;
    }
    if (!chatId) {
      $("trajectoryStatus").textContent = "Нет активного чата.";
      return;
    }
    var payload = exportPayload(chatId);
    if (payload.redactionMode === "none" && !window.confirm(
      "Экспорт без redaction содержит расшифрованные prompts, document data и event data. Продолжить?")) return;
    var button = $("exportTrajectoryButton");
    try {
      button.disabled = true;
      button.textContent = "Готовлю ZIP…";
      $("trajectoryStatus").textContent = "Проверяю stream/CAS и формирую одноразовую projection…";
      var response = await send("exportChatTrajectory", payload);
      downloadBase64(response);
      $("trajectoryStatus").textContent = "Экспортировано событий: " + value(response, "EventCount", "eventCount", 0) +
        " · ZIP " + bytesLabel(value(response, "ByteLength", "byteLength", 0)) +
        " · sha256=" + shortHash(value(response, "BundleSha256", "bundleSha256", ""));
    } catch (error) {
      $("trajectoryStatus").textContent = "Не удалось экспортировать trajectory: " + error.message;
    } finally {
      button.disabled = false;
      button.textContent = "Скачать ZIP";
    }
  }

  function updateExportControls() {
    var mode = $("trajectoryExportRedactionInput").value || "metadata";
    var full = mode === "none";
    var vba = ($("trajectoryViewInput").value || "raw") === "vba-mutations";
    $("trajectoryExportCasInput").disabled = !full || vba;
    if (!full) $("trajectoryExportCasInput").checked = false;
    $("exportTrajectoryButton").disabled = vba;
    $("trajectoryExportNotice").textContent = vba
      ? "VBA export будет отдельным bundle из journal."
      : (mode === "metadata"
        ? "Data и CAS bodies не попадут в архив."
        : (mode === "secrets"
          ? "Credential fields скрыты; prompts и document text могут остаться."
          : "Без redaction: архив содержит чувствительные данные."));
  }

  function clearTrajectoryRows() {
    events = [];
    selected = null;
    nextCursor = null;
    $("trajectoryEvents").replaceChildren();
    $("trajectoryWorkspace").classList.add("hidden");
    $("trajectoryEventTitle").textContent = "Запись не выбрана";
    $("trajectoryEventMeta").textContent = "";
    $("trajectoryEventData").textContent = "";
    resetLazyDetail();
    renderCorrelationActions({});
  }

  async function refreshTrajectory(append) {
    var requestedView = $("trajectoryViewInput").value || "raw";
    var vba = requestedView === "vba-mutations";
    var chatId = trajectoryChatId || state.activeChatId;
    if (!append) clearTrajectoryRows();
    if (!vba && !chatId) {
      $("trajectoryStatus").textContent = "Нет активного чата.";
      return;
    }
    var requestId = ++trajectoryRequestId;
    try {
      setTrajectoryBusy(true);
      $("trajectoryStatus").textContent = vba ? "Читаю VBA mutation journal…" : "Читаю event stream…";
      var response = await send(vba ? "getVbaMutations" : "getChatTrajectory", queryPayload(chatId, append ? nextCursor : null));
      if (requestId !== trajectoryRequestId ||
        requestedView !== ($("trajectoryViewInput").value || "raw") ||
        (!vba && (trajectoryChatId || state.activeChatId) !== chatId)) return;
      renderEvents(response, !!append);
    } catch (error) {
      if (requestId !== trajectoryRequestId) return;
      $("trajectoryStatus").textContent = "Не удалось прочитать диагностику: " + error.message;
      $("trajectoryWorkspace").classList.add("hidden");
    } finally {
      if (requestId === trajectoryRequestId) setTrajectoryBusy(false);
    }
  }

  async function loadPayload() {
    if (activeView !== "raw" || !selected) return;
    var chatId = trajectoryChatId || state.activeChatId;
    if (!chatId) return;
    var button = $("loadTrajectoryPayloadButton");
    var target = $("trajectoryEventPayload");
    var selectedId = eventId(selected);
    var requestId = ++detailRequestId;
    try {
      button.disabled = true;
      button.textContent = "Загружаю…";
      var response = await send("getChatEventPayload", { chatId: chatId, eventId: selectedId });
      if (requestId !== detailRequestId || !selected || eventId(selected) !== selectedId) return;
      var text = value(response, "Text", "text", "");
      target.textContent = prettyJson(text) +
        (value(response, "TextTruncated", "textTruncated", false) ? "\n\n[preview truncated; full payload remains in CAS]" : "");
      target.classList.remove("hidden");
      button.textContent = "Payload загружен";
    } catch (error) {
      if (requestId !== detailRequestId) return;
      target.textContent = "Не удалось загрузить payload: " + error.message;
      target.classList.remove("hidden");
      button.textContent = "Повторить";
    } finally {
      if (requestId === detailRequestId) button.disabled = false;
    }
  }

  function shortHash(hash) {
    hash = String(hash || "");
    return hash.length > 16 ? hash.substring(0, 16) + "…" : hash;
  }

  function stateLabel(exists, type, hash) {
    if (exists === null || exists === undefined) return "unknown";
    if (!exists) return "absent";
    return (type || "component") + (hash ? " @ " + shortHash(hash) : "");
  }

  async function restoreVbaComponent(component, detail, button) {
    var name = value(component, "ModuleName", "moduleName", "");
    var backupId = value(component, "BackupId", "backupId", "");
    if (!backupId || !name) return;
    var prompt = "Восстановить «" + name + "» из backup " + backupId + "?\n\n" +
      "Текущее состояние будет отдельно сохранено, а restore создаст новую journaled VBA mutation.";
    if (!window.confirm(prompt)) return;
    try {
      button.disabled = true;
      button.textContent = "Восстанавливаю…";
      var response = await send("restoreVbaBackup", { backupId: backupId, moduleName: name });
      var success = !!value(response, "Success", "success", false);
      var message = value(response, "Message", "message", success ? "VBA backup restored." : "VBA restore failed.");
      if (!success) throw new Error(message);
      if (typeof log === "function") log(message, "success");
      await refreshTrajectory(false);
    } catch (error) {
      if (typeof log === "function") log(error.detail || error.message, "error");
      window.alert("Не удалось восстановить VBA: " + error.message);
      button.disabled = false;
      button.textContent = "Restore before";
    }
  }

  function renderVbaMutationDetail(detail) {
    var target = $("trajectoryVbaDiff");
    target.replaceChildren();
    var components = value(detail, "Components", "components", []) || [];
    components.forEach(function (component) {
      var card = document.createElement("section");
      card.className = "trajectory-vba-component";
      var head = document.createElement("div");
      head.className = "trajectory-vba-component-head";
      var title = document.createElement("strong");
      title.textContent = value(component, "ModuleName", "moduleName", "component");
      head.appendChild(title);
      if (value(component, "CanRestore", "canRestore", false)) {
        var restore = document.createElement("button");
        restore.type = "button";
        restore.className = "danger";
        restore.textContent = "Restore before";
        restore.addEventListener("click", function () { restoreVbaComponent(component, detail, restore); });
        head.appendChild(restore);
      }
      var meta = document.createElement("div");
      meta.className = "trajectory-vba-component-meta";
      meta.textContent = [
        "before=" + stateLabel(value(component, "BeforeExists", "beforeExists", false),
          value(component, "BeforeComponentType", "beforeComponentType", ""), value(component, "BeforeCodeSha256", "beforeCodeSha256", "")),
        "intended=" + stateLabel(value(component, "IntendedAfterExists", "intendedAfterExists", false),
          value(component, "IntendedAfterComponentType", "intendedAfterComponentType", ""), value(component, "IntendedAfterCodeSha256", "intendedAfterCodeSha256", "")),
        "actual=" + stateLabel(value(component, "ActualExists", "actualExists", null),
          value(component, "ActualComponentType", "actualComponentType", ""), value(component, "ActualCodeSha256", "actualCodeSha256", "")),
        value(component, "MatchesBefore", "matchesBefore", null) === true ? "actual matches before" : "",
        value(component, "MatchesIntendedAfter", "matchesIntendedAfter", null) === true ? "actual matches intended" : "",
        value(component, "ErrorCode", "errorCode", ""),
        value(component, "Message", "message", "")
      ].filter(Boolean).join(" · ");
      var diff = document.createElement("div");
      diff.className = "vba-diff";
      var before = value(component, "BeforeExists", "beforeExists", false)
        ? value(component, "BeforeCode", "beforeCode", "") : "";
      var after = value(component, "IntendedAfterExists", "intendedAfterExists", false)
        ? value(component, "IntendedAfterCode", "intendedAfterCode", "") : "";
      head.title = "mutation " + value(detail, "MutationId", "mutationId", "");
      card.appendChild(head);
      card.appendChild(meta);
      card.appendChild(diff);
      target.appendChild(card);
      window.RNAssistantVbaDiff.render(diff, window.RNAssistantVbaDiff.format(before, after));
    });
    if (!components.length) target.textContent = "Mutation не содержит компонентов.";
    target.classList.remove("hidden");
  }

  async function loadVbaMutation() {
    if (!isVbaView() || !selected) return;
    var button = $("loadVbaMutationButton");
    var selectedId = mutationId(selected);
    var requestId = ++detailRequestId;
    try {
      button.disabled = true;
      button.textContent = "Проверяю CAS…";
      var response = await send("getVbaMutationDetail", { mutationId: selectedId });
      if (requestId !== detailRequestId || !selected || mutationId(selected) !== selectedId) return;
      renderVbaMutationDetail(response);
      button.textContent = "Before / after загружен";
    } catch (error) {
      if (requestId !== detailRequestId) return;
      var target = $("trajectoryVbaDiff");
      target.textContent = "Не удалось прочитать CAS source: " + error.message;
      target.classList.remove("hidden");
      button.textContent = "Повторить";
    } finally {
      if (requestId === detailRequestId) button.disabled = false;
    }
  }

  function clearCorrelation() {
    correlationFilter = {};
    trajectoryChatId = null;
    nextCursor = null;
    refreshTrajectory(false);
  }

  function updateViewControls() {
    var view = $("trajectoryViewInput").value || "raw";
    var raw = view === "raw";
    var vba = view === "vba-mutations";
    var panel = document.querySelector(".trajectory-panel");
    if (panel) {
      panel.classList.toggle("is-derived", !raw);
      panel.classList.toggle("is-artifact-tree", view === "artifact-lineage");
      panel.classList.toggle("is-vba", vba);
    }
    $("trajectoryViewField").classList.toggle("hidden", raw || vba);
    $("trajectoryTypeField").classList.toggle("hidden", !raw);
    $("trajectoryTypeInput").disabled = !raw;
    $("trajectoryVisibilityField").classList.toggle("hidden", !raw);
    $("trajectoryVisibilityInput").disabled = !raw;
    $("trajectoryVbaKindField").classList.toggle("hidden", !vba);
    $("trajectoryVbaKindInput").disabled = !vba;
    $("trajectoryVbaStatusField").classList.toggle("hidden", !vba);
    $("trajectoryVbaStatusInput").disabled = !vba;
    if (raw) {
      $("trajectoryTitle").textContent = "События JSONL";
      $("trajectoryDescription").textContent = "Канонические записи активного chat stream. Event data показан сразу, большие payload читаются из CAS только по запросу.";
    } else if (vba) {
      $("trajectoryTitle").textContent = "VBA mutation journal";
      $("trajectoryDescription").textContent = "Document-scoped операции из mutations.events.jsonl. Before/after source загружается из CAS только по запросу.";
    } else {
      $("trajectoryTitle").textContent = "Траектория выполнения";
      $("trajectoryDescription").textContent = "Read-only проекция, которая каждый раз пересобирается из проверенного JSONL stream и связывает исходные event seq/id.";
    }
    updateExportControls();
  }

  function setDiagnosticsMode(mode, refresh) {
    invalidateTrajectoryRequest();
    correlationFilter = {};
    trajectoryChatId = null;
    nextCursor = null;
    if (mode === "events") {
      var current = $("trajectoryViewInput").value || "raw";
      if (current !== "raw" && current !== "vba-mutations") lastDerivedView = current;
      $("trajectoryViewInput").value = "raw";
    } else if (mode === "vba-journal") {
      $("trajectoryViewInput").value = "vba-mutations";
    } else {
      $("trajectoryViewInput").value = lastDerivedView === "raw" ? "model-replay" : lastDerivedView;
    }
    updateViewControls();
    if (refresh) refreshTrajectory(false);
  }

  window.bindTrajectoryActions = function () {
    var refresh = $("refreshTrajectoryButton");
    var more = $("loadMoreTrajectoryButton");
    var payload = $("loadTrajectoryPayloadButton");
    var vbaDetail = $("loadVbaMutationButton");
    var exportButton = $("exportTrajectoryButton");
    if (refresh) refresh.addEventListener("click", function () { refreshTrajectory(false); });
    if (more) more.addEventListener("click", function () { refreshTrajectory(true); });
    if (exportButton) exportButton.addEventListener("click", exportTrajectory);
    ["trajectorySearchInput", "trajectoryTypeInput"].forEach(function (id) {
      $(id).addEventListener("input", function () {
        invalidateTrajectoryRequest();
        nextCursor = null;
        $("loadMoreTrajectoryButton").classList.add("hidden");
      });
      $(id).addEventListener("keydown", function (event) { if (event.key === "Enter") refreshTrajectory(false); });
    });
    ["trajectoryVisibilityInput", "trajectoryVbaKindInput", "trajectoryVbaStatusInput"].forEach(function (id) {
      $(id).addEventListener("change", function () { nextCursor = null; refreshTrajectory(false); });
    });
    $("trajectoryViewInput").addEventListener("change", function () {
      correlationFilter = {};
      trajectoryChatId = null;
      nextCursor = null;
      if ($("trajectoryViewInput").value !== "raw") lastDerivedView = $("trajectoryViewInput").value;
      updateViewControls();
      refreshTrajectory(false);
    });
    $("clearTrajectoryCorrelationButton").addEventListener("click", clearCorrelation);
    $("trajectoryExportRedactionInput").addEventListener("change", updateExportControls);
    if (payload) payload.addEventListener("click", loadPayload);
    if (vbaDetail) vbaDetail.addEventListener("click", loadVbaMutation);
    updateViewControls();
  };
  window.setTrajectoryDiagnosticsMode = setDiagnosticsMode;
}());
