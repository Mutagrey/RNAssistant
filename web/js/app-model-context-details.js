(function () {
  "use strict";
  function node(tag, text, cls) {
    var result = document.createElement(tag); if (text != null) result.textContent = String(text);
    if (cls) result.className = cls; return result;
  }
  function action(root, label, callback) {
    var button = node("button", label, "button compact"); button.type = "button";
    button.addEventListener("click", callback); root.appendChild(button); return button;
  }
  function facts(root, values) {
    var table = node("table", null, "rna-table"), body = node("tbody");
    values.forEach(function (pair) {
      var row = node("tr"), label = node("th", pair[0]); label.scope = "row";
      row.appendChild(label); row.appendChild(node("td", pair[1] == null || pair[1] === "" ? "Не записано" : pair[1])); body.appendChild(row);
    }); table.appendChild(body); root.appendChild(table);
  }
  var acceptance = { NotRecorded: "Нет записи о решении", Accepted: "Принято", Rejected: "Отклонено" };
  function summary(root, detail, navigate) {
    var request = detail.events.find(function (event) { return event.type === "llm.request"; }), trace = request.trace || {};
    var data = detail.summary || {}, box = node("section", null, "diagnostic-summary"); root.appendChild(box);
    box.appendChild(node("h3", data.parser === "Rejected" ? "Ответ модели отклонён" : data.runtime === "Accepted" ?
      "Решение записано runtime" : data.parser === "Accepted" ? "Форма ответа принята" : "Сохранённый обмен с моделью"));
    if (data.reason) box.appendChild(node("p", data.reason, "diagnostic-reason"));
    if (data.captureTruncated) box.appendChild(node("p", "Запись ответа обрезана лимитом захвата.", "diagnostic-reason"));
    else if (data.responseComplete === false) box.appendChild(node("p", "Получена только часть ответа; полнота не подтверждена.", "diagnostic-reason"));
    box.appendChild(node("p", "Принятие ответа и завершение запроса не доказывают выполнение задачи. Подтверждения инструментов — в запуске и доказательствах.", "diagnostic-body-note"));
    facts(box, [["Форма ответа", acceptance[data.parser] || "Нет записи о решении"],
      ["Принятие runtime", acceptance[data.runtime] || "Нет записи о решении"],
      ["Вход / выход по API", (function () { var response = detail.events.find(function (item) { return item.type === "llm.response"; });
        return response && (response.trace.PromptTokens != null || response.trace.CompletionTokens != null) ?
          [response.trace.PromptTokens == null ? "не записан" : response.trace.PromptTokens,
            response.trace.CompletionTokens == null ? "не записан" : response.trace.CompletionTokens].join(" / ") : "API не сообщил"; }())]]);
    var links = node("div", null, "diagnostic-links"); box.appendChild(links);
    action(links, "Что отправлено", function () { navigate("messages"); });
    action(links, "Ответ и причина отказа", function () { navigate("response"); });
    if (data.previousRequestEventId) action(links, "Сравнить с предыдущим запросом", function () { navigate("compare"); });
    var settings = node("details", null, "product-settings-advanced"); settings.appendChild(node("summary", "Настройки этого запроса")); box.appendChild(settings);
    var frozen = trace.Settings || {};
    facts(settings, [["Модель", trace.Model], ["Формат нового ответа", frozen.ResponseFormat || trace.ResponseFormat],
      ["История результатов инструментов", frozen.ToolResultRole === "tool" ? "Native tool history" : frozen.ToolResultRole],
      ["Роль инструкций", frozen.InstructionRole], ["Лимит ответа", frozen.MaxOutputTokens],
      ["Температура / top_p", frozen.Temperature == null ? null : frozen.Temperature + " / " + frozen.TopP],
      ["Поток", trace.Streaming == null ? null : trace.Streaming ? "SSE" : "JSON"]]);
    if (!trace.Settings) settings.appendChild(node("p", "Подробный снимок настроек в этой записи отсутствует. Текущие настройки не подставляются; отправленные параметры доступны в исходнике.", "diagnostic-body-note"));
    box.appendChild(node("p", !request.hasPayload ? "Точное тело запроса не сохранено." :
      "Точное сохранённое тело загружается по запросу" + (data.requestBytes == null ? "." : " · " + formatStorageBytes(data.requestBytes) + "."), "diagnostic-body-note"));
    var technical = node("details", null, "product-settings-advanced"); technical.appendChild(node("summary", "Идентификаторы и сохранённые байты"));
    facts(technical, [["Запрос", request.eventId], ["Запуск", trace.RunId], ["Шаг", trace.StepId],
      ["Попытка модели", trace.ModelAttemptId], ["Документ runtime", trace.DocumentRuntimeId], ["SHA-256 запроса", data.requestSha256]]);
    box.appendChild(technical);
  }
  function compare(root, data, navigate) {
    var box = node("section", null, "diagnostic-summary"); root.appendChild(box);
    box.appendChild(node("h3", "Предыдущий → выбранный запрос"));
    box.appendChild(node("p", data.reason || "Сравнение недоступно."));
    if (!data.available) return;
    facts(box, [["Сообщений", data.beforeMessages + " → " + data.afterMessages],
      ["Размер", formatStorageBytes(data.beforeBytes) + " → " + formatStorageBytes(data.afterBytes)],
      ["Без изменения", "Начало: " + data.unchangedPrefix + "; конец: " + data.unchangedSuffix],
      ["Параметры HTTP, которые изменились", (data.settingsChanged || []).join(", ") || "Без изменений"]]);
    (data.changes || []).forEach(function (change) {
      var labels = { added: "Добавлено", removed: "Удалено", changed: "Изменено" };
      var indices = (change.beforeIndex == null ? "—" : change.beforeIndex + 1) + " → " + (change.afterIndex == null ? "—" : change.afterIndex + 1);
      var text = (labels[change.kind] || change.kind) + " · сообщение " + indices + " · " + (change.afterRole || change.beforeRole || "роль не указана") +
        " · поля: " + (change.fields || []).join(", ");
      if (change.afterIndex == null) box.appendChild(node("p", text));
      else action(box, text, function () { navigate("messages", change.afterIndex); }).classList.add("diagnostic-comparison-change");
    });
    if (!(data.changes || []).length) box.appendChild(node("p", "Разобранные сообщения совпадают. Точные байты проверяются по хэшам исходников."));
    if (data.truncated) box.appendChild(node("p", "Сравнение частичное: показано до 200 изменённых сообщений и до 40 полей на запись.", "diagnostic-reason"));
    var links = node("div", null, "diagnostic-links"); box.appendChild(links);
    action(links, "Открыть предыдущий запрос", function () { navigate("request", data.beforeEventId); });
    action(links, "Исходник выбранного", function () { navigate("json"); });
  }
  window.RNAssistantModelContextDetails = { summary: summary, compare: compare };
}());
