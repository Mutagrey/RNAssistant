(function () {
  "use strict";
  var P = window.Prototype, E = P.escape, I = P.icon, G = P.go;
  P.inspectorTitles = { context: "Контекст чата", abilities: "Возможности агента", usage: "Использование контекста", request: "Запрос и ответ модели", activity: "Ход работы", changes: "Изменения", model: "Модель и параметры", attach: "Добавить в контекст", queue: "Ожидающие сообщения" };
  P.sampleMessages = function () { return [
    { role: "user", text: "Сравни сентябрь с августом. Что изменилось в продажах и на что стоит обратить внимание?" },
    { role: "assistant", kind: "report", changes: { complete: true, items: [{ title: "Итоги сентября.md", scope: "Материал чата", availability: "available", beforeExists: true, afterExists: true, before: "# Итоги сентября\n\nПервый вариант отчёта.\n", after: P.fixtures.report }] }, text: "В сентябре выручка выросла на 18,4%. Москва и Казань дали основной прирост. По Самаре есть пропуски в исходных данных." }
  ]; };
  function report(index) {
    var history = P.renderRunHistory({ id: "sample-" + index, done: true, duration: "8 с", blocks: [
      { type: "text", phase: "commentary", text: "Проверю исходные таблицы и сравню периоды." },
      { type: "work", id: "analysis", state: "complete", summary: "Изучены 2 источника", duration: "8 с", steps: [
        { id: "read", title: "Прочитать источники", state: "complete", result: "2 таблицы", detail: "Прочитаны листы продаж за сентябрь и август. Структура колонок совпадает.", target: "Продажи · сентябрь.xlsx / Продажи · август.xlsx" },
        { id: "compare", title: "Сравнить периоды", state: "complete", result: "+18,4%", detail: "Выручка: 12,8 млн ₽. Заказы: 1 248. Средний чек: 10 256 ₽." },
        { id: "report", title: "Подготовить отчёт", state: "complete", result: "Markdown", detail: "Краткий отчёт доступен в материалах чата." }
      ] }
    ] });
    return history + '<p>В сентябре выручка выросла на <strong>18,4%</strong>. Рост связан и с количеством заказов, и со средним чеком.</p>' + P.table(["Показатель", "Сентябрь", "К августу"], [["Выручка", "12,8 млн ₽", '<span class="increase">+18,4%</span>'], ["Заказы", "1 248", '<span class="increase">+13,0%</span>'], ["Средний чек", "10 256 ₽", '<span class="increase">+4,8%</span>']]) + '<h3>На что обратить внимание</h3><p>Москва и Казань дали основной прирост. По Самаре есть пропуски в исходных данных — проверьте их до итоговых выводов.</p><button class="artifact-link" data-go="materials/report"><span class="file-icon">' + I("file") + '</span><span class="row-copy"><strong>Итоги сентября</strong><small>Отчёт · Markdown · версия 2</small></span>' + I("right") + '</button>';
  }
  P.renderChatMessages = function () {
    var c = P.state.chats[P.state.chatId];
    var origin = c.forkOf && P.state.chats[c.forkOf] ? '<button class="fork-origin" data-chat="' + c.forkOf + '">' + I("fork") + 'Ветка · к исходному чату</button>' : '';
    return origin + c.messages.map(function (message, index) {
      var buttons = P.iconButton("Копировать сообщение", "copy-message:" + index, "copy") + (c.status === "running" ? "" : P.iconButton("Ответвить чат отсюда", "fork-message:" + index, "fork"));
      if (message.role === "user") return '<article class="user-message-entry" data-motion-key="user-' + index + '" data-scroll-key="user-' + index + '"><div class="user-message">' + E(message.text) + '</div><div class="message-actions">' + buttons + (c.status === "running" ? "" : P.iconButton("Редактировать сообщение", "edit-message:" + index, "edit")) + '</div></article>';
      return '<article class="assistant-message" data-motion-key="assistant-' + E(message.id || index) + '" aria-label="Ответ агента">' + (message.kind === "agent" ? P.renderAgentMessage(message) : message.kind === "report" ? report(index) : '<p>' + E(message.text) + '</p>') + P.diffUI.slot(message, index) + (message.kind === "agent" && !message.done ? '' : '<div class="message-actions">' + buttons + '</div>') + '</article>';
    }).join("");
  };
  P.chatModel = function () { return P.state.chats[P.state.chatId].model || "Модель A"; };
  P.composerMenu = function () {
    var plan = !!P.state.chats[P.state.chatId].plan;
    var label = plan ? "Вложения и действия · планирование включено" : "Вложения и действия";
    return '<details class="popover composer-add-menu"><summary class="icon-btn' + (plan ? ' is-active' : '') + '" aria-label="' + label + '" title="' + label + '">' + I("plus") + '</summary><div class="popover-menu">' +
      '<button type="button" data-action="pick-file">' + I("paperclip") + 'Файл с компьютера</button>' +
      '<button type="button" data-action="attach-selection">' + I("selection") + 'Выделение из Office</button>' +
      '<button type="button" data-go="materials/all">' + I("file") + 'Материал чата</button><hr>' +
      '<button type="button" data-action="toggle-plan" role="menuitemcheckbox" aria-checked="' + plan + '">' + I("layers") + '<span>Планирование</span>' + (plan ? I("check") : '') + '</button>' +
      '<button type="button" data-action="compact">' + I("compress") + 'Сжать контекст</button></div></details>';
  };
  P.resizeComposer = function () {
    var input = document.getElementById("chat-input"); if (!input) return;
    input.style.height = "auto";
    input.style.height = Math.min(180, input.scrollHeight) + "px";
  };
  P.renderModelPicker = function () {
    var selected = P.state.chats[P.state.chatId].model || "default";
    var options = [
      ["default", "По умолчанию", "Модель A · из общих настроек", []],
      ["Модель A", "Модель A", "Сбалансированная · обычные задачи", ["Reasoning", "Vision", "128k контекст", "8k ответ"]],
      ["Модель B", "Модель B", "Быстрые ответы · короткие запросы", ["Vision", "64k контекст", "8k ответ"]],
      ["Модель C", "Модель C", "Сложные задачи · глубокий анализ", ["Reasoning", "Vision", "200k контекст", "16k ответ"]]
    ];
    return '<details class="popover model-picker"><summary class="button ghost" aria-label="Выбрать модель" aria-haspopup="menu">' + E(P.chatModel()) + I("down") + '</summary><div class="popover-menu"><p class="model-menu-caption">Модель этого чата · демокаталог</p><div role="menu" aria-label="Модели">' + options.map(function (item) {
      return '<button type="button" class="model-option" role="menuitemradio" aria-checked="' + (selected === item[0]) + '" data-action="model:' + item[0] + '"><span class="model-option-copy"><strong>' + item[1] + '</strong><small>' + item[2] + '</small>' + (item[3].length ? '<span class="model-badges">' + item[3].map(function (tag) { return '<span>' + tag + '</span>'; }).join("") + '</span>' : '') + '</span>' + (selected === item[0] ? I("check") : '') + '</button>';
    }).join("") + '</div><hr>' + G("Параметры чата", "chat/model", "settings", "ghost") + '</div></details>';
  };
}());
