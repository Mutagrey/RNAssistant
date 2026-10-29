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

  window.RNAssistantConversationState = { status: status, lifecycle: lifecycle };
})();
