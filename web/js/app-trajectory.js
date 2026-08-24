(function () {
  var events = [];
  var selected = null;
  var nextCursor = null;

  function value(source, pascal, camel, fallback) {
    source = source || {};
    return source[camel] !== undefined ? source[camel] : (source[pascal] !== undefined ? source[pascal] : fallback);
  }

  function prettyJson(text) {
    if (!text) return "{}";
    try { return JSON.stringify(JSON.parse(text), null, 2); }
    catch (error) { return text; }
  }

  function bytesLabel(bytes) {
    var size = Number(bytes || 0);
    if (size < 1024) return size + " B";
    if (size < 1024 * 1024) return (size / 1024).toFixed(1) + " KB";
    return (size / (1024 * 1024)).toFixed(1) + " MB";
  }

  function eventId(item) { return value(item, "EventId", "eventId", ""); }

  function selectEvent(item, button) {
    selected = item;
    Array.prototype.slice.call($("trajectoryEvents").querySelectorAll(".trajectory-event")).forEach(function (node) {
      node.classList.toggle("active", node === button);
      node.setAttribute("aria-selected", node === button ? "true" : "false");
    });
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
    var statuses = value(item, "Statuses", "statuses", []) || [];
    $("trajectoryEventTitle").textContent = "#" + sequence + "  " + type;
    $("trajectoryEventMeta").textContent = [
      runId ? "run=" + runId : "",
      turnId ? "turn=" + turnId : "",
      stepId ? "step=" + stepId : "",
      visibility || "",
      toolCallIds.length ? "tool=" + toolCallIds.join(",") : "",
      artifactIds.length ? "artifact=" + artifactIds.join(",") : "",
      statuses.length ? "status=" + statuses.join(",") : "",
      previousHash ? "prev=" + previousHash : "root",
      hash ? "hash=" + hash : "",
      payloadSize === null ? "" : "payload=" + bytesLabel(payloadSize)
    ].filter(Boolean).join(" · ");
    var data = value(item, "DataJson", "dataJson", "");
    $("trajectoryEventData").textContent = prettyJson(data) +
      (value(item, "DataTruncated", "dataTruncated", false) ? "\n\n[preview truncated]" : "");
    $("trajectoryEventPayload").textContent = "";
    $("trajectoryEventPayload").classList.add("hidden");
    var payloadButton = $("loadTrajectoryPayloadButton");
    payloadButton.classList.toggle("hidden", payloadSize === null);
    payloadButton.disabled = false;
    payloadButton.textContent = "Показать payload";
  }

  function renderEvents(response, append) {
    var page = value(response, "Events", "events", []) || [];
    events = append ? events.concat(page) : page;
    var root = $("trajectoryEvents");
    root.replaceChildren();
    events.forEach(function (item) {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "trajectory-event";
      button.setAttribute("role", "option");
      button.setAttribute("aria-selected", "false");
      var first = document.createElement("span");
      first.className = "trajectory-event-line";
      var sequence = document.createElement("span");
      sequence.className = "trajectory-event-sequence";
      sequence.textContent = "#" + value(item, "Sequence", "sequence", 0);
      var type = document.createElement("span");
      type.className = "trajectory-event-type";
      type.textContent = value(item, "Type", "type", "event");
      first.appendChild(sequence);
      first.appendChild(type);
      if (value(item, "PayloadByteLength", "payloadByteLength", null) !== null) {
        var payload = document.createElement("span");
        payload.className = "trajectory-event-payload";
        payload.textContent = "payload";
        first.appendChild(payload);
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

    var total = value(response, "TotalEvents", "totalEvents", events.length);
    var matches = value(response, "TotalMatches", "totalMatches", events.length);
    nextCursor = value(response, "NextCursor", "nextCursor", null);
    var hasMore = !!value(response, "HasMore", "hasMore", false);
    $("trajectoryStatus").textContent = "Совпадений: " + matches + " из " + total + " · загружено " + events.length +
      " · payload только по запросу";
    $("loadMoreTrajectoryButton").classList.toggle("hidden", !hasMore);
    $("trajectoryWorkspace").classList.toggle("hidden", events.length === 0);
    if (!append && events.length) {
      selectEvent(events[0], root.firstElementChild);
      root.scrollTop = 0;
    } else if (append && selected) {
      var selectedIndex = events.map(eventId).indexOf(eventId(selected));
      if (selectedIndex >= 0) selectEvent(events[selectedIndex], root.children[selectedIndex]);
    } else if (!events.length) {
      selected = null;
      $("trajectoryEventTitle").textContent = "Событие не выбрано";
      $("trajectoryEventMeta").textContent = "";
      $("trajectoryEventData").textContent = "";
      $("trajectoryEventPayload").textContent = "";
      $("loadTrajectoryPayloadButton").classList.add("hidden");
    }
  }

  function queryPayload(chatId, cursor) {
    return {
      chatId: chatId,
      cursor: cursor || null,
      pageSize: 100,
      search: $("trajectorySearchInput").value.trim(),
      eventTypes: $("trajectoryTypeInput").value.split(",").map(function (item) { return item.trim(); }).filter(Boolean),
      visibility: $("trajectoryVisibilityInput").value || null
    };
  }

  async function refreshTrajectory(append) {
    var button = $("refreshTrajectoryButton");
    if (!state.activeChatId) {
      $("trajectoryStatus").textContent = "Нет активного чата.";
      return;
    }
    var chatId = state.activeChatId;
    try {
      button.disabled = true;
      $("loadMoreTrajectoryButton").disabled = true;
      $("trajectoryStatus").textContent = "Читаю event stream…";
      var response = await send("getChatTrajectory", queryPayload(chatId, append ? nextCursor : null));
      if (state.activeChatId !== chatId) return;
      renderEvents(response, !!append);
    } catch (error) {
      $("trajectoryStatus").textContent = "Не удалось прочитать траекторию: " + error.message;
      $("trajectoryWorkspace").classList.add("hidden");
    } finally {
      button.disabled = false;
      $("loadMoreTrajectoryButton").disabled = false;
    }
  }

  async function loadPayload() {
    if (!selected || !state.activeChatId) return;
    var button = $("loadTrajectoryPayloadButton");
    var target = $("trajectoryEventPayload");
    var selectedId = eventId(selected);
    try {
      button.disabled = true;
      button.textContent = "Загружаю…";
      var response = await send("getChatEventPayload", { chatId: state.activeChatId, eventId: selectedId });
      if (!selected || eventId(selected) !== selectedId) return;
      var text = value(response, "Text", "text", "");
      target.textContent = prettyJson(text) +
        (value(response, "TextTruncated", "textTruncated", false) ? "\n\n[preview truncated; full payload remains in CAS]" : "");
      target.classList.remove("hidden");
      button.textContent = "Payload загружен";
    } catch (error) {
      target.textContent = "Не удалось загрузить payload: " + error.message;
      target.classList.remove("hidden");
      button.textContent = "Повторить";
    } finally {
      button.disabled = false;
    }
  }

  window.bindTrajectoryActions = function () {
    var refresh = $("refreshTrajectoryButton");
    var more = $("loadMoreTrajectoryButton");
    var payload = $("loadTrajectoryPayloadButton");
    if (refresh) refresh.addEventListener("click", function () { refreshTrajectory(false); });
    if (more) more.addEventListener("click", function () { refreshTrajectory(true); });
    ["trajectorySearchInput", "trajectoryTypeInput"].forEach(function (id) {
      $(id).addEventListener("input", function () {
        nextCursor = null;
        $("loadMoreTrajectoryButton").classList.add("hidden");
      });
      $(id).addEventListener("keydown", function (event) { if (event.key === "Enter") refreshTrajectory(false); });
    });
    $("trajectoryVisibilityInput").addEventListener("change", function () { refreshTrajectory(false); });
    if (payload) payload.addEventListener("click", loadPayload);
  };
}());
