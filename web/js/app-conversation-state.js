(function () {
  "use strict";

  // A saved kernel checkpoint records lifecycle, but cannot prove that its
  // owner is still executing after a reconnect. Both chat shells use this rule.
  function status(confirmedRunning, savedRunning, awaitingReply) {
    if (confirmedRunning) return { status: "running", label: "Запрос выполняется" };
    if (awaitingReply) return { status: "waiting", label: "Требуется ответ" };
    if (savedRunning) return { status: "unknown", label: "Состояние запуска не подтверждено" };
    return { status: "", label: "" };
  }

  // The kernel view wins over a stale shell status only for the same run.
  function lifecycle(savedStatus, runId, view) {
    var saved = String(savedStatus || "").toLowerCase();
    if (saved === "awaitingconfirmation") saved = "awaiting_confirmation";
    if (saved === "awaitinguser") saved = "awaiting_user";
    return view && runId && view.runId === runId ? view.lifecycle : saved;
  }

  // A delayed snapshot at the same revision must not replace accepted history
  // or discard pages already loaded by the reader. Both shells use this rule.
  function messageWindow(current, incoming, sameRevision) {
    if (!sameRevision || !current || !incoming ||
        !Array.isArray(current.messages) || !Array.isArray(incoming.messages)) return incoming;
    var oldStart = current.messageStartIndex, nextStart = incoming.messageStartIndex;
    var oldTotal = current.messageTotalCount, nextTotal = incoming.messageTotalCount;
    if (!Number.isSafeInteger(oldStart) || !Number.isSafeInteger(nextStart) ||
        !Number.isSafeInteger(oldTotal) || !Number.isSafeInteger(nextTotal)) return incoming;
    function id(message) { return message && (message.Id || message.id || ""); }
    if (oldStart === nextStart && oldTotal === nextTotal &&
        (current.messages.length > incoming.messages.length ||
          current.messages.length === incoming.messages.length && current.messages.some(function (message, index) {
            return id(message) !== id(incoming.messages[index]);
          }))) {
      incoming.messages = current.messages;
      return incoming;
    }
    if (oldStart < nextStart && nextTotal >= oldTotal) {
      var prependCount = nextStart - oldStart;
      var overlap = Math.min(current.messages.length - prependCount, incoming.messages.length);
      if (prependCount <= current.messages.length && overlap > 0 &&
          current.messages.slice(prependCount, prependCount + overlap).every(function (message, index) {
            return id(message) && id(message) === id(incoming.messages[index]);
          })) {
        incoming.messages = current.messages.slice(0, prependCount).concat(incoming.messages);
        incoming.messageStartIndex = oldStart;
      }
    }
    return incoming;
  }

  window.RNAssistantConversationState = { status: status, lifecycle: lifecycle, messageWindow: messageWindow };
})();
