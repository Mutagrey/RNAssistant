(function () {
  "use strict";

  var events = [];
  var selected = null;
  var nextCursor = null;
  var activeView = "raw";
  var correlationFilter = {};
  var trajectoryChatId = null;
  var trajectoryWorkspaceId = null, trajectoryOwner = "office", trajectoryLabel = "";
  var routeHistory = [];
  var trajectoryRequestId = 0;
  var detailRequestId = 0;
  var journalFilter = "all";
  var expandedJournalRows = {};
  var journalStateChatId = "";
  var activeExport = null;
  var activePayload = null;
  var detailData = null;
  var detailEvidence = null;
  var detailPayload = null;

  function readPayload(chatId, eventId, signal, isCurrent, workspaceId) {
    return window.RNAssistantTrajectoryPayload.read(chatId, eventId, {
      send: send, cancelRequest: cancelBridgeRequest, fetch: window.fetch.bind(window),
      signal: signal, isCurrent: isCurrent, workspaceId: workspaceId === undefined ? trajectoryWorkspaceId : workspaceId
    });
  }

  function value(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function selectedSource() {
    var workspace = window.RNAssistantWorkspaceChat, owner = workspace ? workspace.owner() : "office";
    if (owner === "office") return { owner: owner, chatId: state.activeChatId, workspaceId: null, label: "" };
    var chat = workspace.snapshot(), project = value(chat, "Project", "project", {}), snapshot = value(chat, "State", "state", {});
    return { owner: owner, chatId: value(chat, "SessionId", "sessionId", null),
      workspaceId: value(project, "WorkspaceId", "workspaceId", null),
      label: value(snapshot, "Title", "title", "Проектный чат"), runId: value(snapshot, "RunId", "runId", "") };
  }
  function useSource(source) {
    trajectoryChatId = source.chatId || null; trajectoryWorkspaceId = source.workspaceId || null;
    trajectoryOwner = source.owner; trajectoryLabel = source.label || "";
  }
  function selectedChatId() { return trajectoryChatId || selectedSource().chatId; }
  function scopeKey() { return JSON.stringify([trajectoryOwner, trajectoryWorkspaceId, selectedChatId()]); }
  function sourceSelectionKey() { var source = selectedSource(); return JSON.stringify([source.owner, source.workspaceId, source.chatId]); }
  function matchesScope(response, chatId, workspaceId) {
    return response && value(response, "ChatId", "chatId", null) === chatId &&
      value(response, "WorkspaceId", "workspaceId", null) === workspaceId;
  }

  function jsonText(valueToSerialize) {
    try { return JSON.stringify(valueToSerialize === undefined ? null : valueToSerialize); }
    catch (error) { return JSON.stringify({ displayError: "Typed projection cannot be serialized." }); }
  }

  function setDetailData(text, completeness) {
    detailData = { text: text, completeness: completeness || "full" };
  }

  function unmountTrajectoryJson(targetId) {
    if (window.RNAssistantViewerRegistry) window.RNAssistantViewerRegistry.unmount($(targetId));
  }

  function renderDetail() {
    if (!detailData) return;
    var part = $("trajectoryDetailPart").value || "data", content = detailData;
    var payloadOption = $("trajectoryDetailPart").querySelector('option[value="payload"]');
    if (payloadOption) { payloadOption.disabled = !detailPayload; payloadOption.title = detailPayload ? detailPayload.contentType : ""; }
    if (part === "evidence") content = { text: jsonText(detailEvidence || {}), completeness: "full" };
    else if (part === "payload" && detailPayload) content = { text: detailPayload.text,
      completeness: detailPayload.error ? "unavailable" : detailPayload.truncated ? "preview" : "full" };
    window.RNAssistantViewerRegistry.mount("json", $("trajectoryEventData"), {
      text: content.text, mode: "tree", compact: true, completeness: content.completeness,
      onCopy: window.copyTextResult
    });
  }

  function showEvidence(valueToShow) {
    detailEvidence = valueToShow;
    renderDetail();
  }

  function hideEvidence() {
    detailData = detailEvidence = detailPayload = null;
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
  function isRunJournal() { return activeView === "run-causal"; }

  function latestKnownRunId() {
    var source = selectedSource();
    if (trajectoryChatId && trajectoryChatId !== source.chatId) return "";
    if (source.owner !== "office") return source.runId || "";
    var active = state.chatRuns && state.activeChatId ? state.chatRuns[state.activeChatId] : null;
    if (active && active.runId) return active.runId;
    for (var index = (state.messages || []).length - 1; index >= 0; index -= 1) {
      var message = state.messages[index] || {};
      var runId = message.runId !== undefined ? message.runId : message.RunId;
      if (runId && !value(message, "Local", "local", false)) return String(runId);
    }
    return "";
  }

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
    if (activePayload) activePayload.abort();
    activePayload = null;
    detailPayload = null;
    detailEvidence = null;
    $("trajectoryDetailPart").value = "data";
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
    cancelTrajectoryExport();
    window.RNAssistantTrajectoryPayload.cancelAll();
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

  function captureRoute() {
    return { scope: scopeKey(), selection: sourceSelectionKey(), view: $("trajectoryViewInput").value,
      filter: Object.assign({}, correlationFilter), journalFilter: journalFilter,
      expanded: Object.assign({}, expandedJournalRows), search: $("trajectorySearchInput").value,
      type: $("trajectoryTypeInput").value, visibility: $("trajectoryVisibilityInput").value };
  }

  function updateBackButton() { $("trajectoryBackButton").classList.toggle("hidden", !routeHistory.length); }

  function goBack() {
    var route = routeHistory.pop();
    updateBackButton();
    if (!route || route.scope !== scopeKey() || route.selection !== sourceSelectionKey()) return;
    correlationFilter = route.filter; journalFilter = route.journalFilter; expandedJournalRows = route.expanded;
    $("trajectoryViewInput").value = route.view; $("trajectorySearchInput").value = route.search;
    $("trajectoryTypeInput").value = route.type; $("trajectoryVisibilityInput").value = route.visibility;
    nextCursor = null; updateViewControls(); refreshTrajectory(false);
  }

  function navigateCorrelation(field, filterValue, targetView, sourceChatId) {
    var history = routeHistory.concat([captureRoute()]).slice(-20);
    var source = { owner: trajectoryOwner, workspaceId: trajectoryWorkspaceId,
      chatId: sourceChatId || selectedChatId(), label: trajectoryLabel };
    if (typeof setDiagnosticsTab === "function") {
      setDiagnosticsTab("trajectory", false);
    }
    routeHistory = history; updateBackButton();
    correlationFilter = {};
    if (field === "sourceRange") {
      correlationFilter.minSequence = filterValue.min;
      correlationFilter.maxSequence = filterValue.max;
    } else {
      correlationFilter[field] = filterValue;
    }
    useSource(source);
    $("trajectoryViewInput").value = targetView;
    nextCursor = null;
    updateViewControls();
    refreshTrajectory(false);
  }

  function correlationButton(root, label, field, filterValue, targetView, sourceChatId) {
    if (!filterValue || typeof filterValue === "object" && (!filterValue.min || !filterValue.max)) return;
    var button = document.createElement("button");
    button.type = "button";
    button.textContent = label;
    button.title = typeof filterValue === "object" ? "#" + filterValue.min + "…#" + filterValue.max : String(filterValue);
    var route = scopeKey(), selection = sourceSelectionKey();
    button.addEventListener("click", function () {
      if (scopeKey() !== route || sourceSelectionKey() !== selection) return;
      navigateCorrelation(field, filterValue, targetView, sourceChatId);
    });
    root.appendChild(button);
  }

  function activeFilterText() {
    var labels = {
      runId: "запуск",
      turnId: "turn",
      stepId: "step",
      toolCallId: "tool call",
      artifactId: "artifact",
      resourceUri: "resource",
      minSequence: "от #",
      maxSequence: "до #"
    };
    var parts = Object.keys(correlationFilter).map(function (key) {
      return (labels[key] || key) + "=" + correlationFilter[key];
    });
    return parts.length ? "Фильтр: " + parts.join(" · ") : "";
  }

  function renderCorrelationActions(item) {
    var bar = $("trajectoryCorrelationBar");
    var root = $("trajectoryCorrelationActions");
    var clear = $("clearTrajectoryCorrelationButton");
    root.replaceChildren();
    var active = activeFilterText();

    var sourceChatId = value(item, "SessionId", "sessionId", null);
    var runId = value(item, "RunId", "runId", "");
    var turnId = value(item, "TurnId", "turnId", "");
    var stepId = value(item, "StepId", "stepId", "");
    var directToolCallId = value(item, "ToolCallId", "toolCallId", "");
    unique(directToolCallId ? [directToolCallId] : value(item, "ToolCallIds", "toolCallIds", []) || []).forEach(function (id) {
      correlationButton(root, "События действия", "toolCallId", id, "tool-execution", sourceChatId);
    });
    if (runId) correlationButton(root, "Весь запуск", "runId", runId, isVbaView() ? "raw" : activeView, sourceChatId);
    if (turnId) correlationButton(root, "События запроса", "turnId", turnId, isVbaView() ? "raw" : activeView, sourceChatId);
    if (stepId) correlationButton(root, "События шага", "stepId", stepId, isVbaView() ? "raw" : activeView, sourceChatId);

    var resourceRefs = value(item, "ResourceRefs", "resourceRefs", []) || [];
    unique(resourceRefs.map(function (reference) { return value(reference, "Uri", "uri", ""); })).forEach(function (uri) {
      correlationButton(root, "История ресурса", "resourceUri", uri, "raw", sourceChatId);
    });
    var artifactIds = unique([value(item, "ArtifactId", "artifactId", "")]
      .concat(value(item, "ArtifactIds", "artifactIds", []) || []));
    artifactIds.forEach(function (id) {
      correlationButton(root, "Версии артефакта", "artifactId", id, "artifact-lineage", sourceChatId);
    });
    var parentArtifactId = value(item, "ParentArtifactId", "parentArtifactId", "");
    if (parentArtifactId) {
      correlationButton(root, "Исходный артефакт", "artifactId", parentArtifactId, "artifact-lineage", sourceChatId);
    }

    var sourceSeqs = value(item, "SourceEventSeqs", "sourceEventSeqs", []) || [];
    if (!isVbaView() && activeView !== "raw" && sourceSeqs.length) {
      var minimum = Math.min.apply(Math, sourceSeqs);
      var maximum = Math.max.apply(Math, sourceSeqs);
      correlationButton(root, "Исходные события #" + minimum + (maximum === minimum ? "" : "…#" + maximum),
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
    var resourceRefs = value(item, "ResourceRefs", "resourceRefs", []) || [];
    $("trajectoryEventTitle").textContent = value(item, "Title", "title", value(item, "Kind", "kind", "row"));
    $("trajectoryEventMeta").textContent = [
      "#" + firstSequence + (lastSequence !== firstSequence ? "…" + lastSequence : ""),
      value(item, "Status", "status", ""),
      duration === null ? "" : duration + " мс",
      totalTokens === null ? "" : totalTokens + " токенов",
      costUsd === null ? "" : "$" + costUsd
    ].filter(Boolean).join(" · ");
    setDetailData(value(item, "DataJson", "dataJson", "{}"),
      value(item, "DataTruncated", "dataTruncated", false) ? "preview" : "full");
    showEvidence({
      sourceEventSeqs: sourceSeqs,
      sourceEventIds: sourceIds,
      runId: value(item, "RunId", "runId", null), turnId: value(item, "TurnId", "turnId", null),
      stepId: value(item, "StepId", "stepId", null), toolId: value(item, "ToolId", "toolId", null),
      artifactId: value(item, "ArtifactId", "artifactId", null), promptTokens: promptTokens, completionTokens: completionTokens,
      modelAttemptId: value(item, "ModelAttemptId", "modelAttemptId", null),
      toolCallId: value(item, "ToolCallId", "toolCallId", null),
      mutationId: value(item, "MutationId", "mutationId", null),
      journalRunId: value(item, "JournalRunId", "journalRunId", null),
      resourceRefs: resourceRefs
    });
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
    setDetailData(jsonText(item), "full");
    showEvidence({
      sessionId: value(item, "SessionId", "sessionId", null),
      mutationId: mutationId(item),
      journalRunId: value(item, "JournalRunId", "journalRunId", null),
      firstSequence: firstSequence,
      lastSequence: lastSequence
    });
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
      value(item, "CreatedUtc", "createdUtc", "") ? new Date(value(item, "CreatedUtc", "createdUtc", "")).toLocaleString("ru-RU") : "",
      payloadSize === null ? "" : bytesLabel(payloadSize)
    ].filter(Boolean).join(" · ");
    setDetailData(value(item, "DataJson", "dataJson", ""),
      value(item, "DataTruncated", "dataTruncated", false) ? "preview" : "full");
    showEvidence({
      schemaVersion: value(item, "SchemaVersion", "schemaVersion", null),
      eventId: eventId(item), runId: runId, turnId: turnId, stepId: stepId, visibility: visibility,
      toolCallIds: toolCallIds, artifactIds: artifactIds, statuses: statuses,
      sourceEventSeqs: value(item, "SourceEventSeqs", "sourceEventSeqs", []) || [],
      sourceEventIds: value(item, "SourceEventIds", "sourceEventIds", []) || [],
      previousHash: previousHash || null,
      hash: hash || null,
      hashAlgorithm: value(item, "HashAlgorithm", "hashAlgorithm", null),
      dataEncrypted: value(item, "DataEncrypted", "dataEncrypted", false),
      payload: payloadSize === null ? null : {
        sha256: value(item, "PayloadSha256", "payloadSha256", null),
        byteLength: payloadSize,
        contentType: value(item, "PayloadContentType", "payloadContentType", null),
        encryption: value(item, "PayloadEncryption", "payloadEncryption", null)
      },
      resourceRefs: resourceRefs
    });
    var payloadButton = $("loadTrajectoryPayloadButton");
    payloadButton.classList.toggle("hidden", payloadSize === null);
    payloadButton.disabled = false;
    payloadButton.textContent = "Содержимое";
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

  function renderRunJournal(response, previousScroll, loadedLimitReached) {
    var root = $("trajectoryEvents");
    var adapter = window.RNAssistantRunJournal;
    previousScroll = Number(previousScroll || 0);
    var responseChatId = value(response, "ChatId", "chatId", selectedChatId() || "");
    var responseScope = scopeKey(), responseWorkspace = trajectoryWorkspaceId, sourceSelection = sourceSelectionKey();
    if (journalStateChatId && journalStateChatId !== responseScope) {
      expandedJournalRows = {};
    }
    journalStateChatId = responseScope;
    selected = null;
    var loadedIds = {};
    events.forEach(function (item) { loadedIds[itemId(item)] = true; });
    Object.keys(expandedJournalRows).forEach(function (id) {
      if (!loadedIds[id] && !loadedIds[id.replace(/:technical$/, "")]) delete expandedJournalRows[id];
    });
    root.setAttribute("role", "region");
    root.setAttribute("aria-label", "Хронологический журнал запуска");
    if (!adapter || typeof adapter.render !== "function") {
      root.replaceChildren();
      var unavailable = document.createElement("div");
      unavailable.className = "rn-run-journal-empty is-error";
      unavailable.textContent = "RunJournal renderer недоступен.";
      root.appendChild(unavailable);
      $("trajectoryStatus").textContent = "Журнал запуска не загружен.";
      $("trajectoryWorkspace").classList.remove("hidden");
      return;
    }
    var result;
    function rerender(keepScroll) {
      renderRunJournal(response, keepScroll !== false ? root.scrollTop : 0, loadedLimitReached);
    }
    result = adapter.render(root, events, {
      filter: journalFilter,
      expanded: expandedJournalRows,
      activeRunId: correlationFilter.runId || "",
      onFilterChange: function (filter) {
        journalFilter = filter;
        renderRunJournal(response, 0, loadedLimitReached);
      },
      onExpandedChange: function (id, open) {
        if (open) expandedJournalRows[id] = true;
        else delete expandedJournalRows[id];
      },
      onExpandedSet: function (ids, open) {
        (ids || []).forEach(function (id) {
          if (open) expandedJournalRows[id] = true;
          else delete expandedJournalRows[id];
        });
        rerender(true);
      },
      onNavigate: function (field, filterValue, targetView) {
        if (scopeKey() !== responseScope || sourceSelectionKey() !== sourceSelection) return;
        navigateCorrelation(field, filterValue, targetView, selectedChatId());
      },
      onLoadPayload: function (eventId, signal) {
        return readPayload(responseChatId, eventId, signal, function () {
          return isRunJournal() && scopeKey() === responseScope && sourceSelectionKey() === sourceSelection;
        }, responseWorkspace);
      }
    });
    root.scrollTop = previousScroll;
    nextCursor = value(response, "NextCursor", "nextCursor", null);
    var hasMore = !!value(response, "HasMore", "hasMore", false);
    var total = value(response, "TotalRows", "totalRows", events.length);
    var matches = value(response, "TotalMatches", "totalMatches", total);
    var message = result.error
      ? "Журнал не отображён: " + result.error
      : "Показано " + result.displayed + " из " + matches +
        (result.displayed !== result.loaded ? " · загружено " + result.loaded : "") +
        (result.truncated || loadedLimitReached ? " · достигнут лимит, уточните поиск" : "");
    $("trajectoryStatus").textContent = message;
    $("loadMoreTrajectoryButton").textContent = "Ещё строки";
    $("loadMoreTrajectoryButton").classList.toggle("hidden", !hasMore || !!loadedLimitReached);
    $("trajectoryWorkspace").classList.remove("hidden");
    renderCorrelationActions({});
  }

  function renderEvents(response, append, journalScrollTop) {
    activeView = value(response, "View", "view", "raw") || "raw";
    var page = activeView === "raw"
      ? (value(response, "Events", "events", []) || [])
      : (value(response, "Rows", "rows", []) || []);
    var combined = append ? events.concat(page) : page;
    var loadedLimitReached = false;
    if (isRunJournal()) {
      var journalLimit = window.RNAssistantRunJournal
        ? window.RNAssistantRunJournal.maxRenderedRows : 1000;
      loadedLimitReached = combined.length > journalLimit ||
        (combined.length === journalLimit && !!value(response, "HasMore", "hasMore", false));
      events = combined.slice(0, journalLimit);
    } else {
      events = combined;
    }
    if (isRunJournal()) {
      renderRunJournal(response, journalScrollTop, loadedLimitReached);
      return;
    }
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
      root.setAttribute("aria-label", activeView === "raw" ? "Технические события" : "Строки траектории");
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
        payload.textContent = bytesLabel(value(item, "PayloadByteLength", "payloadByteLength", 0));
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
      second.textContent = created ? new Date(created).toLocaleString("ru-RU", { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" }) : "";
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
    $("trajectoryStatus").textContent = "Показано " + events.length + " из " + matches + (matches !== total ? " · всего " + total : "");
    $("loadMoreTrajectoryButton").textContent = "Старше";
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
      unmountTrajectoryJson("trajectoryEventData");
      hideEvidence();
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
      workspaceId: trajectoryWorkspaceId,
      view: view,
      cursor: cursor || null,
      pageSize: view === "run-causal" ? 200 : 100,
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

  function downloadBundle(response, bytes) {
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

  function closeExportLease(operation) {
    if (operation && operation.data && operation.data.leaseId)
      return send("resourceDataClose", { chatId: operation.chatId, workspaceId: "trajectory-export" + (operation.workspaceId ? ":" + operation.workspaceId : ""),
        leaseId: operation.data.leaseId }).catch(function () {});
    return Promise.resolve();
  }

  function cancelTrajectoryExport() {
    if (!activeExport) return;
    activeExport.abort.abort();
    if (activeExport.requestId) cancelBridgeRequest(activeExport.requestId).catch(function () {});
    closeExportLease(activeExport);
  }

  async function exportTrajectory() {
    if (activeExport) return;
    var view = $("trajectoryViewInput").value || "raw";
    var chatId = selectedChatId();
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
    var operation = { chatId: chatId, workspaceId: trajectoryWorkspaceId, abort: new AbortController(), data: null, requestId: null };
    var selection = JSON.stringify(payload);
    var sourceSelection = sourceSelectionKey();
    activeExport = operation;
    window.addEventListener("pagehide", cancelTrajectoryExport, { once: true });
    function isCurrent() {
      return activeExport === operation && !operation.abort.signal.aborted && sourceSelectionKey() === sourceSelection &&
        selectedChatId() === chatId && JSON.stringify(exportPayload(chatId)) === selection;
    }
    try {
      button.disabled = true;
      button.textContent = "Готовлю ZIP…";
      $("trajectoryStatus").textContent = "Проверяю stream/CAS и формирую одноразовую projection…";
      var request = send("exportChatTrajectory", payload);
      operation.requestId = request.requestId;
      var response = await request;
      operation.requestId = null;
      operation.data = response && response.data;
      if (!isCurrent()) throw new Error("RESOURCE_DOWNLOAD_CANCELLED");
      if (!matchesScope(response, chatId, operation.workspaceId) || response.contentType !== "application/zip" || !operation.data ||
          !operation.data.payload || operation.data.payload.contentType !== response.contentType ||
          operation.data.payload.byteLength !== response.byteLength || operation.data.payload.sha256 !== response.bundleSha256)
        throw new Error("RESOURCE_DOWNLOAD_INVALID");
      button.textContent = "Скачиваю ZIP…";
      var bytes = await window.RNAssistantResourceDownload.read(operation.data, {
        fetch: window.fetch.bind(window), signal: operation.abort.signal, isCurrent: isCurrent, maxBytes: 24 * 1024 * 1024
      });
      if (!isCurrent()) throw new Error("RESOURCE_DOWNLOAD_CANCELLED");
      downloadBundle(response, bytes);
      $("trajectoryStatus").textContent = "Экспортировано событий: " + value(response, "EventCount", "eventCount", 0) +
        " · ZIP " + bytesLabel(value(response, "ByteLength", "byteLength", 0)) +
        " · sha256=" + shortHash(value(response, "BundleSha256", "bundleSha256", ""));
    } catch (error) {
      if (isCurrent()) $("trajectoryStatus").textContent = "Не удалось экспортировать trajectory: " + error.message;
    } finally {
      await closeExportLease(operation);
      window.removeEventListener("pagehide", cancelTrajectoryExport);
      if (activeExport === operation) {
        activeExport = null;
        button.textContent = "Скачать ZIP";
        updateExportControls();
      }
    }
  }

  function updateExportControls() {
    var mode = $("trajectoryExportRedactionInput").value || "metadata";
    var full = mode === "none";
    var vba = ($("trajectoryViewInput").value || "raw") === "vba-mutations";
    $("trajectoryExportCasInput").disabled = !full || vba;
    if (!full) $("trajectoryExportCasInput").checked = false;
    $("exportTrajectoryButton").disabled = vba || !!activeExport;
    $("trajectoryExportNotice").textContent = vba
      ? "Экспорт журнала VBA пока недоступен."
      : (mode === "metadata"
        ? "Тела событий и файлов не попадут в архив."
        : (mode === "secrets"
          ? "Секреты скрыты. Тексты запросов и документов могут остаться."
          : "Архив включает исходные данные без скрытия секретов."));
  }

  function clearTrajectoryRows() {
    events = [];
    selected = null;
    nextCursor = null;
    if (window.RNAssistantRunJournal) window.RNAssistantRunJournal.unmount($("trajectoryEvents"));
    else $("trajectoryEvents").replaceChildren();
    $("trajectoryWorkspace").classList.add("hidden");
    $("trajectoryEventTitle").textContent = "Запись не выбрана";
    $("trajectoryEventMeta").textContent = "";
    unmountTrajectoryJson("trajectoryEventData");
    hideEvidence();
    resetLazyDetail();
    renderCorrelationActions({});
  }

  async function refreshTrajectory(append, preserveJournalScroll) {
    if (!append) cancelTrajectoryExport();
    var requestedView = $("trajectoryViewInput").value || "raw";
    var vba = requestedView === "vba-mutations";
    var chatId = selectedChatId();
    var journalScrollTop = requestedView === "run-causal" && (append || preserveJournalScroll)
      ? $("trajectoryEvents").scrollTop : 0;
    if (!append) clearTrajectoryRows();
    if (!vba && !chatId) {
      $("trajectoryStatus").textContent = "Нет активного чата.";
      return;
    }
    var requestId = ++trajectoryRequestId;
    var requestedScope = scopeKey(), workspaceId = trajectoryWorkspaceId, sourceSelection = sourceSelectionKey();
    try {
      setTrajectoryBusy(true);
      if (vba && workspaceId) throw new Error("Журнал VBA доступен только из Office-чата.");
      $("trajectoryStatus").textContent = vba ? "Читаю VBA mutation journal…" : "Читаю event stream…";
      var response = await send(vba ? "getVbaMutations" : "getChatTrajectory", queryPayload(chatId, append ? nextCursor : null));
      if (requestId !== trajectoryRequestId ||
        requestedView !== ($("trajectoryViewInput").value || "raw") ||
        (!vba && selectedChatId() !== chatId)) return;
      if (requestedScope !== scopeKey() || sourceSelectionKey() !== sourceSelection) return;
      if (!vba && !matchesScope(response, chatId, workspaceId)) throw new Error("Ответ журнала относится к другому чату или проекту.");
      renderEvents(response, !!append, journalScrollTop);
    } catch (error) {
      if (requestId !== trajectoryRequestId || sourceSelectionKey() !== sourceSelection) return;
      $("trajectoryStatus").textContent = "Не удалось прочитать диагностику: " + error.message;
      $("trajectoryWorkspace").classList.add("hidden");
    } finally {
      if (requestId === trajectoryRequestId) setTrajectoryBusy(false);
    }
  }

  async function loadPayload() {
    if (activeView !== "raw" || !selected) return;
    var chatId = selectedChatId();
    if (!chatId) return;
    var button = $("loadTrajectoryPayloadButton");
    var selectedId = eventId(selected);
    var requestId = ++detailRequestId;
    if (activePayload) activePayload.abort();
    var operation = new AbortController();
    var payloadScope = scopeKey(), sourceSelection = sourceSelectionKey();
    activePayload = operation;
    function isCurrent() {
      return !operation.signal.aborted && scopeKey() === payloadScope && sourceSelectionKey() === sourceSelection && requestId === detailRequestId && activeView === "raw" &&
        selected && eventId(selected) === selectedId && selectedChatId() === chatId;
    }
    try {
      button.disabled = true;
      button.textContent = "Загружаю…";
      button.title = "";
      var response = await readPayload(chatId, selectedId, operation.signal, isCurrent);
      if (!isCurrent()) return;
      var text = value(response, "Text", "text", "");
      var truncated = value(response, "TextTruncated", "textTruncated", false);
      var contentType = value(response, "ContentType", "contentType", "");
      detailPayload = { text: text, truncated: truncated, contentType: contentType };
      $("trajectoryDetailPart").value = "payload";
      renderDetail();
      button.textContent = "Обновить";
    } catch (error) {
      if (!isCurrent()) return;
      $("trajectoryDetailPart").value = "payload";
      detailPayload = { text: "Не удалось загрузить содержимое: " + error.message,
        contentType: "text/plain", truncated: false, error: true };
      renderDetail();
      button.title = detailPayload.text;
      button.textContent = "Повторить";
    } finally {
      if (activePayload === operation) activePayload = null;
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
      window.RNAssistantTextDiff.render(diff, window.RNAssistantTextDiff.format(before, after));
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
    routeHistory = routeHistory.concat([captureRoute()]).slice(-20); updateBackButton();
    correlationFilter = {};
    updateViewControls();
    nextCursor = null;
    refreshTrajectory(false);
  }

  function updateViewControls() {
    var vbaOption = $("trajectoryViewInput").querySelector('option[value="vba-mutations"]');
    if (vbaOption) { vbaOption.hidden = !!trajectoryWorkspaceId; vbaOption.disabled = !!trajectoryWorkspaceId; }
    var view = $("trajectoryViewInput").value || "raw";
    var raw = view === "raw";
    var vba = view === "vba-mutations";
    var journal = view === "run-causal";
    var panel = document.querySelector(".trajectory-panel");
    if (panel) {
      panel.classList.toggle("is-derived", !raw);
      panel.classList.toggle("is-artifact-tree", view === "artifact-lineage");
      panel.classList.toggle("is-vba", vba);
      panel.classList.toggle("is-run-journal", journal);
    }
    $("trajectoryViewField").classList.remove("hidden");
    $("trajectoryTypeField").classList.toggle("hidden", !raw);
    $("trajectoryTypeInput").disabled = !raw;
    $("trajectoryVisibilityField").classList.toggle("hidden", !raw);
    $("trajectoryVisibilityInput").disabled = !raw;
    $("trajectoryVbaKindField").classList.toggle("hidden", !vba);
    $("trajectoryVbaKindInput").disabled = !vba;
    $("trajectoryVbaStatusField").classList.toggle("hidden", !vba);
    $("trajectoryVbaStatusInput").disabled = !vba;
    $("trajectoryTitle").textContent = "История чата";
    var scope = correlationFilter.toolCallId ? "Выбранное действие" : correlationFilter.stepId ? "Выбранный шаг" :
      correlationFilter.runId ? "Выбранный запуск" : correlationFilter.minSequence ? "Выбранные события" : "Весь чат";
    $("trajectoryDescription").textContent = [trajectoryLabel, vba ? "Операции текущего документа" : scope].filter(Boolean).join(" · ");
    $("trajectoryDescription").title = activeFilterText();
    $("trajectoryFiltersButton").classList.toggle("hidden", !raw && !vba);
    if (!raw && !vba) { $("trajectoryAdvancedOptions").open = false; $("trajectoryAdvancedOptions").classList.add("hidden"); }
    updateBackButton();
    updateExportControls();
  }

  function setDiagnosticsMode(mode, refresh) {
    invalidateTrajectoryRequest();
    correlationFilter = {};
    useSource(selectedSource());
    routeHistory = [];
    nextCursor = null;
    $("trajectoryViewInput").value = "run-causal";
    var latestRunId = latestKnownRunId();
    if (latestRunId) correlationFilter.runId = latestRunId;
    updateViewControls();
    if (refresh) refreshTrajectory(false);
  }

  function openRunJournal(options) {
    options = options || {};
    var source = selectedSource();
    if (source.owner !== "office") {
      if (options.owner !== source.owner || !source.workspaceId || !source.chatId ||
          options.workspaceId !== source.workspaceId || options.sessionId !== source.chatId) return false;
    } else {
      if (options.workspaceId || options.owner && options.owner !== "office") return false;
      source.chatId = options.chatId || source.chatId;
    }
    if (!source.chatId) return false;
    if (typeof switchTab === "function") switchTab("settings");
    var diagnosticsNav = document.querySelector('#settingsNavTree [data-settings-page="service"]');
    if (diagnosticsNav) diagnosticsNav.click();
    if (typeof setDiagnosticsTab === "function") setDiagnosticsTab("trajectory", false);
    invalidateTrajectoryRequest();
    correlationFilter = {};
    useSource(source);
    $("trajectoryViewInput").value = "run-causal";
    if (options.runId) correlationFilter.runId = String(options.runId);
    if (options.turnId) correlationFilter.turnId = String(options.turnId);
    if (options.stepId) correlationFilter.stepId = String(options.stepId);
    if (options.toolCallId) correlationFilter.toolCallId = String(options.toolCallId);
    if (!correlationFilter.runId && !correlationFilter.toolCallId) {
      var latestRunId = latestKnownRunId();
      if (latestRunId) correlationFilter.runId = latestRunId;
    }
    journalFilter = options.filter || "all";
    expandedJournalRows = {};
    nextCursor = null;
    updateViewControls();
    refreshTrajectory(false);
    return true;
  }

  window.bindTrajectoryActions = function () {
    window.addEventListener("pagehide", window.RNAssistantTrajectoryPayload.cancelAll);
    $("trajectoryDetailPart").addEventListener("change", renderDetail);
    $("trajectoryBackButton").addEventListener("click", goBack);
    [["trajectoryFiltersButton", "trajectoryAdvancedOptions"], ["trajectoryExportOptionsButton", "trajectoryExportOptions"]].forEach(function (pair) {
      $(pair[0]).addEventListener("click", function () {
        var panel = $(pair[1]); panel.classList.remove("hidden"); panel.open = true;
        panel.querySelector("summary").focus();
      });
      $(pair[1]).addEventListener("toggle", function () { if (!this.open) this.classList.add("hidden"); });
    });
    var refresh = $("refreshTrajectoryButton");
    var more = $("loadMoreTrajectoryButton");
    var payload = $("loadTrajectoryPayloadButton");
    var vbaDetail = $("loadVbaMutationButton");
    var exportButton = $("exportTrajectoryButton");
    if (refresh) refresh.addEventListener("click", function () { refreshTrajectory(false, true); });
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
      nextCursor = null;
      updateViewControls();
      refreshTrajectory(false);
    });
    $("clearTrajectoryCorrelationButton").addEventListener("click", clearCorrelation);
    $("trajectoryExportRedactionInput").addEventListener("change", function () { cancelTrajectoryExport(); updateExportControls(); });
    $("trajectoryExportCasInput").addEventListener("change", cancelTrajectoryExport);
    if (payload) payload.addEventListener("click", loadPayload);
    if (vbaDetail) vbaDetail.addEventListener("click", loadVbaMutation);
    updateViewControls();
  };
  window.setTrajectoryDiagnosticsMode = setDiagnosticsMode;
  window.openRunJournal = openRunJournal;
}());
