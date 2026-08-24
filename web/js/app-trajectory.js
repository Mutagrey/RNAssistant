(function () {
  var events = [];
  var selected = null;

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
    var payloadSize = value(item, "PayloadByteLength", "payloadByteLength", null);
    $("trajectoryEventTitle").textContent = "#" + sequence + "  " + type;
    $("trajectoryEventMeta").textContent = [
      runId ? "run=" + runId : "",
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

  function renderEvents(response) {
    events = value(response, "Events", "events", []) || [];
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
    var start = value(response, "StartSequence", "startSequence", null);
    var truncated = !!value(response, "Truncated", "truncated", false);
    $("trajectoryStatus").textContent = "Событий: " + total +
      (truncated ? " · показаны #" + start + "–#" + value(response, "Revision", "revision", total) : "") +
      " · payload загружается только по запросу";
    $("trajectoryWorkspace").classList.toggle("hidden", events.length === 0);
    if (events.length) {
      var lastButton = root.lastElementChild;
      selectEvent(events[events.length - 1], lastButton);
      root.scrollTop = root.scrollHeight;
    }
  }

  async function refreshTrajectory() {
    var button = $("refreshTrajectoryButton");
    if (!state.activeChatId) {
      $("trajectoryStatus").textContent = "Нет активного чата.";
      return;
    }
    var chatId = state.activeChatId;
    try {
      button.disabled = true;
      $("trajectoryStatus").textContent = "Читаю event stream…";
      var response = await send("getChatTrajectory", { chatId: chatId });
      if (state.activeChatId !== chatId) return;
      renderEvents(response);
    } catch (error) {
      $("trajectoryStatus").textContent = "Не удалось прочитать траекторию: " + error.message;
      $("trajectoryWorkspace").classList.add("hidden");
    } finally {
      button.disabled = false;
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
    var payload = $("loadTrajectoryPayloadButton");
    if (refresh) refresh.addEventListener("click", refreshTrajectory);
    if (payload) payload.addEventListener("click", loadPayload);
  };
}());
