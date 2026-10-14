(function () {
  "use strict";

  var lifecycles = {
    running: true,
    completed: true,
    awaiting_confirmation: true,
    awaiting_user: true,
    cancelled: true,
    failed: true
  };
  var healthValues = { clean: true, errors: true, unknown: true };

  function text(source, key) {
    var result = source ? source[key] : null;
    return result === null || result === undefined ? "" : String(result);
  }

  function count(source, key) {
    var result = source ? source[key] : null;
    return Number.isSafeInteger(result) && result >= 0 ? result : null;
  }

  function pending(source) {
    var raw = source.PendingConfirmation;
    if (!raw) return null;
    var result = {
      pendingId: text(raw, "PendingId"),
      toolCallId: text(raw, "ToolCallId"),
      toolName: text(raw, "ToolName")
    };
    if (!result.pendingId || !result.toolCallId || !result.toolName) return null;
    return Object.freeze(result);
  }

  function normalize(source) {
    if (!source) return null;
    var normalizedLifecycle = text(source, "Lifecycle");
    var executionHealth = text(source, "ExecutionHealth");
    var result = {
      runId: text(source, "RunId"),
      turnId: text(source, "TurnId"),
      narrative: text(source, "Narrative"),
      lifecycle: normalizedLifecycle,
      executionHealth: executionHealth,
      goalVerification: text(source, "GoalVerification") || "not_assessed",
      successfulReads: count(source, "SuccessfulReads"),
      verifiedWrites: count(source, "VerifiedWrites"),
      noChangeWrites: count(source, "NoChangeWrites"),
      unverifiedWrites: count(source, "UnverifiedWrites"),
      failedCalls: count(source, "FailedCalls"),
      unknownEffects: count(source, "UnknownEffects"),
      pendingConfirmation: pending(source),
      reason: text(source, "Reason"),
      currentAction: text(source, "CurrentAction"),
      startedUtc: text(source, "StartedUtc")
    };
    if (!result.runId || !result.turnId || lifecycles[normalizedLifecycle] !== true ||
        healthValues[executionHealth] !== true ||
        result.goalVerification !== "not_assessed" ||
        result.successfulReads === null || result.verifiedWrites === null || result.noChangeWrites === null ||
        result.unverifiedWrites === null || result.failedCalls === null || result.unknownEffects === null ||
        executionHealth !== (result.unknownEffects > 0 ? "unknown" : (result.failedCalls > 0 ? "errors" : "clean")) ||
        result.unverifiedWrites > result.unknownEffects ||
        ((normalizedLifecycle === "awaiting_confirmation") !== !!result.pendingConfirmation)) return null;
    return Object.freeze(result);
  }

  function fromMessage(message) {
    return normalize(message && message.RunViewState);
  }

  function fromChatSummary(chat) {
    return normalize(chat && chat.RunViewState);
  }

  function sessionRevision(source) {
    return count(source, "sessionRevision");
  }

  function chatRevision(chat) {
    return count(chat, "Revision");
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
      var id = text(chat, "Id");
      if (id) currentById[id] = chat;
    });
    var incomingById = {};
    var incomingOrder = [];
    (incoming || []).forEach(function (chat) {
      var id = text(chat, "Id");
      if (id && !Object.prototype.hasOwnProperty.call(incomingById, id)) incomingOrder.push(id);
      if (id) incomingById[id] = chat;
    });

    function newest(chat, previous) {
      var id = text(chat, "Id");
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
      var id = text(chat, "Id");
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
    if (viewState.lifecycle === "awaiting_confirmation" || viewState.lifecycle === "awaiting_user") return "waiting";
    return viewState.lifecycle;
  }

  // Kernel reasons only: never infer a timeout/effect from arbitrary diagnostic text.
  function failureReasonLabel(reason) {
    var labels = {
      interrupted: "Предыдущий запуск прерван. Сохранённые результаты доступны в истории.",
      interrupted_unknown: "Запуск прерван. Результат действия неизвестен — проверьте документ.",
      provider: "Не удалось получить ответ от сервиса модели.",
      model_infrastructure: "Не удалось выполнить запрос к модели.",
      infrastructure: "Не удалось выполнить запрос к модели.",
      provider_refused: "Сервис модели отклонил запрос.",
      missing_model_result: "Модель не вернула ответ.",
      protocolexhausted: "Модель не вернула ответ в нужном формате.",
      invalid_model_response: "Модель не вернула ответ в нужном формате.",
      invalid_accepted_response: "Модель не вернула ответ в нужном формате.",
      promptbudgetexceeded: "Контекст превышает допустимый размер запроса.",
      prompt_budget_exceeded: "Контекст превышает допустимый размер запроса.",
      iteration_limit: "Достигнут лимит шагов выполнения.",
      tool_step_limit: "Достигнут лимит действий.",
      step_limit_reached: "Достигнут лимит шагов выполнения.",
      no_tool_progress: "Модель повторила описание прогресса без действия или завершения. Запуск остановлен.",
      repeated_unknown_tool_call: "Результат предыдущего действия неизвестен — проверьте документ перед повтором."
    };
    return labels[String(reason || "").toLowerCase()] || "Выполнение остановлено из-за ошибки. Подробности — в диагностике.";
  }

  function outcomeLabel(viewState) {
    if (!viewState) return "Статус неизвестен";
    if (viewState.lifecycle === "awaiting_confirmation") return "Нужно подтверждение";
    if (viewState.lifecycle === "awaiting_user") return "Нужен ваш ответ";
    if (viewState.lifecycle === "cancelled") return "Отменено";
    if (viewState.lifecycle === "failed") return viewState.executionHealth === "unknown"
      ? "Работа остановлена · результат действия неизвестен"
      : viewState.reason === "provider_refused" ? "Отказ провайдера" : "Ошибка выполнения";
    if (viewState.lifecycle === "completed") {
      if (viewState.reason === "model_blocked") return "Работа заблокирована";
      if (viewState.reason === "model_needs_input") return "Нужен ответ";
      return "Ответ завершён";
    }
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
    failureReasonLabel: failureReasonLabel,
    outcomeLabel: outcomeLabel
  });
}());
