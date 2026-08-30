(function () {
  "use strict";

  var lifecycles = {
    running: "running",
    completed: "completed",
    awaitingconfirmation: "awaiting_confirmation",
    awaitinguser: "awaiting_user",
    cancelled: "cancelled",
    failed: "failed"
  };
  var healthValues = { clean: true, errors: true, unknown: true };

  function value(source, pascal, camel, fallback) {
    source = source || {};
    return source[pascal] !== undefined ? source[pascal] :
      (source[camel] !== undefined ? source[camel] : fallback);
  }

  function text(source, pascal, camel) {
    var result = value(source, pascal, camel, "");
    return result === null || result === undefined ? "" : String(result);
  }

  function count(source, pascal, camel) {
    var result = value(source, pascal, camel, null);
    return Number.isSafeInteger(result) && result >= 0 ? result : null;
  }

  function lifecycle(source) {
    var raw = text(source, "Lifecycle", "lifecycle").toLowerCase().replace(/[^a-z]/g, "");
    return lifecycles[raw] || "";
  }

  function pending(source) {
    var raw = value(source, "PendingConfirmation", "pendingConfirmation", null);
    if (!raw) return null;
    var result = {
      pendingId: text(raw, "PendingId", "pendingId"),
      toolCallId: text(raw, "ToolCallId", "toolCallId"),
      toolName: text(raw, "ToolName", "toolName")
    };
    if (!result.pendingId || !result.toolCallId || !result.toolName) return null;
    return Object.freeze(result);
  }

  function normalize(source) {
    if (!source) return null;
    var normalizedLifecycle = lifecycle(source);
    var executionHealth = text(source, "ExecutionHealth", "executionHealth").toLowerCase();
    var result = {
      runId: text(source, "RunId", "runId"),
      turnId: text(source, "TurnId", "turnId"),
      narrative: text(source, "Narrative", "narrative"),
      lifecycle: normalizedLifecycle,
      executionHealth: executionHealth,
      successfulReads: count(source, "SuccessfulReads", "successfulReads"),
      verifiedWrites: count(source, "VerifiedWrites", "verifiedWrites"),
      noChangeWrites: count(source, "NoChangeWrites", "noChangeWrites"),
      unverifiedWrites: count(source, "UnverifiedWrites", "unverifiedWrites"),
      failedCalls: count(source, "FailedCalls", "failedCalls"),
      unknownEffects: count(source, "UnknownEffects", "unknownEffects"),
      pendingConfirmation: pending(source),
      reason: text(source, "Reason", "reason"),
      currentAction: text(source, "CurrentAction", "currentAction"),
      startedUtc: text(source, "StartedUtc", "startedUtc")
    };
    if (!result.runId || !result.turnId || !normalizedLifecycle || !healthValues[executionHealth] ||
        result.successfulReads === null || result.verifiedWrites === null || result.noChangeWrites === null ||
        result.unverifiedWrites === null || result.failedCalls === null || result.unknownEffects === null ||
        executionHealth !== (result.unknownEffects > 0 ? "unknown" : (result.failedCalls > 0 ? "errors" : "clean")) ||
        result.unverifiedWrites > result.unknownEffects ||
        ((normalizedLifecycle === "awaiting_confirmation") !== !!result.pendingConfirmation)) return null;
    return Object.freeze(result);
  }

  function fromMessage(message) {
    return normalize(value(message, "RunViewState", "runViewState", null));
  }

  function fromChatSummary(chat) {
    return normalize(value(chat, "RunViewState", "runViewState", null));
  }

  function sessionRevision(source) {
    var revision = value(source, "SessionRevision", "sessionRevision", null);
    return Number.isSafeInteger(revision) && revision >= 0 ? revision : null;
  }

  function chatRevision(chat) {
    var revision = value(chat, "Revision", "revision", null);
    return Number.isSafeInteger(revision) && revision >= 0 ? revision : null;
  }

  function accept(revisions, chatId, revision) {
    if (!chatId) return true;
    revisions = revisions || {};
    var known = Object.prototype.hasOwnProperty.call(revisions, chatId) ? revisions[chatId] : null;
    if (revision === null) return known === null;
    if (known !== null && revision < known) return false;
    revisions[chatId] = revision;
    return true;
  }

  function mergeCatalog(current, incoming, revisions, preserveExisting) {
    var currentById = {};
    (current || []).forEach(function (chat) {
      var id = text(chat, "Id", "id");
      if (id) currentById[id] = chat;
    });
    var incomingById = {};
    var incomingOrder = [];
    (incoming || []).forEach(function (chat) {
      var id = text(chat, "Id", "id");
      if (id && !Object.prototype.hasOwnProperty.call(incomingById, id)) incomingOrder.push(id);
      if (id) incomingById[id] = chat;
    });

    function newest(chat, previous) {
      var id = text(chat, "Id", "id");
      var nextRevision = chatRevision(chat);
      var previousRevision = chatRevision(previous);
      var selected = previous && previousRevision !== null &&
        (nextRevision === null || previousRevision > nextRevision) ? previous : chat;
      var selectedRevision = chatRevision(selected);
      if (id && selectedRevision !== null) revisions[id] = Math.max(revisions[id] || 0, selectedRevision);
      return selected;
    }

    if (!preserveExisting) {
      return incomingOrder.map(function (id) {
        return newest(incomingById[id], currentById[id]);
      });
    }

    var seen = {};
    var merged = (current || []).map(function (chat) {
      var id = text(chat, "Id", "id");
      if (id) seen[id] = true;
      return id && incomingById[id] ? newest(incomingById[id], chat) : chat;
    });
    incomingOrder.forEach(function (id) {
      if (seen[id]) return;
      merged.push(newest(incomingById[id], null));
    });
    return merged;
  }

  function displayStatus(viewState, liveStatus) {
    if (!viewState) return liveStatus || "unknown";
    if (viewState.executionHealth === "unknown") return "unknown";
    if (viewState.executionHealth === "errors") return "failed";
    if (viewState.lifecycle === "awaiting_confirmation" || viewState.lifecycle === "awaiting_user") return "waiting";
    return viewState.lifecycle;
  }

  function outcomeLabel(viewState) {
    if (!viewState) return "Статус неизвестен";
    if (viewState.lifecycle === "awaiting_confirmation") return "Нужно подтверждение";
    if (viewState.lifecycle === "awaiting_user") return "Ожидает ответа";
    if (viewState.lifecycle === "cancelled") return "Отменено";
    if (viewState.lifecycle === "failed") return viewState.reason === "provider_refused" ? "Отказ провайдера" : "Ошибка выполнения";
    if (viewState.lifecycle === "completed") return "Готово";
    return "Выполняется";
  }

  window.RNAssistantRunViewState = Object.freeze({
    normalize: normalize,
    fromMessage: fromMessage,
    fromChatSummary: fromChatSummary,
    sessionRevision: sessionRevision,
    chatRevision: chatRevision,
    accept: accept,
    mergeCatalog: mergeCatalog,
    displayStatus: displayStatus,
    outcomeLabel: outcomeLabel
  });
}());
