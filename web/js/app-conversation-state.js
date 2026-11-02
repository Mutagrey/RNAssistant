(function () {
  "use strict";
  var messageWindowLimit = 240;

  // An addressed source validates delivery; it retains no snapshot or history.
  function createSnapshotSource(target, identify) {
    if (!target || !target.source || !target.sessionId ||
        target.source === "workspace" && !target.workspaceId)
      throw new Error("Не задан адрес чата.");
    var address = Object.freeze({ source: target.source, workspaceId: target.workspaceId || "", sessionId: target.sessionId });
    function matches(snapshot) {
      var actual = snapshot && identify(snapshot);
      return !!actual && actual.source === address.source && actual.sessionId === address.sessionId &&
        (actual.workspaceId || "") === address.workspaceId;
    }
    function accept(snapshot) {
      if (!matches(snapshot)) throw new Error("Состояние получено для другого чата. Повторите загрузку.");
      return snapshot;
    }
    return { target: address, matches: matches, accept: accept,
      read: async function (load) { return accept(await load(address)); } };
  }

  // One bounded projection per owner; the runtime/store still owns durable data.
  function createSnapshotOwner(source, merge) {
    var snapshot = null, disposed = false;
    return { target: source.target,
      snapshot: function () { return snapshot; },
      isCurrent: function () { return !disposed; },
      accept: function (incoming, options) {
        if (disposed) return null;
        source.accept(incoming);
        snapshot = boundedWindow(merge(snapshot, incoming, options), false);
        return snapshot;
      },
      dispose: function () { disposed = true; snapshot = null; }
    };
  }

  function boundedWindow(snapshot, keepStart) {
    if (!snapshot || !Array.isArray(snapshot.messages) || snapshot.messages.length <= messageWindowLimit) return snapshot;
    var offset = keepStart ? 0 : snapshot.messages.length - messageWindowLimit;
    snapshot.messages = snapshot.messages.slice(offset, offset + messageWindowLimit);
    snapshot.messageStartIndex += offset;
    return snapshot;
  }

  function prependMessageWindow(current, page) {
    var messages = page && page.messages, start = page && page.startIndex;
    if (!current || !Array.isArray(messages) || !messages.length ||
        !Number.isSafeInteger(start) || start < 0 ||
        start + messages.length !== current.messageStartIndex ||
        page.totalCount !== current.messageTotalCount)
      throw new Error("Неверная граница страницы истории чата.");
    return boundedWindow({ messages: messages.concat(current.messages || []),
      messageStartIndex: start, messageTotalCount: page.totalCount }, true);
  }

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
        !Array.isArray(current.messages) || !Array.isArray(incoming.messages)) return boundedWindow(incoming, false);
    var oldStart = current.messageStartIndex, nextStart = incoming.messageStartIndex;
    var oldTotal = current.messageTotalCount, nextTotal = incoming.messageTotalCount;
    if (!Number.isSafeInteger(oldStart) || !Number.isSafeInteger(nextStart) ||
        !Number.isSafeInteger(oldTotal) || !Number.isSafeInteger(nextTotal)) return boundedWindow(incoming, false);
    function id(message) { return message && (message.Id || message.id || ""); }
    if (oldStart === nextStart && oldTotal === nextTotal &&
        (current.messages.length > incoming.messages.length ||
          current.messages.length === incoming.messages.length && current.messages.some(function (message, index) {
            return id(message) !== id(incoming.messages[index]);
          }))) {
      incoming.messages = current.messages;
      return boundedWindow(incoming, true);
    }
    // An unchanged snapshot may contain only the latest page, outside the
    // reader's bounded window. Keep the reading position until an explicit jump.
    if (oldStart < nextStart && oldTotal === nextTotal && current.messages.length) {
      incoming.messages = current.messages;
      incoming.messageStartIndex = oldStart;
      return boundedWindow(incoming, true);
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
        return boundedWindow(incoming, true);
      }
    }
    return boundedWindow(incoming, false);
  }

  window.RNAssistantConversationState = { status: status, lifecycle: lifecycle,
    messageWindow: messageWindow, prependMessageWindow: prependMessageWindow,
    createSnapshotSource: createSnapshotSource, createSnapshotOwner: createSnapshotOwner };
})();
