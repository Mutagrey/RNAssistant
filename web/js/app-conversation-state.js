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

  window.RNAssistantConversationState = { status: status };
})();
