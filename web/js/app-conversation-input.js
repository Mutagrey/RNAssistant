(function () {
  "use strict";

  function accepted(kind, target, operationId, candidate, steerRunId) {
    if (!target || !operationId || !candidate) return false;
    var receipt = kind === "office" ? candidate.acceptedInput : candidate;
    if (!receipt) return false;
    if (kind === "office") {
      return candidate.chatId === target.chatId && receipt.OperationId === operationId &&
        !!receipt.Id && Number.isSafeInteger(receipt.Revision) && receipt.Revision > 0 &&
        (receipt.Status === "Pending" || receipt.Status === "Delivering" || receipt.Status === "Applied") &&
        (!steerRunId || receipt.AcceptedRunId === steerRunId && receipt.Delivery === "Steer");
    }
    if (kind !== "workspace") throw new Error("Unknown conversation input kind: " + kind);
    if (receipt.SessionId !== target.sessionId || receipt.RequestId !== operationId ||
        !receipt.InputId || !receipt.RunId || !receipt.AcceptedCursor ||
        !Number.isSafeInteger(receipt.AcceptedSequence) || receipt.AcceptedSequence <= 0) return false;
    if (steerRunId) return receipt.RunId === steerRunId && receipt.Steering === true;
    return receipt.DeliveryRecorded === true && receipt.Steering !== true;
  }

  function create(kind, target, operationId, steerRunId, onAccepted) {
    var acknowledged = false;
    function accept(candidate) {
      if (acknowledged) return true;
      if (!accepted(kind, target, operationId, candidate, steerRunId)) return false;
      if (onAccepted) onAccepted(candidate);
      acknowledged = true;
      return true;
    }
    return {
      accept: accept,
      submit: async function (flush, dispatch, receiptOf) {
        await flush();
        var response = await dispatch(accept);
        if (!accept(receiptOf(response)))
          throw new Error("Доставка сообщения не подтверждена. Черновик сохранён.");
        return response;
      }
    };
  }

  window.RNAssistantConversationInput = { accepted: accepted, create: create };
})();
